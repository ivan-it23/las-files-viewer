"""Расчёты ВИКИЗ по методическому руководству (Эпов, Антонов, 2000).

Здесь — то, что методичка задаёт правилами и числами, а не то, что модель
должна выводить сама: допустимый диапазон разности фаз и допуски повторного
замера (гл. 6), форма кривой зондирования (гл. 3, 7), границы пластов по
функции вертикального разрешения (гл. 5). Номера страниц — в комментариях и
в ответах: модель ссылается на них пользователю.

Инверсии (подбора ρп, ρзп, rзп) здесь нет: оценки по показаниям зондов
называются оценками.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .las import LasFile

SOURCE = "Методичка ВИКИЗ (2000)"

ZOND_LENGTHS = (0.5, 0.7, 1.0, 1.4, 2.0)                # табл. 4.1, с. 32
PHASE_ERRORS = (0.03, 0.04, 0.02, 0.02, 0.02)           # погрешность Δφ, с. 41
REPEAT_LIMITS = (0.05, 0.05, 0.05, 0.03, 0.03)          # повторный замер, с. 59
PHASE_MIN = 0.2                                         # шум аппаратуры, с. 62
PHASE_MAX = 90.0                                        # ≈ 0,22 Ом·м, с. 62
STEP_MAX = 0.2                                          # шаг квантования, с. 58
THIN_LAYER = 1.5                                        # маломощный пласт, с. 85–86
CUSTOM_PHASE_ERROR = 0.03                               # условная погрешность для чужих кривых

MAX_LAYERS = 100
DEFAULT_LAYERS = 40
MAX_INTERVALS = 30

_CODES = {"05": 0.5, "07": 0.7, "10": 1.0, "14": 1.4, "20": 2.0}


class VikizError(Exception):
    """Ошибка в аргументах или данных — описание уходит модели."""


@dataclass
class Zond:
    length: float | None
    phase: Any = None          # кривая Δφ, град
    resistivity: Any = None    # кривая ρк, Ом·м
    error: float = CUSTOM_PHASE_ERROR
    repeat_limit: float = 0.05


@dataclass
class ZondSet:
    zonds: list[Zond]
    custom: bool
    found_by: str

    def describe(self) -> list[dict[str, Any]]:
        return [
            {
                "zond_m": zond.length,
                "phase_curve": zond.phase.mnemonic if zond.phase is not None else None,
                "resistivity_curve": zond.resistivity.mnemonic if zond.resistivity is not None else None,
            }
            for zond in self.zonds
        ]


# ----------------------------------------------------------------------
# Поиск кривых ВИКИЗ
# ----------------------------------------------------------------------


def find_zonds(las: LasFile, phase_curves: Any = None, resistivity_curves: Any = None) -> ZondSet:
    """Кривые ВИКИЗ файла: по описаниям («ВИКИЗ зонд 0.7 м»), иначе по мнемоникам IK07/RO07.

    Явно переданные списки (от короткого зонда к длинному) — для других
    приборов: тогда погрешности ВИКИЗ к ним не применяются.
    """
    if phase_curves is not None or resistivity_curves is not None:
        phases = _curve_list(las, phase_curves, "phase_curves")
        resistivities = _curve_list(las, resistivity_curves, "resistivity_curves")
        count = max(len(phases), len(resistivities))
        if phases and resistivities and len(phases) != len(resistivities):
            raise VikizError("phase_curves и resistivity_curves должны быть одной длины — по кривой на зонд.")
        if count < 2:
            raise VikizError("Нужно не меньше двух зондов.")
        zonds = [
            Zond(None, phases[i] if phases else None, resistivities[i] if resistivities else None)
            for i in range(count)
        ]
        return ZondSet(zonds, custom=True, found_by="переданы явно")

    zonds = {length: Zond(length) for length in ZOND_LENGTHS}
    found_by = "описания кривых"
    for curve in las.curves[1:]:
        description = (curve.description or "").lower()
        if "викиз" not in description:
            continue
        match = re.search(r"зонд\w*\s*(\d+(?:[.,]\d+)?)\s*м", description)
        length = _nearest_length(float(match.group(1).replace(",", "."))) if match else None
        if length is None:
            continue
        if "фаз" in description:
            zonds[length].phase = zonds[length].phase or curve
        elif "сопротив" in description or "ом" in (curve.unit or "").lower():
            zonds[length].resistivity = zonds[length].resistivity or curve

    if not any(z.phase or z.resistivity for z in zonds.values()):
        found_by = "мнемоники"
        for curve in las.curves[1:]:
            match = re.fullmatch(r"(IK|DF|DPH)(05|07|10|14|20)", curve.mnemonic.upper())
            if match:
                zonds[_CODES[match.group(2)]].phase = curve
            match = re.fullmatch(r"(RO|RK)(05|07|10|14|20)", curve.mnemonic.upper())
            if match:
                zonds[_CODES[match.group(2)]].resistivity = curve

    ordered = [zonds[length] for length in ZOND_LENGTHS]
    if not any(z.phase or z.resistivity for z in ordered):
        raise VikizError(
            f"В файле {Path(las.source).name} не найдены кривые ВИКИЗ (ни по описанию «ВИКИЗ зонд … м», "
            "ни по мнемоникам IK05…IK20 / RO05…RO20). Для других приборов передайте кривые явно: "
            "phase_curves и/или resistivity_curves от короткого зонда к длинному."
        )
    for zond, error, limit in zip(ordered, PHASE_ERRORS, REPEAT_LIMITS):
        zond.error = error
        zond.repeat_limit = limit
    return ZondSet(ordered, custom=False, found_by=found_by)


# ----------------------------------------------------------------------
# Качество записи (гл. 6)
# ----------------------------------------------------------------------


def quality(las: LasFile, top: Any = None, bottom: Any = None, repeat: LasFile | None = None,
            phase_curves: Any = None, resistivity_curves: Any = None) -> dict[str, Any]:
    zset = find_zonds(las, phase_curves, resistivity_curves)
    rows = _rows(las, top, bottom)
    depths = las.curves[0].values
    step = _median_step(depths, rows)
    findings: list[str] = []

    if step is not None and step > STEP_MAX + 1e-9:
        findings.append(f"Шаг записи {step:g} м больше допустимых {STEP_MAX} м (с. 58).")

    zond_results = []
    for zond in zset.zonds:
        entry: dict[str, Any] = {"zond_m": zond.length}
        if zond.phase is not None:
            values = zond.phase.values
            present = [i for i in rows if _ok(values[i])]
            negative = [i for i in present if values[i] < 0]
            low = [i for i in present if values[i] < PHASE_MIN]
            high = [i for i in present if values[i] > PHASE_MAX]
            entry.update({
                "phase_curve": zond.phase.mnemonic,
                "count": len(present),
                "phase_below_0_2": len(low),
                "phase_negative": len(negative),
                "phase_above_90": len(high),
                "invalid_share_percent": 100.0 * (len(low) + len(high)) / len(present) if present else None,
                "invalid_intervals": _intervals(depths, sorted(low + high), step),
            })
        if zond.resistivity is not None:
            entry["resistivity_curve"] = zond.resistivity.mnemonic
            limit = _limit_plateau(zond.resistivity.values, rows)
            if limit is not None:
                value, at = limit
                entry["resistivity_limit"] = {"value": value, "count": len(at)}
                if zond.phase is not None:
                    bad_phase = sum(1 for i in at if _ok(zond.phase.values[i]) and zond.phase.values[i] < PHASE_MIN)
                    entry["resistivity_limit"]["at_phase_below_0_2"] = bad_phase
        zond_results.append(entry)

    with_phase = [z for z in zond_results if "phase_curve" in z]
    bad = [z for z in with_phase if (z["phase_below_0_2"] or z["phase_above_90"])]
    if not with_phase:
        findings.append(
            "Кривых разности фаз в файле нет — диапазон Δφ 0,2–90° (с. 62) проверить нельзя; "
            "проверено только ρк."
        )
    elif bad:
        for z in bad:
            label = f"зонд {_metres(z['zond_m'])}" if z["zond_m"] else z["phase_curve"]
            parts = []
            if z["phase_negative"]:
                parts.append(f"отрицательная Δφ — {z['phase_negative']} отсч.")
            if z["phase_below_0_2"] - z["phase_negative"]:
                parts.append(f"0…0,2° — {z['phase_below_0_2'] - z['phase_negative']} отсч.")
            if z["phase_above_90"]:
                parts.append(f"больше 90° — {z['phase_above_90']} отсч.")
            findings.append(
                f"{label} ({z['phase_curve']}): {', '.join(parts)} "
                f"({z['invalid_share_percent']:.1f} % записи). Вне диапазона 0,2–90° запись недостоверна (с. 62)."
            )
    else:
        findings.append("Разность фаз всех зондов в допустимом диапазоне 0,2–90° (с. 62).")

    for z in zond_results:
        limit = z.get("resistivity_limit")
        if limit and limit.get("at_phase_below_0_2"):
            findings.append(
                f"{z['resistivity_curve']}: ρк = {limit['value']:g} Ом·м стоит {limit['count']} раз, из них "
                f"{limit['at_phase_below_0_2']} — при Δφ < 0,2°: это предел пересчёта Δφ → ρк, а не УЭС породы "
                "(наблюдение по файлам компании, см. карточку vikiz/kachestvo)."
            )

    result: dict[str, Any] = {
        "source": SOURCE,
        "file": Path(las.source).name,
        "curves_found_by": zset.found_by,
        "interval": _interval(depths, rows),
        "step_m": step,
        "zonds": zond_results,
        "findings": findings,
        "not_checked": (
            "Калибровочные уровни 0° и 25° и «нули воздуха» после первичной обработки в LAS обычно не "
            "сохраняются (с. 59) — по файлу их не проверить."
        ),
    }
    if zset.custom:
        result["warning"] = "Кривые переданы явно: допуски ВИКИЗ применены условно."
    if repeat is not None:
        result["repeat"] = _repeat_check(las, repeat, zset, rows)
        result["findings"].extend(result["repeat"]["findings"])
    return result


def _repeat_check(las: LasFile, repeat: LasFile, zset: ZondSet, rows: list[int]) -> dict[str, Any]:
    """Повторный замер: расхождение ≤ 3 % (два длинных) и ≤ 5 % (три коротких) — с. 59;
    отклонение каждой записи от среднего ≤ ±(0,2° + 0,03·Δφ) — с. 58."""
    other = find_zonds(repeat) if not zset.custom else None
    depths = las.curves[0].values
    other_depths = repeat.curves[0].values
    results = []
    findings = []
    for index, zond in enumerate(zset.zonds):
        curve = zond.phase or zond.resistivity
        if curve is None:
            continue
        if other is not None:
            other_zond = other.zonds[index]
            other_curve = other_zond.phase if zond.phase is not None else other_zond.resistivity
        else:
            other_curve = _find(repeat, curve.mnemonic)
        if other_curve is None:
            continue
        pairs = sorted((other_depths[i], other_curve.values[i]) for i in range(len(other_depths))
                       if _ok(other_depths[i]))
        xs = [d for d, _ in pairs]
        ys = [v for _, v in pairs]
        relative = []
        outside = 0
        compared = 0
        for i in rows:
            a = curve.values[i]
            b = _interpolate(xs, ys, depths[i])
            if not _ok(a) or b is None or not _ok(b):
                continue
            if zond.phase is not None and (min(a, b) < PHASE_MIN or max(a, b) > PHASE_MAX):
                continue
            compared += 1
            mean = (a + b) / 2
            if mean != 0:
                relative.append(abs(a - b) / abs(mean))
            if zond.phase is not None and abs(a - b) / 2 > 0.2 + 0.03 * abs(mean):
                outside += 1
        if compared < 2:
            continue
        median = _median(relative) if relative else None
        entry = {
            "zond_m": zond.length,
            "curve": curve.mnemonic,
            "other_curve": other_curve.mnemonic,
            "compared": compared,
            "median_relative_difference_percent": 100 * median if median is not None else None,
            "limit_percent": 100 * zond.repeat_limit,
        }
        if zond.phase is not None:
            entry["outside_tolerance_share_percent"] = 100.0 * outside / compared
        results.append(entry)
        if median is not None and median > zond.repeat_limit:
            label = f"зонд {_metres(zond.length)}" if zond.length else curve.mnemonic
            findings.append(
                f"Повторный замер, {label}: медианное расхождение {100 * median:.1f} % больше допустимых "
                f"{100 * zond.repeat_limit:g} % (с. 59) — неисправность прибора или некачественная запись."
            )
    if results and not findings:
        findings.append("Повторный замер: расхождения зондов в пределах 3 % (длинные) и 5 % (короткие), с. 59.")
    if not results:
        findings.append("Повторный замер: нет общего интервала или одноимённых кривых для сравнения.")
    findings.append("Сравнивать можно только записи одного каротажа (с. 59).")
    return {"file": Path(repeat.source).name, "zonds": results, "findings": findings}


# ----------------------------------------------------------------------
# Кривая зондирования (гл. 3, 7)
# ----------------------------------------------------------------------


def sounding(las: LasFile, top: Any = None, bottom: Any = None, depth: Any = None,
             phase_curves: Any = None, resistivity_curves: Any = None) -> dict[str, Any]:
    zset = find_zonds(las, phase_curves, resistivity_curves)
    depths = las.curves[0].values
    if depth is not None:
        if top is not None or bottom is not None:
            raise VikizError("Укажите либо depth, либо интервал top/bottom.")
        number = _number(depth, "depth")
        rows = [_nearest_row(depths, number)]
    else:
        rows = _rows(las, top, bottom)
    result = _sounding_rows(zset, depths, rows)
    result["file"] = Path(las.source).name
    result["source"] = SOURCE
    if zset.custom:
        result["warning"] = "Кривые переданы явно: погрешности ВИКИЗ к ним не относятся, допуск условный 3 %."
    return result


def _sounding_rows(zset: ZondSet, depths, rows: list[int]) -> dict[str, Any]:
    values: list[float | None] = []
    used = []
    for zond in zset.zonds:
        curve = zond.resistivity
        if curve is None:
            values.append(None)
            used.append(0)
            continue
        good = [curve.values[i] for i in rows if _valid_point(zond, i)]
        values.append(_median(good) if good else None)
        used.append(len(good))

    top_depth, bottom_depth = depths[rows[0]], depths[rows[-1]]
    thickness = abs(bottom_depth - top_depth) + (_median_step(depths, rows) or 0.0) if len(rows) > 1 else 0.0
    labels = [_metres(z.length) if z.length else (z.resistivity.mnemonic if z.resistivity is not None else "?")
              for z in zset.zonds]
    shape = classify(values, [z.error for z in zset.zonds], labels)
    notes = list(shape.pop("notes"))
    if len(rows) > 1 and thickness < THIN_LAYER:
        notes.append(
            f"Мощность {thickness:.1f} м меньше 1,5 м: расхождение зондов может быть вызвано вмещающими "
            "породами, а не проникновением (с. 85–86)."
        )
    skipped = sum(len(rows) - n for n in used if n)
    if skipped:
        notes.append("Отсчёты с Δφ вне 0,2–90° не учитывались (с. 62).")

    return {
        "interval": {"top": min(top_depth, bottom_depth), "bottom": max(top_depth, bottom_depth),
                     "rows": len(rows), "thickness": thickness},
        "zonds": [
            {"zond_m": z.length, "curve": z.resistivity.mnemonic if z.resistivity is not None else None,
             "rho_k": v, "points": n}
            for z, v, n in zip(zset.zonds, values, used)
        ],
        **shape,
        "notes": notes,
    }


def classify(values: list[float | None], errors: list[float], labels: list[str] | None = None) -> dict[str, Any]:
    """Форма кривой зондирования по ρк зондов от короткого к длинному.

    Две соседние точки считаются равными, если их различие в логарифме не
    больше совокупной погрешности: погрешность Δφ зонда (с. 41), умноженная
    на коэффициент усиления при пересчёте в ρк (1,4 до 10 Ом·м, до 2,0 выше
    100 Ом·м, с. 18–19).
    """
    points = [(i, v) for i, v in enumerate(values) if v is not None and v > 0 and math.isfinite(v)]
    if len(points) < 2:
        return {"shape": "нет данных", "meaning": "Недостаточно достоверных ρк для кривой зондирования.",
                "pages": "", "notes": []}

    logs = [math.log(v) for _, v in points]
    errs = [_rho_error(v, errors[i]) for i, v in points]

    def tolerance(a: int, b: int) -> float:
        return math.hypot(errs[a], errs[b])

    steps = []
    for k in range(len(points) - 1):
        d = logs[k + 1] - logs[k]
        t = tolerance(k, k + 1)
        steps.append(1 if d > t else -1 if d < -t else 0)

    spread_flat = all(abs(logs[a] - logs[b]) <= tolerance(a, b)
                      for a in range(len(points)) for b in range(a + 1, len(points)))
    notes: list[str] = []
    k_min = min(range(len(points)), key=lambda k: logs[k])
    k_max = max(range(len(points)), key=lambda k: logs[k])

    def significant(a: int, b: int) -> bool:
        return abs(logs[a] - logs[b]) > tolerance(a, b)

    if spread_flat:
        shape, meaning, pages = (
            "ровная",
            "Показания зондов совпадают в пределах погрешности: прискважинной неоднородности не видно — "
            "непроницаемый пласт (глина, уплотнённый) или нейтральное проникновение.",
            "18–21, 86",
        )
    elif all(s <= 0 for s in steps) and logs[-1] < logs[0] and significant(0, len(points) - 1):
        shape, meaning, pages = (
            "убывает",
            "ρк убывает с длиной зонда: ρзп > ρп — повышающее проникновение (типично для водонасыщенного и "
            "нефтенасыщенного коллектора; большое расхождение зондов — признак водонасыщенного).",
            "22–23, 66, 70",
        )
    elif all(s >= 0 for s in steps) and logs[-1] > logs[0] and significant(0, len(points) - 1):
        shape, meaning, pages = (
            "возрастает",
            "ρк растёт с длиной зонда: ρзп < ρп — понижающее проникновение (газонасыщенный коллектор; в "
            "глинистых и алевролитовых пластах — неглубокое понижающее проникновение по прослоям и "
            "трещиноватости, различать по ПС и ГК). Так же выглядит влияние сильнопроводящего бурового "
            "раствора (менее ≈ 0,2 Ом·м): он занижает ρк коротких зондов — уточните УЭС раствора.",
            "23, 71, 86, 94–96",
        )
    elif 0 < k_min < len(points) - 1 and significant(0, k_min) and significant(len(points) - 1, k_min):
        length = _label(labels, points[k_min][0])
        shape, meaning, pages = (
            "минимум",
            f"Минимум ρк на промежуточном зонде ({length}): признак окаймляющей зоны — подвижные углеводороды "
            "с пластовой водой. Минимум смещается к длинным зондам по мере удаления зоны от скважины. "
            "Интерпретировать осторожно.",
            "24, 66, 70, 74, 90",
        )
    elif 0 < k_max < len(points) - 1 and significant(0, k_max) and significant(len(points) - 1, k_max):
        length = _label(labels, points[k_max][0])
        shape, meaning, pages = (
            "максимум",
            f"Максимум ρк на промежуточном зонде ({length}): немонотонная кривая; по одним показаниям тип "
            "неоднородности однозначно не определить, нужны априорные ограничения.",
            "74",
        )
    else:
        shape, meaning, pages = (
            "немонотонная",
            "Кривая немонотонная, без выраженного экстремума: зонды различаются, но порядок неустойчив "
            "(тонкие прослои, вмещающие породы, шум).",
            "85–86",
        )

    estimates = {}
    n = len(points)
    if n >= 2 and not significant(n - 1, n - 2) and shape != "ровная":
        estimates["rho_long"] = math.exp((logs[-1] + logs[-2]) / 2)
        notes.append("Два длинных зонда близки: их ρк близко к ρп (оценка без инверсии), с. 22–23, 71, 79.")
    if n >= 2 and points[0][0] == 0 and points[1][0] == 1 and shape in ("убывает", "возрастает") \
            and not significant(0, 1):
        estimates["rho_short"] = math.exp((logs[0] + logs[1]) / 2)
        notes.append("Два коротких зонда близки: их ρк близко к ρзп (оценка без инверсии), с. 22–23.")
    return {"shape": shape, "meaning": meaning, "pages": pages, "estimates": estimates, "notes": notes}


def _label(labels: list[str] | None, index: int) -> str:
    if labels and index < len(labels):
        return labels[index]
    return _metres(ZOND_LENGTHS[index]) if index < len(ZOND_LENGTHS) else f"зонд {index + 1}"


def _rho_error(rho: float, phase_error: float) -> float:
    # Коэффициент усиления погрешности при пересчёте Δφ в ρк (с. 18–19, рис. 3.2).
    if rho <= 10:
        gain = 1.4
    elif rho >= 100:
        gain = 2.0
    else:
        gain = 1.4 + 0.6 * math.log10(rho / 10)
    return gain * phase_error


# ----------------------------------------------------------------------
# Пластовая разбивка (гл. 5, с. 51)
# ----------------------------------------------------------------------


def layers(las: LasFile, top: Any = None, bottom: Any = None, min_thickness: Any = None,
           max_layers: Any = DEFAULT_LAYERS, phase_curves: Any = None,
           resistivity_curves: Any = None) -> dict[str, Any]:
    zset = find_zonds(las, phase_curves, resistivity_curves)
    depths = las.curves[0].values
    rows = sorted(_rows(las, top, bottom), key=lambda i: depths[i])
    if len(rows) < 5:
        raise VikizError("Слишком короткий интервал для разбивки.")
    step = _median_step(depths, rows) or 0.1
    minimum = 0.4 if min_thickness is None else _number(min_thickness, "min_thickness")
    minimum = max(minimum, 2 * step)                      # Hmin ≥ 2Δz, с. 51
    try:
        limit = max(1, min(int(max_layers), MAX_LAYERS))
    except (TypeError, ValueError) as exc:
        raise VikizError("max_layers — целое число.") from exc

    # Три средних зонда (с. 51). Если Δφ в файле нет — по ρк: Δφ монотонно
    # зависит от ρ, и максимумы изменения совпадают по глубине.
    middle = zset.zonds[1:-1] if len(zset.zonds) >= 3 else zset.zonds
    use_phase = all(z.phase is not None for z in middle)
    series = []
    for zond in middle:
        curve = zond.phase if use_phase else zond.resistivity
        if curve is None:
            raise VikizError("Для разбивки нужны кривые трёх средних зондов (Δφ или ρк).")
        logs = []
        for i in rows:
            v = curve.values[i]
            ok = _ok(v) and (PHASE_MIN <= v <= PHASE_MAX if use_phase else v > 0)
            logs.append(math.log(abs(v)) if ok else None)
        series.append(_smooth(logs))

    eta = [None] * len(rows)
    for k in range(1, len(rows) - 1):
        dz = depths[rows[k + 1]] - depths[rows[k - 1]]
        if dz <= 0:
            continue
        total, parts = 0.0, 0
        for s in series:
            if s[k - 1] is not None and s[k + 1] is not None:
                total += abs(s[k + 1] - s[k - 1]) / dz
                parts += 1
        if parts:
            eta[k] = total * len(series) / parts

    present = [e for e in eta if e is not None]
    if not present:
        raise VikizError("На интервале нет достоверных значений для разбивки.")
    med = _median(present)
    mad = _median([abs(e - med) for e in present]) * 1.4826
    # Порог — выше фона записи и выше погрешности измерения: изменение сигнала
    # меньше 5 % на двух шагах (погрешность Δφ — 2–4 %, с. 41) границей не считается.
    floor = len(series) * 0.05 / (2 * step)
    threshold = max(med + 3 * mad, floor)
    peaks = [k for k in range(1, len(rows) - 1)
             if eta[k] is not None and eta[k] >= threshold
             and (eta[k - 1] is None or eta[k] >= eta[k - 1])
             and (eta[k + 1] is None or eta[k] >= eta[k + 1])]
    chosen: list[int] = []
    for k in sorted(peaks, key=lambda k: -eta[k]):
        if all(abs(depths[rows[k]] - depths[rows[c]]) >= minimum for c in chosen):
            chosen.append(k)
    chosen.sort()

    edges = [0, *chosen, len(rows) - 1]
    found = []
    for a, b in zip(edges, edges[1:]):
        # Отсчёты у самой границы относятся к переходной зоне — в средние не входят.
        inner = rows[a + 1:b] if b - a > 2 else rows[a:b + 1]
        if not inner:
            continue
        thickness = depths[rows[b]] - depths[rows[a]]
        if thickness + 1e-9 < minimum and found:
            continue
        info = _sounding_rows(zset, depths, inner)
        found.append({
            "top": depths[rows[a]],
            "bottom": depths[rows[b]],
            "thickness": thickness,
            "rho_k": [z["rho_k"] for z in info["zonds"]],
            "shape": info["shape"],
            "estimates": info.get("estimates", {}),
            "thin": thickness < THIN_LAYER,
        })

    summary: dict[str, dict[str, float]] = {}
    for layer in found:
        item = summary.setdefault(layer["shape"], {"layers": 0, "thickness": 0.0})
        item["layers"] += 1
        item["thickness"] += layer["thickness"]

    return {
        "source": SOURCE,
        "file": Path(las.source).name,
        "method": (
            "Границы — максимумы ηz = Σ|∂ln|Δφᵢ|/∂z| трёх средних зондов (с. 51)" if use_phase
            else "Границы — максимумы изменения ln ρк трёх средних зондов (Δφ в файле нет; аналог ηz, с. 51)"
        ),
        "zond_order": [z.length for z in zset.zonds],
        "interval": {"top": depths[rows[0]], "bottom": depths[rows[-1]], "rows": len(rows)},
        "min_thickness": minimum,
        "layers_found": len(found),
        "summary_by_shape": summary,
        "layers": found[:limit],
        "truncated": len(found) > limit,
        "legend": {
            "убывает": "ρк убывает с длиной зонда — повышающее проникновение (с. 22–23)",
            "возрастает": "ρк растёт с длиной зонда — понижающее проникновение (с. 23)",
            "минимум": "минимум на промежуточном зонде — признак окаймляющей зоны (с. 24, 90)",
            "ровная": "зонды совпадают — нет выраженной прискважинной неоднородности (с. 21, 86)",
            "максимум": "максимум на промежуточном зонде (с. 74)",
            "немонотонная": "порядок зондов неустойчив (с. 85–86)",
        },
        "notes": [
            "rho_k — медианы ρк зондов в пласте без отсчётов у границ, от короткого зонда к длинному.",
            "Пласты thin=true (менее 1,5 м): форма кривой может отражать вмещающие породы (с. 85–86).",
            "Характер насыщения по форме кривой — качественный признак; вывод — по комплексу и критическим "
            "значениям залежи (с. 63, 71).",
        ],
    }


# ----------------------------------------------------------------------
# Вспомогательное
# ----------------------------------------------------------------------


def _valid_point(zond: Zond, i: int) -> bool:
    rho = zond.resistivity.values[i]
    if not _ok(rho) or rho <= 0:
        return False
    if zond.phase is not None:
        phase = zond.phase.values[i]
        return _ok(phase) and PHASE_MIN <= phase <= PHASE_MAX
    return True


def _limit_plateau(values, rows: list[int]) -> tuple[float, list[int]] | None:
    """Максимум кривой, повторённый много раз подряд, — предел пересчёта."""
    present = [i for i in rows if _ok(values[i])]
    if not present:
        return None
    top_value = max(values[i] for i in present)
    at = [i for i in present if values[i] >= top_value * (1 - 1e-6)]
    return (top_value, at) if len(at) >= 3 else None


def _rows(las: LasFile, top: Any, bottom: Any) -> list[int]:
    depths = las.curves[0].values
    low = -math.inf if top is None else _number(top, "top")
    high = math.inf if bottom is None else _number(bottom, "bottom")
    if low > high:
        low, high = high, low
    rows = [i for i in range(len(depths)) if _ok(depths[i]) and low - 1e-9 <= depths[i] <= high + 1e-9]
    if not rows:
        raise VikizError(f"В интервале {top}–{bottom} нет данных. Запись: {depths[0]}–{depths[-1]}.")
    return rows


def _nearest_row(depths, depth: float) -> int:
    best = min((i for i in range(len(depths)) if _ok(depths[i])), key=lambda i: abs(depths[i] - depth), default=None)
    if best is None:
        raise VikizError("В файле нет глубин.")
    return best


def _interval(depths, rows: list[int]) -> dict[str, Any]:
    values = [depths[i] for i in rows]
    return {"top": min(values), "bottom": max(values), "rows": len(rows)}


def _intervals(depths, rows: list[int], step: float | None) -> list[dict[str, Any]]:
    """Подряд идущие строки — интервалы; промежуток до двух шагов сливается."""
    if not rows:
        return []
    gap = 2 * (step or 0.1) + 1e-9
    ordered = sorted(depths[i] for i in rows)
    result = [[ordered[0], ordered[0], 1]]
    for d in ordered[1:]:
        if d - result[-1][1] <= gap:
            result[-1][1] = d
            result[-1][2] += 1
        else:
            result.append([d, d, 1])
    result.sort(key=lambda r: -r[2])
    return [{"top": a, "bottom": b, "points": n} for a, b, n in sorted(result[:MAX_INTERVALS])]


def _median_step(depths, rows: list[int]) -> float | None:
    ordered = sorted(depths[i] for i in rows)
    steps = [b - a for a, b in zip(ordered, ordered[1:]) if b > a]
    return float(f"{_median(steps):.10g}") if steps else None


def _smooth(values: list[float | None]) -> list[float | None]:
    """Скользящее среднее по трём отсчётам: производная по сырой записи слишком шумная."""
    result: list[float | None] = []
    for k in range(len(values)):
        window = [v for v in values[max(0, k - 1):k + 2] if v is not None]
        result.append(sum(window) / len(window) if values[k] is not None and window else None)
    return result


def _median(values: list[float]) -> float:
    ordered = sorted(values)
    n = len(ordered)
    mid = n // 2
    return ordered[mid] if n % 2 else (ordered[mid - 1] + ordered[mid]) / 2


def _interpolate(xs: list[float], ys: list[float], x: float) -> float | None:
    import bisect

    if not xs or x < xs[0] or x > xs[-1]:
        return None
    k = bisect.bisect_left(xs, x)
    if k < len(xs) and xs[k] == x:
        return ys[k]
    left, right = k - 1, k
    if left < 0 or right >= len(xs):
        return None
    t = (x - xs[left]) / (xs[right] - xs[left])
    return ys[left] + (ys[right] - ys[left]) * t


def _metres(value: float) -> str:
    return f"{value:.1f}".replace(".", ",") + " м"


def _nearest_length(value: float) -> float | None:
    best = min(ZOND_LENGTHS, key=lambda length: abs(length - value))
    return best if abs(best - value) <= 0.06 else None


def _curve_list(las: LasFile, names: Any, label: str) -> list[Any]:
    if names is None:
        return []
    if isinstance(names, str):
        names = [names]
    if not isinstance(names, list) or not all(isinstance(n, str) for n in names) or len(names) > 8:
        raise VikizError(f"{label} — список мнемоник от короткого зонда к длинному (до 8).")
    result = []
    for name in names:
        curve = _find(las, name)
        if curve is None:
            available = ", ".join(c.mnemonic for c in las.curves[1:])
            raise VikizError(f"Кривой {name} нет в файле {Path(las.source).name}. Есть: {available}.")
        result.append(curve)
    return result


def _find(las: LasFile, name: str):
    for curve in las.curves[1:]:
        if curve.mnemonic == name:
            return curve
    for curve in las.curves[1:]:
        if curve.mnemonic.lower() == name.lower():
            return curve
    return None


def _number(value: Any, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise VikizError(f"{name} должен быть числом.")
    return float(value)


def _ok(value: Any) -> bool:
    return value == value and value is not None and math.isfinite(value)
