"""Сервер JSON-RPC 2.0 поверх двоичных потоков.

Кадрирование — одна JSON-строка UTF-8 на сообщение. ``json.dumps`` без отступов
не выдаёт переводов строки внутри значения, поэтому граница сообщения
однозначна.
"""

from __future__ import annotations

import inspect
import json
import logging
import threading
import traceback
from typing import Any, BinaryIO, Callable

log = logging.getLogger(__name__)

PARSE_ERROR = -32700
INVALID_REQUEST = -32600
METHOD_NOT_FOUND = -32601
INVALID_PARAMS = -32602
INTERNAL_ERROR = -32603

Handler = Callable[..., Any]


class RpcError(Exception):
    """Ошибка, которая уходит клиенту как ответ, а не роняет движок."""

    def __init__(self, code: int, message: str, data: Any = None) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
        self.data = data


class Background:
    """Ответ, который обработчик досчитает в отдельном потоке.

    Обработчик делает быструю часть сам (например, читает открытые файлы) и
    возвращает ``Background(work)``. Сервер сразу переходит к следующему
    запросу, а ответ уходит, когда ``work()`` закончится. Так долгий скрипт
    агента не останавливает открытие файлов и другие запросы. ``work`` не
    должна трогать общее состояние движка — только то, что ей передали.
    """

    def __init__(self, work: Callable[[], Any]) -> None:
        self.work = work


class Server:
    def __init__(self, reader: BinaryIO, writer: BinaryIO) -> None:
        self._reader = reader
        self._writer = writer
        self._write_lock = threading.Lock()
        self.methods: dict[str, Handler] = {}
        self.stop_requested = False

    def serve_forever(self) -> int:
        """Обрабатывает запросы до конца входного потока или ``system.shutdown``.

        Конец потока — штатный сигнал: приложение закрыло канал или завершилось
        само, и продолжать работу движку незачем.
        """
        for raw_line in self._reader:
            if not raw_line.strip():
                continue
            response = self.handle_line(raw_line)
            if response is not None:
                self._write(response)
            if self.stop_requested:
                break
        return 0

    def notify(self, method: str, params: Any = None) -> None:
        """Отправляет клиенту уведомление — сообщение без ``id``, ответа не ждёт."""
        message: dict[str, Any] = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        self._write(message)

    def handle_line(self, raw_line: bytes) -> dict[str, Any] | None:
        try:
            text = raw_line.decode("utf-8").lstrip("﻿")
            message = json.loads(text)
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            return _error_response(None, PARSE_ERROR, f"Сообщение не разобрано: {exc}")

        if isinstance(message, list):
            return _error_response(None, INVALID_REQUEST, "Пакетные запросы не поддерживаются.")
        if not isinstance(message, dict) or message.get("jsonrpc") != "2.0":
            return _error_response(None, INVALID_REQUEST, "Ожидался объект JSON-RPC 2.0.")

        request_id = message.get("id")
        is_notification = "id" not in message
        method = message.get("method")
        if not isinstance(method, str):
            return None if is_notification else _error_response(
                request_id, INVALID_REQUEST, "Поле method обязательно и должно быть строкой.")

        try:
            result = self._dispatch(method, message.get("params"))
        except RpcError as exc:
            return None if is_notification else _error_response(
                request_id, exc.code, exc.message, exc.data)
        except Exception as exc:  # noqa: BLE001 — любая ошибка обработчика уходит клиенту
            log.exception("Ошибка обработчика %s", method)
            return None if is_notification else _error_response(
                request_id,
                INTERNAL_ERROR,
                f"{type(exc).__name__}: {exc}",
                {"traceback": traceback.format_exc()},
            )

        if isinstance(result, Background):
            thread = threading.Thread(
                target=self._finish_background,
                args=(method, request_id, is_notification, result.work),
                name=f"rpc-{method}",
                daemon=True,
            )
            thread.start()
            return None

        if is_notification:
            return None
        return {"jsonrpc": "2.0", "id": request_id, "result": result}

    def _finish_background(self, method: str, request_id: Any, is_notification: bool, work: Callable[[], Any]) -> None:
        try:
            result = work()
        except RpcError as exc:
            response = _error_response(request_id, exc.code, exc.message, exc.data)
        except Exception as exc:  # noqa: BLE001 — как и у обычного обработчика
            log.exception("Ошибка обработчика %s", method)
            response = _error_response(
                request_id, INTERNAL_ERROR, f"{type(exc).__name__}: {exc}", {"traceback": traceback.format_exc()})
        else:
            response = {"jsonrpc": "2.0", "id": request_id, "result": result}
        if not is_notification:
            self._write(response)

    def _dispatch(self, method: str, params: Any) -> Any:
        handler = self.methods.get(method)
        if handler is None:
            raise RpcError(METHOD_NOT_FOUND, f"Метод {method} не найден.")

        if params is None:
            args: tuple[Any, ...] = ()
            kwargs: dict[str, Any] = {}
        elif isinstance(params, dict):
            args, kwargs = (), params
        elif isinstance(params, list):
            args, kwargs = tuple(params), {}
        else:
            raise RpcError(INVALID_PARAMS, "Параметры должны быть объектом или массивом.")

        # Привязка проверяется отдельно от вызова: TypeError изнутри обработчика —
        # это его ошибка, а не неверные параметры клиента.
        try:
            inspect.signature(handler).bind(*args, **kwargs)
        except TypeError as exc:
            raise RpcError(INVALID_PARAMS, f"Неверные параметры {method}: {exc}") from exc

        return handler(*args, **kwargs)

    def _write(self, message: dict[str, Any]) -> None:
        payload = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        with self._write_lock:
            self._writer.write(payload + b"\n")
            self._writer.flush()


def _error_response(request_id: Any, code: int, message: str, data: Any = None) -> dict[str, Any]:
    error: dict[str, Any] = {"code": code, "message": message}
    if data is not None:
        error["data"] = data
    return {"jsonrpc": "2.0", "id": request_id, "error": error}
