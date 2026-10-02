"""Разбор LAS 1.2 / 2.0 и журнальных секций LAS 3.0.

Разборщик терпим к отклонениям от стандарта, но не молчалив: каждое
допущение попадает в список замечаний с номером строки. Неверно прочитанный
файл даёт правдоподобный, но неверный планшет, поэтому пользователь должен
видеть, что именно было прочитано не по стандарту.
"""

from __future__ import annotations

import math
import re
from array import array
from pathlib import Path

from .encoding import detect_encoding
from .model import Curve, HeaderItem, Issue, LasFile, Section

# Сведения о скважине, у которых в LAS 1.2 значение записано в поле описания.
_LAS12_DESCRIPTION_VALUES = frozenset(
    {"COMP", "WELL", "FLD", "LOC", "PROV", "CNTY", "STAT", "CTRY", "SRVC", "DATE", "UWI", "API", "LIC"}
)
# Поля ~Well, которые обычно заполняют; пустые перечисляются в замечании.
_DESCRIPTIVE_WELL_FIELDS = ("COMP", "WELL", "FLD", "LOC", "SRVC", "DATE")
_KNOWN_INDEX_MNEMONICS = frozenset({"DEPT", "DEPTH", "MD", "TVD", "TIME", "ETIM", "INDEX"})

_STANDARD_SECTION_PREFIXES = {
    "version": ("VERSION",),
    "well": ("WELL",),
    "curve": ("CURVE", "LOG_DEFINITION"),
    "parameter": ("PARAMETER", "LOG_PARAMETER"),
    "other": ("OTHER",),
    "data": ("A", "ASCII", "LOG_DATA"),
}
_SECTION_BY_LETTER = {"V": "version", "W": "well", "C": "curve", "P": "parameter", "O": "other", "A": "data"}
_LAS3_SECTION_NAMES = {"LOG_DEFINITION": "curve", "LOG_PARAMETER": "parameter", "LOG_DATA": "data"}

_UNIT_PATTERN = re.compile(r"[^\s:]*")
_MAX_LISTED_LINES = 10
_MAX_FILE_SIZE = 1024 * 1024 * 1024


class LasFormatError(Exception):
    """Файл не является LAS-файлом или не может быть прочитан вовсе."""


def read_las(path: str | Path) -> LasFile:
    file_path = Path(path)
    size = file_path.stat().st_size
    if size > _MAX_FILE_SIZE:
        raise LasFormatError(f"Файл слишком большой для чтения: {size / 1024 / 1024:.0f} МБ.")
    return parse_las_bytes(file_path.read_bytes(), str(file_path))


def parse_las_bytes(data: bytes, source: str = "") -> LasFile:
    issues: list[Issue] = []
    guess = detect_encoding(data)
    text = data.decode(guess.codec, errors="replace")
    if guess.method == "heuristic":
        issues.append(Issue(
            "info",
            "encoding_detected",
            f"Кодировка не указана в файле и определена по содержимому: {guess.display_name}.",
        ))
    replaced = text.count("�")
    if replaced:
        issues.append(Issue(
            "warning",
            "undecodable_bytes",
            f"{replaced} байт не соответствуют кодировке {guess.display_name} и заменены знаком «�».",
        ))

    lines = text.splitlines()
    sections, data_lines = _split_sections(lines, issues)
    if not any(section.kind == "curve" for section in sections) or data_lines is None:
        raise LasFormatError("В файле нет секций ~Curve и ~ASCII — это не LAS-файл.")

    version, wrap, delimiter = _read_version(sections, issues)
    if version == "1.2":
        _apply_las12_value_swap(sections)
    null_value = _read_null(sections, issues)
    curves = _build_curves(sections, issues)
    if not curves:
        raise LasFormatError("Секция ~Curve не содержит ни одной кривой.")

    _read_data(data_lines, curves, wrap, delimiter, null_value, issues)
    _check_well_fields(sections, issues)
    _check_index(sections, curves, issues)

    return LasFile(
        source=source,
        size_bytes=len(data),
        encoding=guess.codec,
        encoding_display=guess.display_name,
        encoding_method=guess.method,
        encoding_confidence=guess.confidence,
        version=version,
        wrap=wrap,
        delimiter=delimiter,
        null_value=null_value,
        sections=sections,
        curves=curves,
        issues=issues,
    )


