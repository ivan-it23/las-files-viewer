import base64
import json
import math
import struct
import sys
import tempfile
import unittest
from pathlib import Path

ENGINE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE_ROOT))

from lasviewer_engine import las_methods  # noqa: E402
from lasviewer_engine.las import LasFormatError, detect_encoding, parse_las_bytes  # noqa: E402
from lasviewer_engine.las.parser import index_summary  # noqa: E402
from lasviewer_engine.rpc import Server  # noqa: E402

LAS20_CP1251 = """~Version information
VERS.                               2.0:   CWLS log ASCII Standart-VERSION 2.0
WRAP.                                NO:
~Well information
# MNEM.UNIT         DATA TYPE INFORMATION
  STRT.M                       2006.040:
  STOP.M                       2006.340:
  STEP.M                          0.100:
  NULL.                      -32768.000:
  COMP.                                : COMPANY
  WELL.                            3201: WELL
  FLD .                                : FIELD
  CTRY.                          Россия: COUNTRY
  DATE.                        13:45:00: LOG TIME
~Curve information
  DEPT.M                               :
  F1_400_Sk.grad                       :
  RO1_400_S.Омм                        :симметризированное кажущееся УЭС на частоте 400 кГц - зонд 0.6, Омм
~Parametr information
~Other information
# ------------------- REMARKS AREA -------------------------
Комментарий оператора.
~ASCII Log Data
 2006.04    0.59703    31.09759
 2006.14    0.56534    32.97252
 2006.24    0.54806  -32768.00
 2006.34    0.53000    34.50000
""".replace("\n", "\r\n").encode("cp1251")


def build(text: str, encoding: str = "ascii") -> bytes:
    return text.encode(encoding)


class EncodingTests(unittest.TestCase):
    def test_cp1251_las(self):
        guess = detect_encoding(LAS20_CP1251)
        self.assertEqual(guess.codec, "cp1251")
        self.assertEqual(guess.method, "heuristic")

    def test_cp866_station_file(self):
        data = '3201\r\nНГКМ им.Н.В.Мышевского\r\nООО "ИНК-Синергия"\r\nАМК Горизонт\r\n'.encode("cp866")
        self.assertEqual(detect_encoding(data).codec, "cp866")

    def test_utf8_and_bom_and_ascii(self):
        self.assertEqual(detect_encoding("Ом·м".encode("utf-8")).codec, "utf-8")
        self.assertEqual(detect_encoding(b"\xef\xbb\xbf~V").codec, "utf-8-sig")
        self.assertEqual(detect_encoding(b"~VERSION").codec, "ascii")


class Las20Tests(unittest.TestCase):
    def setUp(self):
        self.las = parse_las_bytes(LAS20_CP1251, "sample.las")

    def test_header(self):
        self.assertEqual(self.las.version, "2.0")
        self.assertFalse(self.las.wrap)
        self.assertEqual(self.las.null_value, -32768.0)
        self.assertEqual(self.las.well_value("CTRY"), "Россия")
        self.assertEqual(self.las.well_value("WELL"), "3201")
        self.assertEqual(self.las.well_value("FLD"), "")

    def test_last_colon_keeps_time_in_value(self):
        self.assertEqual(self.las.well_value("DATE"), "13:45:00")

    def test_curves_and_units(self):
        mnemonics = [curve.mnemonic for curve in self.las.curves]
        self.assertEqual(mnemonics, ["DEPT", "F1_400_Sk", "RO1_400_S"])
        self.assertEqual(self.las.curves[2].unit, "Омм")
        self.assertTrue(self.las.curves[2].description.startswith("симметризированное"))

    def test_values_and_null(self):
        ro = self.las.curves[2].values
        self.assertEqual(self.las.row_count, 4)
        self.assertEqual(ro[0], 31.09759)
        self.assertTrue(math.isnan(ro[2]))
        self.assertEqual(self.las.curves[2].stats()["nulls"], 1)

    def test_nonstandard_parameter_section_is_recognised_and_reported(self):
        kinds = [section.kind for section in self.las.sections]
        self.assertEqual(kinds, ["version", "well", "curve", "parameter", "other", "data"])
        issue = next(i for i in self.las.issues if i.code == "nonstandard_section_name")
        self.assertIn("Parametr", issue.message)
        self.assertEqual(issue.line, 19)

    def test_other_section_text_is_kept(self):
        other = self.las.section("other")
        self.assertIn("Комментарий оператора.", other.text)

    def test_empty_well_fields_reported(self):
        issue = next(i for i in self.las.issues if i.code == "empty_well_fields")
        self.assertIn("COMP", issue.message)
        self.assertNotIn("WELL,", issue.message)

    def test_index_summary(self):
        summary = index_summary(self.las)
        self.assertEqual(summary["direction"], "increasing")
        self.assertEqual(summary["step"], 0.1)
        self.assertEqual(summary["start"], 2006.04)
        self.assertEqual(summary["stop"], 2006.34)
        codes = {issue.code for issue in self.las.issues}
        self.assertNotIn("stop_mismatch", codes)
        self.assertNotIn("step_mismatch", codes)


