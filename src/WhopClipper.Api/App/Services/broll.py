"""Footage measurement for the podcast-split edit style.

Two modes, both run on files already on disk:

  broll.py probe <media> <output.json> [--min-shot 1.0]
      Measures a downloaded stretch of candidate B-roll: where its camera cuts are, whether a
      broadcaster's logo or score bug sits in it, and which 1080x960-shaped crop keeps the picture
      while leaving the logo out. Writes
      {"width","height","still","motion","shots":[{"in","duration"}],
       "crop":{"x","y","w","h"},"blur":[{"x","y","w","h"}],"logoBoxes":N}

  broll.py topcrop <media> <output.json> --pieces a:b,c:d [--height-fraction 0.79]
      Frames the podcast camera for the top half. Each piece is split at camera cuts and every shot
      gets a fixed crop on the guest's face, moved so sponsor bugs fall outside it. Writes
      {"width","height","shots":[{"in","duration","crop":{...},"blur":[...],"face":bool}]}

Crop boxes are in source pixels. Blur boxes are in output pixels of the 1080x960 half frame, for
the renderer's delogo filter.

Why a crop rather than a blur first: the campaign brief bans every logo except its own watermark,
and a smeared box in the corner of a highlight still reads as "this was a TV broadcast". Moving the
crop loses nothing the viewer wanted; blurring is only the fallback when the bug sits mid-frame.
"""
import argparse
import json
import sys

TARGET_ASPECT = 1080.0 / 960.0
HALF_W, HALF_H = 1080, 960
ANALYSIS_WIDTH = 480
SAMPLE_STEP = 0.2


