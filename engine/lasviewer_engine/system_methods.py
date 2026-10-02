"""Служебные методы: рукопожатие, проверка связи, завершение."""

from __future__ import annotations

import os
import platform
import sys
from importlib import metadata
from pathlib import Path
from typing import Any

from . import PROTOCOL_VERSION, __version__
from .rpc import INVALID_PARAMS, RpcError, Server

# Пакеты, версии которых показываются в окне «О программе». Отсутствие пакета
# — не ошибка: на первом этапе движок обходится стандартной библиотекой.
REPORTED_PACKAGES = ("numpy", "pandas", "matplotlib", "python-docx")


def register(server: Server) -> None:
    def hello(client: str = "", client_version: str = "", protocol_version: int = 0) -> dict[str, Any]:
        return engine_info()

    def ping(payload: str = "") -> dict[str, Any]:
        if not isinstance(payload, str):
            raise RpcError(INVALID_PARAMS, "payload должен быть строкой.")
        return {"payload": payload}

    def shutdown() -> None:
        server.stop_requested = True
        return None

    server.methods["system.hello"] = hello
    server.methods["system.ping"] = ping
    server.methods["system.shutdown"] = shutdown


def engine_info() -> dict[str, Any]:
    return {
        "protocol_version": PROTOCOL_VERSION,
        "engine_version": __version__,
        "pid": os.getpid(),
        "python": {
            "version": platform.python_version(),
            "implementation": platform.python_implementation(),
            "executable": sys.executable,
            "embedded": is_embedded_python(),
            "bits": 64 if sys.maxsize > 2**32 else 32,
        },
        "packages": {name: _package_version(name) for name in REPORTED_PACKAGES},
    }


def is_embedded_python() -> bool:
    """Встроенный дистрибутив Python узнаётся по файлу ``python3XX._pth`` рядом с интерпретатором."""
    executable_dir = Path(sys.executable).parent
    return any(executable_dir.glob("python*._pth"))


def _package_version(name: str) -> str | None:
    try:
        return metadata.version(name)
    except metadata.PackageNotFoundError:
        return None
