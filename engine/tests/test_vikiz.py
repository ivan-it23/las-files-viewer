"""Расчёты ВИКИЗ по методичке: качество записи, кривая зондирования, пластовая разбивка."""

import io
import json
import sys
import unittest
from pathlib import Path

ENGINE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE_ROOT))

from lasviewer_engine import agent_tools, vikiz  # noqa: E402
from lasviewer_engine.las.parser import parse_las_bytes  # noqa: E402
from lasviewer_engine.las_methods import Workspace  # noqa: E402
from lasviewer_engine.rpc import Server  # noqa: E402

LENGTHS = ("0.5", "0.7", "1.0", "1.4", "2.0")
CODES = ("05", "07", "10", "14", "20")

# Разрез 100–120 м по типичным кривым методички: ρк зондов 0,5…2,0 м.
SECTION = [
    (100.0, 105.0, (4.0, 4.0, 4.0, 4.0, 4.0)),        # глина — ровная (с. 21)
    (105.0, 110.0, (25.0, 16.0, 7.5, 5.0, 3.2)),      # водонасыщенный, табл. 7.1 — убывает
    (110.0, 115.0, (20.0, 12.0, 8.0, 7.8, 11.0)),     # нефть с окаймляющей зоной — минимум
    (115.0, 120.0, (18.0, 22.0, 30.0, 45.0, 50.0)),   # газ, понижающее — возрастает
]


def phase_of(rho):
    """Монотонная Δφ(ρ), похожая на рис. 3.1: ≈ 50° при 1 Ом·м, ≈ 11° при 10 Ом·м."""
    return 50.0 / rho ** 0.66


def make_vikiz(step=0.1, broken_rows=5, scale_long=1.0, by_description=True):
    rows = []
    count = int(round(20 / step))
    for k in range(count):
        depth = round(100 + k * step, 3)
        rho = next(values for top, bottom, values in SECTION if top <= depth < bottom)
        phases = [phase_of(r) for r in rho]
        rhos = list(rho)
        if k < broken_rows:
            # Отрицательная Δφ зонда 0,7 м и «полка» 1000 Ом·м — как в файле скважины 51.
            phases[1] = -3.0
            rhos[1] = 1000.0
        phases[4] *= scale_long
        rows.append((depth, *phases, *rhos))

    header = [
        "~Version information", "VERS. 2.0 :", "WRAP. NO :",
        "~Well information",
        f"STRT.M {rows[0][0]} :", f"STOP.M {rows[-1][0]} :", f"STEP.M {step} :", "NULL. -32768 :",
        "~Curve information", "DEPT.M :",
    ]
    for code, length in zip(CODES, LENGTHS):
        if by_description:
            header.append(f"IK{code}. : кривая разности фаз по методу ВИКИЗ зонд {length} м, град")
        else:
            header.append(f"IK{code}. :")
    for code, length in zip(CODES, LENGTHS):
        if by_description:
            header.append(f"RO{code}.Омм : кривая сопротивления по методу ВИКИЗ зонд {length} м, Ом*м")
        else:
            header.append(f"RO{code}.Омм :")
    header.append("~ASCII")
    body = [" ".join(f"{value:.4f}" for value in row) for row in rows]
    return parse_las_bytes("\n".join(header + body).encode("utf-8"), "vikiz.las")


def make_emk():
    rows = [(round(200 + k * 0.1, 1), 10.0, 8.0, 6.0, 5.0) for k in range(50)]
    header = [
        "~Version information", "VERS. 2.0 :", "WRAP. NO :",
        "~Well information", "STRT.M 200 :", "STOP.M 204.9 :", "STEP.M 0.1 :", "NULL. -32768 :",
        "~Curve information", "DEPT.M :",
    ] + [f"RO{i}_400_S. : УЭС по методу ЭМК зонд {i} на частоте 400 кГц, Омм" for i in range(1, 5)] + ["~ASCII"]
    body = [" ".join(str(v) for v in row) for row in rows]
    return parse_las_bytes("\n".join(header + body).encode("utf-8"), "emk.las")


class FindZondsTests(unittest.TestCase):
    def test_by_description_and_by_mnemonic(self):
        for by_description in (True, False):
            zset = vikiz.find_zonds(make_vikiz(by_description=by_description))
            self.assertFalse(zset.custom)
            self.assertEqual([z.phase.mnemonic for z in zset.zonds], [f"IK{c}" for c in CODES])
            self.assertEqual([z.resistivity.mnemonic for z in zset.zonds], [f"RO{c}" for c in CODES])

    def test_other_instrument_needs_explicit_curves(self):
        emk = make_emk()
        with self.assertRaisesRegex(vikiz.VikizError, "передайте кривые явно"):
            vikiz.find_zonds(emk)
        result = vikiz.sounding(emk, resistivity_curves=["RO1_400_S", "RO2_400_S", "RO3_400_S", "RO4_400_S"])
        self.assertEqual(result["shape"], "убывает")
        self.assertIn("warning", result)


