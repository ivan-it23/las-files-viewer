"""Методы работы с LAS-файлами: открытие, закрытие, список открытых.

Открытые файлы хранятся в движке: в следующих этапах к ним обращаются
обработка и агент. Приложение получает данные один раз при открытии и
рисует по своей копии.
"""

from __future__ import annotations

import base64
import sys
import uuid
from pathlib import Path
from typing import Any

from .las import LasFile, LasFormatError, read_las
from .las.parser import index_summary
from .rpc import INVALID_PARAMS, RpcError, Server

# Коды ошибок приложения — диапазон -32000…-32099 JSON-RPC.
FILE_NOT_FOUND = -32001
FILE_ACCESS_DENIED = -32002
NOT_LAS = -32003
UNKNOWN_FILE_ID = -32004

# Формат столбцов данных: float64 little-endian в base64. Точность исходных
# значений сохраняется полностью — таблица показывает то, что записано в файле.
DATA_ENCODING = "f64le-base64"


class Workspace:
    def __init__(self) -> None:
        self.files: dict[str, LasFile] = {}


def register(server: Server, workspace: Workspace | None = None) -> Workspace:
    workspace = workspace or Workspace()

    def open_file(path: str) -> dict[str, Any]:
        if not isinstance(path, str) or not path:
            raise RpcError(INVALID_PARAMS, "path должен быть непустой строкой.")
        try:
            las = read_las(path)
        except FileNotFoundError as exc:
            raise RpcError(FILE_NOT_FOUND, f"Файл не найден: {path}") from exc
        except PermissionError as exc:
            raise RpcError(FILE_ACCESS_DENIED, f"Нет доступа к файлу: {path}") from exc
        except IsADirectoryError as exc:
            raise RpcError(FILE_NOT_FOUND, f"Это папка, а не файл: {path}") from exc
        except LasFormatError as exc:
            raise RpcError(NOT_LAS, str(exc)) from exc

        file_id = uuid.uuid4().hex[:12]
        workspace.files[file_id] = las
        return describe(file_id, las, include_data=True)

    def close_file(file_id: str) -> None:
        if workspace.files.pop(file_id, None) is None:
            raise RpcError(UNKNOWN_FILE_ID, f"Файл {file_id} не открыт.")

    def list_files() -> list[dict[str, Any]]:
        return [
            {"file_id": file_id, "path": las.source, "row_count": las.row_count, "curve_count": len(las.curves)}
            for file_id, las in workspace.files.items()
        ]

    server.methods["las.open"] = open_file
    server.methods["las.close"] = close_file
    server.methods["las.list"] = list_files
    return workspace


def describe(file_id: str, las: LasFile, include_data: bool) -> dict[str, Any]:
    result: dict[str, Any] = {
        "file_id": file_id,
        "path": las.source,
        "name": Path(las.source).name,
        "size_bytes": las.size_bytes,
        "encoding": las.encoding,
        "encoding_display": las.encoding_display,
        "encoding_method": las.encoding_method,
        "version": las.version,
        "wrap": las.wrap,
        "delimiter": las.delimiter,
        "null_value": las.null_value,
        "row_count": las.row_count,
        "index": index_summary(las),
        "sections": [section.to_dict() for section in las.sections],
        "curves": [
            {
                "mnemonic": curve.mnemonic,
                "original_mnemonic": curve.original_mnemonic,
                "unit": curve.unit,
                "api_code": curve.api_code,
                "description": curve.description,
                "stats": curve.stats(),
            }
            for curve in las.curves
        ],
        "issues": [issue.to_dict() for issue in las.issues],
    }
    if include_data:
        result["data"] = {
            "encoding": DATA_ENCODING,
            "columns": [_encode_column(curve.values) for curve in las.curves],
        }
    return result


def _encode_column(values) -> str:
    raw = values.tobytes()
    if sys.byteorder != "little":
        swapped = values.__copy__()
        swapped.byteswap()
        raw = swapped.tobytes()
    return base64.b64encode(raw).decode("ascii")
