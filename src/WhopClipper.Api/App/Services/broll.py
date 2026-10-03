"""Footage measurement for the podcast-split edit style.

Two modes, both run on files already on disk:

  broll.py probe <media> <output.json> [--min-shot 1.0]
      Measures a downloaded stretch of candidate B-roll: where its camera cuts are, whether a
      broadcaster's logo or score bug sits in it, and which 1080x960-shaped crop keeps the picture
      while leaving the logo out. Writes
      {"width","height","still","motion","shots":[{"in","duration"}],
       "crop":{"x","y","w","h"},"blur":[{"x","y","w","h"}],"logoBoxes":N}

  broll.py topcrop <media> <output.json> --pieces a:b,c:d [--height-fraction 0.79] [--seat 0 --seats 2]
      Frames the podcast camera for the top half. Each piece is split at camera cuts and each camera
      angle gets one fixed crop, top edge kept: on the teller's seat when --seat is given, else on the
      person seen most. Writes
      {"width","height","shots":[{"in","duration","crop":{...},"blur":[...],"face":bool}]}

Crop boxes are in source pixels. Blur boxes are in output pixels of the 1080x960 half frame, for
the renderer's delogo filter.

Why a crop rather than a blur first: the campaign brief bans every logo except its own watermark,
and a smeared box in the corner of a highlight still reads as "this was a TV broadcast". Moving the
crop loses nothing the viewer wanted; blurring is only the fallback when the bug sits mid-frame.
"""
import argparse
import json
import math
import os
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


