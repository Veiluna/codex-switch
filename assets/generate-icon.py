"""Render the small SVG subset used by CodexSwitch.svg. Requires Pillow 12.x.

Run from any directory: python assets/generate-icon.py
The SVG is the sole geometry/color source; ICO frames retain RGBA transparency.
"""
from pathlib import Path
from io import BytesIO
import struct
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parent
SIZES = (16, 24, 32, 48, 64, 128, 256)
NS = "{http://www.w3.org/2000/svg}"


def render(size):
    source = ET.parse(ROOT / "CodexSwitch.svg").getroot()
    scale = size * 8 / 24
    image = Image.new("RGBA", (size * 8, size * 8))
    draw = ImageDraw.Draw(image)
    rect = source.find(NS + "rect")
    x, y, w, h, radius = (float(rect.get(k)) * scale for k in ("x", "y", "width", "height", "rx"))
    draw.rounded_rectangle((x, y, x + w, y + h), radius, fill=rect.get("fill"))
    group = source.find(NS + "g")
    # A slightly heavier 16px stroke keeps both arrowheads open and legible.
    stroke = (2.5 if size == 16 else float(group.get("stroke-width"))) * scale
    for polyline in group:
        points = [tuple(float(n) * scale for n in p.split(",")) for p in polyline.get("points").split()]
        color = polyline.get("stroke")
        draw.line(points, fill=color, width=round(stroke), joint="curve")
        for px, py in points:
            draw.ellipse((px - stroke / 2, py - stroke / 2, px + stroke / 2, py + stroke / 2), fill=color)
    image = image.resize((size, size), Image.Resampling.LANCZOS)
    # Drop imperceptible Lanczos fringe outside the rounded tile.
    alpha = image.getchannel("A").point(lambda value: 0 if value < 4 else value)
    image.putalpha(alpha)
    for corner in ((0, 0), (size - 1, 0), (0, size - 1), (size - 1, size - 1)):
        image.putpixel(corner, (0, 0, 0, 0))
    return image


if __name__ == "__main__":
    frames = [render(size) for size in SIZES]
    ico_path = ROOT / "CodexSwitch.ico"
    frames[-1].save(ico_path, sizes=[(s, s) for s in SIZES], append_images=frames[:-1], bitmap_format="bmp")
    # Keep classic BMP frames for small Shell/.NET consumers; compress only 256px.
    data = bytearray(ico_path.read_bytes())
    large_entry = 6 + (len(SIZES) - 1) * 16
    offset = struct.unpack_from("<I", data, large_entry + 12)[0]
    assert data[large_entry:large_entry + 2] == b"\0\0"
    png = BytesIO()
    frames[-1].save(png, format="PNG")
    struct.pack_into("<I", data, large_entry + 8, len(png.getvalue()))
    ico_path.write_bytes(data[:offset] + png.getvalue())
    with Image.open(ROOT / "CodexSwitch.ico") as decoded:
        assert decoded.ico.sizes() == {(s, s) for s in SIZES}
        for size, expected in zip(SIZES, frames):
            actual = decoded.ico.getimage((size, size)).convert("RGBA")
            assert actual.tobytes() == expected.tobytes(), f"ICO frame differs: {size}"
            assert all(actual.getpixel(p)[3] == 0 for p in ((0, 0), (size - 1, 0), (0, size - 1), (size - 1, size - 1)))
    print("ICO decoded: 16/24/32/48/64/128/256; RGBA roundtrip and transparent corners passed")
