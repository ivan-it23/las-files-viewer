"""База знаний агента: формат карточек, поиск, выдача карточек модели."""

import io
import json
import sys
import unittest
from pathlib import Path

ENGINE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE_ROOT))

from lasviewer_engine import agent_tools, knowledge_base as kb  # noqa: E402
from lasviewer_engine.las_methods import Workspace  # noqa: E402
from lasviewer_engine.rpc import Server  # noqa: E402


class CardFormatTests(unittest.TestCase):
    def test_all_cards_load_with_valid_headers(self):
        cards = kb.load_cards()
        ids = [card.id for card in cards]
        self.assertGreaterEqual(len(cards), 10)
        self.assertEqual(len(ids), len(set(ids)))
        self.assertIn("glossary", ids)
        self.assertIn("vikiz/kachestvo", ids)
        for card in cards:
            self.assertIn(card.status, kb.STATUSES, card.id)
            self.assertTrue(card.summary and card.title and card.source, card.id)
            self.assertGreater(len(card.body), 200, card.id)
            if card.source.startswith("Методичка ВИКИЗ"):
                self.assertTrue(card.pages, f"{card.id}: у карточки из методички должны быть страницы")

    def test_index_lists_every_card(self):
        index = kb.index_text()
        for card in kb.load_cards():
            self.assertIn(card.id, index)
        # Оглавление уходит в описание инструмента при каждом запросе — держим его компактным.
        self.assertLess(len(index), 6000)

    def test_bad_cards_are_rejected_with_reason(self):
        with self.assertRaisesRegex(kb.KnowledgeError, "шапки"):
            kb.parse_card("x", "без шапки")
        with self.assertRaisesRegex(kb.KnowledgeError, "summary"):
            kb.parse_card("x", "---\ntitle: T\nsource: S\npages: 1\nmethods: М\nmnemonics:\nstatus: эталон\n---\nтекст")
        with self.assertRaisesRegex(kb.KnowledgeError, "status"):
            kb.parse_card("x", "---\ntitle: T\nsource: S\npages: 1\nmethods: М\nmnemonics:\nstatus: догадка\nsummary: s\n---\nт")


class SearchTests(unittest.TestCase):
    def top(self, query, **options):
        return [item["id"] for item in kb.search(query, **options)["results"]]

    def test_word_forms_find_the_right_card(self):
        self.assertEqual(self.top("повторного замера допуски")[0], "vikiz/kachestvo")
        self.assertEqual(self.top("отрицательная разность фаз")[0], "vikiz/kachestvo")
        self.assertIn("vikiz/proniknovenie", self.top("окаймляющую зону", limit=3))
        self.assertIn("vikiz/krivye-zondirovaniya", self.top("кривые зондирования газонасыщенный", limit=3))
        self.assertIn("vikiz/razreshenie", self.top("границы пластов", limit=3))

    def test_mnemonic_and_method_filter(self):
        self.assertIn("vikiz/zondy", self.top("IK07"))
        emk = self.top("зонды", method="ЭМК")
        self.assertIn("emk/primenimost", emk)
        self.assertTrue(all(card in ("emk/primenimost", "glossary") for card in emk), emk)
        with self.assertRaisesRegex(kb.KnowledgeError, "Есть методы"):
            kb.search("зонды", method="ДЛЯ ТЕСТА")

    def test_results_carry_snippet_without_references(self):
        item = kb.search("стандарт-сигнал")["results"][0]
        self.assertEqual(item["id"], "vikiz/kachestvo")
        self.assertNotIn("pages", item)
        self.assertIn("25", item["snippet"])
        self.assertNotIn("с. 5", item["snippet"])

    def test_get_card_without_page_references(self):
        card = kb.get("vikiz/kachestvo")
        self.assertEqual(card["status"], "эталон")
        self.assertIn("0,2", card["text"])
        self.assertNotRegex(card["text"], r"\(с\.\s")
        self.assertNotIn("cite_as", card)
        with self.assertRaisesRegex(kb.KnowledgeError, "vikiz/zondy"):
            kb.get("нет/такой")

    def test_strip_references(self):
        self.assertEqual(kb.strip_references("Δφ не больше 90° (с. 62)."), "Δφ не больше 90°.")
        self.assertEqual(kb.strip_references("Зонды (табл. 4.1, с. 32): длины"), "Зонды: длины")
        self.assertEqual(kb.strip_references("пример (вода, с. 66) и (рис. 2.8)"), "пример (вода) и")
        self.assertEqual(kb.strip_references("вывод (методичка ВИКИЗ, с. 62)"), "вывод")
        self.assertEqual(kb.strip_references("скобки (без ссылки) остаются"), "скобки (без ссылки) остаются")


class ContextTests(unittest.TestCase):
    def test_glossary_and_cards_are_always_attached(self):
        for question, methods in (("Опиши открытые файлы", []), ("Что это?", ["ВИКИЗ"]), ("", None)):
            context = kb.context_for(question, methods)
            self.assertEqual(context["cards"][0], "glossary")
            self.assertGreaterEqual(len(context["cards"]), 2, question)
            self.assertLessEqual(len(context["text"]), kb.CONTEXT_CHARS + 10)
            self.assertNotRegex(context["text"], r"\(с\.\s")

    def test_question_match_and_method_core_card(self):
        context = kb.context_for("допуски повторного замера", ["ВИКИЗ"])
        self.assertEqual(context["cards"][:2], ["glossary", "vikiz/kachestvo"])
        self.assertIn("vikiz/krivye-zondirovaniya", context["cards"])
        emk = kb.context_for("зашкал УЭС", ["ЭМК"])
        self.assertIn("emk/primenimost", emk["cards"])
        self.assertIn("не подтверждено", emk["text"])

    def test_bad_queries(self):
        for query in ("", "   ", None, 5):
            with self.assertRaises(kb.KnowledgeError):
                kb.search(query)


class KnowledgeToolTests(unittest.TestCase):
    def setUp(self):
        self.server = Server(io.BytesIO(), io.BytesIO())
        agent_tools.register(self.server, Workspace())

    def call(self, name, **arguments):
        response = self.server.methods["agent.call"](name, arguments)
        json.dumps(response, allow_nan=False)
        return response

    def test_tools_answer_and_report_errors_to_model(self):
        found = self.call("search_knowledge", query="окаймляющая зона", method="ВИКИЗ")
        self.assertTrue(found["ok"])
        self.assertTrue(found["result"]["results"])
        card = self.call("get_knowledge", id=found["result"]["results"][0]["id"])
        self.assertTrue(card["ok"])
        missing = self.call("get_knowledge", id="нет")
        self.assertFalse(missing["ok"])
        self.assertIn("Есть", missing["error"])

    def test_knowledge_context_method(self):
        context = self.server.methods["agent.knowledge_context"]("Где зашкал?")
        self.assertEqual(context["cards"][0], "glossary")
        self.assertTrue(context["text"])

    def test_index_is_in_tool_description(self):
        tools = {tool["function"]["name"]: tool for tool in self.server.methods["agent.tools"]()}
        description = tools["search_knowledge"]["function"]["description"]
        self.assertIn("vikiz/kachestvo", description)
        self.assertIn("[черновик]", description)


if __name__ == "__main__":
    unittest.main()
