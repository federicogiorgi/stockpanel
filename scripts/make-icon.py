"""Draws the stockpanel icon: a dark rounded tile with a rising green price line over a soft area,
a dashed reference level and a red dip. Writes src/StockPanel/stockpanel.ico (16-256 px).
Needs Pillow: pip install pillow"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
S = 1024


def draw():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([24, 24, S - 24, S - 24], radius=200, fill=(20, 22, 26, 255), outline=(70, 74, 80, 255), width=12)

    pts = [(150, 640), (260, 560), (360, 610), (450, 700), (540, 600), (640, 470), (730, 500), (870, 300)]
    ref = 640
    # dashed reference line (previous close)
    for x in range(140, 890, 70):
        d.line([(x, ref), (x + 36, ref)], fill=(150, 150, 150, 255), width=14)
    # area under the line, green above the reference, red below
    area = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    a = ImageDraw.Draw(area)
    a.polygon(pts + [(870, ref), (150, ref)], fill=(52, 209, 122, 90))
    mask_up = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask_up).rectangle([0, 0, S, ref], fill=255)
    img.paste(area, (0, 0), Image.composite(area, Image.new("RGBA", (S, S)), mask_up).split()[3])
    down = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(down).polygon(pts + [(870, ref), (150, ref)], fill=(255, 90, 90, 110))
    mask_dn = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask_dn).rectangle([0, ref, S, S], fill=255)
    img.paste(down, (0, 0), Image.composite(down, Image.new("RGBA", (S, S)), mask_dn).split()[3])
    d = ImageDraw.Draw(img)
    d.line(pts, fill=(52, 209, 122, 255), width=48, joint="curve")
    r = 46
    x, y = pts[-1]
    d.ellipse([x - r, y - r, x + r, y + r], fill=(255, 255, 255, 255))
    return img


def main():
    big = draw()
    sizes = [16, 24, 32, 48, 64, 128, 256]
    out = ROOT / "src" / "StockPanel" / "stockpanel.ico"
    big.resize((256, 256), Image.LANCZOS).save(out, sizes=[(s, s) for s in sizes])
    big.resize((256, 256), Image.LANCZOS).save(ROOT / "docs" / "icon.png")
    print("wrote", out)


if __name__ == "__main__":
    main()
