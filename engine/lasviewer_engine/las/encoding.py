"""Определение кодировки текстовых файлов.

Файлы компании приходят в разных кодировках: LAS — в CP1251, служебные файлы
станции — в CP866 (DOS). Признака кодировки в них нет, поэтому она
выбирается по содержимому: правильная однобайтовая кодировка даёт русские
буквы, неправильная — псевдографику и буквы других кириллических алфавитов
(«ЌѓЉЊ» вместо «НГКМ»).
"""

from __future__ import annotations

from dataclasses import dataclass

_RUSSIAN_LETTERS = frozenset("абвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ")
# Символы, которые законно встречаются в русском тексте вне ASCII.
_COMMON_SYMBOLS = frozenset("№«»°–—±·×µ²³")

# Порядок важен: при равной оценке выбирается первая. CP1251 — кодировка
# LAS-файлов, она вероятнее.
_SINGLE_BYTE_CANDIDATES = ("cp1251", "cp866")


@dataclass(frozen=True)
class EncodingGuess:
    codec: str
    # Как определена: "bom", "utf-8", "ascii" или "heuristic".
    method: str
    # Доля «правильных» символов среди не-ASCII для эвристики, иначе 1.0.
    confidence: float

    @property
    def display_name(self) -> str:
        return {
            "utf-8-sig": "UTF-8 (BOM)",
            "utf-8": "UTF-8",
            "utf-16": "UTF-16",
            "ascii": "ASCII",
            "cp1251": "Windows-1251",
            "cp866": "CP866 (DOS)",
        }.get(self.codec, self.codec)


def detect_encoding(data: bytes) -> EncodingGuess:
    if data.startswith(b"\xef\xbb\xbf"):
        return EncodingGuess("utf-8-sig", "bom", 1.0)
    if data.startswith((b"\xff\xfe", b"\xfe\xff")):
        return EncodingGuess("utf-16", "bom", 1.0)

    if all(byte < 0x80 for byte in data):
        return EncodingGuess("ascii", "ascii", 1.0)

    # Случайный однобайтовый текст почти никогда не складывается в корректный
    # UTF-8, поэтому успешное строгое декодирование — надёжный признак.
    try:
        data.decode("utf-8")
        return EncodingGuess("utf-8", "utf-8", 1.0)
    except UnicodeDecodeError:
        pass

    best_codec = _SINGLE_BYTE_CANDIDATES[0]
    best_score = -1.0
    for codec in _SINGLE_BYTE_CANDIDATES:
        score = _russian_score(data.decode(codec, errors="replace"))
        if score > best_score:
            best_codec, best_score = codec, score
    return EncodingGuess(best_codec, "heuristic", best_score)


def _russian_score(text: str) -> float:
    non_ascii = 0
    good = 0
    for char in text:
        if ord(char) < 0x80:
            continue
        non_ascii += 1
        if char in _RUSSIAN_LETTERS or char in _COMMON_SYMBOLS:
            good += 1
    return good / non_ascii if non_ascii else 1.0
