"""Иконка «Просмотрщика LAS»: белый лист с загнутым углом и тёмно-синяя
плашка с крупной подписью «.LAS» — больше на листе ничего нет.

Строгий вариант прежней иконки: тонкий холодно-серый контур, прямоугольная
плашка вровень с листом, шрифт Bahnschrift вместо Segoe UI Black.

Пересобирает LasViewer/Assets/LasViewer.ico со всеми размерами Windows.
Нужен Pillow (только разработчику, в программу не входит):

    python scripts/make_icon.py [--preview файл.png]

Каждый размер рисуется отдельно: в 16–24 px плашка шире листа и выше,
а контур толще — иначе подпись не читается.
"""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "LasViewer" / "Assets" / "LasViewer.ico"
FONT = Path(r"C:\Windows\Fonts\bahnschrift.ttf")  # Bahnschrift — строгий «инженерный» шрифт Windows

NAVY = (0x0F, 0x3B, 0x6E, 255)
WHITE = (255, 255, 255, 255)
PAGE_EDGE = (0x8C, 0x96, 0xA3, 255)
FOLD = (0xE3, 0xE7, 0xEC, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SUPERSAMPLE = 8

def render(size: int) -> Image.Image:
    big = size * SUPERSAMPLE
    u = big / 256
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    small = size <= 24

    # Лист с загнутым правым верхним углом.
    side = (30 if small else 40) * u
    left, top, right, bottom = side, 10 * u, big - side, 246 * u
    fold = 46 * u
    edge = max(SUPERSAMPLE, round((7 if small else 4) * u))
    draw.polygon([(left, top), (right - fold, top), (right, top + fold), (right, bottom), (left, bottom)],
                 fill=WHITE, outline=PAGE_EDGE, width=edge)
    draw.polygon([(right - fold, top), (right - fold, top + fold), (right, top + fold)],
                 fill=FOLD, outline=PAGE_EDGE, width=edge)

    # Плашка вровень с листом (в мелких размерах — во всю ширину) и подпись.
    band = (0, 98 * u, big, 234 * u) if small else (left, 106 * u, right, 222 * u)
    draw.rectangle(band, fill=NAVY)
    draw_label(draw, band, (band[2] - band[0]) * (0.94 if small else 0.90))
    return image.resize((size, size), Image.Resampling.LANCZOS)


def draw_label(draw: ImageDraw.ImageDraw, box: tuple[float, float, float, float], width: float) -> None:
    """«.LAS» по центру плашки с лёгкой разрядкой. Точка — квадрат: точка шрифта в мелких размерах теряется."""
    height = box[3] - box[1]
    font_size = 600
    while True:
        font = ImageFont.truetype(str(FONT), font_size)
        font.set_variation_by_name("SemiBold")
        letters = [draw.textbbox((0, 0), letter, font=font) for letter in "LAS"]
        cap_height = letters[0][3] - letters[0][1]
        gap = font_size * 0.05
        dot = cap_height * 0.22
        total = dot + gap * 1.6 + sum(box_[2] - box_[0] for box_ in letters) + gap * 2
        if total <= width and cap_height <= height * 0.62:
            break
        font_size -= max(1, font_size // 40)

    x = (box[0] + box[2] - total) / 2
    baseline = (box[1] + box[3] + cap_height) / 2
    draw.rectangle((x, baseline - dot, x + dot, baseline), fill=WHITE)
    x += dot + gap * 1.6
    for letter, bbox in zip("LAS", letters):
        draw.text((x - bbox[0], baseline - cap_height - bbox[1]), letter, font=font, fill=WHITE)
        x += bbox[2] - bbox[0] + gap


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--preview", type=Path, help="сохранить лист со всеми размерами в PNG")
    args = parser.parse_args()

    images = [render(size) for size in SIZES]
    images[-1].save(TARGET, format="ICO", sizes=[(size, size) for size in SIZES], append_images=images[:-1])
    print(f"Иконка: {TARGET} ({', '.join(map(str, SIZES))} px)")

    if args.preview:
        width = sum(SIZES) + 12 * (len(SIZES) + 1)
        sheet = Image.new("RGBA", (width, 256 + 24), (255, 255, 255, 255))
        x = 12
        for size, image in zip(SIZES, images):
            sheet.alpha_composite(image, (x, 12 + 256 - size))
            x += size + 12
        sheet.save(args.preview)
        print(f"Лист: {args.preview}")


if __name__ == "__main__":
    main()