class DeviationTests(unittest.TestCase):
    def test_las12_well_values_come_from_description(self):
        las = parse_las_bytes(build(
            "~V\nVERS. 1.2: CWLS LOG ASCII STANDARD -VERSION 1.2\nWRAP. NO:\n"
            "~W\nSTRT.M 1:\nSTOP.M 2:\nSTEP.M 1:\nNULL. -999.25:\n"
            "COMP. COMPANY: ANY OIL COMPANY INC.\nWELL. WELL: ANY ET AL 12-34\n"
            "~C\nDEPT.M :\nGR.GAPI :\n~A\n1 10\n2 -999.25\n"))
        self.assertEqual(las.version, "1.2")
        self.assertEqual(las.well_value("COMP"), "ANY OIL COMPANY INC.")
        self.assertEqual(las.well_value("WELL"), "ANY ET AL 12-34")
        self.assertTrue(math.isnan(las.curves[1].values[1]))

    def test_wrapped_data(self):
        las = parse_las_bytes(build(
            "~V\nVERS. 2.0:\nWRAP. YES:\n~W\nNULL. -999.25:\n"
            "~C\nDEPT.M :\nA. :\nB. :\nC. :\n~A\n"
            "100.0\n1 2\n3\n100.5\n4 5 6\n"))
        self.assertTrue(las.wrap)
        self.assertEqual(list(las.curves[0].values), [100.0, 100.5])
        self.assertEqual(list(las.curves[3].values), [3.0, 6.0])

    def test_las30_comma_delimited(self):
        las = parse_las_bytes(build(
            "~Version\nVERS. 3.0:\nWRAP. NO:\nDLM. COMMA:\n~Well\nNULL. -999.25:\n"
            "~Log_Definition\nDEPT.M :\nGR.GAPI :\n~Log_Data\n1.0, 55.5\n2.0, -999.25\n"))
        self.assertEqual(las.version, "3.0")
        self.assertEqual(las.delimiter, "comma")
        self.assertEqual(las.curves[1].values[0], 55.5)
        self.assertTrue(math.isnan(las.curves[1].values[1]))

    def test_duplicate_mnemonics_are_renamed(self):
        las = parse_las_bytes(build("~V\nVERS. 2.0:\n~C\nDEPT.M :\nGR. :\nGR. :\n~A\n1 2 3\n"))
        self.assertEqual([c.mnemonic for c in las.curves], ["DEPT", "GR", "GR:2"])
        self.assertEqual(las.curves[2].original_mnemonic, "GR")
        self.assertIn("duplicate_mnemonic", {i.code for i in las.issues})

    def test_rows_with_wrong_column_count_are_skipped_with_line_numbers(self):
        las = parse_las_bytes(build("~V\nVERS. 2.0:\n~C\nDEPT.M :\nA. :\n~A\n1 2\n2\n3 4 5\n4 8\n"))
        self.assertEqual(list(las.curves[0].values), [1.0, 4.0])
        issue = next(i for i in las.issues if i.code == "column_count_mismatch")
        self.assertIn("2 строк", issue.message)
        self.assertEqual(issue.line, 8)

    def test_decimal_comma_and_text_values(self):
        las = parse_las_bytes(build("~V\nVERS. 2.0:\n~C\nDEPT.M :\nA. :\n~A\n1 516,3\n2 abc\n"))
        self.assertEqual(las.curves[1].values[0], 516.3)
        self.assertTrue(math.isnan(las.curves[1].values[1]))
        codes = {i.code for i in las.issues}
        self.assertIn("decimal_comma", codes)
        self.assertIn("non_numeric_values", codes)

    def test_header_mismatch_and_decreasing_index(self):
        las = parse_las_bytes(build(
            "~V\nVERS. 2.0:\n~W\nSTRT.M 10:\nSTOP.M 5:\nSTEP.M -1:\n~C\nDEPT.M :\n~A\n10\n9\n8\n"))
        self.assertEqual(index_summary(las)["direction"], "decreasing")
        issue = next(i for i in las.issues if i.code == "stop_mismatch")
        self.assertIn("STOP в заголовке 5", issue.message)

    def test_non_finite_header_values_are_not_set(self):
        las = parse_las_bytes(build("~V\nVERS. 2.0:\n~W\nSTRT.M nan:\nNULL. inf:\n~C\nDEPT.M :\n~A\n1\n"))
        self.assertIsNone(las.null_value)
        self.assertIsNone(index_summary(las)["header_start"])
        json.dumps(index_summary(las), allow_nan=False)

    def test_not_las_raises(self):
        with self.assertRaises(LasFormatError):
            parse_las_bytes(build("просто текст\n1 2 3\n", "utf-8"))

    def test_duplicate_section_ignored(self):
        las = parse_las_bytes(build(
            "~V\nVERS. 2.0:\n~C\nDEPT.M :\n~C\nX. :\n~A\n1\n"))
        self.assertEqual([c.mnemonic for c in las.curves], ["DEPT"])
        self.assertIn("duplicate_section", {i.code for i in las.issues})


