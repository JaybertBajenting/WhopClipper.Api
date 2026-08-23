"""Word-level transcription for clip captions.

YouTube's auto-captions arrive as pre-chunked multi-line blocks with speaker markers and no
per-word timings, which cannot drive one-word-at-a-time captions. faster-whisper gives a
timestamp for every word, which is what the caption renderer needs.

The audio is decoded to 16 kHz mono with ffmpeg and fed through in windows. Handing a full
stream VOD to faster-whisper in one go builds a single STFT array over the whole recording
and dies with a numpy allocation error on anything long.

Usage: transcribe.py <media> <output.json> [--model small.en] [--ffmpeg ffmpeg]
                     [--chunk-seconds 300] [--start 0] [--end 0]
Writes {"language":..., "duration":..., "words":[{"start":..,"end":..,"word":..}], "segments":[...]}
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile
import wave


CHUNK_OVERLAP_SECONDS = 0.4  # keeps a word that straddles a window boundary intact


def audio_duration(path: str) -> float:
    with wave.open(path, "rb") as handle:
        return handle.getnframes() / float(handle.getframerate())


def decode_to_wav(ffmpeg: str, media: str, destination: str, start: float, end: float) -> None:
    command = [ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin", "-y"]
    if start > 0:
        command += ["-ss", f"{start:.3f}"]
    command += ["-i", media]
    if end > start:
        command += ["-t", f"{end - start:.3f}"]
    # 16 kHz mono PCM is what the model wants; anything else is resampled internally.
    command += ["-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", destination]
    subprocess.run(command, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)


def slice_wav(source: str, destination: str, start: float, end: float) -> None:
    with wave.open(source, "rb") as reader:
        rate = reader.getframerate()
        reader.setpos(min(int(start * rate), reader.getnframes()))
        frames = reader.readframes(max(0, int((end - start) * rate)))
        with wave.open(destination, "wb") as writer:
            writer.setnchannels(reader.getnchannels())
            writer.setsampwidth(reader.getsampwidth())
            writer.setframerate(rate)
            writer.writeframes(frames)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("media")
    parser.add_argument("output")
    parser.add_argument("--model", default="small.en")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--chunk-seconds", type=float, default=300.0)
    parser.add_argument("--start", type=float, default=0.0)
    parser.add_argument("--end", type=float, default=0.0)
    args = parser.parse_args()

    from faster_whisper import WhisperModel

    # CPU int8 keeps this runnable on any machine without a CUDA setup.
    model = WhisperModel(args.model, device="cpu", compute_type="int8")

    words: list[dict] = []
    segments_out: list[dict] = []
    language = None

    with tempfile.TemporaryDirectory(prefix="clipforge-stt-") as work:
        full = os.path.join(work, "audio.wav")
        decode_to_wav(args.ffmpeg, args.media, full, args.start, args.end)
        total = audio_duration(full)
        print(f"decoded {total:.1f}s", file=sys.stderr, flush=True)

        offset = 0.0
        index = 0
        while offset < total:
            window_end = min(total, offset + args.chunk_seconds)
            chunk = os.path.join(work, f"chunk{index}.wav")
            slice_wav(full, chunk, offset, min(total, window_end + CHUNK_OVERLAP_SECONDS))

            segments, info = model.transcribe(chunk, word_timestamps=True, vad_filter=True)
            language = language or info.language

            for segment in segments:
                # Rebase onto the original media timeline, and drop anything that belongs to
                # the overlap so a word is never emitted twice.
                seg_start = segment.start + offset + args.start
                if seg_start >= window_end + args.start and offset + args.chunk_seconds < total:
                    continue
                segments_out.append({
                    "start": round(seg_start, 3),
                    "end": round(segment.end + offset + args.start, 3),
                    "text": segment.text.strip(),
                })
                for word in (segment.words or []):
                    text = word.word.strip()
                    start = word.start + offset + args.start
                    if not text or (words and start < words[-1]["start"]):
                        continue
                    words.append({
                        "start": round(start, 3),
                        "end": round(word.end + offset + args.start, 3),
                        "word": text,
                        "probability": round(getattr(word, "probability", 0.0) or 0.0, 3),
                    })

            print(f"progress {min(window_end, total):.0f}/{total:.0f}s words={len(words)}", file=sys.stderr, flush=True)
            os.remove(chunk)
            offset = window_end
            index += 1

    payload = {
        "language": language,
        "duration": round(total, 3),
        "model": args.model,
        "words": words,
        "segments": segments_out,
    }
    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False)

    print(f"done words={len(words)} segments={len(segments_out)}", file=sys.stderr, flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
