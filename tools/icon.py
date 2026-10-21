# Downloads Lifecycle icons, drawn with Pillow (py tools/icon.py from the repo root):
# docs/icon.png 256, src/DownloadsLifecycle/Assets/app.ico (16-256), tray-light.ico (dark glyph) and tray-dark.ico (white glyph).
from PIL import Image, ImageDraw

S = 1024  # drawn large, scaled down for anti-aliasing
TEAL = (0x0E, 0x7C, 0x74, 255)


def glyph(d, cx, cy, s, fill):
    """A down arrow dropping into an open tray, centred at (cx, cy), s = overall height."""
    t = s * 0.11                                   # stroke
    w = s * 0.86                                   # tray width
    top, mid, bottom = cy - s * 0.50, cy + s * 0.05, cy + s * 0.48
    d.rectangle([cx - t * 0.55, top, cx + t * 0.55, mid - s * 0.06], fill=fill)                       # shaft
    d.polygon([(cx - s * 0.24, mid - s * 0.16), (cx + s * 0.24, mid - s * 0.16), (cx, mid + s * 0.10)], fill=fill)  # head
    y = cy + s * 0.12                              # tray rim
    d.rounded_rectangle([cx - w / 2, y, cx - w / 2 + t, bottom], t / 2, fill=fill)                    # left wall
    d.rounded_rectangle([cx + w / 2 - t, y, cx + w / 2, bottom], t / 2, fill=fill)                    # right wall
    d.rounded_rectangle([cx - w / 2, bottom - t, cx + w / 2, bottom], t / 2, fill=fill)               # floor


def app_icon():
    im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([40, 40, S - 40, S - 40], 200, fill=TEAL)
    glyph(d, S * 0.45, S * 0.47, S * 0.60, (255, 255, 255, 255))
    # badge: amber clock, "lifetime"
    cx, cy, r = S * 0.74, S * 0.70, S * 0.17
    d.ellipse([cx - r - 22, cy - r - 22, cx + r + 22, cy + r + 22], fill=TEAL)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(0xF2, 0xA9, 0x3B, 255))
    w = int(r * 0.22)
    d.line([(cx, cy), (cx, cy - r * 0.62)], fill=(255, 255, 255, 255), width=w)
    d.line([(cx, cy), (cx + r * 0.45, cy + r * 0.18)], fill=(255, 255, 255, 255), width=w)
    d.ellipse([cx - w * 0.7, cy - w * 0.7, cx + w * 0.7, cy + w * 0.7], fill=(255, 255, 255, 255))
    return im


def tray_icon(color):
    im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    glyph(ImageDraw.Draw(im), S * 0.5, S * 0.5, S * 0.86, color)
    return im


app = app_icon()
app.resize((256, 256), Image.LANCZOS).save('docs/icon.png')
app.resize((256, 256), Image.LANCZOS).save('src/DownloadsLifecycle/Assets/app.ico', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (256, 256)])
tray_sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (48, 48)]
tray_icon((0x1C, 0x1C, 0x1C, 255)).resize((48, 48), Image.LANCZOS).save('src/DownloadsLifecycle/Assets/tray-light.ico', sizes=tray_sizes)
tray_icon((255, 255, 255, 255)).resize((48, 48), Image.LANCZOS).save('src/DownloadsLifecycle/Assets/tray-dark.ico', sizes=tray_sizes)
print('ok')
