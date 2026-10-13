"""Среда выполнения скрипта агента — запускается отдельным процессом.

Вызов: ``python -I -B -X utf8 script_runner.py <папка запуска>`` (у Python
разработчика вместо ``-I`` — ``-E -P``, см. ``scripts._isolation_flags``). Модуль
самостоятельный: в изолированном режиме пакет движка может быть не виден,
поэтому здесь только стандартная библиотека и пакеты скриптов (numpy,
pandas, matplotlib).

Папка запуска (её готовит ``scripts.py``):

- ``script.py`` — код агента;
- ``input/meta.json`` и ``input/<ключ>.f64`` — данные открытых файлов:
  столбцы float64 подряд, пропуски — NaN;
- ``output/`` — рабочая папка скрипта: графики, таблицы, всё, что он запишет;
- ``log.txt`` — что скрипт напечатал;
- ``result.json`` — итог: состояние, ошибка, список результатов.

Ограничения (сеть, запуск программ, запись вне папки запуска) ставятся
перехватчиком аудита Python. Это защита от ошибок скрипта, а не от
злонамеренного кода: настоящая граница — пользователь, который видит код и
подтверждает запуск.
"""

from __future__ import annotations

import json
import math
import os
import re
import sys
import time
import traceback
from pathlib import Path
from typing import Any

# Сколько символов вывода сохраняется в log.txt; дальше вывод отбрасывается.
MAX_LOG_CHARS = 1_000_000
# Предел числа строк в таблице для Excel (у листа Excel — 1 048 576 строк).
MAX_TABLE_ROWS = 1_000_000


class CappedWriter:
    """Поток вывода скрипта: пишет в log.txt, после предела — молча отбрасывает."""

    def __init__(self, path: Path) -> None:
        self._file = open(path, "w", encoding="utf-8", newline="\n")  # noqa: SIM115 — закрывается в finish
        self._left = MAX_LOG_CHARS
        self.truncated = False

    def write(self, text: str) -> int:
        if self._left > 0:
            piece = text[: self._left]
            self._file.write(piece)
            self._left -= len(piece)
            if len(piece) < len(text):
                self.truncated = True
                self._file.write("\n…[вывод обрезан]\n")
        else:
            self.truncated = True
        return len(text)

    def flush(self) -> None:
        self._file.flush()

    def isatty(self) -> bool:
        return False

    def close(self) -> None:
        self._file.close()


class LasData:
    """Открытый LAS-файл в скрипте: глубина, кривые и сведения заголовка."""

    def __init__(self, meta: dict[str, Any], matrix: Any) -> None:
        import numpy as np

        self.file_id: str = meta["file_id"]
        self.name: str = meta["name"]
        self.path: str = meta["path"]
        self.null_value = meta.get("null_value")
        self.well: dict[str, str] = dict(meta.get("well", {}))
        columns = meta["curves"]
        depth = columns[0]
        self.depth_mnemonic: str = depth["mnemonic"]
        self.depth_unit: str = depth["unit"]
        self.depth = np.asarray(matrix[0])
        self.curves: dict[str, Any] = {}
        self.units: dict[str, str] = {}
        self.descriptions: dict[str, str] = {}
        for position, column in enumerate(columns[1:], start=1):
            self.curves[column["mnemonic"]] = np.asarray(matrix[position])
            self.units[column["mnemonic"]] = column["unit"]
            self.descriptions[column["mnemonic"]] = column["description"]
        self._df = None

    @property
    def df(self):
        """Таблица pandas: индекс — глубина, столбцы — кривые в порядке файла, пропуски — NaN."""
        if self._df is None:
            import pandas as pd

            index = pd.Index(self.depth, name=self.depth_mnemonic)
            self._df = pd.DataFrame(self.curves, index=index)
        return self._df

    def curve(self, name: str):
        """Значения кривой по мнемонике без учёта регистра."""
        if name in self.curves:
            return self.curves[name]
        for mnemonic, values in self.curves.items():
            if mnemonic.casefold() == name.casefold():
                return values
        raise KeyError(f"В файле {self.name} нет кривой {name}. Есть: {', '.join(self.curves)}")

    def interval(self, top: float | None = None, bottom: float | None = None):
        """Строки таблицы на интервале глубин (границы включаются; порядок не важен)."""
        frame = self.df
        low = -math.inf if top is None else top
        high = math.inf if bottom is None else bottom
        if low > high:
            low, high = high, low
        mask = (frame.index >= low) & (frame.index <= high)
        return frame[mask]

    def __repr__(self) -> str:
        return f"<LasData {self.name}: {len(self.depth)} строк, кривые {', '.join(self.curves)}>"