def choose_crop(boxes, area, focus=None, zooms=(1.0, 1.12, 1.25, 1.35), height_fraction=1.0, keep_focus=False, face_at=0.42,
                top_edge=False):
    """Tries crops across the picture and a few punch-ins, and keeps the one that leaves the logos
    out while staying closest to the subject. Overlap dominates the score: any logo pixel left in
    costs more than moving off-centre. top_edge keeps the frame's top edge, as the reference channel's
    camera half always does: a crop that slid down to a seated guest's face on a wide shot brought the
    episode's DraftKings bug in from the bottom corner, which the overlay detector had missed."""
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
        if top_edge:
            ys = [cy]
        else:
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
        return choose_crop(boxes, area, focus, zooms, height_fraction, keep_focus=False, face_at=face_at, top_edge=top_edge)
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

    # Review the same short intervals the renderer can consume, not an arbitrary frame from a
    # long search result. Each strip shows the cropped beginning, middle and ending of ONE take.
    review_shots = []
    for shot in shots:
        count = max(1, math.ceil(shot["duration"] / 2.2))
        length = shot["duration"] / count
        if length < 0.5:
            continue
        for part in range(count):
            review_shots.append({"in": round(shot["in"] + part * length, 3),
                                 "duration": round(length, 3)})
    review_shots = review_shots[:15]
    source_crop = to_source(rect, scale, width, height)
    sheets = []
    review_capture, _, _, _, _ = open_capture(cv2, args.media)
    measured_shots = []
    for shot in review_shots:
        thumbnails = []
        for fraction in (0.05, 0.5, 0.95):
            review_capture.set(cv2.CAP_PROP_POS_MSEC, (shot["in"] + shot["duration"] * fraction) * 1000)
            ok, frame = review_capture.read()
            if not ok:
                break
            x, y, w, h = (source_crop[k] for k in ("x", "y", "w", "h"))
            thumbnails.append(cv2.resize(frame[y:y+h, x:x+w], (288, 256)))
        if len(thumbnails) != 3:
            continue
        index = len(measured_shots)
        strip = np.zeros((288, 864, 3), dtype=np.uint8)
        for column, thumbnail in enumerate(thumbnails):
            strip[32:, column*288:(column+1)*288] = thumbnail
        cv2.putText(strip, f"SHOT {index} | {shot['in']:.3f}-{shot['in']+shot['duration']:.3f}s | start / middle / end",
                    (8, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.48, (255, 255, 255), 1, cv2.LINE_AA)
        measured_shots.append((shot, strip))
    review_capture.release()
    for first in range(0, len(measured_shots), 5):
        sheet_path = os.path.abspath(args.output + f".review-{first // 5 + 1}.jpg")
        if not cv2.imwrite(sheet_path, np.vstack([s[1] for s in measured_shots[first:first+5]]),
                           [cv2.IMWRITE_JPEG_QUALITY, 90]):
            raise SystemExit("Could not save B-roll visual review frames.")
        sheets.append(sheet_path)

    payload = {
        "width": width, "height": height, "still": bool(still), "motion": round(motion, 2),
        "darkShare": round(dark_share, 2),
        "shots": [s[0] for s in measured_shots], "crop": source_crop, "blur": blur_boxes(boxes, rect),
        "reviewSheets": sheets,
        "logoBoxes": len(boxes),
    }
    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump(payload, handle)
    print(f"done shots={len(shots)} logos={len(boxes)} still={still}", file=sys.stderr, flush=True)
    return 0


def skin_mask(cv2, np, bgr):
    """Pixels whose colour is human skin, in YCrCb. Measured on the Double Coverage set, a real
    guest's face is ~0.78 skin by this test while the flat pink caricatures of the hosts painted on
    the back wall are 0.00 - their colour sits outside the skin range - which is what tells them apart."""
    ycc = cv2.cvtColor(bgr, cv2.COLOR_BGR2YCrCb)
    cr = ycc[:, :, 1]
    cb = ycc[:, :, 2]
    return (cr > 133) & (cr < 180) & (cb > 77) & (cb < 127)


def face_boxes(cv2, np, cascades, bgr):
    """Every detected face with its skin fraction, largest first, so callers can tell a real face
    (skin ~0.78 on this set) from a flat pink wall caricature (skin ~0.00). Rejecting the caricatures
    stops the crop locking onto the graphic and cutting off the guest in wide shots."""
    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    mask = skin_mask(cv2, np, bgr)
    boxes = []
    for cascade, flip in cascades:
        image = gray[:, ::-1] if flip else gray
        for (x, y, w, h) in cascade.detectMultiScale(image, scaleFactor=1.15, minNeighbors=6, minSize=(40, 40)):
            if flip:
                x = image.shape[1] - x - w
            if w <= 0 or h <= 0:
                continue
            boxes.append((x, y, w, h, float(mask[y:y + h, x:x + w].mean())))
    return sorted(boxes, key=lambda r: -r[2] * r[3])


def skin_focus(cv2, np, bgr, min_area_frac=0.008):
    """The centre of the largest blob of skin, or None. Used for a wide shot where the cascade found
    no real face but the guest's skin is still on screen, so the crop follows the person, not the set."""
    mask = skin_mask(cv2, np, bgr).astype(np.uint8)
    count, _, stats, centroids = cv2.connectedComponentsWithStats(mask, connectivity=8)
    if count <= 1:
        return None
    idx = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
    if stats[idx, cv2.CC_STAT_AREA] < mask.size * min_area_frac:
        return None
    return float(centroids[idx][0]), float(centroids[idx][1])


def group_angles(np, means, threshold=25.0):
    """Shots from one camera angle look alike on average; a different angle does not."""
    groups, representatives = [], []
    for index, mean in enumerate(means):
        for group, representative in zip(groups, representatives):
            if float(np.mean(np.abs(mean - representative))) <= threshold:
                group.append(index)
                break
        else:
            groups.append([index])
            representatives.append(mean)
    return groups


def angle_focus(cv2, np, cascades, shots, seat=None, seats=None, log=None):
    """Where to hold the crop for one whole camera angle, or None when no real face is seen.

    The reference channel keeps one fixed punch-in on whoever is telling the story. Taking the largest face
    in each shot instead swung the crop between seated guests on a two-shot: Mike Majlak's own story cut to
    Jingle Jared for 1.6 s. Picking the talker by mouth movement was measured on six sections whose talker
    was known from the transcript, and it was wrong too often (at best 3 of 4, margins under 0.2): one guest
    simply moves more whether or not he is talking. So the talker's seat comes from outside: the story names
    its teller and the campaign records who sits where on each episode's wide shot (`seat` of `seats`, left
    to right). Without that, the person seen most on the angle holds it, which at least never flips."""
    detections = []
    for s, shot in enumerate(shots):
        for f, frame in enumerate(shot["frames"]):
            for (x, y, w, h, skin) in face_boxes(cv2, np, cascades, frame):
                if skin >= 0.3:
                    detections.append((s, f, x + w / 2.0, (x, y, w, h)))
    if not detections:
        return None
    width = shots[0]["frames"][0].shape[1]
    frame_count = sum(len(shot["frames"]) for shot in shots)
    # People by seat: split the sorted face centres wherever there is a wide empty gap.
    detections.sort(key=lambda d: d[2])
    people = [[detections[0]]]
    for detection in detections[1:]:
        if detection[2] - people[-1][-1][2] > 0.1 * width:
            people.append([])
        people[-1].append(detection)
    # A person has to be on screen for a fair share of the angle, not a one-frame false detection.
    people = [p for p in people if len({(d[0], d[1]) for d in p}) >= max(3, 0.2 * frame_count)]
    if not people:
        return None

    def centre(person):
        x, y, w, h = np.median(np.array([d[3] for d in person], dtype=np.float32), axis=0)
        return float(x + w / 2.0), float(y + h / 2.0)

    people.sort(key=lambda p: centre(p)[0])
    # A host's own close-up has one person in the middle; a couch shot has people off to the sides, even when
    # only one of them faces the camera.
    couch = len(people) >= 2 or abs(centre(people[0])[0] / width - 0.5) > 0.15
    if seat is not None and seats and seats >= 2 and couch:
        if len(people) == seats:
            chosen = centre(people[seat])
        else:
            # Someone is turned away and never detected: place the seat by where it sits on the couch.
            expected = (seat + 0.5) / seats * width
            nearest = min(people, key=lambda p: abs(centre(p)[0] - expected))
            x, y = centre(nearest)
            chosen = (x, y) if abs(x - expected) < 0.2 * width else (expected, y)
        if log:
            log("couch angle: %d people at x=%s, teller seat %d of %d -> x=%.0f"
                % (len(people), [round(centre(p)[0]) for p in people], seat, seats, chosen[0]))
        return chosen
    most_seen = max(people, key=lambda p: len({(d[0], d[1]) for d in p}))
    return centre(most_seen)


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

    # One focus per camera angle, held for every shot from that angle: the teller's seat on a couch shot.
    held = {}
    for group in group_angles(np, means):
        focus = angle_focus(cv2, np, cascades, [raw_shots[i] for i in group], seat=args.seat, seats=args.seats,
                            log=lambda message: print(message, file=sys.stderr))
        for index in group:
            held[index] = focus

    shots = []
    previous_focus = None
    for index, shot in enumerate(raw_shots):
        picks = shot["frames"][:: max(1, len(shot["frames"]) // 6)][:6]
        frame_h, frame_w = picks[0].shape[:2]
        # Per pick: the real (skin) faces and whether a big wall caricature is on screen. The caricature
        # is only large when the camera is wide, so its presence marks a wide shot.
        detections = [face_boxes(cv2, np, cascades, f) for f in picks]
        reals_per_pick = [[b for b in d if b[4] >= 0.3] for d in detections]
        centres = [(r[0][0] + r[0][2] / 2.0, r[0][1] + r[0][3] / 2.0) for r in reals_per_pick if r]
        if held.get(index) is not None:
            centres = [held[index]]
        # The camera half is ALWAYS cropped to fill, never letterboxed. Scaling a 16:9 angle to fit this
        # 1080x960 window leaves black bands above and below the host, which reads as a broken video on a
        # phone, and the reference channel never does it. Earlier attempts letterboxed "wide" shots so
        # nobody got cut off, but a podcast two-shot puts every face under a tenth of the frame width, so
        # that fired on almost every shot. Losing the edges of the desk beats bars through the middle.
        if centres:
            focus = (sorted(p[0] for p in centres)[len(centres) // 2], sorted(p[1] for p in centres)[len(centres) // 2])
        else:
            # No skin-backed face: follow the largest skin blob, else hold the last shot's focus, so the
            # crop stays on the person rather than snapping to the caricatures painted on the back wall.
            skins = [s for s in (skin_focus(cv2, np, f) for f in picks) if s is not None]
            focus = (sorted(p[0] for p in skins)[len(skins) // 2], sorted(p[1] for p in skins)[len(skins) // 2]) if skins else previous_focus
        # The measured Double Coverage punch-in and no tighter: the top 79% of the frame, top edge kept, x on
        # the guest. The episode's sponsor bugs sit in the bottom corners, below that window.
        rect, _ = choose_crop(boxes, area, focus=focus, zooms=(1.0,), height_fraction=args.height_fraction,
                              keep_focus=True, face_at=0.3, top_edge=True)
        previous_focus = focus if focus is not None else previous_focus
        shots.append({
            "in": round(shot["in"], 3), "duration": round(shot["duration"], 3),
            "crop": to_source(rect, scale, width, height),
            "blur": blur_boxes(boxes, rect), "face": bool(centres), "fit": False,
        })

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
    # The story teller's seat on the episode's couch shot, counted from the left, and how many seats there are.
    parser.add_argument("--seat", type=int, default=None)
    parser.add_argument("--seats", type=int, default=None)
    args = parser.parse_args()
    return probe(args) if args.mode == "probe" else topcrop(args)


if __name__ == "__main__":
    raise SystemExit(main())