# ---------------------------------------------------------------------------
# Секции
# ---------------------------------------------------------------------------

def _split_sections(lines: list[str], issues: list[Issue]) -> tuple[list[Section], list[tuple[int, str]] | None]:
    sections: list[Section] = []
    current: Section | None = None
    data_lines: list[tuple[int, str]] | None = None
    in_data = False
    preamble_lines = 0

    for number, raw in enumerate(lines, start=1):
        stripped = raw.strip()
        if stripped.startswith("~"):
            title = stripped[1:].strip()
            kind = _classify_section(title, number, issues)
            if kind == "data":
                if data_lines is not None:
                    issues.append(Issue(
                        "warning", "duplicate_data_section",
                        "В файле больше одной секции данных; прочитана первая.", number))
                    in_data = False
                    current = Section("ignored", title, number)
                    continue
                data_lines = []
                in_data = True
                current = Section("data", title, number)
                sections.append(current)
                continue

            in_data = False
            if kind in ("version", "well", "curve", "parameter") and any(s.kind == kind for s in sections):
                issues.append(Issue(
                    "warning", "duplicate_section",
                    f"Секция «~{title}» повторяется; учтена первая, повтор пропущен.", number))
                # Повтор не входит в модель файла: его строки читаются и отбрасываются.
                current = Section("ignored", title, number)
                continue
            else:
                current = Section(kind, title, number)
            sections.append(current)
            continue

        if in_data:
            if stripped and not stripped.startswith("#"):
                data_lines.append((number, stripped))  # type: ignore[union-attr]
            continue

        if current is None:
            if stripped:
                preamble_lines += 1
            continue

        if current.kind == "ignored":
            continue

        if current.kind in ("other", "unknown"):
            current.text.append(raw.rstrip())
            continue

        if not stripped or stripped.startswith("#"):
            continue

        item = _parse_header_line(stripped, number, issues)
        if item is not None:
            current.items.append(item)

    if preamble_lines:
        issues.append(Issue(
            "warning", "text_before_sections",
            f"{preamble_lines} строк до первой секции пропущены.", 1))
    return sections, data_lines


def _classify_section(title: str, line: int, issues: list[Issue]) -> str:
    name = title.upper()
    if not name:
        issues.append(Issue("warning", "unnamed_section", "Секция без имени пропущена.", line))
        return "unknown"

    first_word = name.split()[0]
    kind = _LAS3_SECTION_NAMES.get(first_word) or _SECTION_BY_LETTER.get(name[0])
    if kind is None:
        issues.append(Issue(
            "info", "unknown_section",
            f"Секция «~{title}» не входит в стандарт; её текст сохранён без разбора.", line))
        return "unknown"

    if not any(first_word.startswith(prefix) for prefix in _STANDARD_SECTION_PREFIXES[kind]):
        issues.append(Issue(
            "info", "nonstandard_section_name",
            f"Секция «~{title}» названа не по стандарту; распознана по первой букве как "
            f"~{_STANDARD_SECTION_PREFIXES[kind][0].capitalize()}.", line))
    return kind


def _parse_header_line(line: str, number: int, issues: list[Issue]) -> HeaderItem | None:
    """Разбирает строку вида ``MNEM.UNIT  ЗНАЧЕНИЕ : ОПИСАНИЕ``.

    По стандарту единица идёт сразу за первой точкой и кончается пробелом, а
    описание отделяется последним двоеточием строки — так время «13:45»
    остаётся в значении.
    """
    dot = line.find(".")
    if dot < 0:
        issues.append(Issue(
            "warning", "malformed_header_line",
            f"Строка заголовка без точки после мнемоники пропущена: «{line}».", number))
        return None

    mnemonic = line[:dot].strip()
    rest = line[dot + 1:]
    unit = _UNIT_PATTERN.match(rest).group(0)  # type: ignore[union-attr]
    after_unit = rest[len(unit):]

    colon = after_unit.rfind(":")
    if colon < 0:
        issues.append(Issue(
            "warning", "missing_colon",
            f"В строке заголовка «{mnemonic}» нет двоеточия; описание считается пустым.", number))
        value, description = after_unit.strip(), ""
    else:
        value, description = after_unit[:colon].strip(), after_unit[colon + 1:].strip()

    return HeaderItem(mnemonic, unit, value, description, number)


