"""База знаний агента: карточки с терминологией и правилами интерпретации.

Карточки лежат в ``knowledge/`` рядом с модулем (формат — ``knowledge/README.md``).
К каждому вопросу пользователя программа прикладывает подходящие карточки
(``context_for``) — модель опирается на методические материалы, даже если её
об этом не просили. Остальные она находит поиском (оглавление — в описании
``search_knowledge``) и открывает ``get_knowledge``. Поиск локальный, по
словам: сеть движку не нужна.

Номера страниц в карточках — для тех, кто их ведёт и проверяет. Модели текст
уходит без ссылок на источник (``strip_references``): пользователю сноски в
ответах не нужны — он знает, что агент опирается на методички.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass, field
from functools import lru_cache
from pathlib import Path
from typing import Any

KNOWLEDGE_DIR = Path(__file__).with_name("knowledge")

MAX_RESULTS = 10
DEFAULT_RESULTS = 5
MAX_CARD_CHARS = 16000
SNIPPET_CHARS = 320

# Материалы к вопросу уходят в системное сообщение на каждом шаге модели —
# объём ограничен, чтобы вопрос не дорожал многократно.
CONTEXT_CHARS = 10000
CONTEXT_CARDS = 2
GLOSSARY_ID = "glossary"

# Ссылки на источник внутри текста: «(с. 62)», «(методичка ВИКИЗ, с. 62)»,
# «(табл. 4.1, с. 32)», «(рис. 2.8, с. 12–13)», «, с. 85–86» в скобках.
_REFERENCE = re.compile(r"\s*\((?:методичк[^()]*|с\.\s[^()]*|стр\.[^()]*|табл\.[^()]*|рис\.[^()]*)\)", re.I)
_INNER_PAGES = re.compile(r",\s*с\.\s*[\d–\-—,\s]+(?=\))")
_TRAILING_PAGES = re.compile(r"\s*[—–-]\s*с\.\s*[\d–\-, ]+(?=[.;,]|$)", re.M)

STATUSES = ("эталон", "практика", "черновик")
_FIELDS = ("title", "source", "pages", "methods", "mnemonics", "status", "summary")


class KnowledgeError(Exception):
    """Ошибка в карточке или в запросе — описание уходит модели или в тест."""


@dataclass(frozen=True)
class Card:
    id: str
    title: str
    source: str
    pages: str
    methods: tuple[str, ...]
    mnemonics: tuple[str, ...]
    status: str
    summary: str
    body: str
    priority: int = 0
    terms: dict[str, float] = field(default_factory=dict, compare=False, repr=False)
    length: float = 0.0


# ----------------------------------------------------------------------
# Чтение карточек
# ----------------------------------------------------------------------


def parse_card(card_id: str, text: str) -> Card:
    text = text.lstrip("﻿")
    match = re.match(r"---\s*\n(.*?)\n---\s*\n", text, re.S)
    if match is None:
        raise KnowledgeError(f"{card_id}: нет шапки между строками ---.")
    header: dict[str, str] = {}
    for line in match.group(1).splitlines():
        if not line.strip():
            continue
        key, sep, value = line.partition(":")
        if not sep:
            raise KnowledgeError(f"{card_id}: строка шапки без двоеточия: {line!r}.")
        header[key.strip()] = value.strip()
    missing = [name for name in _FIELDS if name not in header]
    if missing:
        raise KnowledgeError(f"{card_id}: в шапке нет полей {', '.join(missing)}.")
    if header["status"] not in STATUSES:
        raise KnowledgeError(f"{card_id}: status должен быть одним из {', '.join(STATUSES)}.")
    for name in ("title", "source", "summary"):
        if not header[name]:
            raise KnowledgeError(f"{card_id}: пустое поле {name}.")

    body = text[match.end():].strip()
    card = Card(
        id=card_id,
        title=header["title"],
        source=header["source"],
        pages=header["pages"],
        methods=_split_list(header["methods"]),
        mnemonics=_split_list(header["mnemonics"]),
        status=header["status"],
        summary=header["summary"],
        body=body,
        priority=_priority(card_id, header.get("priority", "")),
    )
    terms = _weighted_terms(card)
    return Card(**{**card.__dict__, "terms": terms, "length": sum(terms.values())})


@lru_cache(maxsize=4)
def load_cards(directory: Path = KNOWLEDGE_DIR) -> tuple[Card, ...]:
    """Все карточки каталога; README.md — описание формата, не карточка."""
    cards = []
    for path in sorted(directory.rglob("*.md")):
        if path.name.lower() == "readme.md":
            continue
        card_id = path.relative_to(directory).with_suffix("").as_posix()
        cards.append(parse_card(card_id, path.read_text(encoding="utf-8")))
    return tuple(cards)


def index_text(cards: tuple[Card, ...] | None = None) -> str:
    """Оглавление для описания инструмента: номер, заголовок, о чём."""
    cards = load_cards() if cards is None else cards
    lines = []
    for card in cards:
        status = "" if card.status == "эталон" else f" [{card.status}]"
        lines.append(f"- {card.id} — {card.title}{status}: {card.summary}")
    return "\n".join(lines)


# ----------------------------------------------------------------------
# Поиск
# ----------------------------------------------------------------------


def search(query: Any, method: Any = None, limit: Any = DEFAULT_RESULTS,
           cards: tuple[Card, ...] | None = None) -> dict[str, Any]:
    if not isinstance(query, str) or not query.strip():
        raise KnowledgeError("query — непустая строка: слова или мнемоники, например «окаймляющая зона» или «IK07».")
    try:
        count = int(limit)
    except (TypeError, ValueError) as exc:
        raise KnowledgeError("limit — целое число.") from exc
    count = max(1, min(count, MAX_RESULTS))
    cards = load_cards() if cards is None else cards

    pool = list(cards)
    if method is not None:
        if not isinstance(method, str) or not method.strip():
            raise KnowledgeError("method — название метода, например «ВИКИЗ».")
        wanted = method.strip().lower()
        pool = [card for card in pool if any(wanted == m.lower() for m in card.methods)]
        if not pool:
            known = sorted({m for card in cards for m in card.methods})
            raise KnowledgeError(f"Карточек по методу {method} нет. Есть методы: {', '.join(known)}.")

    query_terms = _terms(query)
    mnemonic_hits = {word.lower() for word in re.findall(r"[\w.]+", query)}
    if not query_terms and not mnemonic_hits:
        raise KnowledgeError("В запросе нет слов для поиска.")

    average = sum(card.length for card in pool) / len(pool) if pool else 1.0
    scored = []
    for card in pool:
        score = _bm25(query_terms, card, pool, average)
        if mnemonic_hits & {m.lower() for m in card.mnemonics}:
            score += 3.0
        if score > 0:
            scored.append((score, card))
    scored.sort(key=lambda pair: (-pair[0], pair[1].id))

    return {
        "query": query,
        "found": len(scored),
        "results": [
            {
                "id": card.id,
                "title": card.title,
                "status": card.status,
                "summary": card.summary,
                "snippet": strip_references(_snippet(card.body, query_terms)),
                "score": round(score, 3),
            }
            for score, card in scored[:count]
        ],
        "hint": "Откройте нужную карточку get_knowledge(id), прежде чем опираться на неё.",
    }


def get(card_id: Any, cards: tuple[Card, ...] | None = None) -> dict[str, Any]:
    cards = load_cards() if cards is None else cards
    by_id = {card.id: card for card in cards}
    if not isinstance(card_id, str) or card_id.strip() not in by_id:
        raise KnowledgeError(f"Карточки {card_id} нет. Есть: {', '.join(by_id)}.")
    card = by_id[card_id.strip()]
    text = strip_references(card.body)
    truncated = len(text) > MAX_CARD_CHARS
    return {
        "id": card.id,
        "title": card.title,
        "status": card.status,
        "methods": list(card.methods),
        "text": text[:MAX_CARD_CHARS],
        "truncated": truncated,
    }


def context_for(question: Any, methods: Any = None, cards: tuple[Card, ...] | None = None) -> dict[str, Any]:
    """Методические материалы к вопросу: словарь терминов и самые подходящие карточки.

    Карточки ищутся по тексту вопроса; если он ни с чем не совпал («опиши
    файлы»), — по методам открытых файлов. Так модель получает материалы на
    любой вопрос, а не только когда сама решит их поискать.
    """
    cards = load_cards() if cards is None else cards
    by_id = {card.id: card for card in cards}
    chosen: list[Card] = []

    text = question if isinstance(question, str) else ""
    if _terms(text):
        for item in search(text, limit=MAX_RESULTS, cards=cards)["results"]:
            if item["id"] != GLOSSARY_ID and len(chosen) < CONTEXT_CARDS:
                chosen.append(by_id[item["id"]])

    # Первая карточка — лучшая по вопросу; вторая — главная карточка метода
    # открытых файлов (priority), чтобы основы интерпретации были всегда.
    chosen = chosen[:1]
    wanted = [m.lower() for m in methods if isinstance(m, str)] if isinstance(methods, list) else []
    core = sorted(
        (card for card in cards
         if card.priority > 0 and any(m.lower() in wanted for m in card.methods)),
        key=lambda card: card.priority,
    )
    for card in core:
        if len(chosen) < CONTEXT_CARDS and card not in chosen:
            chosen.append(card)
    if len(chosen) < CONTEXT_CARDS and _terms(text):
        for item in search(text, limit=MAX_RESULTS, cards=cards)["results"]:
            card = by_id[item["id"]]
            if card.id != GLOSSARY_ID and card not in chosen and len(chosen) < CONTEXT_CARDS:
                chosen.append(card)
    if not chosen:
        # Ни вопрос, ни файлы ничего не подсказали — главная карточка эталона.
        fallback = sorted((card for card in cards if card.priority > 0 and card.status == "эталон"),
                          key=lambda card: card.priority)
        chosen.extend(fallback[:1])

    parts = []
    used = []
    budget = CONTEXT_CHARS
    for card in ([by_id[GLOSSARY_ID]] if GLOSSARY_ID in by_id else []) + chosen:
        status = "" if card.status == "эталон" else f" (статус: {card.status} — не подтверждено)"
        block = f"### {card.title}{status}\n{strip_references(card.body)}"
        if len(block) > budget:
            if budget < 800:
                break
            block = block[: budget - 1] + "…"
        parts.append(block)
        used.append(card.id)
        budget -= len(block)
    return {"cards": used, "text": "\n\n".join(parts)}


def strip_references(text: str) -> str:
    """Убирает из текста ссылки на страницы, рисунки и таблицы источника."""
    text = _REFERENCE.sub("", text)
    text = _INNER_PAGES.sub("", text)
    text = _TRAILING_PAGES.sub("", text)
    return re.sub(r"[ \t]+([.,;:])", r"\1", text)


# ----------------------------------------------------------------------
# Слова
# ----------------------------------------------------------------------

# Окончания для грубого русского стемминга: «окаймляющей», «окаймляющая»,
# «окаймляющую» сводятся к одной основе. Точная морфология здесь не нужна:
# карточек десятки, и важно лишь не промахнуться мимо формы слова.
_ENDINGS = sorted(
    """
    иями ями ами ией ием иям иях ого его ому ему ыми ими ией ией ость ости остью
    ая яя ое ее ые ие ой ей ий ый ом ем ам ям ах ях ов ев ую юю ию ия ья ье ьи
    ть ти ет ут ют ит ат ят ешь ишь ем им
    а я о е ы и у ю ь й
    """.split(),
    key=len,
    reverse=True,
)

_STOP = set(
    """
    и в во на по с со к ко о об от до из за для при без не ни но а или же ли что как это
    то так все всё его ее её их он она они оно мы вы я ты бы был была были быть есть
    где когда какой какая какие каких который которая которые чем тем этот эта эти
    the of and or a an to in on for is are
    """.split()
)


def _stem(word: str) -> str:
    word = word.replace("ё", "е")
    if not re.search("[а-я]", word):
        return word
    for ending in _ENDINGS:
        if word.endswith(ending) and len(word) - len(ending) >= 3:
            return word[: -len(ending)]
    return word


def _terms(text: str) -> list[str]:
    words = re.findall(r"[a-zа-яё0-9]+", text.lower())
    return [_stem(word) for word in words if word not in _STOP and len(word) > 1]


def _weighted_terms(card: Card) -> dict[str, float]:
    weights: dict[str, float] = {}
    for text, weight in ((card.title, 3.0), (card.summary, 2.0), (" ".join(card.methods), 2.0), (card.body, 1.0)):
        for term in _terms(text):
            weights[term] = weights.get(term, 0.0) + weight
    return weights


def _bm25(query_terms: list[str], card: Card, pool: list[Card], average: float) -> float:
    k1, b = 1.4, 0.75
    total = len(pool)
    score = 0.0
    for term in set(query_terms):
        frequency = card.terms.get(term, 0.0)
        if frequency <= 0:
            continue
        containing = sum(1 for other in pool if term in other.terms)
        idf = math.log(1 + (total - containing + 0.5) / (containing + 0.5))
        score += idf * frequency * (k1 + 1) / (frequency + k1 * (1 - b + b * card.length / max(average, 1.0)))
    return score


def _snippet(body: str, query_terms: list[str]) -> str:
    """Абзац или строка карточки, где больше всего слов запроса."""
    wanted = set(query_terms)
    best, best_hits = "", 0
    for part in re.split(r"\n\s*\n|\n(?=[-|#])", body):
        part = part.strip()
        if not part or part.startswith("|---") or part.startswith("| ---"):
            continue
        hits = len(wanted & set(_terms(part)))
        if hits > best_hits:
            best, best_hits = part, hits
    if not best:
        best = body.strip().split("\n\n", 1)[0]
    best = re.sub(r"\s+", " ", best)
    return best if len(best) <= SNIPPET_CHARS else best[: SNIPPET_CHARS - 1] + "…"


def _priority(card_id: str, value: str) -> int:
    if not value:
        return 0
    if not value.isdigit():
        raise KnowledgeError(f"{card_id}: priority — целое число (1 — главная карточка метода).")
    return int(value)


def _split_list(value: str) -> tuple[str, ...]:
    return tuple(item.strip() for item in value.split(",") if item.strip())
