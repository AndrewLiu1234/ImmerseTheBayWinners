#!/usr/bin/env python3
"""Writes the SYNTHETIC demo-fallback recording unity/Assets/StreamingAssets/fixtures-demo/learn-g.wav.

Not a recording of a guitar: six Karplus-Strong plucked strings tuned to an open G chord (frets 3 2 0 0 0 3, strings 6..1 =
MIDI 43 47 50 55 59 67 = G2 B2 D3 G3 B3 G4, A4 = 440 Hz), five down-strums 2 s apart (low string first, 14 ms apart),
48 kHz mono 16-bit PCM, 10 s, peak -10 dBFS. Standard library only and seeded, so a rerun gives the same bytes.

    python3 art/audio/make_learn_g.py            # writes the file in place
    python3 art/audio/make_learn_g.py --check    # prints its numbers, writes nothing
"""
import argparse
import math
import os
import random
import struct
import sys
import wave

RATE = 48000
SECONDS = 10.0
MIDI = [43, 47, 50, 55, 59, 67]            # string 6 (low G) .. string 1 (high G)
STRUM_TIMES = [0.30, 2.30, 4.30, 6.30, 8.30]
STRUM_SPREAD_SEC = 0.014                   # a down-strum reaches string 1 70 ms after string 6
T60_SEC = [3.2, 3.0, 2.8, 2.5, 2.2, 1.9]   # low strings ring longer
LEVEL = [1.00, 0.95, 0.90, 0.85, 0.80, 0.75]
PEAK = 10 ** (-10 / 20.0)                  # -10 dBFS
FADE_OUT_SEC = 0.25                        # the replay loops: no click at the seam
SEED = 20261114

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "unity", "Assets", "StreamingAssets", "fixtures-demo", "learn-g.wav"))


def midi_hz(m):
    return 440.0 * 2 ** ((m - 69) / 12.0)


def render():
    rng = random.Random(SEED)
    n_total = int(SECONDS * RATE)
    mix = [0.0] * n_total
    for s, m in enumerate(MIDI):
        freq = midi_hz(m)
        period = RATE / freq
        n = int(math.floor(period - 0.5))
        frac = period - 0.5 - n
        if frac < 0.1:
            n -= 1
            frac += 1.0
        c = (1.0 - frac) / (1.0 + frac)
        loss = 10 ** (-3.0 / (T60_SEC[s] * freq))
        # down-strum: string 6 (s = 0) first
        onsets = [int(round((t + s * STRUM_SPREAD_SEC) * RATE)) for t in STRUM_TIMES]
        buf = [0.0] * n
        idx = 0
        prev = 0.0                              # last value through the averager (x[n-1])
        ap_x1 = 0.0                             # allpass state
        ap_y1 = 0.0
        active = False
        next_onset = 0
        for i in range(n_total):
            if next_onset < len(onsets) and i == onsets[next_onset]:
                # pluck: lowpassed noise, zero mean, one period long
                noise = [rng.uniform(-1.0, 1.0) for _ in range(n)]
                lp = 0.0
                for j in range(n):
                    lp = 0.5 * lp + 0.5 * noise[j]
                    noise[j] = lp
                mean = sum(noise) / n
                buf = [(v - mean) * LEVEL[s] for v in noise]
                idx = 0
                prev = 0.0
                ap_x1 = ap_y1 = 0.0
                active = True
                next_onset += 1
            if not active:
                continue
            x = buf[idx]
            avg = 0.5 * (x + prev) * loss
            prev = x
            y = c * avg + ap_x1 - c * ap_y1     # first-order allpass: fractional delay
            ap_x1 = avg
            ap_y1 = y
            buf[idx] = y
            idx += 1
            if idx == n:
                idx = 0
            mix[i] += x
    peak = max(abs(v) for v in mix) or 1.0
    gain = PEAK / peak
    fade = int(FADE_OUT_SEC * RATE)
    for i in range(n_total):
        g = gain
        if i >= n_total - fade:
            g *= (n_total - i) / float(fade)
        mix[i] *= g
    return mix


def stats(samples):
    peak = max(abs(v) for v in samples)
    rms = math.sqrt(sum(v * v for v in samples) / len(samples))
    return peak, rms


def write(path, samples):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    frames = bytearray()
    for v in samples:
        q = int(round(max(-1.0, min(1.0, v)) * 32767))
        frames += struct.pack("<h", q)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(bytes(frames))


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true", help="print the numbers, write nothing")
    ap.add_argument("--out", default=OUT, help="output path (default: the StreamingAssets fixture)")
    args = ap.parse_args()
    samples = render()
    peak, rms = stats(samples)
    print("samples=%d rate=%d seconds=%.2f peak=%.4f (%.1f dBFS) rms=%.4f (%.1f dBFS)" % (
        len(samples), RATE, len(samples) / float(RATE), peak, 20 * math.log10(peak), rms, 20 * math.log10(rms)))
    for s, m in enumerate(MIDI):
        print("string %d: MIDI %d = %.2f Hz" % (6 - s, m, midi_hz(m)))
    if not args.check:
        write(args.out, samples)
        print("wrote", args.out, os.path.getsize(args.out), "bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