def _read_version(sections: list[Section], issues: list[Issue]) -> tuple[str, bool, str]:
    section = next((s for s in sections if s.kind == "version"), None)
    items = {item.mnemonic.upper(): item for item in section.items} if section else {}

    version = "2.0"
    vers = items.get("VERS")
    if vers is None:
        issues.append(Issue("warning", "missing_version", "Нет ~Version/VERS; файл читается как LAS 2.0."))
    else:
        try:
            number = float(vers.value)
            version = {1.2: "1.2", 2.0: "2.0", 3.0: "3.0"}.get(number, "")
        except ValueError:
            version = ""
        if not version:
            issues.append(Issue(
                "warning", "unknown_version",
                f"Неизвестная версия LAS «{vers.value}»; файл читается как LAS 2.0.", vers.line))
            version = "2.0"

    wrap = False
    wrap_item = items.get("WRAP")
    if wrap_item is not None:
        wrap_value = wrap_item.value.upper()
        if wrap_value in ("YES", "Y"):
            wrap = True
        elif wrap_value not in ("NO", "N"):
            issues.append(Issue(
                "warning", "unknown_wrap",
                f"Непонятное значение WRAP «{wrap_item.value}»; принято WRAP = NO.", wrap_item.line))

    delimiter = "space"
    dlm = items.get("DLM")
    if dlm is not None:
        delimiter = {"SPACE": "space", "COMMA": "comma", "TAB": "tab"}.get(dlm.value.upper(), "")
        if not delimiter:
            issues.append(Issue(
                "warning", "unknown_delimiter",
                f"Непонятный разделитель DLM «{dlm.value}»; принят пробел.", dlm.line))
            delimiter = "space"

    return version, wrap, delimiter


def _apply_las12_value_swap(sections: list[Section]) -> None:
    """В LAS 1.2 у описательных полей ~Well значение стоит после двоеточия."""
    for section in sections:
        if section.kind != "well":
            continue
        for item in section.items:
            if item.mnemonic.upper() in _LAS12_DESCRIPTION_VALUES and item.description:
                item.value, item.description = item.description, item.value


def _read_null(sections: list[Section], issues: list[Issue]) -> float | None:
    well = next((s for s in sections if s.kind == "well"), None)
    item = next((i for i in well.items if i.mnemonic.upper() == "NULL"), None) if well else None
    if item is None or not item.value:
        issues.append(Issue("info", "missing_null", "Значение NULL не задано; пропуски в данных не распознаются."))
        return None
    value = _to_float(item.value)
    if value is None:
        issues.append(Issue(
            "warning", "invalid_null",
            f"NULL «{item.value}» — не число; пропуски в данных не распознаются.", item.line))
    return value


def _build_curves(sections: list[Section], issues: list[Issue]) -> list[Curve]:
    section = next(s for s in sections if s.kind == "curve")
    curves: list[Curve] = []
    seen: dict[str, int] = {}
    for item in section.items:
        original = item.mnemonic
        mnemonic = original or "UNKNOWN"
        if not original:
            issues.append(Issue("warning", "empty_mnemonic", "Кривая без мнемоники названа UNKNOWN.", item.line))

        key = mnemonic.upper()
        if key in seen:
            seen[key] += 1
            renamed = f"{mnemonic}:{seen[key]}"
            issues.append(Issue(
                "warning", "duplicate_mnemonic",
                f"Мнемоника {mnemonic} повторяется; кривая переименована в {renamed}.", item.line))
            mnemonic = renamed
        else:
            seen[key] = 1

        curves.append(Curve(mnemonic, original, item.unit, item.value, item.description))
    return curves