class QualityTests(unittest.TestCase):
    def test_negative_phase_and_resistivity_limit(self):
        result = vikiz.quality(make_vikiz())
        zond07 = result["zonds"][1]
        self.assertEqual(zond07["phase_negative"], 5)
        self.assertEqual(zond07["invalid_intervals"][0]["top"], 100.0)
        self.assertEqual(zond07["resistivity_limit"], {"value": 1000.0, "count": 5, "at_phase_below_0_2": 5})
        text = " ".join(result["findings"])
        self.assertIn("с. 62", text)
        self.assertIn("предел пересчёта", text)
        self.assertEqual(result["zonds"][0]["phase_negative"], 0)

    def test_clean_record_and_coarse_step(self):
        clean = vikiz.quality(make_vikiz(broken_rows=0))
        self.assertIn("в допустимом диапазоне", clean["findings"][0])
        coarse = vikiz.quality(make_vikiz(step=0.25, broken_rows=0))
        self.assertIn("больше допустимых 0.2 м", coarse["findings"][0])

    def test_repeat_measurement_limits(self):
        main = make_vikiz(broken_rows=0)
        same = vikiz.quality(main, repeat=make_vikiz(broken_rows=0))
        self.assertIn("в пределах 3 %", " ".join(same["findings"]))
        # Длинный зонд повтора на 6 % выше: допуск 3 % (с. 59) превышен только у него.
        shifted = vikiz.quality(main, repeat=make_vikiz(broken_rows=0, scale_long=1.06))
        over = [f for f in shifted["findings"] if "Повторный замер, зонд" in f]
        self.assertEqual(len(over), 1)
        self.assertIn("2,0 м", over[0])


class SoundingTests(unittest.TestCase):
    def setUp(self):
        self.las = make_vikiz()

    def test_typical_curves_of_the_methodology(self):
        cases = [((101, 104), "ровная"), ((106, 109), "убывает"), ((111, 114), "минимум"), ((116, 119), "возрастает")]
        for (top, bottom), shape in cases:
            result = vikiz.sounding(self.las, top, bottom)
            self.assertEqual(result["shape"], shape, (top, bottom))
            self.assertTrue(result["pages"])
        minimum = vikiz.sounding(self.las, 111, 114)
        self.assertIn("1,4 м", minimum["meaning"])

    def test_single_depth_and_thin_interval(self):
        self.assertEqual(vikiz.sounding(self.las, depth=107.0)["shape"], "убывает")
        thin = vikiz.sounding(self.las, 106, 107)
        self.assertTrue(any("1,5 м" in note for note in thin["notes"]))

    def test_invalid_phase_points_are_excluded(self):
        # На 100.0–100.4 у зонда 0,7 м Δφ < 0 и ρк = 1000: в медиану не входят.
        result = vikiz.sounding(self.las, 100.0, 100.9)
        self.assertAlmostEqual(result["zonds"][1]["rho_k"], 4.0)
        self.assertEqual(result["zonds"][1]["points"], 5)

    def test_small_steps_with_significant_total_growth(self):
        # Каждый соседний шаг в пределах погрешности, но в сумме ρк растёт — это не «убывает».
        shape = vikiz.classify([1.0755, 1.11, 1.1665, 1.1975, 1.2075], list(vikiz.PHASE_ERRORS))
        self.assertEqual(shape["shape"], "возрастает")


class LayersTests(unittest.TestCase):
    def test_boundaries_and_shapes(self):
        result = vikiz.layers(make_vikiz(), 100, 120)
        self.assertIn("с. 51", result["method"])
        boundaries = [layer["top"] for layer in result["layers"][1:]]
        for expected in (105.0, 110.0, 115.0):
            self.assertTrue(any(abs(b - expected) <= 0.21 for b in boundaries), (expected, boundaries))
        shapes = [layer["shape"] for layer in result["layers"]]
        self.assertEqual(shapes, ["ровная", "убывает", "минимум", "возрастает"])
        self.assertEqual(result["summary_by_shape"]["убывает"]["layers"], 1)

    def test_minimum_thickness_is_at_least_two_steps(self):
        result = vikiz.layers(make_vikiz(), 100, 120, min_thickness=0.05)
        self.assertEqual(result["min_thickness"], 0.2)


class VikizToolTests(unittest.TestCase):
    def test_through_agent_call(self):
        server = Server(io.BytesIO(), io.BytesIO())
        workspace = Workspace()
        workspace.files["v"] = make_vikiz()
        workspace.files["r"] = make_vikiz(broken_rows=0)
        agent_tools.register(server, workspace)

        def call(name, **arguments):
            response = server.methods["agent.call"](name, arguments)
            json.dumps(response, allow_nan=False)
            return response

        quality = call("vikiz_quality", file_id="v", repeat_file_id="r")
        self.assertTrue(quality["ok"])
        # Модели — без ссылок на страницы методички: сноски в ответе пользователю не нужны.
        self.assertNotIn("с. 62", json.dumps(quality, ensure_ascii=False))
        self.assertNotIn("source", quality["result"])
        self.assertEqual(call("vikiz_sounding", file_id="v", top=106, bottom=109)["result"]["shape"], "убывает")
        self.assertTrue(call("vikiz_layers", file_id="v", top=100, bottom=120)["ok"])
        error = call("vikiz_sounding", file_id="v", depth=107, top=100)
        self.assertFalse(error["ok"])
        files = call("list_files")["result"]["files"]
        self.assertTrue(any("ВИКИЗ" in hint for hint in files[0]["methods"]))
        context = server.methods["agent.knowledge_context"]("Что здесь?")
        self.assertIn("vikiz/krivye-zondirovaniya", context["cards"])


if __name__ == "__main__":
    unittest.main()