def open_capture(cv2, path):
    capture = cv2.VideoCapture(path)
    if not capture.isOpened():
        raise SystemExit(f"Could not open {path}")
    width = int(capture.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(capture.get(cv2.CAP_PROP_FRAME_HEIGHT))
    fps = capture.get(cv2.CAP_PROP_FPS) or 30.0
    count = capture.get(cv2.CAP_PROP_FRAME_COUNT) or 0
    duration = count / fps if count and fps else 0.0
    return capture, width, height, fps, duration


def sample(cv2, capture, start, end, width, height, step=SAMPLE_STEP):
    """Small colour frames every `step` seconds between start and end. Frames are grabbed in order
    after a single seek, because seeking an HLS section for every sample lands on the same keyframe
    again and again."""
    analysis_height = max(2, int(round(ANALYSIS_WIDTH * height / max(1, width))))
    capture.set(cv2.CAP_PROP_POS_MSEC, max(0.0, start) * 1000.0)
    samples = []
    next_time = start
    while True:
        if not capture.grab():
            break
        t = capture.get(cv2.CAP_PROP_POS_MSEC) / 1000.0
        if t > end:
            break
        if t + 1e-3 < next_time:
            continue
        ok, frame = capture.retrieve()
        if not ok:
            break
        samples.append((t, cv2.resize(frame, (ANALYSIS_WIDTH, analysis_height), interpolation=cv2.INTER_AREA)))
        next_time = t + step
    return samples


def find_cuts(cv2, np, samples):
    """A cut is a jump in the picture well above its own recent movement, with the brightness
    histogram changing too - fast action alone moves pixels without changing the histogram much."""
    diffs = []
    previous = None
    for t, frame in samples:
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        if previous is not None:
            mad = float(np.mean(cv2.absdiff(gray, previous)))
            corr = cv2.compareHist(cv2.calcHist([previous], [0], None, [32], [0, 256]),
                                   cv2.calcHist([gray], [0], None, [32], [0, 256]), cv2.HISTCMP_CORREL)
            diffs.append((t, mad, corr))
        previous = gray
    mads = [m for _, m, _ in diffs]
    cuts = []
    for index, (t, mad, corr) in enumerate(diffs):
        around = mads[max(0, index - 6):index] + mads[index + 1:index + 7]
        base = sorted(around)[len(around) // 2] if around else 0.0
        if mad > 14 and mad > 2.5 * max(base, 2.0) and (corr < 0.9 or mad > 40):
            if not cuts or t - cuts[-1] >= 0.3:
                cuts.append(t)
    return cuts


def refine_cut(cv2, np, capture, coarse, width, height):
    """Pins a coarse cut to the exact frame, so a crop change lands on the camera switch rather
    than up to a fifth of a second after it."""
    analysis_height = max(2, int(round(160 * height / max(1, width))))
    capture.set(cv2.CAP_PROP_POS_MSEC, max(0.0, coarse - SAMPLE_STEP - 0.05) * 1000.0)
    best_time, best_diff, previous = coarse, -1.0, None
    while True:
        if not capture.grab():
            break
        t = capture.get(cv2.CAP_PROP_POS_MSEC) / 1000.0
        if t > coarse + 0.05:
            break
        ok, frame = capture.retrieve()
        if not ok:
            break
        gray = cv2.cvtColor(cv2.resize(frame, (160, analysis_height)), cv2.COLOR_BGR2GRAY)
        if previous is not None:
            diff = float(np.mean(cv2.absdiff(gray, previous)))
            if diff > best_diff:
                best_time, best_diff = t, diff
        previous = gray
    return best_time


def overlay_boxes(cv2, np, frames, spans_cuts, locked=False):
    """Boxes that keep showing the same hard-edged pixels while the picture moves: network bugs,
    score strips, watermarks. Only boxes towards the frame edges count, because that is where
    broadcasters put them. When the frames cross a camera cut the background changes and anything
    that persists is certainly an overlay; within one shot the rules are stricter, since a
    locked-off camera makes its whole background persist too.

    Persistence, not stillness: a highlight reel drops the score bug for replays and close-ups, and
    a translucent bug lets the play show through, so its pixels are never still across the whole
    stretch. Measured on a broadcast Lakers-Bucks reel, the bug's text matched the median frame in
    57-64% of samples against 17% for the rest of the picture, while its standard deviation was as
    high as the court's."""
    if len(frames) < 4:
        return [], 0.0
    grays = np.stack([cv2.cvtColor(f, cv2.COLOR_BGR2GRAY).astype(np.int16) for f in frames])
    motion = float(np.mean(np.abs(np.diff(grays, axis=0))))
    median = np.median(grays, axis=0).astype(np.uint8)
    persistence = (np.abs(grays - median.astype(np.int16)[None]) < 12).mean(axis=0)
    edges = cv2.magnitude(cv2.Sobel(median, cv2.CV_32F, 1, 0), cv2.Sobel(median, cv2.CV_32F, 0, 1))
    mask = ((persistence >= 0.5) & (edges > 50)).astype(np.uint8)
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8))
    h, w = mask.shape
    count, _, stats, _ = cv2.connectedComponentsWithStats(mask)
    boxes = []
    # A full score strip runs to about 4% of the frame (the Lakers-Bucks bug was 3.4%); anything
    # bigger that persists is a letterbox bar, a title slate or the set itself, not a bug.
    # A locked-off podcast camera makes its whole set persist - the wall, the plants, the chair - so
    # there only a small, dense mark tucked into a corner can be a sponsor bug. Motion cannot decide
    # that when the frames come from separate pieces: the jumps between pieces read as movement.
    strict = locked or (not spans_cuts and motion < 4.0)
    max_area = 0.02 if strict else 0.06
    for index in range(1, count):
        x, y, bw, bh, area = (int(v) for v in stats[index])
        if area < 30 or bw * bh > max_area * w * h or bw < 6 or bh < 4:
            continue
        near_edge = x < 0.22 * w or x + bw > 0.78 * w or y < 0.22 * h or y + bh > 0.78 * h
        if not near_edge:
            continue
        if strict:
            # A still-camera interview: static edges are the room, not a bug. Only dense marks tucked
            # into a corner survive.
            in_corner = (x < 0.18 * w or x + bw > 0.82 * w) and (y < 0.18 * h or y + bh > 0.82 * h)
            density = float(mask[y:y + bh, x:x + bw].mean())
            if not in_corner or density < 0.3:
                continue
        boxes.append((x, y, bw, bh))
    return boxes, motion