# ---------------------------------------------------------------------------
# Данные
# ---------------------------------------------------------------------------

def _read_data(
    data_lines: list[tuple[int, str]],
    curves: list[Curve],
    wrap: bool,
    delimiter: str,
    null_value: float | None,
    issues: list[Issue],
) -> None:
    columns = [curve.values for curve in curves]
    width = len(columns)
    counters = {"non_numeric": [], "decimal_comma": [], "bad_rows": []}

    def split(line: str) -> list[str]:
        if delimiter == "comma":
            return [token.strip() for token in line.split(",")]
        if delimiter == "tab":
            return [token.strip() for token in line.split("\t")]
        return line.split()

    def append_row(tokens: list[str], line_number: int) -> None:
        for column, token in zip(columns, tokens):
            column.append(_parse_value(token, null_value, delimiter, line_number, counters))

    if wrap:
        pending: list[str] = []
        pending_line = 0
        for line_number, line in data_lines:
            if not pending:
                pending_line = line_number
            pending.extend(split(line))
            while len(pending) >= width:
                append_row(pending[:width], pending_line)
                del pending[:width]
                pending_line = line_number
        if pending:
            issues.append(Issue(
                "warning", "incomplete_wrapped_row",
                f"В конце данных осталось {len(pending)} значений — меньше, чем кривых ({width}); "
                "они пропущены.", pending_line))
    else:
        for line_number, line in data_lines:
            tokens = split(line)
            if len(tokens) != width:
                counters["bad_rows"].append((line_number, len(tokens)))
                continue
            append_row(tokens, line_number)

    if counters["bad_rows"]:
        rows = counters["bad_rows"]
        listed = ", ".join(f"{line} ({count})" for line, count in rows[:_MAX_LISTED_LINES])
        more = f" и ещё {len(rows) - _MAX_LISTED_LINES}" if len(rows) > _MAX_LISTED_LINES else ""
        issues.append(Issue(
            "warning", "column_count_mismatch",
            f"{len(rows)} строк данных содержат не {width} значений и пропущены. "
            f"Строки (число значений): {listed}{more}.", rows[0][0]))

    if counters["decimal_comma"]:
        lines = counters["decimal_comma"]
        issues.append(Issue(
            "warning", "decimal_comma",
            f"{len(lines)} значений записаны с десятичной запятой и прочитаны как числа.", lines[0]))

    if counters["non_numeric"]:
        lines = counters["non_numeric"]
        issues.append(Issue(
            "warning", "non_numeric_values",
            f"{len(lines)} нечисловых значений заменены пропусками.", lines[0]))

    if not columns[0]:
        issues.append(Issue("error", "no_data", "В секции данных нет ни одной прочитанной строки."))


def _parse_value(
    token: str,
    null_value: float | None,
    delimiter: str,
    line_number: int,
    counters: dict[str, list],
) -> float:
    try:
        value = float(token)
    except ValueError:
        value = None
        if delimiter != "comma" and "," in token:
            value = _to_float(token.replace(",", "."))
            if value is not None:
                counters["decimal_comma"].append(line_number)
        if value is None:
            counters["non_numeric"].append(line_number)
            return math.nan

    if math.isnan(value) or math.isinf(value):
        return math.nan
    if null_value is not None and value == null_value:
        return math.nan
    return value


def _to_float(text: str) -> float | None:
    # float() принимает «nan» и «inf», но в JSON-ответе такие значения
    # недопустимы, а по смыслу заголовка это «не задано».
    try:
        value = float(text)
    except ValueError:
        return None
    return value if math.isfinite(value) else None


# ---------------------------------------------------------------------------
# Проверки
# ---------------------------------------------------------------------------

