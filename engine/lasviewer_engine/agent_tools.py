"""Инструменты ИИ-агента: чтение открытых файлов без их изменения.

Модель не получает файл целиком. Она вызывает эти функции, а приложение
передаёт ей компактный результат: сведения о файле, статистику, найденные
интервалы, прореженные значения. Всё считается здесь, на рабочей станции.

Ошибка в аргументах — обычный исход для модели: она получает понятное
описание и может повторить вызов. Поэтому ``agent.call`` не бросает RpcError
на неверные аргументы, а возвращает ``{"ok": false, "error": ...}``.
"""

from __future__ import annotations

import bisect
import math
import sys
from pathlib import Path
from typing import Any, Callable

from . import knowledge_base, vikiz
from .las import LasFile
from .las.parser import index_summary
from .las_methods import Workspace
from .rpc import Server

# Пределы объёма ответа: результат уходит в контекст модели, и каждая строка
# стоит денег и места в окне контекста.
MAX_SAMPLE_POINTS = 500
DEFAULT_SAMPLE_POINTS = 150
MAX_INTERVALS = 200
MAX_CURVES_PER_CALL = 12

# Скрипт запускает приложение после подтверждения пользователем
# (agent.run_script в scripts.py), а не agent.call.
RUN_PYTHON = "run_python"


class ToolError(Exception):
    """Ошибка в аргументах или данных — описание уходит модели."""


# ----------------------------------------------------------------------
# Описания инструментов (формат OpenAI tools)
# ----------------------------------------------------------------------

_FILE_ID = {"type": "string", "description": "Идентификатор открытого файла из списка открытых файлов."}
_TOP = {"type": "number", "description": "Кровля интервала в единицах глубины файла. Без неё — от начала записи."}
_BOTTOM = {"type": "number", "description": "Подошва интервала в единицах глубины файла. Без неё — до конца записи."}
_CURVES = {
    "type": "array",
    "items": {"type": "string"},
    "description": "Мнемоники кривых, как в списке кривых файла.",
}
_PHASE_CURVES = {
    "type": "array",
    "items": {"type": "string"},
    "description": (
        "Только для приборов не ВИКИЗ: кривые разности фаз от короткого зонда к длинному. Для ВИКИЗ кривые "
        "находятся сами по описаниям и мнемоникам IK05…IK20."
    ),
}
_RESISTIVITY_CURVES = {
    "type": "array",
    "items": {"type": "string"},
    "description": (
        "Только для приборов не ВИКИЗ (например, ЭМК одной частоты): кривые ρк от короткого зонда к длинному. "
        "Для ВИКИЗ находятся сами (RO05…RO20)."
    ),
}


def _knowledge_index() -> str:
    try:
        return knowledge_base.index_text()
    except (OSError, knowledge_base.KnowledgeError) as exc:
        # Испорченная карточка не должна останавливать движок: агент работает и без базы знаний.
        print(f"База знаний не загружена: {exc}", file=sys.stderr)
        return "(база знаний недоступна)"