def content_box(np, frames):
    """Letterbox and pillarbox bars belong to the frame but not to the picture. A bar is a line
    that stays near black and near uniform the whole time."""
    stack = np.stack([f.mean(axis=2) for f in frames]).astype(np.float32)
    mean = stack.mean(axis=0)
    h, w = mean.shape
    row_dark = (mean.mean(axis=1) < 20) & (mean.std(axis=1) < 6)
    col_dark = (mean.mean(axis=0) < 20) & (mean.std(axis=0) < 6)
    rows = np.where(~row_dark)[0]
    cols = np.where(~col_dark)[0]
    if len(rows) < h * 0.3 or len(cols) < w * 0.3:
        return 0, 0, w, h
    return int(cols[0]), int(rows[0]), int(cols[-1] - cols[0] + 1), int(rows[-1] - rows[0] + 1)


def is_dark(np, frame):
    """A frame that is nearly black and nearly flat: a fade, a black slate, or the first frames of a
    section cut before its keyframe, which decode as nothing."""
    gray = frame.mean(axis=2)
    return float(gray.mean()) < 22 and float(gray.std()) < 14


def subtract(interval, bad):
    """What is left of an interval once every bad stretch is taken out of it."""
    pieces = [interval]
    for bad_start, bad_end in bad:
        remaining = []
        for start, end in pieces:
            if bad_end <= start or bad_start >= end:
                remaining.append((start, end))
                continue
            if bad_start > start:
                remaining.append((start, bad_start))
            if bad_end < end:
                remaining.append((bad_end, end))
        pieces = remaining
    return pieces


def intersection(a, b):
    ax, ay, aw, ah = a
    bx, by, bw, bh = b
    x0, y0 = max(ax, bx), max(ay, by)
    x1, y1 = min(ax + aw, bx + bw), min(ay + ah, by + bh)
    return max(0.0, x1 - x0) * max(0.0, y1 - y0)


def choose_crop(boxes, area, focus=None, zooms=(1.0, 1.12, 1.25, 1.35), height_fraction=1.0, keep_focus=False, face_at=0.42):
    """Tries crops across the picture and a few punch-ins, and keeps the one that leaves the logos
    out while staying closest to the subject. Overlap dominates the score: any logo pixel left in
    costs more than moving off-centre."""
    cx, cy, cw, ch = area
    fx, fy = focus if focus is not None else (cx + cw / 2.0, cy + ch / 2.0)
    best = None
    for zoom in zooms:
        crop_h = ch * height_fraction / zoom
        crop_w = crop_h * TARGET_ASPECT
        if crop_w > cw:
            crop_w = cw
            crop_h = crop_w / TARGET_ASPECT
        # The subject's own position first, then sweeps across the frame.
        xs = [min(max(cx, fx - crop_w / 2.0), cx + cw - crop_w)]
        xs += [cx + (cw - crop_w) * k / 16.0 for k in range(17)]
        ys = [min(max(cy, fy - face_at * crop_h), cy + ch - crop_h)]
        ys += [cy + (ch - crop_h) * k / 4.0 for k in range(5)]
        for x in xs:
            for y in ys:
                rect = (x, y, crop_w, crop_h)
                # The guest is the point of the top half: no crop may push their face out of the
                # middle of the frame to dodge a logo. Whatever cannot be dodged gets blurred.
                if keep_focus and focus is not None and not (
                        0.3 * crop_w <= fx - x <= 0.7 * crop_w and 0.12 * crop_h <= fy - y <= 0.62 * crop_h):
                    continue
                overlap = sum(intersection(rect, box) for box in boxes)
                off_subject = abs((x + crop_w / 2.0) - fx) / cw + 0.5 * abs((y + face_at * crop_h) - fy) / ch
                score = overlap * 10.0 + off_subject * 40.0 + (zoom - 1.0) * 30.0
                if best is None or score < best[0]:
                    best = (score, rect, overlap)
    if best is None:
        # A face so close to the frame edge that no crop can centre it: frame it as well as possible.
        return choose_crop(boxes, area, focus, zooms, height_fraction, keep_focus=False, face_at=face_at)
    return best[1], best[2]