class OpenMethodTests(unittest.TestCase):
    def setUp(self):
        self.server = Server(None, None)  # type: ignore[arg-type]
        self.workspace = las_methods.register(self.server)
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name) / "Скважина 3201.las"
        self.path.write_bytes(LAS20_CP1251)

    def tearDown(self):
        self.directory.cleanup()

    def call(self, method, params):
        line = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
        return self.server.handle_line(line)

    def test_open_returns_columns_bit_exact(self):
        result = self.call("las.open", {"path": str(self.path)})["result"]
        self.assertEqual(result["name"], "Скважина 3201.las")
        self.assertEqual(result["data"]["encoding"], "f64le-base64")
        raw = base64.b64decode(result["data"]["columns"][2])
        values = struct.unpack(f"<{len(raw) // 8}d", raw)
        self.assertEqual(values[0], 31.09759)
        self.assertTrue(math.isnan(values[2]))
        self.assertEqual(result["curves"][2]["stats"]["nulls"], 1)
        self.assertIn(result["file_id"], self.workspace.files)

    def test_close(self):
        file_id = self.call("las.open", {"path": str(self.path)})["result"]["file_id"]
        self.assertIsNone(self.call("las.close", {"file_id": file_id})["result"])
        self.assertEqual(self.call("las.close", {"file_id": file_id})["error"]["code"], las_methods.UNKNOWN_FILE_ID)

    def test_missing_file(self):
        response = self.call("las.open", {"path": str(self.path) + ".нет"})
        self.assertEqual(response["error"]["code"], las_methods.FILE_NOT_FOUND)

    def test_not_las(self):
        other = Path(self.directory.name) / "notes.txt"
        other.write_text("не LAS", encoding="utf-8")
        response = self.call("las.open", {"path": str(other)})
        self.assertEqual(response["error"]["code"], las_methods.NOT_LAS)


if __name__ == "__main__":
    unittest.main()
