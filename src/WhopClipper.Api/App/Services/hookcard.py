"""Render a hook overlay as a white rounded card with dark text.

Short-form clips title themselves with a thumbnail-style caption card - dark text on a white
rounded rectangle - rather than outlined text laid over the picture. FFmpeg's drawtext can only
draw a square-cornered box, so the card is produced here as a transparent PNG and overlaid.

Casing is used exactly as given. The reference style mixes Title Case with deliberate all-caps
emphasis ("Customer Gave Me FREE GAMING PC!!"), and once a string has been upper-cased there is
no way to tell an intentional shout from an ordinary short word.

Usage: hookcard.py <text-file> <output.png> --frame-width 1080 [--max-fraction 0.86]
                   [--font-size 62] [--font <path>]
Prints "done <width>x<height>" to stderr.
"""
import argparse
import os
import sys

from PIL import Image, ImageDraw, ImageFont

FONT_CANDIDATES = [
    r"C:\Windows\Fonts\segoeuib.ttf",
    r"C:\Windows\Fonts\arialbd.ttf",
    r"C:\Windows\Fonts\calibrib.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
]
TEXT_COLOR = (17, 17, 17, 255)
CARD_COLOR = (255, 255, 255, 255)
SHADOW_COLOR = (0, 0, 0, 70)
MAX_LINES = 2


def load_font(size, preferred=None):
    for path in ([preferred] if preferred else []) + FONT_CANDIDATES:
        if path and os.path.exists(path):
            try:
                return ImageFont.truetype(path, size)
            except OSError:
                continue
    return ImageFont.load_default()


def wrap(text, font, max_width, draw):
    """At most two lines, split as evenly as possible so the card looks deliberate."""
    if draw.textlength(text, font=font) <= max_width:
        return [text]
    words = text.split()
    best = None
    for split in range(1, len(words)):
        first = " ".join(words[:split])
        second = " ".join(words[split:])
        a, b = draw.textlength(first, font=font), draw.textlength(second, font=font)
        if max(a, b) <= max_width and (best is None or abs(a - b) < best[0]):
            best = (abs(a - b), [first, second])
    return best[1] if best else [text]


def render(text, out_path, frame_width=1080, max_fraction=0.86, start_size=62, font_path=None):
    text = " ".join(text.split())
    if not text:
        raise SystemExit("the hook text is empty")
    scratch = ImageDraw.Draw(Image.new("RGBA", (8, 8)))

    pad_x, pad_y, radius = 30, 18, 20
    max_text_width = int(frame_width * max_fraction) - 2 * pad_x

    size = start_size
    while size > 28:
        font = load_font(size, font_path)
        lines = wrap(text, font, max_text_width, scratch)
        if len(lines) <= MAX_LINES and all(
                scratch.textlength(line, font=font) <= max_text_width for line in lines):
            break
        size -= 2
    font = load_font(size, font_path)
    lines = wrap(text, font, max_text_width, scratch)

    ascent, descent = font.getmetrics()
    line_height, gap = ascent + descent, 6
    text_width = int(max(scratch.textlength(line, font=font) for line in lines))
    card_w = text_width + 2 * pad_x
    card_h = line_height * len(lines) + gap * (len(lines) - 1) + 2 * pad_y

    margin = 14  # keeps the drop shadow inside the canvas
    image = Image.new("RGBA", (card_w + margin * 2, card_h + margin * 2), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle([margin + 3, margin + 5, margin + card_w + 3, margin + card_h + 5],
                           radius=radius, fill=SHADOW_COLOR)
    draw.rounded_rectangle([margin, margin, margin + card_w, margin + card_h],
                           radius=radius, fill=CARD_COLOR)

    y = margin + pad_y
    for line in lines:
        width = scratch.textlength(line, font=font)
        draw.text((margin + (card_w - width) / 2, y), line, font=font, fill=TEXT_COLOR)
        y += line_height + gap

    image.save(out_path)
    return image.size


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("text_file")
    parser.add_argument("output")
    parser.add_argument("--frame-width", type=int, default=1080)
    parser.add_argument("--max-fraction", type=float, default=0.86)
    parser.add_argument("--font-size", type=int, default=62)
    parser.add_argument("--font", default=None)
    args = parser.parse_args()

    with open(args.text_file, encoding="utf-8") as handle:
        text = handle.read()
    width, height = render(text, args.output, args.frame_width, args.max_fraction,
                           args.font_size, args.font)
    print(f"done {width}x{height}", file=sys.stderr, flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