def blur_boxes(boxes, rect):
    """Logo boxes still inside the chosen crop, mapped onto the 1080x960 half frame."""
    x, y, w, h = rect
    out = []
    for bx, by, bw, bh in boxes:
        if intersection(rect, (bx, by, bw, bh)) <= 0:
            continue
        pad = 6
        ox = int((bx - x) / w * HALF_W) - pad
        oy = int((by - y) / h * HALF_H) - pad
        ow = int(bw / w * HALF_W) + 2 * pad
        oh = int(bh / h * HALF_H) + 2 * pad
        x0, y0 = max(0, ox), max(0, oy)
        x1, y1 = min(HALF_W, ox + ow), min(HALF_H, oy + oh)
        if x1 - x0 >= 2 and y1 - y0 >= 2:
            out.append({"x": x0, "y": y0, "w": x1 - x0, "h": y1 - y0})
    return out


def to_source(rect, scale, width, height):
    x, y, w, h = rect
    sw = int(round(w * scale / 2.0)) * 2
    sh = int(round(h * scale / 2.0)) * 2
    sw, sh = max(2, min(sw, width - (width % 2))), max(2, min(sh, height - (height % 2)))
    sx = int(round(x * scale))
    sy = int(round(y * scale))
    sx = max(0, min(sx, width - sw))
    sy = max(0, min(sy, height - sh))
    return {"x": sx, "y": sy, "w": sw, "h": sh}


def probe(args):
    import cv2
    import numpy as np

    capture, width, height, _, duration = open_capture(cv2, args.media)
    samples = sample(cv2, capture, 0.0, duration if duration > 0 else 1e9, width, height)
    capture.release()
    if not samples:
        raise SystemExit("No frames could be read from the footage.")
    end_time = samples[-1][0] + SAMPLE_STEP
    frames = [f for _, f in samples]
    cuts = find_cuts(cv2, np, samples)
    scale = width / float(ANALYSIS_WIDTH)

    boxes, motion = overlay_boxes(cv2, np, frames, spans_cuts=len(cuts) > 0)
    still = motion < 1.0
    if still:
        boxes = []
    area = content_box(np, frames)
    rect, _ = choose_crop(boxes, area)

    # Dark frames are never footage. A render once showed 1.5 seconds of black under the guest because
    # every shot started at 0.0 of its section: the first half second is skipped as keyframe warm-up,
    # shots are cut around each dark stretch, and footage that is mostly dark is not used at all.
    dark = [(t - SAMPLE_STEP, t + SAMPLE_STEP) for t, f in samples if is_dark(np, f)]
    dark_share = len(dark) / float(len(samples))
    bad = [(0.0, 0.5)] + dark
    shots = []
    bounds = [0.0] + cuts + [end_time]
    if dark_share < 0.5:
        for index in range(len(bounds) - 1):
            # Skip the frames either side of a cut: dissolves and flash frames live there.
            start = bounds[index] + (0.25 if index > 0 else 0.0)
            stop = bounds[index + 1] - (0.15 if index + 1 < len(bounds) - 1 else 0.0)
            for usable_start, usable_end in subtract((start, stop), bad):
                if usable_end - usable_start >= args.min_shot:
                    shots.append({"in": round(usable_start, 3), "duration": round(usable_end - usable_start, 3)})

    payload = {
        "width": width, "height": height, "still": bool(still), "motion": round(motion, 2),
        "darkShare": round(dark_share, 2),
        "shots": shots, "crop": to_source(rect, scale, width, height), "blur": blur_boxes(boxes, rect),
        "logoBoxes": len(boxes),
    }
    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump(payload, handle)
    print(f"done shots={len(shots)} logos={len(boxes)} still={still}", file=sys.stderr, flush=True)
    return 0


