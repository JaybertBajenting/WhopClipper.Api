"""Picture helpers for the narrated-luxury style (the @mambafx.luxury edit).

  faces <spec.json> <out.json>     where the main face is at given moments of given files
  overlays <spec.json> <out.json>  the meme title, the red arrow and the skull as transparent PNGs
  sheet <spec.json> <out.json>     labelled frame rows of candidate montage shots, for an AI to look at
  drops <spec.json> <out.json>     where a song slams back in after a quiet stretch, for timing the ending to it
  sfx <spec.json> <out.json>       the opening bell and the hand-over camera shutter, as WAV files at playing level

Every spec is JSON so no path or title ever passes through a command line.
"""
import json
import math
import os
import sys

TEXT_FONTS = [r"C:\Windows\Fonts\segoeuib.ttf", r"C:\Windows\Fonts\arialbd.ttf",
              "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"]
EMOJI_FONTS = [r"C:\Windows\Fonts\seguiemj.ttf", "/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf"]
ARROW_RED = (228, 28, 28, 255)


def first_existing(paths):
    for path in paths:
        if os.path.exists(path):
            return path
    raise FileNotFoundError("None of these fonts exist: " + ", ".join(paths))


# ---------------------------------------------------------------- faces

def real_face(cv2, frame, box, eyes, width, height):
    """A Haar box counts as a face only when it looks like one: skin-toned, not in the bottom third of the frame (a
    hand or a belt buckle down there fooled it), and with eyes inside unless it fills a good part of the frame."""
    x, y, w, h, skin = box
    if skin < 0.3 or (y + h / 2) / height > 0.66:
        return False
    if w / width >= 0.12:
        return True
    gray = cv2.cvtColor(frame[y:y + h, x:x + w], cv2.COLOR_BGR2GRAY)
    return len(eyes.detectMultiScale(gray[: max(1, h * 2 // 3)], scaleFactor=1.1, minNeighbors=4,
                                     minSize=(max(8, w // 10), max(8, w // 10)))) > 0


def faces(spec, out):
    import cv2
    import numpy as np
    from broll import face_boxes, skin_focus
    cascades = [
        (cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_frontalface_default.xml"), False),
        (cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_frontalface_alt2.xml"), False),
        (cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_profileface.xml"), False),
        (cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_profileface.xml"), True),
    ]
    eyes = cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_eye_tree_eyeglasses.xml")
    result = {}
    for item in spec["items"]:
        capture = cv2.VideoCapture(item["file"])
        found = []
        for t in item["times"]:
            capture.set(cv2.CAP_PROP_POS_MSEC, max(0.0, float(t)) * 1000.0)
            ok, frame = capture.read()
            if not ok or frame is None:
                found.append({"t": t, "face": None, "focusX": None})
                continue
            height, width = frame.shape[:2]
            boxes = [b for b in face_boxes(cv2, np, cascades, frame) if real_face(cv2, frame, b, eyes, width, height)]
            face = None
            if boxes:
                x, y, w, h, _ = boxes[0]
                face = {"x": (x + w / 2) / width, "y": (y + h / 2) / height, "size": w / width}
            skin = skin_focus(cv2, np, frame)
            focus = face["x"] if face else (skin[0] / width if skin else None)
            found.append({"t": t, "face": face, "focusX": focus})
        capture.release()
        result[item["file"]] = found
    write(out, result)


# ---------------------------------------------------------------- overlays

def is_emoji(ch):
    code = ord(ch)
    return code >= 0x1F000 or 0x2600 <= code <= 0x27BF or code in (0xFE0F, 0x200D, 0x2B50, 0x2B55)


def runs(text):
    parts = []
    for ch in text:
        emoji = is_emoji(ch)
        if parts and parts[-1][1] == emoji:
            parts[-1][0] += ch
        else:
            parts.append([ch, emoji])
    return [(t, e) for t, e in parts if t]


def title(text, out, width=1080, max_width=1000, size=50):
    from PIL import Image, ImageDraw, ImageFont
    text_path, emoji_path = first_existing(TEXT_FONTS), first_existing(EMOJI_FONTS)
    scratch = ImageDraw.Draw(Image.new("RGBA", (8, 8)))

    def measure(line, fsize):
        tf, ef = ImageFont.truetype(text_path, fsize), ImageFont.truetype(emoji_path, fsize)
        return sum(scratch.textlength(t, font=ef if e else tf) for t, e in runs(line)), tf, ef

    lines = [text.strip()]
    while size > 36 and measure(lines[0], size)[0] > max_width:
        size -= 2
    if measure(lines[0], size)[0] > max_width:
        # Two lines, split at the space nearest the middle.
        words = text.split()
        best = min(range(1, len(words)), key=lambda i: abs(len(" ".join(words[:i])) - len(" ".join(words[i:]))))
        lines = [" ".join(words[:best]), " ".join(words[best:])]
    line_height = int(size * 1.3)
    image = Image.new("RGBA", (width, line_height * len(lines) + 12), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    for row, line in enumerate(lines):
        total, tf, ef = measure(line, size)
        x = (width - total) / 2
        y = 4 + row * line_height
        for part, emoji in runs(line):
            if emoji:
                # Colour emoji sit a little high against bold text; nudge them onto the baseline.
                draw.text((x, y + size * 0.06), part, font=ef, embedded_color=True)
                x += scratch.textlength(part, font=ef)
            else:
                draw.text((x + 2, y + 3), part, font=tf, fill=(0, 0, 0, 150))
                draw.text((x, y), part, font=tf, fill=(255, 255, 255, 255))
                x += scratch.textlength(part, font=tf)
    box = image.getbbox() or (0, 0, width, image.height)
    image = image.crop((0, max(0, box[1] - 4), width, min(image.height, box[3] + 4)))
    image.save(out)
    return {"path": out, "width": image.width, "height": image.height}


def arrow(out, length=250, shaft=34, head=92, angle=-38):
    """A thick red arrow pointing right, then turned by angle degrees (negative points up-right). Returns where
    its tip ends up in the saved image, which is what gets placed on the subject."""
    from PIL import Image, ImageDraw
    pad = 16
    canvas = Image.new("RGBA", (length + pad * 2, head + pad * 2), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)
    cy = pad + head / 2
    tip = (pad + length, cy)
    body = [(pad, cy - shaft / 2), (pad + length - head * 0.8, cy - shaft / 2), (pad + length - head * 0.8, pad),
            tip, (pad + length - head * 0.8, pad + head), (pad + length - head * 0.8, cy + shaft / 2), (pad, cy + shaft / 2)]
    draw.polygon(body, fill=ARROW_RED, outline=(90, 0, 0, 255))
    rotated = canvas.rotate(-angle, resample=Image.BICUBIC, expand=True)
    # Where the tip moved to: rotate it about the canvas centre, then shift by how much the canvas grew.
    cx, cyc = canvas.width / 2, canvas.height / 2
    rad = math.radians(angle)
    dx, dy = tip[0] - cx, tip[1] - cyc
    tx = cx + dx * math.cos(rad) - dy * math.sin(rad) + (rotated.width - canvas.width) / 2
    ty = cyc + dx * math.sin(rad) + dy * math.cos(rad) + (rotated.height - canvas.height) / 2
    rotated.save(out)
    return {"path": out, "width": rotated.width, "height": rotated.height, "tipX": round(tx), "tipY": round(ty)}


def skull(out, size=420):
    from PIL import Image, ImageDraw, ImageFont
    font = ImageFont.truetype(first_existing(EMOJI_FONTS), size)
    image = Image.new("RGBA", (int(size * 2), int(size * 2)), (0, 0, 0, 0))
    ImageDraw.Draw(image).text((size * 0.4, size * 0.3), "\U0001F480", font=font, embedded_color=True)
    image = image.crop(image.getbbox())
    image.save(out)
    return {"path": out, "width": image.width, "height": image.height}


def overlays(spec, out):
    result = {}
    if spec.get("title"):
        result["title"] = title(spec["title"]["text"], spec["title"]["out"])
    if spec.get("arrow"):
        result["arrow"] = arrow(spec["arrow"]["out"], angle=spec["arrow"].get("angle", -38))
    if spec.get("skull"):
        result["skull"] = skull(spec["skull"]["out"], spec["skull"].get("size", 420))
    write(out, result)


# ---------------------------------------------------------------- review sheets

def sheet(spec, out):
    import cv2
    import numpy as np
    rows = []
    for row in spec["rows"]:
        capture = cv2.VideoCapture(row["file"])
        frames = []
        for t in row["times"]:
            capture.set(cv2.CAP_PROP_POS_MSEC, max(0.0, float(t)) * 1000.0)
            ok, frame = capture.read()
            frames.append(cv2.resize(frame, (384, 216)) if ok and frame is not None else np.zeros((216, 384, 3), np.uint8))
        capture.release()
        label = np.zeros((216, 150, 3), np.uint8)
        cv2.putText(label, row["label"], (12, 120), cv2.FONT_HERSHEY_SIMPLEX, 1.1, (255, 255, 255), 2)
        rows.append(np.hstack([label] + frames))
    paths = []
    for first in range(0, len(rows), 5):
        path = os.path.abspath(f"{spec['outPrefix']}-{first // 5 + 1}.jpg")
        cv2.imwrite(path, np.vstack(rows[first:first + 5]), [cv2.IMWRITE_JPEG_QUALITY, 82])
        paths.append(path)
    write(out, {"sheets": paths})


# ---------------------------------------------------------------- music drops

def drops(spec, out):
    """Where a song slams back in after a quiet stretch. The reference times every clip so its last word lands on
    the drop, and the skull freeze is the drop. A drop is a rise of at least 8 dB between the half second before a
    point and the half second after it; the time returned is where the level first comes within 3 dB of where it
    settles."""
    import subprocess
    import numpy as np
    hop = 0.05
    rate = 22050
    raw = subprocess.run([spec["ffmpeg"], "-v", "error", "-i", spec["file"], "-ac", "1", "-ar", str(rate), "-f", "s16le", "-"],
                         capture_output=True).stdout
    data = np.frombuffer(raw, np.int16).astype(np.float32) / 32768
    step = int(rate * hop)
    frames = data[: len(data) // step * step].reshape(-1, step)
    if len(frames) < 40:
        write(out, {"drops": [], "seconds": len(data) / rate})
        return
    db = 20 * np.log10(np.sqrt((frames ** 2).mean(axis=1)) + 1e-6)
    n = int(0.5 / hop)
    total = np.cumsum(np.insert(db, 0, 0))
    after = (total[2 * n:] - total[n:-n]) / n
    before = (total[n:-n] - total[:-2 * n]) / n
    jump = after - before
    found = []
    for i in np.argsort(-jump):
        if jump[i] < 8:
            break
        centre = i + n
        if any(abs(centre * hop - d["t"]) < 2 for d in found):
            continue
        # The exact moment: the first frame that is within 3 dB of the level the song settles at over the next
        # second. NO BATIDAO comes back in two steps (a beat at 53.85 s, full weight at 54.2 s); a threshold part of
        # the way up caught the first step, and the slam landed 0.4 s into a 0.6 s freeze.
        # Counted from the quietest frame just before it, so the loud tail before a short dip is not mistaken for it.
        settled = float(np.percentile(db[centre:centre + 2 * n], 90))
        onset = centre
        low = max(0, centre - n - 2)
        quietest = low + int(np.argmin(db[low:centre + 1]))
        for k in range(quietest, min(len(db), centre + 2 * n)):
            if db[k] >= settled - 3:
                onset = k
                break
        # level: how loud the song is once it has dropped, so every song's drop can be played at the same loudness.
        level = float(db[onset:onset + n + 2].mean())
        found.append({"t": round(onset * hop, 2), "rise": round(float(jump[i]), 1), "level": round(level, 1)})
    write(out, {"drops": sorted(found, key=lambda d: d["t"]), "seconds": round(len(data) / rate, 2)})


# ---------------------------------------------------------------- sound effects

def sfx(spec, out):
    """The two sounds the reference lays over its music, written as WAV files at the level they should play.

    ding: the bell on the first frame, with the arrow. Measured on 9 shorts it is almost a pure tone at 2642 Hz that
    starts at 0.00 s, peaks within 0.03 s, falls 20 dB in about 0.6 s, and is about as loud as the narrator.
    shutter: a camera shutter on the white flash where the narrator hands over, 0.1-0.2 s long and some 6 dB above
    the narrator. Each is taken from the sound library ("source") when there is one; otherwise the bell is that tone
    and the shutter a short bright double click."""
    import subprocess
    import wave
    import numpy as np
    rate = 48000
    result = {}

    def save(path, samples):
        # Two identical channels: a mono file is turned down 3 dB when the renderer spreads it to stereo.
        data = (np.clip(samples, -1, 1) * 32767).astype(np.int16)
        with wave.open(path, "wb") as handle:
            handle.setnchannels(2)
            handle.setsampwidth(2)
            handle.setframerate(rate)
            handle.writeframes(np.repeat(data, 2).tobytes())

    step = int(rate * 0.01)

    def loudest(sound):
        return max(np.sqrt((sound[i:i + step] ** 2).mean()) for i in range(0, max(1, len(sound) - step), step))

    def library(source, seconds, fade_seconds):
        """A sound from the library, cut to start on its main hit. Recordings open on silence, and a shutter often has
        a faint wind-up click a third of a second before the real one; kept, the loud part landed after the flash and
        under the first words of the moment."""
        if not source or not os.path.exists(source):
            return None
        raw = subprocess.run([spec["ffmpeg"], "-v", "error", "-i", source, "-vn", "-ac", "1", "-ar", str(rate), "-f", "s16le", "-"],
                             capture_output=True).stdout
        sound = np.frombuffer(raw, np.int16).astype(np.float32) / 32768
        level = np.array([np.sqrt((sound[i:i + step] ** 2).mean()) for i in range(0, max(0, len(sound) - step), step)])
        if not len(level) or level.max() <= 0:
            return None
        first = int(np.argmax(level > level.max() * 10 ** (-12 / 20)))
        sound = sound[max(0, (first - 1) * step):][: int(rate * seconds)].copy()
        fade = min(int(rate * fade_seconds), len(sound))
        if fade:
            sound[-fade:] *= np.linspace(1, 0, fade)
        return sound if len(sound) >= rate * 0.03 else None

    if spec.get("ding"):
        sound = library(spec["ding"].get("source"), 1.6, 0.2)
        if sound is None:
            t = np.arange(int(rate * 1.6)) / rate
            sound = np.sin(2 * np.pi * 2642 * t) * np.exp(-t / 0.26)
            sound += 0.012 * np.sin(2 * np.pi * 7064 * t) * np.exp(-t / 0.06)   # the faint overtone two shorts show
            sound *= np.minimum(1, t / 0.003)                                    # a 3 ms strike, not a click
        # Loudest 10 ms at -18 dB RMS: about the narrator's level, as the reference has it.
        save(spec["ding"]["out"], sound * (10 ** (-18 / 20) / (loudest(sound) + 1e-9)))
        result["ding"] = spec["ding"]["out"]
    if spec.get("shutter"):
        sound = library(spec["shutter"].get("source"), 0.4, 0.05)
        if sound is None:
            rng = np.random.default_rng(7)
            t = np.arange(int(rate * 0.2)) / rate
            click = rng.standard_normal(len(t)) * (np.exp(-t / 0.012) + 0.7 * np.exp(-np.maximum(0, t - 0.09) / 0.015) * (t >= 0.09))
            sound = np.diff(click, prepend=0)                                 # tilt it bright, like a shutter
        # Loudest 10 ms at -10 dB RMS, which is where the reference's sits.
        save(spec["shutter"]["out"], sound * (10 ** (-10 / 20) / (loudest(sound) + 1e-9)))
        result["shutter"] = spec["shutter"]["out"]
    write(out, result)


def write(path, value):
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(value, handle)


def main():
    modes = {"faces": faces, "overlays": overlays, "sheet": sheet, "drops": drops, "sfx": sfx}
    if len(sys.argv) != 4 or sys.argv[1] not in modes:
        print(__doc__, file=sys.stderr)
        return 2
    with open(sys.argv[2], encoding="utf-8") as handle:
        spec = json.load(handle)
    modes[sys.argv[1]](spec, sys.argv[3])
    return 0


if __name__ == "__main__":
    sys.exit(main())
