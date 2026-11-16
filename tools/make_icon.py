"""Generates Assets/TimesheetBuddy.ico (hourglass). Run: python tools/make_icon.py (needs Pillow)."""
from pathlib import Path
from PIL import Image, ImageChops, ImageDraw

INK = (28, 36, 48, 255)        # #1C2430
SAND = (15, 110, 86, 255)      # #0F6E56
GLASS = (216, 243, 234, 255)   # #D8F3EA
SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 8  # supersampling factor


def glass_outline():
    """Hourglass bulb polygon in 256-unit space: convex bulbs meeting at a waist."""
    top, waist, bottom = 46, 128, 210
    half_top, half_waist = 70, 9
    steps = 32
    pts = []
    for i in range(steps + 1):
        t = i / steps
        pts.append((128 - (half_waist + (half_top - half_waist) * (1 - t ** 1.7)),
                    top + (waist - top) * t))
    for i in range(1, steps + 1):
        t = i / steps
        pts.append((128 - (half_waist + (half_top - half_waist) * (1 - (1 - t) ** 1.7)),
                    waist + (bottom - waist) * t))
    return pts + [(256 - x, y) for x, y in reversed(pts)]


def render(size):
    s = size * SS
    k = s / 256
    sc = lambda pts: [(x * k, y * k) for x, y in pts]
    px = 256 / size  # units per output pixel
    stroke = max(10, px * 1.1)
    stream_w = max(9, px * 1.3)
    bar_h = max(30, px * 2.2)

    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    outline = sc(glass_outline())
    inner = Image.new("L", (s, s), 0)
    ImageDraw.Draw(inner).polygon(outline, fill=255)
    img.paste(GLASS, (0, 0), inner)

    # sand: small heap left in the top bulb, big mound in the bottom
    region = Image.new("L", (s, s), 0)
    rd = ImageDraw.Draw(region)
    rd.polygon(sc([(40, 128), (216, 128), (216, 104), (40, 104)]), fill=255)
    rd.polygon(sc([(20, 256), (236, 256), (236, 182), (128, 160), (20, 182)]), fill=255)
    img.paste(SAND, (0, 0), ImageChops.multiply(region, inner))

    # falling stream through the waist
    d.rectangle(sc([(128 - stream_w / 2, 112), (128 + stream_w / 2, 170)]), fill=SAND)

    d.line(outline + outline[:2], fill=INK, width=round(stroke * k), joint="curve")

    # frame bars top and bottom
    snap = lambda v: round(v / px) * px  # crisp bar edges at small sizes
    r = bar_h * k * 0.35
    d.rounded_rectangle(sc([(snap(34), snap(50 - bar_h)), (snap(222), snap(50))]), radius=r, fill=INK)
    d.rounded_rectangle(sc([(snap(34), snap(206)), (snap(222), snap(206 + bar_h))]), radius=r, fill=INK)

    return img.resize((size, size), Image.LANCZOS)


def main():
    root = Path(__file__).resolve().parent.parent
    out = root / "Assets" / "TimesheetBuddy.ico"
    out.parent.mkdir(exist_ok=True)
    frames = [render(n) for n in SIZES]
    frames[-1].save(out, format="ICO", sizes=[(n, n) for n in SIZES],
                    append_images=frames[:-1])
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
