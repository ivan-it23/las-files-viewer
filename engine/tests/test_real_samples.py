"""Проверка на реальных файлах скважины 3201 (Кийское, ИК прибор 004-2025).

Файлы — данные заказчика, в репозиторий они не входят. Каталог берётся из
LASVIEWER_SAMPLES_DIR или из места, куда их сохранили при разработке; если
файлов нет, тесты пропускаются.
"""

import math
import os
import sys
import unittest
from pathlib import Path

ENGINE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE_ROOT))

from lasviewer_engine.las import detect_encoding, read_las  # noqa: E402
from lasviewer_engine.las.parser import index_summary  # noqa: E402

DEFAULT_SAMPLES = Path.home() / "Downloads" / "3201_32_Кийское_ИК прибор 004-2025" / "DeviceBur1" / "Индукционник" / "RAM"
SAMPLES = Path(os.environ.get("LASVIEWER_SAMPLES_DIR", DEFAULT_SAMPLES))


@unittest.skipUnless((SAMPLES / "3201 ИК.las").exists(), f"нет образцов в {SAMPLES}")
class Well3201Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.merged = read_las(SAMPLES / "3201 ИК.las")

    def test_header(self):
        las = self.merged
        self.assertEqual(las.encoding, "cp1251")
        self.assertEqual(las.version, "2.0")
        self.assertEqual(las.null_value, -32768.0)
        self.assertEqual(las.row_count, 10267)
        self.assertEqual(len(las.curves), 17)
        self.assertEqual(las.curves[5].unit, "Омм")

    def test_index(self):
        summary = index_summary(self.merged)
        self.assertEqual((summary["start"], summary["stop"], summary["step"]), (2006.04, 3032.64, 0.1))
        self.assertEqual(summary["direction"], "increasing")

    def test_known_values(self):
        curves = {curve.mnemonic: curve for curve in self.merged.curves}
        self.assertEqual(curves["RO1_400_S"].values[0], 31.09759)
        self.assertEqual(curves["RO4_2000_S"].values[0], 1550.70264)
        self.assertTrue(math.isnan(curves["RO1_400_S"].values[-1]))
        self.assertEqual(curves["RO1_400_S"].stats()["nulls"], 1)

    def test_only_expected_remarks(self):
        codes = sorted(issue.code for issue in self.merged.issues)
        self.assertEqual(codes, ["empty_well_fields", "encoding_detected", "nonstandard_section_name"])

    def test_merged_file_is_shorter_than_parts(self):
        part = read_las(SAMPLES / "400.las")
        self.assertEqual(index_summary(part)["stop"], 3034.34)
        self.assertLess(index_summary(self.merged)["stop"], index_summary(part)["stop"])

    def test_station_config_is_cp866(self):
        data = (SAMPLES / "config" / "3201.TXT").read_bytes()
        self.assertEqual(detect_encoding(data).codec, "cp866")


if __name__ == "__main__":
    unittest.main()