def topcrop(args):
    import cv2
    import numpy as np
    from framing import detect_faces

    cascades = [
        (cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_frontalface_default.xml"), False),
        (cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_profileface.xml"), False),
        (cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_profileface.xml"), True),
    ]
    pieces = []
    for part in args.pieces.split(","):
        start, end = (float(v) for v in part.split(":"))
        if end > start:
            pieces.append((start, end))
    if not pieces:
        raise SystemExit("No pieces were given.")

    capture, width, height, _, _ = open_capture(cv2, args.media)
    scale = width / float(ANALYSIS_WIDTH)
    raw_shots = []
    overlay_frames = []
    means = []
    for start, end in pieces:
        samples = sample(cv2, capture, start, end, width, height)
        if not samples:
            continue
        cuts = [refine_cut(cv2, np, capture, t, width, height) for t in find_cuts(cv2, np, samples)]
        bounds = [start] + [c for c in cuts if start + 0.1 < c < end - 0.1] + [end]
        for index in range(len(bounds) - 1):
            s, e = bounds[index], bounds[index + 1]
            frames = [f for t, f in samples if s <= t < e] or [min(samples, key=lambda x: abs(x[0] - s))[1]]
            raw_shots.append({"in": s, "duration": e - s, "frames": frames})
            means.append(np.mean(np.stack(frames).astype(np.float32), axis=0))
        overlay_frames.extend(f for _, f in samples[::2])
    capture.release()
    if not raw_shots:
        raise SystemExit("No frames could be read from the pieces.")

    # Sponsor bugs stay put through every camera switch; the set behind the guest does not. With at
    # least two different angles, anything that never moved is an overlay.
    distinct = any(float(np.mean(np.abs(means[i] - means[0]))) > 25 for i in range(1, len(means)))
    # A podcast's own sponsor bugs sit in the corners. Pooling a host angle with a guest angle is not
    # enough to tell the set from an overlay - the angle with more screen time makes its whole set
    # "persist" - and that blurred a guest's head, so the camera half only ever looks in the corners.
    boxes, _ = overlay_boxes(cv2, np, overlay_frames, spans_cuts=distinct, locked=True)
    area = (0, 0, ANALYSIS_WIDTH, int(round(ANALYSIS_WIDTH * height / max(1, width))))

    shots = []
    previous_focus = None
    for shot in raw_shots:
        picks = shot["frames"][:: max(1, len(shot["frames"]) // 6)][:6]
        found = [detect_faces(cascades, cv2.cvtColor(f, cv2.COLOR_BGR2GRAY)) for f in picks]
        found = [f for f in found if f is not None]
        if found:
            focus = (sorted(p[0] for p in found)[len(found) // 2], sorted(p[1] for p in found)[len(found) // 2])
        else:
            focus = previous_focus
        # The measured Double Coverage punch-in and no tighter, with the face a third of the way down so
        # the top of the head stays in: centring it at 42% cut off a seated guest's forehead.
        rect, _ = choose_crop(boxes, area, focus=focus, zooms=(1.0,), height_fraction=args.height_fraction,
                              keep_focus=True, face_at=0.3)
        shots.append({
            "in": round(shot["in"], 3), "duration": round(shot["duration"], 3),
            "crop": to_source(rect, scale, width, height), "blur": blur_boxes(boxes, rect), "face": bool(found),
        })
        previous_focus = focus if found else previous_focus

    # Flash frames and consecutive shots that ended up with the same box are joined, which keeps
    # the render graph to one input per real change of picture.
    merged = []
    for shot in shots:
        if merged:
            last = merged[-1]
            contiguous = abs(last["in"] + last["duration"] - shot["in"]) < 0.02
            same_box = all(abs(last["crop"][k] - shot["crop"][k]) <= 12 for k in ("x", "y", "w", "h"))
            if contiguous and (same_box or shot["duration"] < 0.35):
                last["duration"] = round(shot["in"] + shot["duration"] - last["in"], 3)
                continue
        merged.append(shot)

    payload = {"width": width, "height": height, "shots": merged, "logoBoxes": len(boxes)}
    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump(payload, handle)
    print(f"done shots={len(merged)} logos={len(boxes)}", file=sys.stderr, flush=True)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["probe", "topcrop"])
    parser.add_argument("media")
    parser.add_argument("output")
    parser.add_argument("--min-shot", type=float, default=1.0)
    parser.add_argument("--pieces", default="")
    parser.add_argument("--height-fraction", type=float, default=0.79)
    args = parser.parse_args()
    return probe(args) if args.mode == "probe" else topcrop(args)


if __name__ == "__main__":
    raise SystemExit(main())
