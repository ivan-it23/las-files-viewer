"""Инструменты ИИ-агента: результаты на известных данных и поведение при ошибках."""

import io
import json
import math
import os
import sys
import unittest
from pathlib import Path

ENGINE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE_ROOT))

from lasviewer_engine import agent_tools  # noqa: E402
from lasviewer_engine.las import read_las  # noqa: E402
from lasviewer_engine.las.parser import parse_las_bytes  # noqa: E402
from lasviewer_engine.las_methods import Workspace  # noqa: E402
from lasviewer_engine.rpc import Server  # noqa: E402


def make_las(rows, curves=("A", "B")):
    header = [
        "~Version information",
        "VERS. 2.0 :",
        "WRAP. NO :",
        "~Well information",
        f"STRT.M {rows[0][0]} :",
        f"STOP.M {rows[-1][0]} :",
        "STEP.M 0.1 :",
        "NULL. -999.25 :",
        "WELL. 51 : WELL",
        "~Curve information",
        "DEPT.M :",
    ]
    header += [f"{name}.OHMM : кривая {name}" for name in curves]
    header.append("~ASCII")
    body = [" ".join(str(value) for value in row) for row in rows]
    return parse_las_bytes("\n".join(header + body).encode("utf-8"), "test.las")


def server_with(**files):
    server = Server(io.BytesIO(), io.BytesIO())
    workspace = Workspace()
    workspace.files.update(files)
    agent_tools.register(server, workspace)
    return server


def call(server, name, **arguments):
    response = server.methods["agent.call"](name, arguments)
    # Результат обязан сериализоваться в строгий JSON: NaN в нём не допускается.
    json.dumps(response, allow_nan=False)
    return response


