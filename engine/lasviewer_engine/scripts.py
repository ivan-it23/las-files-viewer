"""Скрипты агента: подготовка папки запуска, отдельный процесс, итог.

Скрипт не выполняется внутри движка: у него свой процесс (``script_runner``)
с таймаутом, своей рабочей папкой, без сети и без права писать вне этой
папки. Процесс наследует задание Windows движка, поэтому при закрытии
программы завершается вместе с ним.

Запуск возможен только методом ``agent.run_script``, который вызывает
приложение после подтверждения пользователем. Через ``agent.call`` модель
скрипт не запустит.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from array import array
from datetime import datetime
from pathlib import Path
from typing import Any

from .las import LasFile
from .las.parser import index_summary
from .las_methods import Workspace
from .rpc import INVALID_PARAMS, Background, RpcError, Server

# Таймаут скрипта. Приложение передаёт своё значение; движок только
# ограничивает его, чтобы зависший скрипт не жил вечно.
DEFAULT_TIMEOUT_SECONDS = 60
MIN_TIMEOUT_SECONDS = 5
MAX_TIMEOUT_SECONDS = 300

MAX_CODE_CHARS = 60_000
# Сколько вывода скрипта уходит модели: начало и конец, середина выпускается.
MAX_OUTPUT_FOR_MODEL = 12_000
MAX_TRACEBACK_FOR_MODEL = 4_000

RUNNER = Path(__file__).with_name("script_runner.py")

# Без окна консоли: движок запущен без неё, и python.exe скрипта иначе
# открыл бы чёрное окно поверх программы.
_CREATE_NO_WINDOW = 0x08000000 if sys.platform == "win32" else 0


class ScriptRunner:
    def __init__(self, workspace: Workspace) -> None:
        self._workspace = workspace
        self._lock = threading.Lock()
        self._processes: dict[str, subprocess.Popen[bytes]] = {}
        # Запуски, принятые, но ещё не завершённые, и отменённые из них.
        self._pending: set[str] = set()
        self._cancelled: set[str] = set()

    def run(
        self,
        run_id: str,
        code: str,
        scripts_dir: str,
        title: str = "",
        file_id: str | None = None,
        file_ids: list[str] | None = None,
        timeout_seconds: float | None = None,
    ) -> Any:
        if not isinstance(run_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", run_id):
            raise RpcError(INVALID_PARAMS, "run_id: от 1 до 64 латинских букв, цифр, «_» или «-».")
        if not isinstance(scripts_dir, str) or not os.path.isabs(scripts_dir):
            raise RpcError(INVALID_PARAMS, "scripts_dir должен быть полным путём.")
        if not isinstance(code, str) or not code.strip():
            return _failure("Пустой скрипт: передайте код в поле code.")
        if len(code) > MAX_CODE_CHARS:
            return _failure(f"Скрипт длиннее {MAX_CODE_CHARS} символов — разбейте задачу на части.")

        if file_id is not None and not isinstance(file_id, str):
            return _failure("file_id должен быть строкой.")
        if file_ids is None:
            selected = list(self._workspace.files)
        elif isinstance(file_ids, list) and all(isinstance(item, str) for item in file_ids):
            selected = list(dict.fromkeys(file_ids))
        else:
            return _failure("file_ids должен быть списком идентификаторов файлов.")
        if file_id is not None and file_id not in selected:
            selected.insert(0, file_id)
        unknown = [item for item in selected if item not in self._workspace.files]
        if unknown:
            open_ids = ", ".join(self._workspace.files) or "нет"
            return _failure(f"Файлы не открыты: {', '.join(unknown)}. Открыты: {open_ids}.")
        if not selected:
            return _failure("В программе нет открытых файлов — скрипту нечего анализировать.")
        main_id = file_id or selected[0]

        timeout = _timeout(timeout_seconds)
        run_dir = _new_run_dir(Path(scripts_dir), title)
        # Данные выгружаются здесь, в потоке запросов: дальше скрипт не
        # трогает открытые файлы движка и может идти параллельно.
        _write_inputs(run_dir, code, title, main_id, {item: self._workspace.files[item] for item in selected})
        mpl_dir = Path(scripts_dir) / ".matplotlib"
        mpl_dir.mkdir(parents=True, exist_ok=True)
        with self._lock:
            self._pending.add(run_id)
        return Background(lambda: self._execute(run_id, run_dir, mpl_dir, title, timeout))

    def cancel(self, run_id: str) -> bool:
        with self._lock:
            if run_id not in self._pending:
                return False
            self._cancelled.add(run_id)
            process = self._processes.get(run_id)
        # Процесс ещё не запущен — _execute увидит отмену и не запустит его.
        if process is not None:
            process.kill()
        return True

    def _execute(self, run_id: str, run_dir: Path, mpl_dir: Path, title: str, timeout: float) -> dict[str, Any]:
        env = dict(os.environ)
        env.update({
            "MPLBACKEND": "Agg",
            "MPLCONFIGDIR": str(mpl_dir),
            "TEMP": str(run_dir / "tmp"),
            "TMP": str(run_dir / "tmp"),
            "PYTHONIOENCODING": "utf-8",
        })
        # Прокси не нужен: сети у скрипта нет.
        for name in ("HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy"):
            env.pop(name, None)
        (run_dir / "tmp").mkdir(exist_ok=True)

        started = time.monotonic()
        status = "error"
        with open(run_dir / "runner.txt", "wb") as console:
            with self._lock:
                if run_id in self._cancelled:
                    self._pending.discard(run_id)
                    self._cancelled.discard(run_id)
                    shutil.rmtree(run_dir / "input", ignore_errors=True)
                    return _failure("Запуск скрипта отменён.", folder=str(run_dir))
                process = subprocess.Popen(
                    [sys.executable, *_isolation_flags(), "-B", "-X", "utf8", str(RUNNER), str(run_dir)],
                    cwd=run_dir,
                    env=env,
                    stdin=subprocess.DEVNULL,
                    stdout=console,
                    stderr=subprocess.STDOUT,
                    creationflags=_CREATE_NO_WINDOW,
                )
                self._processes[run_id] = process
            try:
                process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
                status = "timeout"
            finally:
                with self._lock:
                    self._processes.pop(run_id, None)
                    self._pending.discard(run_id)
                    cancelled = run_id in self._cancelled
                    self._cancelled.discard(run_id)

        # Копия данных скважины больше не нужна; код и результаты остаются.
        shutil.rmtree(run_dir / "input", ignore_errors=True)
        shutil.rmtree(run_dir / "tmp", ignore_errors=True)
        elapsed = round(time.monotonic() - started, 2)
        return _collect(run_dir, title, status, cancelled, process.returncode, timeout, elapsed)


def register(server: Server, workspace: Workspace) -> ScriptRunner:
    runner = ScriptRunner(workspace)
    server.methods["agent.run_script"] = runner.run
    server.methods["agent.cancel_script"] = runner.cancel
    return runner


# ----------------------------------------------------------------------
# Папка запуска
# ----------------------------------------------------------------------


def _isolation_flags() -> list[str]:
    """Флаги Python скрипта — как у самого движка.

    Встроенный Python установленной программы запускается изолированным
    (-I): пакеты — только из его Lib\\site-packages. Python разработчика —
    без -I, иначе не видны пакеты, поставленные в пользовательскую папку;
    -P не даёт модулям движка рядом с script_runner заслонить пакеты скрипта.
    """
    return ["-I"] if sys.flags.isolated else ["-E", "-P"]


def _timeout(value: Any) -> float:
    if value is None:
        return DEFAULT_TIMEOUT_SECONDS
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise RpcError(INVALID_PARAMS, "timeout_seconds должен быть числом.")
    return float(min(max(value, MIN_TIMEOUT_SECONDS), MAX_TIMEOUT_SECONDS))


def _new_run_dir(scripts_dir: Path, title: str) -> Path:
    slug = re.sub(r'[<>:"/\\|?*\x00-\x1f]+', " ", str(title or "")).strip()
    slug = re.sub(r"\s+", " ", slug)[:40].strip(" .") or "скрипт"
    stamp = datetime.now().strftime("%Y-%m-%d_%H%M%S")
    scripts_dir.mkdir(parents=True, exist_ok=True)
    for attempt in range(1, 1000):
        suffix = "" if attempt == 1 else f" ({attempt})"
        candidate = scripts_dir / f"{stamp} {slug}{suffix}"
        try:
            candidate.mkdir()
        except FileExistsError:
            continue
        return candidate
    raise RpcError(INVALID_PARAMS, f"Не удалось создать папку для скрипта в {scripts_dir}.")


def _write_inputs(run_dir: Path, code: str, title: str, main_id: str, files: dict[str, LasFile]) -> None:
    # script.py — ровно код агента: номера строк в ошибке совпадают с его кодом.
    # Что это за запуск — в info.json рядом.
    (run_dir / "script.py").write_text(code, encoding="utf-8")
    info = {
        "title": title,
        "started": datetime.now().isoformat(timespec="seconds"),
        "main_file": Path(files[main_id].source).name,
        "files": [las.source for las in files.values()],
    }
    (run_dir / "info.json").write_text(json.dumps(info, ensure_ascii=False, indent=1), encoding="utf-8")

    input_dir = run_dir / "input"
    input_dir.mkdir()
    entries = []
    for number, (file_id, las) in enumerate(files.items(), start=1):
        data_name = f"f{number}.f64"
        with open(input_dir / data_name, "wb") as stream:
            # Столбцы float64 little-endian подряд, как читает script_runner.
            for curve in las.curves:
                values = array("d", curve.values)
                if sys.byteorder != "little":
                    values.byteswap()
                values.tofile(stream)
        well = las.section("well")
        entries.append({
            "file_id": file_id,
            "name": Path(las.source).name,
            "path": las.source,
            "rows": las.row_count,
            "null_value": las.null_value,
            "data": data_name,
            "depth_unit": index_summary(las)["unit"],
            "well": {item.mnemonic: item.value for item in (well.items if well else []) if item.value},
            "curves": [
                {"mnemonic": curve.mnemonic, "unit": curve.unit, "description": curve.description}
                for curve in las.curves
            ],
        })
    meta = {"main_file_id": main_id, "files": entries}
    (input_dir / "meta.json").write_text(json.dumps(meta, ensure_ascii=False), encoding="utf-8")


# ----------------------------------------------------------------------
# Итог
# ----------------------------------------------------------------------


def _collect(
    run_dir: Path, title: str, status: str, cancelled: bool, exit_code: int | None, timeout: float, elapsed: float,
) -> dict[str, Any]:
    output_dir = run_dir / "output"
    report: dict[str, Any] = {}
    try:
        report = json.loads((run_dir / "result.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        pass

    log = _read_text(run_dir / "log.txt")
    figures = [name for name in report.get("figures", []) if (output_dir / name).is_file()]
    tables = [name for name in report.get("tables", []) if (output_dir / name).is_file()]
    known = set(figures) | set(tables)
    other = sorted(
        path.name for path in output_dir.iterdir() if path.is_file() and path.name not in known
    ) if output_dir.is_dir() else []

    result: dict[str, Any] = {
        "title": title,
        "folder": str(run_dir),
        "elapsed_seconds": elapsed,
        "output": _shorten(log, MAX_OUTPUT_FOR_MODEL),
        "figures": [{"name": name, "path": str(output_dir / name)} for name in figures],
        "tables": [{"name": name, "path": str(output_dir / name)} for name in tables],
        "files": [{"name": name, "path": str(output_dir / name)} for name in other],
    }
    if report.get("output_truncated"):
        result["output_note"] = "Вывод скрипта был слишком длинным и обрезан."

    if cancelled:
        return _failure("Скрипт остановлен пользователем.", **result)
    if status == "timeout":
        return _failure(
            f"Скрипт не уложился в {timeout:g} с и остановлен. Упростите расчёт или прорежьте данные.", **result)
    if report.get("status") == "ok":
        return {"ok": True, "result": result}
    if report:
        failure = _failure(f"Скрипт завершился с ошибкой: {report.get('error', 'без описания')}", **result)
        if report.get("traceback"):
            failure["traceback"] = _shorten(report["traceback"], MAX_TRACEBACK_FOR_MODEL)
        return failure

    console = _read_text(run_dir / "runner.txt")
    return _failure(
        f"Процесс скрипта завершился аварийно (код {exit_code}).", console=_shorten(console, MAX_TRACEBACK_FOR_MODEL), **result)


def _failure(message: str, **details: Any) -> dict[str, Any]:
    return {"ok": False, "error": message, **details}


def _read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""


def _shorten(text: str, limit: int) -> str:
    if len(text) <= limit:
        return text
    head = limit * 2 // 3
    tail = limit - head
    return text[:head] + f"\n…[пропущено {len(text) - limit} символов]…\n" + text[-tail:]
