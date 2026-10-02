from __future__ import annotations

import argparse
import json
import logging
import sys
from typing import BinaryIO

from . import las_methods, system_methods
from .rpc import Server


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="lasviewer_engine")
    parser.add_argument(
        "--self-check",
        action="store_true",
        help="вывести сведения о движке и завершиться (для проверки сборки)",
    )
    args = parser.parse_args(argv)

    if args.self_check:
        print(json.dumps(system_methods.engine_info(), ensure_ascii=False, indent=2))
        return 0

    return serve(sys.stdin.buffer, sys.stdout.buffer)


def serve(reader: BinaryIO, writer: BinaryIO) -> int:
    """Запускает сервер на переданных потоках.

    stdout принадлежит протоколу. Всё, что напечатает сторонний код — print,
    предупреждения библиотек, — отправляется в stderr: одна посторонняя строка
    в stdout ломает разбор ответов на стороне приложения.
    """
    logging.basicConfig(
        level=logging.INFO,
        stream=sys.stderr,
        format="%(levelname)s %(name)s: %(message)s",
    )
    sys.stdout = sys.stderr

    server = Server(reader, writer)
    system_methods.register(server)
    las_methods.register(server)
    logging.getLogger(__name__).info("Движок запущен, ожидаю запросы")
    return server.serve_forever()
