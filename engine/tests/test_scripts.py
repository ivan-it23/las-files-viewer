"""Скрипты агента: отдельный процесс, результаты, ошибки, таймаут, ограничения."""

import io
import json
import shutil
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path

ENGINE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE_ROOT))

from lasviewer_engine import agent_tools, scripts  # noqa: E402
from lasviewer_engine.las.parser import parse_las_bytes  # noqa: E402
from lasviewer_engine.las_methods import Workspace  # noqa: E402
from lasviewer_engine.rpc import Background, Server  # noqa: E402

try:
    import matplotlib  # noqa: F401
    import numpy  # noqa: F401
    import pandas  # noqa: F401

    HAVE_PACKAGES = True
except ImportError:
    HAVE_PACKAGES = False

needs_packages = unittest.skipUnless(HAVE_PACKAGES, "нет numpy/pandas/matplotlib у этого Python")


def make_las(name="51_ЭМК.las"):
    text = "\n".join([
        "~Version information",
        "VERS. 2.0 :",
        "WRAP. NO :",
        "~Well information",
        "NULL. -999.25 :",
        "WELL. 51 : WELL",
        "~Curve information",
        "DEPT.M :",
        "RO.OHMM : сопротивление, Ом*м",
        "GR.UR/H : гамма",
        "~ASCII",
        "100.0 5 1",
        "100.1 7000 2",
        "100.2 -999.25 3",
        "100.3 7000 4",
        "100.4 6 5",
    ])
    return parse_las_bytes(text.encode("utf-8"), name)