class AgentToolTests(unittest.TestCase):
    def setUp(self):
        # Глубины 100.0…101.9, 20 строк. A: 1…20, B: «полка» 50 на 100.5–100.9.
        rows = []
        for i in range(20):
            depth = round(100 + i * 0.1, 1)
            a = i + 1
            b = 50 if 5 <= i <= 9 else i * 3 + 0.5
            rows.append((depth, a, -999.25 if i in (14, 15) else b))
        self.las = make_las(rows)
        self.server = server_with(f1=self.las)

    def test_tool_descriptions_are_valid_openai_functions(self):
        tools = self.server.methods["agent.tools"]()
        names = {tool["function"]["name"] for tool in tools}
        self.assertEqual(names, {"list_files", "get_file_info", "curve_stats", "find_intervals", "get_samples", "compare_curves"})
        for tool in tools:
            self.assertEqual(tool["type"], "function")
            self.assertEqual(tool["function"]["parameters"]["type"], "object")
        json.dumps(tools, allow_nan=False)

    def test_list_and_info(self):
        files = call(self.server, "list_files")["result"]["files"]
        self.assertEqual(files[0]["file_id"], "f1")
        self.assertEqual([c["mnemonic"] for c in files[0]["curves"]], ["A", "B"])

        info = call(self.server, "get_file_info", file_id="f1")["result"]
        self.assertEqual(info["rows"], 20)
        self.assertIn({"mnemonic": "WELL", "value": "51", "description": "WELL"}, info["header"])
        b = next(c for c in info["curves"] if c["mnemonic"] == "B")
        self.assertEqual(b["nulls"], 2)

    def test_curve_stats_on_interval(self):
        result = call(self.server, "curve_stats", file_id="f1", curves=["A"], top=100.0, bottom=100.4)["result"]
        a = result["curves"][0]
        self.assertEqual(a["count"], 5)
        self.assertAlmostEqual(a["mean"], 3.0)
        self.assertEqual(a["max_depth"], 100.4)
        self.assertAlmostEqual(a["median"], 3.0)

    def test_find_threshold_intervals_and_share(self):
        result = call(self.server, "find_intervals", file_id="f1", curve="A", condition="gt", value=15)["result"]
        self.assertEqual(result["found"], 1)
        self.assertEqual(result["intervals"][0]["top"], 101.5)
        self.assertEqual(result["intervals"][0]["bottom"], 101.9)
        self.assertAlmostEqual(result["total_thickness"], 0.5)
        self.assertAlmostEqual(result["share_percent"], 25.0)

    def test_find_nulls_and_plateau(self):
        nulls = call(self.server, "find_intervals", file_id="f1", curve="B", condition="null")["result"]
        self.assertEqual([(i["top"], i["bottom"]) for i in nulls["intervals"]], [(101.4, 101.5)])

        plateau = call(self.server, "find_intervals", file_id="f1", curve="B", condition="plateau", min_thickness=0.3)["result"]
        self.assertEqual([(i["top"], i["bottom"], i["mean"]) for i in plateau["intervals"]], [(100.5, 100.9, 50.0)])

    def test_find_intervals_for_several_curves_in_one_call(self):
        result = call(self.server, "find_intervals", file_id="f1", curves=["A", "B"], condition="gt", value=15)["result"]
        self.assertEqual([r["curve"] for r in result["results"]], ["A", "B"])
        self.assertEqual(result["results"][0]["found"], 1)
        missing = call(self.server, "find_intervals", file_id="f1", condition="gt", value=1)
        self.assertFalse(missing["ok"])

    def test_merge_gap_joins_close_intervals(self):
        separate = call(self.server, "find_intervals", file_id="f1", curve="A", condition="outside", value=3, value2=17)["result"]
        self.assertEqual(separate["found"], 2)
        merged = call(self.server, "find_intervals", file_id="f1", curve="A", condition="outside", value=3, value2=17, merge_gap=2)["result"]
        self.assertEqual(merged["found"], 1)

    def test_samples_are_decimated_means(self):
        result = call(self.server, "get_samples", file_id="f1", curves=["A"], max_points=4, aggregate="minmax")["result"]
        self.assertEqual(result["points"], 4)
        first = result["rows"][0]
        self.assertAlmostEqual(first["A"], 3.0)
        self.assertEqual((first["A_min"], first["A_max"]), (1.0, 5.0))

    def test_compare_identical_and_shifted(self):
        same = call(self.server, "compare_curves", file_id="f1", curve="A", other_curve="A")["result"]
        self.assertAlmostEqual(same["mean_abs_difference"], 0.0)
        self.assertAlmostEqual(same["correlation"], 1.0)

        shifted_rows = [(round(100 + i * 0.1, 1), i + 3, 0) for i in range(20)]
        server = server_with(f1=self.las, f2=make_las(shifted_rows))
        result = call(server, "compare_curves", file_id="f1", curve="A", other_file_id="f2", other_curve="A")["result"]
        self.assertAlmostEqual(result["mean_difference"], 2.0)

    def test_errors_are_returned_to_model_not_raised(self):
        unknown = call(self.server, "curve_stats", file_id="nope", curves=["A"])
        self.assertFalse(unknown["ok"])
        self.assertIn("не открыт", unknown["error"])

        bad_curve = call(self.server, "curve_stats", file_id="f1", curves=["ZZ"])
        self.assertIn("Есть: A, B", bad_curve["error"])

        missing_value = call(self.server, "find_intervals", file_id="f1", curve="A", condition="gt")
        self.assertFalse(missing_value["ok"])

        extra = call(self.server, "list_files", unexpected=1)
        self.assertFalse(extra["ok"])

        empty_range = call(self.server, "curve_stats", file_id="f1", curves=["A"], top=500, bottom=600)
        self.assertIn("нет данных", empty_range["error"])

        unknown_tool = call(self.server, "drop_database")
        self.assertIn("нет", unknown_tool["error"])

    def test_case_insensitive_curve_lookup(self):
        result = call(self.server, "curve_stats", file_id="f1", curves=["a"])
        self.assertTrue(result["ok"])


DEFAULT_SAMPLES = Path.home() / "Downloads" / "3201_32_Кийское_ИК прибор 004-2025" / "DeviceBur1" / "Индукционник" / "RAM"
SAMPLES = Path(os.environ.get("LASVIEWER_SAMPLES_DIR", DEFAULT_SAMPLES))


@unittest.skipUnless((SAMPLES / "3201 ИК.las").exists(), f"нет образцов в {SAMPLES}")
class RealSampleAgentTests(unittest.TestCase):
    def test_saturation_plateau_share_on_well_3201(self):
        server = server_with(w=read_las(SAMPLES / "3201 ИК.las"))
        # «Полка» у предела преобразования: значения выше 6900 Ом·м.
        high = call(server, "find_intervals", file_id="w", curve="RO4_400_S", condition="gt", value=6900)["result"]
        self.assertGreater(high["share_percent"], 50)
        self.assertLess(high["share_percent"], 65)

        plateau = call(server, "find_intervals", file_id="w", curve="RO4_400_S", condition="plateau", min_thickness=1)["result"]
        self.assertGreater(plateau["found"], 0)
        self.assertTrue(all(i["mean"] is None or i["mean"] > 1000 or i["thickness"] >= 1 for i in plateau["intervals"]))

        stats = call(server, "curve_stats", file_id="w", curves=["RO4_400_S", "RO1_400_S"], top=2100, bottom=2400)["result"]
        self.assertTrue(all(math.isfinite(c["mean"]) for c in stats["curves"]))


if __name__ == "__main__":
    unittest.main()
