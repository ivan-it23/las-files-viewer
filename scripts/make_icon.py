"""Иконка «Просмотрщика LAS»: значок файла — белый лист с загнутым углом и
синей полосой с подписью «.LAS», как значки типов файлов в Windows.

Пересобирает LasViewer/Assets/LasViewer.ico со всеми размерами Windows.
Нужен Pillow (только разработчику, в программу не входит):

    python scripts/make_icon.py [--preview файл.png]

Каждый размер рисуется отдельно: в 16–24 px полоса с подписью шире листа и
занимает больше высоты, а контур тоньше — иначе подпись не читается.
"""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "LasViewer" / "Assets" / "LasViewer.ico"
FONT = Path(r"C:\Windows\Fonts\seguibl.ttf")  # Segoe UI Black

BLUE = (0x1A, 0x8F, 0xDA, 255)
WHITE = (255, 255, 255, 255)
PAGE_EDGE = (0x88, 0x87, 0x80, 255)
FOLD = (0xD3, 0xD1, 0xC7, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SUPERSAMPLE = 8


def render(size: int) -> Image.Image:
    big = size * SUPERSAMPLE
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    small = size <= 24

    # Лист с загнутым правым верхним углом.
    side = round(big * (0.12 if small else 0.10))
    fold = round(big * (0.22 if small else 0.24))
    left, top, right, bottom = side, round(big * 0.03), big - side, big - round(big * 0.03)
    edge = max(SUPERSAMPLE, round(big * (0.03 if small else 0.02)))
    draw.polygon([(left, top), (right - fold, top), (right, top + fold), (right, bottom), (left, bottom)],
                 fill=WHITE, outline=PAGE_EDGE, width=edge)
    draw.polygon([(right - fold, top), (right - fold, top + fold), (right, top + fold)], fill=FOLD, outline=PAGE_EDGE, width=edge)

    # Полоса с подписью выходит за края листа — как у значков типов файлов.
    band = (0 if small else left - round(big * 0.06),
            round(big * (0.44 if small else 0.50)),
            big if small else right + round(big * 0.06),
            round(big * (0.86 if small else 0.84)))
    draw.rounded_rectangle(band, radius=round(big * 0.06), fill=BLUE)
    draw_label(draw, band, (band[2] - band[0]) * (0.80 if small else 0.58))
    return image.resize((size, size), Image.Resampling.LANCZOS)


def draw_label(draw: ImageDraw.ImageDraw, box: tuple[float, float, float, float], width: float) -> None:
    """«.LAS» по центру полосы. Точка — круг: точка шрифта в мелких размерах теряется."""
    font_size = 400
    font = ImageFont.truetype(str(FONT), font_size)
    while draw.textbbox((0, 0), "LAS", font=font)[2] > width:
        font_size -= max(1, font_size // 50)
        font = ImageFont.truetype(str(FONT), font_size)

    word = draw.textbbox((0, 0), "LAS", font=font)
    cap_height = word[3] - word[1]
    dot = cap_height * 0.31
    gap = cap_height * 0.08
    total = dot + gap + (word[2] - word[0])
    x = (box[0] + box[2] - total) / 2
    baseline = (box[1] + box[3] + cap_height) / 2
    draw.ellipse((x, baseline - dot, x + dot, baseline), fill=WHITE)
    draw.text((x + dot + gap - word[0], (box[1] + box[3] - cap_height) / 2 - word[1]), "LAS", font=font, fill=WHITE)


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