class ScriptTests(unittest.TestCase):
    def setUp(self):
        self.scripts_dir = Path(tempfile.mkdtemp(prefix="lasviewer-scripts-"))
        self.workspace = Workspace()
        self.workspace.files["f1"] = make_las()
        self.workspace.files["f2"] = make_las("51_РК.las")
        self.runner = scripts.ScriptRunner(self.workspace)

    def tearDown(self):
        shutil.rmtree(self.scripts_dir, ignore_errors=True)

    def run_script(self, code, run_id="r1", **options):
        prepared = self.runner.run(run_id, code, str(self.scripts_dir), title=options.pop("title", "проверка"), **options)
        if isinstance(prepared, Background):
            return prepared.work()
        return prepared

    @needs_packages
    def test_output_figures_and_tables(self):
        result = self.run_script(
            "print(las.name, len(df), list(df.columns))\n"
            "print('пропусков', int(df['RO'].isna().sum()))\n"
            "print('файлов', len(files), files['f2'].name)\n"
            "print('интервал', len(las.interval(100.35, 100.05)))\n"
            "fig, ax = plt.subplots()\n"
            "ax.plot(df['RO'], df.index)\n"
            "save_figure('УЭС')\n"
            "plt.figure(); plt.hist(df['GR'].dropna())\n"
            "save_table(df.describe(), 'статистика')\n"
            "open('заметки.txt', 'w', encoding='utf-8').write('готово')\n",
            title="УЭС и гамма",
        )
        self.assertTrue(result["ok"], result)
        report = result["result"]
        self.assertIn("51_ЭМК.las 5 ['RO', 'GR']", report["output"])
        self.assertIn("пропусков 1", report["output"])
        self.assertIn("файлов 2 51_РК.las", report["output"])
        self.assertIn("интервал 3", report["output"])
        self.assertEqual([figure["name"] for figure in report["figures"]], ["УЭС.png", "график_2.png"])
        self.assertTrue(all(Path(figure["path"]).is_file() for figure in report["figures"]))
        self.assertEqual([table["name"] for table in report["tables"]], ["статистика.csv"])
        self.assertEqual([item["name"] for item in report["files"]], ["заметки.txt"])

        folder = Path(report["folder"])
        self.assertTrue(folder.name.endswith("УЭС и гамма"))
        self.assertEqual((folder / "script.py").read_text(encoding="utf-8").splitlines()[0], "print(las.name, len(df), list(df.columns))")
        self.assertFalse((folder / "input").exists(), "копия данных скважины должна удаляться после запуска")
        csv = (folder / "output" / "статистика.csv").read_bytes()
        self.assertTrue(csv.startswith(b"\xef\xbb\xbf"), "CSV для Excel — с BOM")

    @needs_packages
    def test_main_file_and_selected_files(self):
        result = self.run_script("print(las.file_id, sorted(files))", file_id="f2", file_ids=["f1"])
        self.assertTrue(result["ok"], result)
        self.assertIn("f2 ['f1', 'f2']", result["result"]["output"])

    @needs_packages
    def test_error_returns_traceback_with_script_lines(self):
        result = self.run_script("x = 1\ny = df['НЕТ']\n")
        self.assertFalse(result["ok"])
        self.assertIn("KeyError", result["error"])
        self.assertIn('File "script.py", line 2', result["traceback"])
        self.assertNotIn("script_runner", result["traceback"])

    @needs_packages
    def test_network_programs_and_outside_writes_are_blocked(self):
        outside = self.scripts_dir.parent / f"lasviewer-outside-{time.time_ns()}.txt"
        for code in (
            "import socket\nsocket.create_connection(('example.com', 80), timeout=2)",
            "import subprocess\nsubprocess.run(['cmd', '/c', 'echo'])",
            "import os\nos.system('echo')",
            f"open({str(outside)!r}, 'w').write('x')",
            f"import os\nos.remove({str(ENGINE_ROOT / 'requirements.txt')!r})",
            f"df.to_csv({str(outside)!r})",
        ):
            with self.subTest(code=code):
                result = self.run_script(code)
                self.assertFalse(result["ok"], result)
                self.assertIn("PermissionError", result["error"])
                self.assertIn("запрещено", result["error"])
        self.assertFalse(outside.exists())
        self.assertTrue((ENGINE_ROOT / "requirements.txt").exists())

    @needs_packages
    def test_timeout_stops_script(self):
        started = time.monotonic()
        result = self.run_script("while True:\n    pass", timeout_seconds=5)
        self.assertFalse(result["ok"])
        self.assertIn("не уложился в 5", result["error"])
        self.assertLess(time.monotonic() - started, 30)

    @needs_packages
    def test_cancel_stops_running_script(self):
        prepared = self.runner.run("stop-me", "import time\ntime.sleep(60)", str(self.scripts_dir), timeout_seconds=120)
        outcome = {}
        worker = threading.Thread(target=lambda: outcome.setdefault("result", prepared.work()))
        worker.start()
        deadline = time.monotonic() + 20
        while "stop-me" not in self.runner._processes:
            self.assertLess(time.monotonic(), deadline, "процесс скрипта не появился")
            time.sleep(0.05)
        self.assertTrue(self.runner.cancel("stop-me"))
        worker.join(30)
        self.assertFalse(worker.is_alive())
        self.assertFalse(outcome["result"]["ok"])
        self.assertIn("остановлен пользователем", outcome["result"]["error"])
        self.assertFalse(self.runner.cancel("stop-me"), "завершённый запуск не отменяется")

    def test_cancel_before_start_does_not_run_script(self):
        prepared = self.runner.run("early", "print('не должен запуститься')", str(self.scripts_dir))
        self.assertTrue(self.runner.cancel("early"))
        result = prepared.work()
        self.assertFalse(result["ok"])
        self.assertIn("отменён", result["error"])
        self.assertFalse((Path(result["folder"]) / "log.txt").exists())

    def test_bad_arguments_are_reported_to_model(self):
        self.assertFalse(self.run_script("   ")["ok"])
        unknown = self.run_script("print(1)", file_ids=["нет"])
        self.assertFalse(unknown["ok"])
        self.assertIn("не открыты: нет", unknown["error"])
        self.assertEqual(list(self.scripts_dir.iterdir()), [], "папка не должна создаваться для отвергнутого запуска")

    def test_timeout_is_clamped(self):
        self.assertEqual(scripts._timeout(None), scripts.DEFAULT_TIMEOUT_SECONDS)
        self.assertEqual(scripts._timeout(1), scripts.MIN_TIMEOUT_SECONDS)
        self.assertEqual(scripts._timeout(10_000), scripts.MAX_TIMEOUT_SECONDS)


class ScriptProtocolTests(unittest.TestCase):
    def test_model_cannot_run_script_through_agent_call(self):
        server = Server(io.BytesIO(), io.BytesIO())
        workspace = Workspace()
        agent_tools.register(server, workspace)
        result = server.methods["agent.call"]("run_python", {"title": "x", "code": "print(1)"})
        self.assertFalse(result["ok"])
        self.assertIn("подтверждения", result["error"])
        names = [tool["function"]["name"] for tool in server.methods["agent.tools"]()]
        self.assertIn("run_python", names)

    def test_background_response_does_not_block_next_request(self):
        output = io.BytesIO()
        server = Server(io.BytesIO(), output)
        release = threading.Event()
        server.methods["slow"] = lambda: Background(lambda: release.wait(10) and "медленно")
        server.methods["fast"] = lambda: "быстро"

        self.assertIsNone(server.handle_line(b'{"jsonrpc":"2.0","id":1,"method":"slow"}'))
        fast = server.handle_line(b'{"jsonrpc":"2.0","id":2,"method":"fast"}')
        self.assertEqual(fast["result"], "быстро")

        release.set()
        deadline = time.monotonic() + 10
        while not output.getvalue() and time.monotonic() < deadline:
            time.sleep(0.01)
        slow = json.loads(output.getvalue().decode("utf-8"))
        self.assertEqual((slow["id"], slow["result"]), (1, "медленно"))


if __name__ == "__main__":
    unittest.main()
