from __future__ import annotations

import math
from array import array
from dataclasses import dataclass, field
from typing import Any


@dataclass
class HeaderItem:
    mnemonic: str
    unit: str
    value: str
    description: str
    # Номер строки в файле, с 1. Нужен, чтобы отчёт о разборе и редактор
    # заголовка ссылались на место в исходном файле.
    line: int

    def to_dict(self) -> dict[str, Any]:
        return {
            "mnemonic": self.mnemonic,
            "unit": self.unit,
            "value": self.value,
            "description": self.description,
            "line": self.line,
        }


@dataclass
class Section:
    # version | well | curve | parameter | other | data | unknown
    kind: str
    # Заголовок как в файле, без «~».
    title: str
    line: int
    items: list[HeaderItem] = field(default_factory=list)
    # Сырые строки для ~Other и нераспознанных секций.
    text: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return {
            "kind": self.kind,
            "title": self.title,
            "line": self.line,
            "items": [item.to_dict() for item in self.items],
            "text": "\n".join(self.text),
        }


@dataclass
class Issue:
    # info — особенность файла, не мешающая чтению;
    # warning — часть данных прочитана с допущением или пропущена;
    # error — файл прочитан не полностью.
    level: str
    code: str
    message: str
    line: int | None = None

    def to_dict(self) -> dict[str, Any]:
        return {"level": self.level, "code": self.code, "message": self.message, "line": self.line}


@dataclass
class Curve:
    mnemonic: str
    # Мнемоника в файле. Отличается от mnemonic, если в файле были повторы и
    # кривая переименована.
    original_mnemonic: str
    unit: str
    api_code: str
    description: str
    # Значения; отсутствующие (NULL, нечисловые) — NaN.
    values: array = field(default_factory=lambda: array("d"))

    def stats(self) -> dict[str, Any]:
        count = 0
        total = 0.0
        minimum = math.inf
        maximum = -math.inf
        for value in self.values:
            if value != value:  # NaN
                continue
            count += 1
            total += value
            if value < minimum:
                minimum = value
            if value > maximum:
                maximum = value
        return {
            "count": count,
            "nulls": len(self.values) - count,
            "min": minimum if count else None,
            "max": maximum if count else None,
            "mean": total / count if count else None,
        }


@dataclass
class LasFile:
    source: str
    size_bytes: int
    encoding: str
    encoding_display: str
    encoding_method: str
    encoding_confidence: float
    version: str
    wrap: bool
    delimiter: str
    null_value: float | None
    sections: list[Section]
    curves: list[Curve]
    issues: list[Issue]

    @property
    def row_count(self) -> int:
        return len(self.curves[0].values) if self.curves else 0

    def section(self, kind: str) -> Section | None:
        return next((section for section in self.sections if section.kind == kind), None)

    def well_value(self, mnemonic: str) -> str | None:
        section = self.section("well")
        if section is None:
            return None
        item = next((item for item in section.items if item.mnemonic.upper() == mnemonic), None)
        return item.value if item else None