# ----------------------------------------------------------------------
# Ограничения
# ----------------------------------------------------------------------

_BLOCKED_EVENTS = {
    "socket.connect": "подключение к сети",
    "socket.bind": "открытие сетевого порта",
    "socket.getaddrinfo": "обращение к сети",
    "socket.gethostbyname": "обращение к сети",
    "socket.gethostbyaddr": "обращение к сети",
    "socket.sendto": "отправка по сети",
    "socket.sendmsg": "отправка по сети",
    "subprocess.Popen": "запуск программ",
    "_winapi.CreateProcess": "запуск программ",
    "os.system": "запуск программ",
    "os.exec": "запуск программ",
    "os.spawn": "запуск программ",
    "os.posix_spawn": "запуск программ",
    "os.startfile": "открытие файлов в других программах",
    "os.kill": "завершение процессов",
    "ctypes.dlopen": "загрузка системных библиотек",
    "ctypes.dlsym": "вызов системных функций",
    "ctypes.call_function": "вызов системных функций",
    "winreg.CreateKey": "изменение реестра",
    "winreg.DeleteKey": "изменение реестра",
    "winreg.DeleteValue": "изменение реестра",
    "winreg.SetValue": "изменение реестра",
    "winreg.SaveKey": "изменение реестра",
}

# События, меняющие файловую систему: проверяются пути в первых аргументах.
_PATH_EVENTS = {
    "os.remove": 1,
    "os.rmdir": 1,
    "os.mkdir": 1,
    "os.chmod": 1,
    "os.truncate": 1,
    "os.utime": 1,
    "os.rename": 2,
    "os.link": 2,
    "os.symlink": 2,
    "shutil.rmtree": 1,
}

_WRITE_FLAGS = os.O_WRONLY | os.O_RDWR | os.O_APPEND | os.O_CREAT | os.O_TRUNC


def install_guard(allowed_roots: list[Path]) -> None:
    roots = [os.path.normcase(os.path.abspath(root)) for root in allowed_roots]

    def inside(path: Any) -> bool:
        if isinstance(path, int):
            return True  # уже открытый дескриптор
        try:
            text = os.path.normcase(os.path.abspath(os.fsdecode(path)))
        except (TypeError, ValueError):
            return True
        return any(text == root or text.startswith(root + os.sep) for root in roots)

    def deny(what: str) -> None:
        raise PermissionError(
            f"Скрипту агента запрещено: {what}. Скрипт работает только с данными открытых файлов "
            "и пишет результаты в свою папку (текущая папка, save_figure, save_table).")

    def hook(event: str, args: tuple[Any, ...]) -> None:
        reason = _BLOCKED_EVENTS.get(event)
        if reason is not None:
            deny(reason)
        if event == "open":
            path, mode, flags = (tuple(args) + (None, None, None))[:3]
            writing = (isinstance(mode, str) and any(c in mode for c in "wax+")) or (
                isinstance(flags, int) and flags & _WRITE_FLAGS != 0)
            if writing and path is not None and not inside(path):
                deny(f"запись в {os.fsdecode(path)}")
            return
        count = _PATH_EVENTS.get(event)
        if count is not None:
            for path in args[:count]:
                if path is not None and not inside(path):
                    deny(f"изменение {os.fsdecode(path)}")

    sys.addaudithook(hook)


# ----------------------------------------------------------------------
# Запуск
# ----------------------------------------------------------------------


def _safe_name(name: Any, default: str, suffix: str) -> str:
    text = str(name).strip() if name else default
    text = re.sub(r'[<>:"/\\|?*\x00-\x1f]+', "_", text).strip(" .") or default
    if not text.lower().endswith(suffix):
        text += suffix
    return text[:120]