def _check_well_fields(sections: list[Section], issues: list[Issue]) -> None:
    well = next((s for s in sections if s.kind == "well"), None)
    if well is None:
        issues.append(Issue("warning", "missing_well_section", "Нет секции ~Well."))
        return
    values = {item.mnemonic.upper(): item.value for item in well.items}
    empty = [name for name in _DESCRIPTIVE_WELL_FIELDS if not values.get(name)]
    if empty:
        issues.append(Issue(
            "info", "empty_well_fields",
            "Не заполнены сведения о скважине: " + ", ".join(empty) + ".", well.line))


def _check_index(sections: list[Section], curves: list[Curve], issues: list[Issue]) -> None:
    index = curves[0]
    if index.mnemonic.upper() not in _KNOWN_INDEX_MNEMONICS:
        issues.append(Issue(
            "info", "unusual_index",
            f"Первая кривая {index.mnemonic} не похожа на глубину или время, но используется как индекс."))

    values = index.values
    missing = sum(1 for value in values if value != value)
    if missing:
        issues.append(Issue(
            "warning", "index_has_nulls",
            f"В индексе {index.mnemonic} {missing} пропусков: эти строки не будут показаны на планшете."))

    present = [value for value in values if value == value]
    if len(present) < 2:
        return

    steps = [b - a for a, b in zip(present, present[1:])]
    if all(step > 0 for step in steps):
        direction = "increasing"
    elif all(step < 0 for step in steps):
        direction = "decreasing"
    else:
        direction = "unordered"
        issues.append(Issue(
            "warning", "index_not_monotonic",
            f"Индекс {index.mnemonic} не монотонен: глубины идут не по порядку или повторяются."))

    median_step = sorted(steps)[len(steps) // 2]
    tolerance = max(1e-6, abs(median_step) * 1e-3)
    irregular = sum(1 for step in steps if abs(step - median_step) > tolerance)
    if direction != "unordered" and irregular:
        issues.append(Issue(
            "info", "irregular_step",
            f"Шаг индекса неравномерный: {irregular} из {len(steps)} интервалов отличаются от "
            f"основного шага {median_step:g}."))

    well = next((s for s in sections if s.kind == "well"), None)
    header = {item.mnemonic.upper(): item for item in well.items} if well else {}
    checks = (
        ("STRT", present[0], "первое значение индекса"),
        ("STOP", present[-1], "последнее значение индекса"),
        ("STEP", median_step if not irregular else None, "фактический шаг"),
    )
    for mnemonic, actual, meaning in checks:
        item = header.get(mnemonic)
        if item is None:
            issues.append(Issue("info", f"missing_{mnemonic.lower()}", f"В ~Well нет {mnemonic}."))
            continue
        declared = _to_float(item.value)
        if declared is None or actual is None:
            continue
        if abs(declared - actual) > tolerance:
            issues.append(Issue(
                "warning", f"{mnemonic.lower()}_mismatch",
                f"{mnemonic} в заголовке {declared:g}, а {meaning} {actual:g}.", item.line))


def index_summary(las: LasFile) -> dict:
    index = las.curves[0]
    present = [value for value in index.values if value == value]
    header = {}
    well = las.section("well")
    if well is not None:
        header = {item.mnemonic.upper(): _to_float(item.value) for item in well.items}

    direction = "unknown"
    step = None
    if len(present) >= 2:
        steps = [b - a for a, b in zip(present, present[1:])]
        # Разность соседних глубин несёт ошибку представления (0.1 → 0.0999999999999),
        # а шаг показывается пользователю: округление до 10 значащих цифр её убирает.
        step = float(f"{sorted(steps)[len(steps) // 2]:.10g}")
        if all(s > 0 for s in steps):
            direction = "increasing"
        elif all(s < 0 for s in steps):
            direction = "decreasing"
        else:
            direction = "unordered"

    return {
        "mnemonic": index.mnemonic,
        "unit": index.unit,
        "start": present[0] if present else None,
        "stop": present[-1] if present else None,
        "step": step,
        "direction": direction,
        "header_start": header.get("STRT"),
        "header_stop": header.get("STOP"),
        "header_step": header.get("STEP"),
    }
