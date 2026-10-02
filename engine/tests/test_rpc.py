import io
import json
import os
import subprocess
import sys
import unittest
from pathlib import Path

ENGINE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE_ROOT))

from lasviewer_engine import PROTOCOL_VERSION  # noqa: E402
from lasviewer_engine import main as engine_main  # noqa: E402
from lasviewer_engine import system_methods  # noqa: E402
from lasviewer_engine.rpc import (  # noqa: E402
    INTERNAL_ERROR,
    INVALID_PARAMS,
    INVALID_REQUEST,
    METHOD_NOT_FOUND,
    PARSE_ERROR,
    Server,
)


def request(method, params=None, request_id=1):
    message = {"jsonrpc": "2.0", "id": request_id, "method": method}
    if params is not None:
        message["params"] = params
    return json.dumps(message, ensure_ascii=False).encode("utf-8")


class ServerTests(unittest.TestCase):
    def setUp(self):
        self.server = Server(io.BytesIO(), io.BytesIO())
        system_methods.register(self.server)

    def test_hello_reports_protocol_and_python(self):
        response = self.server.handle_line(request("system.hello", {"client": "tests"}))
        result = response["result"]
        self.assertEqual(result["protocol_version"], PROTOCOL_VERSION)
        self.assertEqual(result["python"]["bits"], 64)
        self.assertIn("numpy", result["packages"])

    def test_ping_round_trips_cyrillic(self):
        payload = "Кийское, RO4_400_S, Ом·м ✓"
        response = self.server.handle_line(request("system.ping", {"payload": payload}, 7))
        self.assertEqual(response, {"jsonrpc": "2.0", "id": 7, "result": {"payload": payload}})

    def test_unknown_method(self):
        response = self.server.handle_line(request("las.nothing"))
        self.assertEqual(response["error"]["code"], METHOD_NOT_FOUND)

    def test_unexpected_parameter_is_invalid_params(self):
        response = self.server.handle_line(request("system.ping", {"unknown": 1}))
        self.assertEqual(response["error"]["code"], INVALID_PARAMS)

    def test_wrong_parameter_type_is_invalid_params(self):
        response = self.server.handle_line(request("system.ping", {"payload": 5}))
        self.assertEqual(response["error"]["code"], INVALID_PARAMS)

    def test_handler_failure_is_internal_error_with_traceback(self):
        def broken():
            raise ValueError("сломано")

        self.server.methods["test.broken"] = broken
        response = self.server.handle_line(request("test.broken"))
        self.assertEqual(response["error"]["code"], INTERNAL_ERROR)
        self.assertIn("сломано", response["error"]["message"])
        self.assertIn("Traceback", response["error"]["data"]["traceback"])

    def test_type_error_inside_handler_is_not_invalid_params(self):
        def broken():
            return len(5)

        self.server.methods["test.broken"] = broken
        response = self.server.handle_line(request("test.broken"))
        self.assertEqual(response["error"]["code"], INTERNAL_ERROR)

    def test_malformed_json_is_parse_error(self):
        response = self.server.handle_line(b"{not json")
        self.assertEqual(response["error"]["code"], PARSE_ERROR)
        self.assertIsNone(response["id"])

    def test_invalid_utf8_is_parse_error(self):
        response = self.server.handle_line("{\"a\": \"Ом\"}".encode("cp1251"))
        self.assertEqual(response["error"]["code"], PARSE_ERROR)

    def test_batch_is_rejected(self):
        response = self.server.handle_line(b"[]")
        self.assertEqual(response["error"]["code"], INVALID_REQUEST)

    def test_notification_gets_no_response(self):
        line = json.dumps({"jsonrpc": "2.0", "method": "system.ping"}).encode()
        self.assertIsNone(self.server.handle_line(line))

    def test_bom_on_first_line_is_tolerated(self):
        response = self.server.handle_line("﻿".encode("utf-8") + request("system.ping"))
        self.assertEqual(response["result"], {"payload": ""})


class ServeTests(unittest.TestCase):
    def setUp(self):
        self.saved_stdout = sys.stdout

    def tearDown(self):
        sys.stdout = self.saved_stdout

    def test_print_from_handler_does_not_reach_protocol_stream(self):
        def noisy():
            print("посторонний вывод")
            return "ok"

        original_register = system_methods.register

        def register_with_noise(server):
            original_register(server)
            server.methods["test.noisy"] = noisy

        system_methods.register = register_with_noise
        try:
            reader = io.BytesIO(request("test.noisy") + b"\n" + request("system.shutdown", request_id=2) + b"\n")
            writer = io.BytesIO()
            exit_code = engine_main.serve(reader, writer)
        finally:
            system_methods.register = original_register

        self.assertEqual(exit_code, 0)
        lines = writer.getvalue().decode("utf-8").splitlines()
        self.assertEqual(len(lines), 2)
        self.assertEqual(json.loads(lines[0])["result"], "ok")
        self.assertEqual(json.loads(lines[1])["id"], 2)


class ProcessTests(unittest.TestCase):
    """Проверяет движок так, как его запускает приложение: отдельным процессом."""

    def start_engine(self):
        env = dict(os.environ, PYTHONPATH=str(ENGINE_ROOT))
        return subprocess.Popen(
            [sys.executable, "-X", "utf8", "-u", "-m", "lasviewer_engine"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=env,
        )

    def test_shutdown_ends_process(self):
        process = self.start_engine()
        stdout, stderr = process.communicate(
            request("system.ping", {"payload": "Ом·м"}) + b"\n" + request("system.shutdown", request_id=2) + b"\n",
            timeout=30,
        )
        self.assertEqual(process.returncode, 0, stderr.decode("utf-8", "replace"))
        lines = [json.loads(line) for line in stdout.decode("utf-8").splitlines()]
        self.assertEqual(lines[0]["result"]["payload"], "Ом·м")
        self.assertEqual(lines[1], {"jsonrpc": "2.0", "id": 2, "result": None})

    def test_closed_stdin_ends_process(self):
        process = self.start_engine()
        process.communicate(b"", timeout=30)
        self.assertEqual(process.returncode, 0)

    def test_self_check(self):
        env = dict(os.environ, PYTHONPATH=str(ENGINE_ROOT))
        output = subprocess.run(
            [sys.executable, "-X", "utf8", "-m", "lasviewer_engine", "--self-check"],
            capture_output=True,
            env=env,
            timeout=30,
            check=True,
        )
        info = json.loads(output.stdout.decode("utf-8"))
        self.assertEqual(info["protocol_version"], PROTOCOL_VERSION)


if __name__ == "__main__":
    unittest.main()
