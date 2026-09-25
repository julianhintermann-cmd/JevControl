#!/usr/bin/env python3
"""Generates the Kairo Browser Bridge icons (16/32/48/128 px) with Pillow.

Design: rounded square with a deep indigo -> violet diagonal gradient (#4F46E5 -> #7C3AED)
and a white stylized "K" whose upper arm ends in a small spark (spark only from 32 px up).

Everything is drawn geometrically (no fonts), supersampled and downscaled, so the output is
reproducible on any machine:

    python3 extension/tools/make_icons.py
"""

from pathlib import Path

from PIL import Image, ImageChops, ImageDraw

SIZES = (16, 32, 48, 128)
SUPERSAMPLE = 16  # draw at 16x and downscale with LANCZOS for smooth edges
START = (0x4F, 0x46, 0xE5)  # indigo-600
END = (0x7C, 0x3A, 0xED)  # violet-600
WHITE = (255, 255, 255, 255)

OUT_DIR = Path(__file__).resolve().parent.parent / "icons"


def lerp(a: int, b: int, t: float) -> int:
    return round(a + (b - a) * t)


def gradient(size: int) -> Image.Image:
    """Diagonal gradient from the top-left (START) to the bottom-right (END)."""
    img = Image.new("RGBA", (size, size))
    px = img.load()
    denom = 2 * (size - 1) or 1
    for y in range(size):
        for x in range(size):
            t = (x + y) / denom
            px[x, y] = (lerp(START[0], END[0], t), lerp(START[1], END[1], t), lerp(START[2], END[2], t), 255)
    return img


def stroke(draw: ImageDraw.ImageDraw, p1, p2, width: float) -> None:
    """Line with round caps."""
    draw.line([p1, p2], fill=WHITE, width=round(width))
    r = width / 2
    for (x, y) in (p1, p2):
        draw.ellipse([x - r, y - r, x + r, y + r], fill=WHITE)


def spark(draw: ImageDraw.ImageDraw, cx: float, cy: float, r: float) -> None:
    """Four-pointed star (concave diamond)."""
    k = r * 0.28
    pts = [
        (cx, cy - r), (cx + k, cy - k), (cx + r, cy), (cx + k, cy + k),
        (cx, cy + r), (cx - k, cy + k), (cx - r, cy), (cx - k, cy - k),
    ]
    draw.polygon(pts, fill=WHITE)


def render(size: int) -> Image.Image:
    s = size * SUPERSAMPLE
    small = size <= 16
    margin = 0 if size <= 32 else s * 0.0625
    radius = (s - 2 * margin) * 0.24

    # Rounded-square mask filled with the gradient.
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, s - 1 - margin, s - 1 - margin], radius=radius, fill=255)
    img = gradient(s)
    img.putalpha(mask)

    glyph = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(glyph)
    inner = s - 2 * margin

    def p(x: float, y: float):
        return (margin + x * inner, margin + y * inner)

    w = inner * (0.15 if small else 0.12)
    top, bottom = (0.25, 0.75) if small else (0.27, 0.73)
    stem_x = 0.33 if small else 0.34
    stroke(d, p(stem_x, top), p(stem_x, bottom), w)  # stem
    stroke(d, p(stem_x + 0.03, 0.53), p(0.66, top), w)  # upper arm
    stroke(d, p(0.47, 0.46), p(0.69, bottom), w)  # lower arm
    if not small:
        spark(d, *p(0.79, 0.2), inner * 0.1)

    img = Image.alpha_composite(img, glyph)
    # Keep the glyph inside the rounded square.
    alpha = ImageChops.multiply(img.getchannel("A"), mask)
    img.putalpha(alpha)
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    for size in SIZES:
        path = OUT_DIR / f"icon{size}.png"
        render(size).save(path, optimize=True)
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
