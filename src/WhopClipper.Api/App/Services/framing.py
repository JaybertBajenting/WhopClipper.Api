"""Subject tracking for the SmartCrop framing mode.

Cropping a 16:9 stream to a 9:16 clip keeps only the middle ~31% of the width. Streamers do
not sit in the middle of their own webcam frame, so a fixed centre crop routinely slices the
face in half. This samples the clip, finds where the face actually is, and emits crop
keyframes the renderer turns into a slow pan.

Haar cascades are used rather than a DNN because they ship inside opencv-python with no model
download. They miss a lot of frames; that is fine, because misses are interpolated over and
the whole track is smoothed afterwards.

Usage: framing.py <media> <output.json> --start S --end E --output-width W --output-height H
                  [--sample-seconds 0.5] [--max-keyframes 60]
Writes {"width":..,"height":..,"cropWidth":..,"detections":N,"keyframes":[{"t":..,"x":..}]}
with x in source pixels and t in seconds from the start of the clip. An empty keyframe list
means "no subject found, centre it".
"""
import argparse
import json
import sys

# A detection has to move the window by more than this fraction of the crop width before the
# pan reacts. Without it the crop jitters on every frame the cascade wobbles.
DEADZONE_FRACTION = 0.08
# Pixels per second the window may travel. Faster than this reads as a camera whip.
MAX_PAN_SPEED = 90.0
# Median window (in samples) used to throw out single-frame false positives.
SMOOTHING_SAMPLES = 5


def detect_faces(cascades, gray):
    for cascade, flip in cascades:
        image = gray[:, ::-1] if flip else gray
        found = cascade.detectMultiScale(image, scaleFactor=1.15, minNeighbors=6, minSize=(40, 40))
        if len(found) == 0:
            continue
        x, y, w, h = max(found, key=lambda r: r[2] * r[3])
        if flip:
            x = image.shape[1] - x - w
        return x + w / 2.0, y + h / 2.0
    return None


def median(values):
    ordered = sorted(values)
    return ordered[len(ordered) // 2]


def smooth(samples, crop_width):
    """Median filter, then a deadzone plus a speed limit so the pan never snaps."""
    if not samples:
        return []
    filtered = []
    for index, (t, x) in enumerate(samples):
        window = samples[max(0, index - SMOOTHING_SAMPLES // 2): index + SMOOTHING_SAMPLES // 2 + 1]
        filtered.append((t, median([value for _, value in window])))

    deadzone = crop_width * DEADZONE_FRACTION
    track = [filtered[0]]
    for t, x in filtered[1:]:
        previous_t, previous_x = track[-1]
        target = previous_x if abs(x - previous_x) < deadzone else x
        limit = MAX_PAN_SPEED * max(1e-3, t - previous_t)
        step = max(-limit, min(limit, target - previous_x))
        track.append((t, previous_x + step))
    return track


def thin(track, limit):
    """Drop keyframes that add nothing, so the render filter expression stays short."""
    if len(track) <= limit:
        return track
    stride = len(track) / float(limit)
    return [track[min(len(track) - 1, int(index * stride))] for index in range(limit)]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("media")
    parser.add_argument("output")
    parser.add_argument("--start", type=float, required=True)
    parser.add_argument("--end", type=float, required=True)
    parser.add_argument("--output-width", type=float, required=True)
    parser.add_argument("--output-height", type=float, required=True)
    parser.add_argument("--sample-seconds", type=float, default=0.5)
    parser.add_argument("--max-keyframes", type=int, default=60)
    args = parser.parse_args()

    import cv2

    cascades = [
        (cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_frontalface_default.xml"), False),
        (cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_profileface.xml"), False),
        (cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_profileface.xml"), True),
    ]

    capture = cv2.VideoCapture(args.media)
    if not capture.isOpened():
        raise SystemExit(f"Could not open {args.media}")
    width = int(capture.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(capture.get(cv2.CAP_PROP_FRAME_HEIGHT))
    # How much source width survives the squeeze into a vertical frame. Only the axis that
    # gets cut is worth tracking.
    crop_width = min(width, height * args.output_width / args.output_height)

    # Detection runs on a downscaled copy; a 1080p frame costs several hundred milliseconds
    # per cascade pass and buys no extra accuracy at this scale.
    analysis_width = 640
    scale = width / float(analysis_width) if width else 1.0

    samples = []
    t = 0.0
    span = max(0.0, args.end - args.start)
    while t < span:
        capture.set(cv2.CAP_PROP_POS_MSEC, (args.start + t) * 1000.0)
        ok, frame = capture.read()
        if not ok:
            break
        small = cv2.resize(frame, (analysis_width, int(analysis_width * height / max(1, width))))
        found = detect_faces(cascades, cv2.cvtColor(small, cv2.COLOR_BGR2GRAY))
        if found is not None:
            samples.append((round(t, 3), found[0] * scale))
        t += args.sample_seconds
    capture.release()

    track = thin(smooth(samples, crop_width), args.max_keyframes)
    half = crop_width / 2.0
    keyframes = [
        {"t": round(t, 3), "x": round(min(max(0.0, x - half), max(0.0, width - crop_width)), 2)}
        for t, x in track
    ]

    payload = {
        "width": width,
        "height": height,
        "cropWidth": round(crop_width, 2),
        "detections": len(samples),
        "keyframes": keyframes,
    }
    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump(payload, handle)

    print(f"done detections={len(samples)} keyframes={len(keyframes)}", file=sys.stderr, flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
