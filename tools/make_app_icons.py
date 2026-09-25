#!/usr/bin/env python3
"""Generates the Kairo desktop app icons (kairo.ico, logo PNGs, tray icons) with the same
design as the browser extension (extension/tools/make_icons.py).

    python3 tools/make_app_icons.py
"""
import importlib.util
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("ext_icons", ROOT / "extension" / "tools" / "make_icons.py")
ext = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ext)

OUT = ROOT / "src" / "Kairo.App" / "Assets"
OUT.mkdir(parents=True, exist_ok=True)

ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
images = {size: ext.render(size) for size in ICO_SIZES}
images[256].save(OUT / "kairo.ico", sizes=[(s, s) for s in ICO_SIZES], append_images=[images[s] for s in ICO_SIZES if s != 256])
ext.render(256).save(OUT / "kairo-logo.png", optimize=True)
ext.render(64).save(OUT / "kairo-logo-64.png", optimize=True)

# Tray icon variant for the "paused" state: grayscale.
paused = {s: ext.render(s).convert("LA").convert("RGBA") for s in [16, 20, 24, 32, 48]}
paused[48].save(OUT / "kairo-paused.ico", sizes=[(s, s) for s in paused], append_images=[paused[s] for s in paused if s != 48])
for f in sorted(OUT.iterdir()):
    print(f.name, f.stat().st_size)
