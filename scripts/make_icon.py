"""Иконка «Просмотрщика LAS»: белая надпись LAS на синей плитке.

Пересобирает LasViewer/Assets/LasViewer.ico со всеми размерами Windows.
Нужен Pillow (только разработчику, в программу не входит):

    python scripts/make_icon.py [--preview файл.png]

Каждый размер рисуется отдельно: в 16–24 px надпись крупнее относительно
плитки, а углы скруглены меньше — иначе буквы сливаются.
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
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SUPERSAMPLE = 8


def render(size: int) -> Image.Image:
    big = size * SUPERSAMPLE
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    small = size <= 24
    margin = 0 if small else round(big * 2 / 64)
    radius = round(big * (0.14 if small else 0.19))
    draw.rounded_rectangle((margin, margin, big - margin - 1, big - margin - 1), radius=radius, fill=BLUE)

    # Надпись занимает почти всю ширину плитки: в малых размерах — больше.
    target_width = (big - 2 * margin) * (0.86 if small else 0.74)
    font_size = big
    font = ImageFont.truetype(str(FONT), font_size)
    while font.getbbox("LAS")[2] - font.getbbox("LAS")[0] > target_width:
        font_size -= max(1, font_size // 50)
        font = ImageFont.truetype(str(FONT), font_size)

    left, top, right, bottom = draw.textbbox((0, 0), "LAS", font=font)
    x = (big - (right - left)) / 2 - left
    y = (big - (bottom - top)) / 2 - top
    draw.text((x, y), "LAS", font=font, fill=WHITE)
    return image.resize((size, size), Image.Resampling.LANCZOS)


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