TOOLS: list[dict[str, Any]] = [
    {
        "type": "function",
        "function": {
            "name": "list_files",
            "description": "Список открытых в программе LAS-файлов: идентификатор, имя, интервал глубин, шаг, кривые с единицами.",
            # Параметр нужен не только по смыслу: схема с пустым "properties"
            # ломалась по пути к Claude (Amazon Bedrock отвечал «Некорректный
            # запрос»), поэтому пустых объектов в описаниях инструментов нет.
            "parameters": {
                "type": "object",
                "properties": {
                    "include_curves": {
                        "type": "boolean",
                        "description": "Включить список кривых каждого файла. По умолчанию — да.",
                    },
                },
                "additionalProperties": False,
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "get_file_info",
            "description": (
                "Подробные сведения о файле: заполненные поля заголовка (скважина, месторождение, заказчик, дата), "
                "кривые с единицами, описаниями и общей статистикой, кодировка, NULL и замечания разбора."
            ),
            "parameters": {
                "type": "object",
                "properties": {"file_id": _FILE_ID},
                "required": ["file_id"],
                "additionalProperties": False,
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "curve_stats",
            "description": (
                "Статистика кривых на интервале: число отсчётов и пропусков, минимум и максимум с глубинами, "
                "среднее, стандартное отклонение, медиана и процентили 5/95."
            ),
            "parameters": {
                "type": "object",
                "properties": {"file_id": _FILE_ID, "curves": _CURVES, "top": _TOP, "bottom": _BOTTOM},
                "required": ["file_id", "curves"],
                "additionalProperties": False,
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "find_intervals",
            "description": (
                "Интервалы глубин, где кривая удовлетворяет условию. Условия: gt, ge, lt, le (сравнение с value), "
                "between и outside (диапазон value…value2), null (пропуски), plateau (кривая почти не меняется — "
                "«полки», насыщение у предела преобразования). Соседние интервалы с промежутком меньше merge_gap "
                "сливаются, интервалы тоньше min_thickness отбрасываются. Для нескольких кривых с одним условием "
                "передайте их списком curves — это один вызов вместо нескольких."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "file_id": _FILE_ID,
                    "curve": {"type": "string", "description": "Мнемоника кривой (или используйте curves)."},
                    "curves": {**_CURVES, "description": "Несколько кривых с одним условием — результат по каждой."},
                    "condition": {
                        "type": "string",
                        "enum": ["gt", "ge", "lt", "le", "between", "outside", "null", "plateau"],
                    },
                    "value": {"type": "number", "description": "Порог; для between/outside — нижняя граница."},
                    "value2": {"type": "number", "description": "Верхняя граница для between/outside."},
                    "tolerance": {
                        "type": "number",
                        "description": (
                            "Для plateau: допустимое изменение между соседними отсчётами в долях значения "
                            "(0.001 — 0.1 %). По умолчанию 0.001."
                        ),
                    },
                    "min_thickness": {"type": "number", "description": "Минимальная мощность интервала. По умолчанию 0."},
                    "merge_gap": {"type": "number", "description": "Сливать интервалы с промежутком меньше этого. По умолчанию 0."},
                    "top": _TOP,
                    "bottom": _BOTTOM,
                },
                "required": ["file_id", "condition"],
                "additionalProperties": False,
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "get_samples",
            "description": (
                "Значения кривых на интервале, прореженные до заданного числа точек: каждая точка — среднее по "
                "своему участку глубин; с aggregate=minmax добавляются минимум и максимум участка. Не больше "
                f"{MAX_SAMPLE_POINTS} точек."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "file_id": _FILE_ID,
                    "curves": _CURVES,
                    "top": _TOP,
                    "bottom": _BOTTOM,
                    "max_points": {"type": "integer", "description": f"По умолчанию {DEFAULT_SAMPLE_POINTS}."},
                    "aggregate": {"type": "string", "enum": ["mean", "minmax"]},
                },
                "required": ["file_id", "curves"],
                "additionalProperties": False,
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "compare_curves",
            "description": (
                "Сравнение двух кривых на общем интервале глубин: например спуск и подъём, 400 и 2000 кГц, "
                "разные зонды. Вторая кривая интерполируется на глубины первой. Возвращает среднее и среднее "
                "абсолютное расхождение, относительное расхождение в процентах, коэффициент корреляции и "
                "участки наибольшего расхождения."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "file_id": _FILE_ID,
                    "curve": {"type": "string"},
                    "other_file_id": {"type": "string", "description": "Файл второй кривой; по умолчанию тот же."},
                    "other_curve": {"type": "string"},
                    "top": _TOP,
                    "bottom": _BOTTOM,
                },
                "required": ["file_id", "curve", "other_curve"],
                "additionalProperties": False,
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "search_knowledge",
            "description": (
                "Поиск по базе знаний программы: терминология и правила интерпретации из методических "
                "руководств, которые компания считает эталоном. Словарь и подходящие карточки уже приложены "
                "к вопросу; ищите, когда их не хватает. Карточку открывает get_knowledge.\nКарточки:\n"
                + _knowledge_index()
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "query": {"type": "string", "description": "Слова или мнемоники: «окаймляющая зона», «повторный замер», «IK07»."},
                    "method": {"type": "string", "description": "Метод, например ВИКИЗ или ЭМК, — чтобы искать только по нему."},
                    "limit": {"type": "integer", "description": f"Сколько карточек вернуть, по умолчанию {knowledge_base.DEFAULT_RESULTS}."},
                },
                "required": ["query"],
                "additionalProperties": False,
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "get_knowledge",
            "description": (
                "Полный текст карточки базы знаний по её номеру (например vikiz/kachestvo). Карточка со "
                "статусом «черновик» не подтверждена — скажите об этом."
            ),
            "parameters": {
                "type": "object",
                "properties": {"id": {"type": "string", "description": "Номер карточки из оглавления или поиска."}},
                "required": ["id"],
                "additionalProperties": False,
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "vikiz_quality",
            "description": (
                "Проверка качества записи ВИКИЗ по методическому руководству: шаг записи (не более 0,2 м), "
                "допустимый диапазон разности фаз 0,2–90° по каждому зонду (отрицательная и почти нулевая Δφ — "
                "брак), интервалы недостоверной записи, «полка» ρк на пределе пересчёта; с repeat_file_id — "
                "повторный замер (расхождение не более 3 % для двух длинных и 5 % для трёх коротких зондов). "
                "Возвращает findings — выводы проверки."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "file_id": _FILE_ID,
                    "top": _TOP,
                    "bottom": _BOTTOM,
                    "repeat_file_id": {"type": "string", "description": "Файл повторной записи того же каротажа."},
                    "phase_curves": _PHASE_CURVES,
                    "resistivity_curves": _RESISTIVITY_CURVES,
                },
                "required": ["file_id"],
                "additionalProperties": False,
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "vikiz_sounding",
            "description": (
                "Кривая зондирования ВИКИЗ на глубине (depth) или в пласте (top–bottom): медианы ρк пяти зондов "
                "от 0,5 до 2,0 м без недостоверных отсчётов и форма кривой по методичке — ровная, убывает "
                "(повышающее проникновение), возрастает (понижающее), минимум на промежуточном зонде (признак "
                "окаймляющей зоны), максимум, немонотонная; оценки ρк длинных и коротких зондов и оговорки "
                "(маломощный пласт и т. п.). Для пласта задавайте его кровлю и подошву — например из vikiz_layers."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "file_id": _FILE_ID,
                    "depth": {"type": "number", "description": "Одна глубина; вместо top/bottom."},
                    "top": _TOP,
                    "bottom": _BOTTOM,
                    "phase_curves": _PHASE_CURVES,
                    "resistivity_curves": _RESISTIVITY_CURVES,
                },
                "required": ["file_id"],
                "additionalProperties": False,
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "vikiz_layers",
            "description": (
                "Пластовая разбивка по ВИКИЗ: границы пластов в максимумах функции вертикального разрешения "
                "трёх средних зондов, для каждого пласта — ρк зондов и форма кривой "
                "зондирования (где ρк растёт, где убывает с длиной зонда, где минимум). Плюс сводка по типам. "
                f"Берите интервал разумной длины: выводится до {vikiz.MAX_LAYERS} пластов."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "file_id": _FILE_ID,
                    "top": _TOP,
                    "bottom": _BOTTOM,
                    "min_thickness": {"type": "number", "description": "Минимальная мощность пласта, м. По умолчанию 0,4 (не меньше двух шагов записи)."},
                    "max_layers": {"type": "integer", "description": f"Сколько пластов вывести, по умолчанию {vikiz.DEFAULT_LAYERS}."},
                    "phase_curves": _PHASE_CURVES,
                    "resistivity_curves": _RESISTIVITY_CURVES,
                },
                "required": ["file_id"],
                "additionalProperties": False,
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": RUN_PYTHON,
            "description": (
                "Выполняет скрипт Python на компьютере пользователя над данными открытых файлов: расчёты, которых "
                "нет в других инструментах, графики, диаграммы, кросс-плоты, гистограммы, таблицы и отчёты Word. "
                "Перед запуском пользователь видит код и подтверждает его — пишите понятный короткий код с "
                "комментариями по-русски. Для простой статистики, поиска интервалов и сравнения двух кривых "
                "используйте другие инструменты: они быстрее и не требуют подтверждения.\n"
                "В скрипте уже есть: np (numpy), pd (pandas), plt (matplotlib, без окон); files — словарь "
                "file_id → файл; las — основной файл (file_id или первый из переданных); df — его таблица "
                "pandas.DataFrame: индекс — глубина, столбцы — кривые с мнемониками как в файле, пропуски — NaN. "
                "У файла: name, depth (numpy), depth_unit, curves (мнемоника → numpy), units, descriptions, "
                "well (поля ~Well), df, interval(top, bottom) → DataFrame, curve(имя) без учёта регистра.\n"
                "save_figure(\"имя\", fig=None) сохраняет график PNG (несохранённые фигуры сохраняются сами); "
                "save_table(таблица, \"имя\") — CSV для Excel. Подписи на графиках — по-русски, с единицами; "
                "глубина на вертикальной оси сверху вниз (ax.invert_yaxis()).\n"
                "Отчёт Word — пакетом python-docx: from docx import Document; from docx.shared import Cm; "
                "doc = Document(); doc.add_heading(...), doc.add_paragraph(...), doc.add_table(rows, cols, "
                "style=\"Table Grid\"); график — doc.add_picture(save_figure(\"имя\"), width=Cm(16)); в конце "
                "save_report(doc, \"имя\") сохраняет .docx. В отчёт — только числа, полученные в этом же скрипте.\n"
                "Вам возвращается то, что скрипт напечатал print (до 12 000 символов), и список файлов. Графики "
                "видит только пользователь, вы — нет: печатайте числа, на которых строите выводы. Сети нет, "
                "запускать программы и писать вне папки результатов нельзя; LAS-файлы скрипт не меняет. "
                "При ошибке вернётся трассировка — исправьте код и запустите снова."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "title": {
                        "type": "string",
                        "description": "Что делает скрипт, в нескольких словах — увидит пользователь, это же имя папки результатов.",
                    },
                    "code": {"type": "string", "description": "Код Python 3.12."},
                    "file_id": {**_FILE_ID, "description": "Основной файл — переменные las и df. По умолчанию первый из file_ids."},
                    "file_ids": {
                        "type": "array",
                        "items": {"type": "string"},
                        "description": "Какие открытые файлы передать скрипту. По умолчанию — все.",
                    },
                    "timeout_seconds": {
                        "type": "integer",
                        "description": "Сколько секунд скрипт может работать: от 5 до 300, по умолчанию 60.",
                    },
                },
                "required": ["title", "code"],
                "additionalProperties": False,
            },
        },
    },
]


# ----------------------------------------------------------------------
# Регистрация
# ----------------------------------------------------------------------


def register(server: Server, workspace: Workspace) -> None:
    handlers: dict[str, Callable[..., Any]] = {
        "list_files": lambda include_curves=True: list_files(workspace, include_curves),
        "get_file_info": lambda file_id: get_file_info(workspace, file_id),
        "curve_stats": lambda file_id, curves, top=None, bottom=None: curve_stats(
            _file(workspace, file_id), curves, top, bottom),
        "find_intervals": lambda file_id, condition, curve=None, curves=None, **options: find_intervals_for(
            _file(workspace, file_id), curve, curves, condition, options),
        "get_samples": lambda file_id, curves, top=None, bottom=None, max_points=DEFAULT_SAMPLE_POINTS, aggregate="mean":
            get_samples(_file(workspace, file_id), curves, top, bottom, max_points, aggregate),
        "compare_curves": lambda file_id, curve, other_curve, other_file_id=None, top=None, bottom=None: compare_curves(
            _file(workspace, file_id), curve, _file(workspace, other_file_id or file_id), other_curve, top, bottom),
        "search_knowledge": lambda query, method=None, limit=knowledge_base.DEFAULT_RESULTS: knowledge_base.search(
            query, method, limit),
        "get_knowledge": lambda id: knowledge_base.get(id),
        "vikiz_quality": lambda file_id, top=None, bottom=None, repeat_file_id=None, phase_curves=None,
        resistivity_curves=None: vikiz.quality(
            _file(workspace, file_id), top, bottom,
            _file(workspace, repeat_file_id) if repeat_file_id is not None else None,
            phase_curves, resistivity_curves),
        "vikiz_sounding": lambda file_id, depth=None, top=None, bottom=None, phase_curves=None,
        resistivity_curves=None: vikiz.sounding(
            _file(workspace, file_id), top, bottom, depth, phase_curves, resistivity_curves),
        "vikiz_layers": lambda file_id, top=None, bottom=None, min_thickness=None, max_layers=vikiz.DEFAULT_LAYERS,
        phase_curves=None, resistivity_curves=None: vikiz.layers(
            _file(workspace, file_id), top, bottom, min_thickness, max_layers, phase_curves, resistivity_curves),
    }

    def tools() -> list[dict[str, Any]]:
        return TOOLS

    def call(name: str, arguments: Any = None) -> dict[str, Any]:
        if name == RUN_PYTHON:
            return {"ok": False, "error": "Скрипт запускается только программой после подтверждения пользователем."}
        handler = handlers.get(name)
        if handler is None:
            return {"ok": False, "error": f"Инструмента {name} нет. Доступны: {', '.join(handlers)}."}
        if arguments is None:
            arguments = {}
        if not isinstance(arguments, dict):
            return {"ok": False, "error": "Аргументы должны быть объектом JSON."}
        try:
            result = handler(**arguments)
        except (ToolError, vikiz.VikizError, knowledge_base.KnowledgeError) as exc:
            return {"ok": False, "error": knowledge_base.strip_references(str(exc))}
        except TypeError as exc:
            # Лишний или пропущенный аргумент — ошибка модели, а не движка.
            return {"ok": False, "error": f"Неверные аргументы {name}: {exc}"}
        return {"ok": True, "result": _without_references(_clean(result))}

    def knowledge_context(question: Any = None, methods: Any = None) -> dict[str, Any]:
        """Материалы к вопросу пользователя: программа кладёт их в системное сообщение."""
        if methods is None:
            methods = sorted({name for las in workspace.files.values() for name in _method_names(las)})
        try:
            return knowledge_base.context_for(question, methods)
        except (OSError, knowledge_base.KnowledgeError) as exc:
            print(f"Материалы к вопросу не подобраны: {exc}", file=sys.stderr)
            return {"cards": [], "text": ""}

    server.methods["agent.tools"] = tools
    server.methods["agent.call"] = call
    server.methods["agent.knowledge_context"] = knowledge_context


# ----------------------------------------------------------------------
# Инструменты
# ----------------------------------------------------------------------


def list_files(workspace: Workspace, include_curves: Any = True) -> dict[str, Any]:
    files = []
    for file_id, las in workspace.files.items():
        index = index_summary(las)
        entry: dict[str, Any] = {
            "file_id": file_id,
            "name": Path(las.source).name,
            "depth": {"unit": index["unit"], "start": index["start"], "stop": index["stop"], "step": index["step"]},
            "rows": las.row_count,
        }
        if include_curves is not False:
            entry["curves"] = [_curve_brief(curve) for curve in las.curves[1:]]
        methods = _methods(las)
        if methods:
            entry["methods"] = methods
        files.append(entry)
    return {"files": files}


def get_file_info(workspace: Workspace, file_id: str) -> dict[str, Any]:
    las = _file(workspace, file_id)
    well = las.section("well")
    header = []
    if well is not None:
        for item in well.items:
            if item.mnemonic.upper() in ("STRT", "STOP", "STEP", "NULL"):
                continue
            value = item.value.strip()
            if value:
                header.append({"mnemonic": item.mnemonic, "value": value, "description": item.description})

    parameters = []
    parameter_section = las.section("parameter")
    if parameter_section is not None:
        parameters = [
            {"mnemonic": item.mnemonic, "unit": item.unit, "value": item.value, "description": item.description}
            for item in parameter_section.items
        ]

    curves = []
    for curve in las.curves[1:]:
        info = _curve_brief(curve)
        stats = curve.stats()
        info.update({"count": stats["count"], "nulls": stats["nulls"], "min": stats["min"], "max": stats["max"], "mean": stats["mean"]})
        curves.append(info)

    return {
        "file_id": file_id,
        "name": Path(las.source).name,
        "las_version": las.version,
        "encoding": las.encoding_display,
        "null_value": las.null_value,
        "depth": index_summary(las),
        "rows": las.row_count,
        "header": header,
        "parameters": parameters,
        "curves": curves,
        "issues": [{"level": issue.level, "message": issue.message} for issue in las.issues[:30]],
    }


def curve_stats(las: LasFile, curves: list[str], top: float | None, bottom: float | None) -> dict[str, Any]:
    names = _curve_names(curves)
    depths, start, stop = _range(las, top, bottom)
    result = []
    for name in names:
        values = _curve(las, name).values
        present = [(values[i], depths[i]) for i in range(start, stop) if values[i] == values[i]]
        entry: dict[str, Any] = {"curve": name, "count": len(present), "nulls": (stop - start) - len(present)}
        if present:
            ordered = sorted(value for value, _ in present)
            minimum = min(present, key=lambda pair: pair[0])
            maximum = max(present, key=lambda pair: pair[0])
            mean = math.fsum(ordered) / len(ordered)
            variance = math.fsum((value - mean) ** 2 for value in ordered) / len(ordered)
            entry.update({
                "min": minimum[0],
                "min_depth": minimum[1],
                "max": maximum[0],
                "max_depth": maximum[1],
                "mean": mean,
                "std": math.sqrt(variance),
                "p5": _percentile(ordered, 5),
                "median": _percentile(ordered, 50),
                "p95": _percentile(ordered, 95),
            })
        result.append(entry)
    return {"interval": _interval(depths, start, stop), "curves": result}


def find_intervals_for(las: LasFile, curve: Any, curves: Any, condition: str, options: dict[str, Any]) -> dict[str, Any]:
    """Одна кривая — результат как есть; список — результат по каждой кривой."""
    if curves is None:
        if curve is None:
            raise ToolError("Укажите кривую: curve или список curves.")
        return find_intervals(las, curve, condition, **options)

    names = _curve_names(curves)
    if curve is not None and curve not in names:
        names = [curve, *names]
    results = []
    for name in names:
        result = find_intervals(las, name, condition, **options)
        result.pop("condition", None)
        results.append(result)
    return {"condition": condition, "results": results}


def find_intervals(
    las: LasFile,
    curve: str,
    condition: str,
    value: float | None = None,
    value2: float | None = None,
    tolerance: float | None = None,
    min_thickness: float | None = None,
    merge_gap: float | None = None,
    top: float | None = None,
    bottom: float | None = None,
) -> dict[str, Any]:
    depths, start, stop = _range(las, top, bottom)
    values = _curve(las, curve).values
    test = _condition(condition, value, value2, tolerance, values, start, stop)

    raw: list[list[int]] = []
    current: list[int] | None = None
    for i in range(start, stop):
        if test(i):
            if current is None:
                current = [i, i]
                raw.append(current)
            else:
                current[1] = i
        else:
            current = None

    step = _step(depths, start, stop)
    gap = max(0.0, float(merge_gap or 0))
    merged: list[list[int]] = []
    for first, last in raw:
        if merged and abs(depths[first] - depths[merged[-1][1]]) - step < gap - 1e-9:
            merged[-1][1] = last
        else:
            merged.append([first, last])

    minimum = max(0.0, float(min_thickness or 0))
    intervals = []
    total = 0.0
    for first, last in merged:
        thickness = abs(depths[last] - depths[first]) + step
        if thickness + 1e-9 < minimum:
            continue
        present = [values[i] for i in range(first, last + 1) if values[i] == values[i]]
        total += thickness
        intervals.append({
            "top": depths[first],
            "bottom": depths[last],
            "thickness": thickness,
            "mean": math.fsum(present) / len(present) if present else None,
            "min": min(present) if present else None,
            "max": max(present) if present else None,
        })

    span = abs(depths[stop - 1] - depths[start]) + step if stop > start else 0.0
    return {
        "curve": curve,
        "condition": condition,
        "interval": _interval(depths, start, stop),
        "found": len(intervals),
        "total_thickness": total,
        "share_percent": 100.0 * total / span if span > 0 else None,
        "intervals": intervals[:MAX_INTERVALS],
        "truncated": len(intervals) > MAX_INTERVALS,
    }


def get_samples(
    las: LasFile,
    curves: list[str],
    top: float | None,
    bottom: float | None,
    max_points: int = DEFAULT_SAMPLE_POINTS,
    aggregate: str = "mean",
) -> dict[str, Any]:
    names = _curve_names(curves)
    if aggregate not in ("mean", "minmax"):
        raise ToolError("aggregate должен быть mean или minmax.")
    try:
        points = int(max_points)
    except (TypeError, ValueError) as exc:
        raise ToolError("max_points должен быть целым числом.") from exc
    points = max(2, min(points, MAX_SAMPLE_POINTS))

    depths, start, stop = _range(las, top, bottom)
    columns = [_curve(las, name).values for name in names]
    count = stop - start
    bins = min(points, count)
    rows = []
    for b in range(bins):
        first = start + b * count // bins
        last = start + (b + 1) * count // bins
        row: dict[str, Any] = {"depth": depths[(first + last - 1) // 2]}
        for name, values in zip(names, columns):
            chunk = [values[i] for i in range(first, last) if values[i] == values[i]]
            row[name] = math.fsum(chunk) / len(chunk) if chunk else None
            if aggregate == "minmax":
                row[name + "_min"] = min(chunk) if chunk else None
                row[name + "_max"] = max(chunk) if chunk else None
        rows.append(row)

    return {
        "interval": _interval(depths, start, stop),
        "source_rows": count,
        "points": len(rows),
        "rows_per_point": count / len(rows) if rows else None,
        "rows": rows,
    }


def compare_curves(
    las: LasFile,
    curve: str,
    other: LasFile,
    other_curve: str,
    top: float | None,
    bottom: float | None,
) -> dict[str, Any]:
    depths, start, stop = _range(las, top, bottom)
    values = _curve(las, curve).values
    other_depths = other.curves[0].values
    other_values = _curve(other, other_curve).values

    # Вторая кривая — на глубины первой, линейно. Пары сортируются по глубине:
    # подъём записан с убывающей глубиной, а поиск соседей требует возрастающей.
    pairs = sorted((other_depths[i], other_values[i]) for i in range(len(other_depths))
                   if other_depths[i] == other_depths[i])
    xs = [d for d, _ in pairs]
    ys = [v for _, v in pairs]

    a: list[float] = []
    b: list[float] = []
    at: list[float] = []
    for i in range(start, stop):
        x = values[i]
        if x != x:
            continue
        y = _interpolate(xs, ys, depths[i])
        if y is None:
            continue
        a.append(x)
        b.append(y)
        at.append(depths[i])

    if len(a) < 2:
        raise ToolError("У кривых нет общего интервала глубин с данными.")

    diffs = [y - x for x, y in zip(a, b)]
    relative = [abs(y - x) / abs(x) * 100.0 for x, y in zip(a, b) if x != 0]
    mean_a = math.fsum(a) / len(a)
    mean_b = math.fsum(b) / len(b)
    cov = math.fsum((x - mean_a) * (y - mean_b) for x, y in zip(a, b))
    var_a = math.fsum((x - mean_a) ** 2 for x in a)
    var_b = math.fsum((y - mean_b) ** 2 for y in b)
    correlation = cov / math.sqrt(var_a * var_b) if var_a > 0 and var_b > 0 else None

    # Участки наибольшего расхождения: окна по 2 % интервала.
    window = max(1, len(a) // 50)
    windows = []
    for first in range(0, len(a), window):
        chunk = diffs[first:first + window]
        windows.append({
            "top": at[first],
            "bottom": at[min(first + window, len(a)) - 1],
            "mean_abs_difference": math.fsum(abs(d) for d in chunk) / len(chunk),
        })
    windows.sort(key=lambda item: item["mean_abs_difference"], reverse=True)

    return {
        "curve": curve,
        "other_curve": other_curve,
        "interval": {"top": at[0], "bottom": at[-1], "points": len(a)},
        "mean_first": mean_a,
        "mean_second": mean_b,
        "mean_difference": math.fsum(diffs) / len(diffs),
        "mean_abs_difference": math.fsum(abs(d) for d in diffs) / len(diffs),
        "median_relative_difference_percent": _percentile(sorted(relative), 50) if relative else None,
        "correlation": correlation,
        "largest_differences": windows[:5],
    }


# ----------------------------------------------------------------------
# Вспомогательное
# ----------------------------------------------------------------------


def _file(workspace: Workspace, file_id: Any) -> LasFile:
    las = workspace.files.get(file_id) if isinstance(file_id, str) else None
    if las is None:
        known = ", ".join(f"{fid} ({Path(f.source).name})" for fid, f in workspace.files.items()) or "нет открытых файлов"
        raise ToolError(f"Файл {file_id} не открыт. Открыты: {known}.")
    if not las.curves:
        raise ToolError("В файле нет кривых.")
    return las


def _curve(las: LasFile, name: Any):
    if isinstance(name, str):
        for curve in las.curves[1:]:
            if curve.mnemonic == name:
                return curve
        lowered = name.lower()
        for curve in las.curves[1:]:
            if curve.mnemonic.lower() == lowered:
                return curve
    available = ", ".join(curve.mnemonic for curve in las.curves[1:])
    raise ToolError(f"Кривой {name} нет в файле {Path(las.source).name}. Есть: {available}.")


def _curve_names(curves: Any) -> list[str]:
    if isinstance(curves, str):
        curves = [curves]
    if not isinstance(curves, list) or not curves or not all(isinstance(name, str) for name in curves):
        raise ToolError("curves — непустой список мнемоник.")
    if len(curves) > MAX_CURVES_PER_CALL:
        raise ToolError(f"Не больше {MAX_CURVES_PER_CALL} кривых за вызов.")
    return curves


_METHOD_HINTS = {
    "ВИКИЗ": "ВИКИЗ — инструменты vikiz_quality, vikiz_sounding, vikiz_layers; карточки vikiz/*",
    "ЭМК": "ЭМК — карточка emk/primenimost (черновик)",
}


def _method_names(las: LasFile) -> list[str]:
    """Методы файла, для которых в программе есть инструменты и карточки знаний."""
    found = []
    try:
        if not vikiz.find_zonds(las).custom:
            found.append("ВИКИЗ")
    except vikiz.VikizError:
        pass
    descriptions = " ".join((curve.description or "") for curve in las.curves[1:]).upper()
    if "ЭМК" in descriptions:
        found.append("ЭМК")
    return found


def _methods(las: LasFile) -> list[str]:
    return [_METHOD_HINTS[name] for name in _method_names(las)]


def _without_references(value: Any) -> Any:
    """Результат для модели — без ссылок на страницы источника: сноски в ответе не нужны."""
    if isinstance(value, str):
        return knowledge_base.strip_references(value)
    if isinstance(value, dict):
        return {key: _without_references(item) for key, item in value.items() if key not in ("pages", "source")}
    if isinstance(value, list):
        return [_without_references(item) for item in value]
    return value


def _curve_brief(curve) -> dict[str, Any]:
    return {"mnemonic": curve.mnemonic, "unit": curve.unit, "description": curve.description}


def _range(las: LasFile, top: Any, bottom: Any) -> tuple[Any, int, int]:
    """Строки файла внутри [top, bottom] — по возрастающей или убывающей глубине."""
    depths = las.curves[0].values
    count = len(depths)
    if count == 0:
        raise ToolError("В файле нет строк данных.")
    for name, bound in (("top", top), ("bottom", bottom)):
        if bound is not None and (not isinstance(bound, (int, float)) or isinstance(bound, bool) or not math.isfinite(bound)):
            raise ToolError(f"{name} должен быть числом.")
    low = -math.inf if top is None else float(top)
    high = math.inf if bottom is None else float(bottom)
    if low > high:
        low, high = high, low

    inside = [i for i in range(count) if depths[i] == depths[i] and low - 1e-9 <= depths[i] <= high + 1e-9]
    if not inside:
        raise ToolError(f"В интервале {top}–{bottom} нет данных. Запись: {depths[0]}–{depths[-1]}.")
    return depths, inside[0], inside[-1] + 1


def _interval(depths, start: int, stop: int) -> dict[str, Any]:
    return {"top": depths[start], "bottom": depths[stop - 1], "rows": stop - start}


def _step(depths, start: int, stop: int) -> float:
    if stop - start < 2:
        return 0.0
    steps = sorted(abs(depths[i + 1] - depths[i]) for i in range(start, stop - 1) if depths[i] == depths[i] and depths[i + 1] == depths[i + 1])
    return float(f"{steps[len(steps) // 2]:.10g}") if steps else 0.0


def _condition(condition: str, value, value2, tolerance, values, start: int, stop: int) -> Callable[[int], bool]:
    def number(x, name: str) -> float:
        if x is None or isinstance(x, bool) or not isinstance(x, (int, float)) or not math.isfinite(x):
            raise ToolError(f"Для условия {condition} нужно число {name}.")
        return float(x)

    if condition in ("gt", "ge", "lt", "le"):
        threshold = number(value, "value")
        compare = {
            "gt": lambda v: v > threshold,
            "ge": lambda v: v >= threshold,
            "lt": lambda v: v < threshold,
            "le": lambda v: v <= threshold,
        }[condition]
        return lambda i: values[i] == values[i] and compare(values[i])

    if condition in ("between", "outside"):
        low, high = sorted((number(value, "value"), number(value2, "value2")))
        if condition == "between":
            return lambda i: values[i] == values[i] and low <= values[i] <= high
        return lambda i: values[i] == values[i] and (values[i] < low or values[i] > high)

    if condition == "null":
        return lambda i: values[i] != values[i]

    if condition == "plateau":
        present = [values[i] for i in range(start, stop) if values[i] == values[i]]
        if not present:
            raise ToolError("У кривой нет данных на интервале.")
        share = max(0.0, 0.001 if tolerance is None else number(tolerance, "tolerance"))
        # Допуск считается от самого значения, а не от размаха кривой: у УЭС
        # размах бывает от 0.5 до 7000 Ом·м, и доля размаха объявила бы «полкой»
        # всю запись в низкоомной части. Для нуля — доля размаха.
        span_limit = (max(present) - min(present)) * share

        def flat(i: int) -> bool:
            v = values[i]
            if v != v:
                return False
            limit = abs(v) * share if v != 0 else span_limit
            neighbours = [values[j] for j in (i - 1, i + 1) if start <= j < stop and values[j] == values[j]]
            return any(abs(v - n) <= limit for n in neighbours)

        return flat

    raise ToolError("condition: gt, ge, lt, le, between, outside, null или plateau.")


def _percentile(ordered: list[float], percent: float) -> float:
    if len(ordered) == 1:
        return ordered[0]
    position = (len(ordered) - 1) * percent / 100.0
    lower = math.floor(position)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def _interpolate(xs: list[float], ys: list[float], x: float) -> float | None:
    if not xs or x < xs[0] or x > xs[-1]:
        return None
    k = bisect.bisect_left(xs, x)
    if k < len(xs) and xs[k] == x:
        return ys[k] if ys[k] == ys[k] else None
    left, right = k - 1, k
    if left < 0 or right >= len(xs):
        return None
    y0, y1 = ys[left], ys[right]
    if y0 != y0 or y1 != y1:
        return None
    t = (x - xs[left]) / (xs[right] - xs[left])
    return y0 + (y1 - y0) * t


def _clean(value: Any) -> Any:
    """NaN и бесконечности — в null, числа — до 7 значащих цифр: результат уходит в JSON и модели."""
    if isinstance(value, float):
        if not math.isfinite(value):
            return None
        return float(f"{value:.7g}")
    if isinstance(value, dict):
        return {key: _clean(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_clean(item) for item in value]
    return value
