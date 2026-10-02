"""Чтение LAS-файлов (CWLS Log ASCII Standard 1.2, 2.0 и журнальные секции 3.0)."""

from .encoding import detect_encoding
from .model import Curve, HeaderItem, Issue, LasFile, Section
from .parser import LasFormatError, parse_las_bytes, read_las

__all__ = [
    "Curve",
    "HeaderItem",
    "Issue",
    "LasFile",
    "LasFormatError",
    "Section",
    "detect_encoding",
    "parse_las_bytes",
    "read_las",
]
