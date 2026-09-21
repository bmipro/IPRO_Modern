"""Share cards, 1200x627 (the size LinkedIn asks for), one per public page.

Built from what the brand already has: the logo (made for a white ground), the navy and the orange
accent of the home page, and each page's own headline and description.

Run `python ops/share-cards/make_share_cards.py` to regenerate (needs Pillow; on Windows it uses
Segoe UI, with Arial as the fallback). The designer can replace the three PNGs under the same names:
the pages name them in their og:image tags (Views/Home/Index, Accountants, Mortgage), and
PublicFrontDoor509Tests checks each is a 1200x627 PNG.

The owner's rule for anything a person reads: the brand domains are written iProAdvisers.com,
iProAccountants.com, iProMortgages.com -- the capitals separate the words (2026-09-21).
"""
import os
from PIL import Image, ImageDraw, ImageFont

# The repository root: this file lives in ops/share-cards/.
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
LOGO = os.path.join(ROOT, "src", "IPRO.Web", "wwwroot", "images", "ipro-advisers-logo.png")
OUT = os.path.join(ROOT, "src", "IPRO.Web", "wwwroot", "images", "share")
W, H = 1200, 627
NAVY, ACCENT, WHITE, SOFT = (23, 59, 85), (208, 106, 53), (255, 255, 255), (201, 216, 226)
FONTS = r"C:\Windows\Fonts"

def font(names, size):
    for name in names:
        path = os.path.join(FONTS, name)
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    raise SystemExit("no font found among %s" % (names,))

BOLD = ["seguisb.ttf", "segoeuib.ttf", "arialbd.ttf"]
REGULAR = ["segoeui.ttf", "arial.ttf"]

def balanced(draw, text, fnt, width):
    # Same number of lines as a plain wrap, but with the widths evened out: no one-word last line.
    lines = wrap(draw, text, fnt, width)
    narrow = width
    while narrow > 200:
        trial = wrap(draw, text, fnt, narrow - 20)
        if len(trial) > len(lines):
            break
        narrow -= 20
        lines = trial
    return lines

def wrap(draw, text, fnt, width):
    lines, line = [], ""
    for word in text.split():
        trial = (line + " " + word).strip()
        if draw.textlength(trial, font=fnt) <= width:
            line = trial
        else:
            lines.append(line)
            line = word
    lines.append(line)
    return lines

def card(filename, headline, sub, domain):
    img = Image.new("RGB", (W, H), WHITE)
    draw = ImageDraw.Draw(img)

    # The logo's soft shadow lives in its alpha channel: composite it over the white ground, never
    # flatten it (flattened, the shadow turns into a solid black slab around the letters).
    logo = Image.open(LOGO).convert("RGBA")
    lw = 760
    lh = round(logo.height * lw / logo.width)
    logo = logo.resize((lw, lh), Image.LANCZOS)
    band_top = 300
    img.paste(logo, ((W - lw) // 2, (band_top - lh) // 2 + 6), logo)

    draw.rectangle([0, band_top, W, H], fill=NAVY)
    draw.rectangle([0, band_top, W, band_top + 8], fill=ACCENT)

    margin = 84
    head_font = font(BOLD, 50)
    lines = balanced(draw, headline, head_font, W - 2 * margin)
    if len(lines) > 2:
        head_font = font(BOLD, 44)
        lines = balanced(draw, headline, head_font, W - 2 * margin)
    assert len(lines) <= 2, (filename, lines)
    y = band_top + 40
    for line in lines:
        draw.text((margin, y), line, font=head_font, fill=WHITE)
        y += head_font.size + 12

    sub_font = font(REGULAR, 28)
    sub_lines = balanced(draw, sub, sub_font, W - 2 * margin)
    assert len(sub_lines) <= 2, (filename, sub_lines)
    y += 8
    for line in sub_lines:
        draw.text((margin, y), line, font=sub_font, fill=SOFT)
        y += sub_font.size + 8

    domain_font = font(BOLD, 30)
    draw.text((margin, H - 66), domain, font=domain_font, fill=WHITE)
    dw = draw.textlength(domain, font=domain_font)
    draw.rectangle([margin, H - 24, margin + dw, H - 20], fill=ACCENT)

    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, filename)
    img.save(path, "PNG", optimize=True)
    print(filename, os.path.getsize(path), "bytes", [l for l in lines], sub_lines)

card("ipro-advisers.png",
     "Website, client records, follow-ups and newsletters",
     "One system, one login, one bill for Canadian insurance, financial, accounting and mortgage professionals",
     "iProAdvisers.com")
card("ipro-accountants.png",
     "Websites and client management for accountants in Canada",
     "A professional website, follow-ups and newsletters for Canadian accountants and bookkeepers",
     "iProAccountants.com")
card("ipro-mortgages.png",
     "Websites and client management for mortgage advisers in Canada",
     "A professional website, follow-ups and newsletters for Canadian mortgage advisers and brokers",
     "iProMortgages.com")