def main(argv: list[str]) -> int:
    run_dir = Path(argv[1]).resolve()
    output_dir = run_dir / "output"
    output_dir.mkdir(exist_ok=True)
    os.chdir(output_dir)

    writer = CappedWriter(run_dir / "log.txt")
    sys.stdout = writer
    sys.stderr = writer
    started = time.monotonic()
    result: dict[str, Any] = {"status": "error", "figures": [], "tables": []}
    saved_figures: dict[int, str] = {}

    try:
        import matplotlib

        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        import numpy as np
        import pandas as pd
    except ImportError as exc:
        result["error"] = f"Не установлены пакеты для скриптов: {exc}"
        _finish(run_dir, result, writer, started)
        return 0

    meta = json.loads((run_dir / "input" / "meta.json").read_text(encoding="utf-8"))
    files: dict[str, LasData] = {}
    for entry in meta["files"]:
        raw = np.fromfile(run_dir / "input" / entry["data"], dtype="<f8")
        matrix = raw.reshape(len(entry["curves"]), entry["rows"])
        files[entry["file_id"]] = LasData(entry, matrix)
    main_file = files[meta["main_file_id"]]

    def save_figure(name: str | None = None, fig: Any = None, dpi: int = 150) -> str:
        """Сохраняет график PNG в папку результатов и возвращает имя файла."""
        figure = fig if fig is not None else plt.gcf()
        file_name = _safe_name(name, f"график_{len(result['figures']) + 1}", ".png")
        figure.savefig(output_dir / file_name, dpi=dpi, bbox_inches="tight")
        saved_figures[id(figure)] = file_name
        if file_name not in result["figures"]:
            result["figures"].append(file_name)
        return file_name

    def save_table(table: Any, name: str | None = None, index: bool = True) -> str:
        """Сохраняет таблицу CSV для Excel (UTF-8 с BOM, «;», десятичная запятая)."""
        frame = table if isinstance(table, pd.DataFrame) else pd.DataFrame(table)
        if len(frame) > MAX_TABLE_ROWS:
            raise ValueError(f"В таблице {len(frame)} строк — больше, чем помещается на лист Excel.")
        file_name = _safe_name(name, f"таблица_{len(result['tables']) + 1}", ".csv")
        frame.to_csv(output_dir / file_name, sep=";", decimal=",", index=index, encoding="utf-8-sig")
        if file_name not in result["tables"]:
            result["tables"].append(file_name)
        return file_name

    def save_report(document: Any, name: str | None = None) -> str:
        """Сохраняет документ Word (python-docx) в папку результатов и возвращает имя файла."""
        file_name = _safe_name(name, "отчёт", ".docx")
        document.save(output_dir / file_name)
        return file_name

    namespace: dict[str, Any] = {
        "__name__": "__main__",
        "np": np,
        "pd": pd,
        "plt": plt,
        "matplotlib": matplotlib,
        "files": files,
        "las": main_file,
        "df": main_file.df,
        "OUTPUT_DIR": output_dir,
        "save_figure": save_figure,
        "save_table": save_table,
        "save_report": save_report,
    }

    script_path = run_dir / "script.py"
    code = script_path.read_text(encoding="utf-8")
    install_guard([run_dir, Path(os.environ.get("MPLCONFIGDIR", run_dir))])
    try:
        compiled = compile(code, str(script_path), "exec")
        exec(compiled, namespace)  # noqa: S102 — ради этого модуль и существует
        result["status"] = "ok"
    except SystemExit as exc:
        if exc.code in (None, 0):
            result["status"] = "ok"
        else:
            result["error"] = f"Скрипт вызвал exit({exc.code!r})"
    except BaseException as exc:  # noqa: BLE001 — любая ошибка скрипта — его итог
        result["error"] = f"{type(exc).__name__}: {exc}"
        result["traceback"] = _script_traceback(exc, str(script_path))

    # Несохранённые графики сохраняются сами: модель часто забывает savefig.
    try:
        for number in plt.get_fignums():
            figure = plt.figure(number)
            if id(figure) not in saved_figures and figure.get_axes():
                save_figure(None, figure)
    except Exception as exc:  # noqa: BLE001
        print(f"Не удалось сохранить график: {exc}")
    finally:
        plt.close("all")

    _finish(run_dir, result, writer, started)
    return 0


def _script_traceback(exc: BaseException, script_path: str) -> str:
    """Трассировка начиная с кадров скрипта: кадры среды выполнения модели не нужны."""
    frames = traceback.extract_tb(exc.__traceback__)
    first = next((i for i, frame in enumerate(frames) if frame.filename == script_path), 0)
    lines = ["Traceback (most recent call last):\n"]
    for frame in frames[first:]:
        where = "script.py" if frame.filename == script_path else frame.filename
        lines.append(f'  File "{where}", line {frame.lineno}, in {frame.name}\n')
        if frame.line:
            lines.append(f"    {frame.line.strip()}\n")
    lines.extend(traceback.format_exception_only(type(exc), exc))
    return "".join(lines)


def _finish(run_dir: Path, result: dict[str, Any], writer: CappedWriter, started: float) -> None:
    result["elapsed_seconds"] = round(time.monotonic() - started, 2)
    result["output_truncated"] = writer.truncated
    writer.flush()
    (run_dir / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
    writer.close()


if __name__ == "__main__":
    sys.exit(main(sys.argv))
