#!/usr/bin/env python3
"""Spike M-S1 aid: writes art/test/sbs-test-3840x1080.png, a side-by-side stereo test image for the XREAL glasses.

  left half  (x 0..1919)    solid red  with a large white "L" -> must reach the LEFT eye only
  right half (x 1920..3839) solid blue with a large white "R" -> must reach the RIGHT eye only
  thin (2 px) white alignment marks in each half: a frame inset 6 px from the half's edges, ticks along the frame
  (every 120 px across, every 135 px down; longer ticks at the middle) and a 2 x 3 grid of small crosses (x = 480 and 1440,
  y = 270, 540 and 810, clear of the letters). They let you see that nothing is cropped or scaled and that each cross of
  the left eye fuses with its twin in the right eye when both eyes are open.

Standard library only: the PNG is written by hand with zlib + struct (8-bit RGB, no interlace, filter 0).

usage:
  python3 make_sbs_test.py                 write sbs-test-3840x1080.png next to this script, then check it
  python3 make_sbs_test.py --out FILE      write somewhere else
  python3 make_sbs_test.py --check [FILE]  only read a PNG written by this script back and check size and sample colours
"""
import math
import os
import struct
import sys
import zlib

W, H = 3840, 1080            # the whole side-by-side frame
EYE_W = W // 2               # 1920: one eye
RED = (255, 0, 0)
BLUE = (0, 0, 255)
WHITE = (255, 255, 255)
DEFAULT_NAME = "sbs-test-3840x1080.png"

# ---------------------------------------------------------------------------------------------------------------------
# Shapes. Coordinates are inside ONE eye image (1920 x 1080, y down). Each shape yields the x-spans it covers on row y.
# ---------------------------------------------------------------------------------------------------------------------


def rect(x0, y0, x1, y1):
    """Filled rectangle [x0, x1) x [y0, y1)."""
    def spans(y):
        return [(x0, x1)] if y0 <= y < y1 else []
    return spans


def half_ring(cx, cy, r_out, r_in):
    """The right half (x >= cx) of a ring: a 'D' shaped bowl."""
    def spans(y):
        dy = y + 0.5 - cy
        if abs(dy) >= r_out:
            return []
        out_dx = math.sqrt(r_out * r_out - dy * dy)
        in_dx = math.sqrt(r_in * r_in - dy * dy) if abs(dy) < r_in else 0.0
        a, b = int(round(cx + in_dx)), int(round(cx + out_dx))
        return [(a, b)] if b > a else []
    return spans


def slanted_bar(y0, y1, left0, right0, left1, right1):
    """A parallelogram: at y0 it covers [left0, right0), at y1 it covers [left1, right1), linear in between."""
    def spans(y):
        if not (y0 <= y < y1):
            return []
        t = (y + 0.5 - y0) / float(y1 - y0)
        a = int(round(left0 + t * (left1 - left0)))
        b = int(round(right0 + t * (right1 - right0)))
        return [(a, b)] if b > a else []
    return spans


# Large block letters, about 550 x 700 px, centred on the eye image (960, 540).
LETTER_L = [
    rect(700, 190, 860, 890),      # stem
    rect(700, 750, 1240, 890),     # foot
]
LETTER_R = [
    rect(700, 190, 860, 890),      # stem
    rect(860, 190, 1040, 330),     # top bar
    rect(860, 470, 1040, 610),     # middle bar
    half_ring(1040, 400, 210, 70), # the bowl
    slanted_bar(610, 890, 900, 1040, 1080, 1240),   # leg
]


# ---------------------------------------------------------------------------------------------------------------------
# Thin alignment marks, as spans per row inside one eye image.
# ---------------------------------------------------------------------------------------------------------------------

FRAME_INSET = 6      # px from the eye image's edge to the outer edge of the frame
LINE = 2             # px, the "thin" in thin marks
TICK = 24            # px, ordinary tick length (pointing inward)
TICK_MID = 60        # px, tick at the middle of each side
TICK_STEP_X = 120    # px between ticks along the top and bottom edges
TICK_STEP_Y = 135    # px between ticks along the left and right edges
CROSS_ARM = 60       # px from the centre of a small cross to the end of each arm
CROSS_XS = (480, 1440)        # the crosses sit at 1/4 and 3/4 of the eye image width ...
CROSS_YS = (270, 540, 810)    # ... and at 1/4, 1/2 and 3/4 of its height: all clear of the letters (x 700..1250)


def alignment_marks():
    lo_x, hi_x = FRAME_INSET, EYE_W - FRAME_INSET      # frame outer bounds: x in [lo_x, hi_x)
    lo_y, hi_y = FRAME_INSET, H - FRAME_INSET
    shapes = []
    # frame
    shapes.append(rect(lo_x, lo_y, hi_x, lo_y + LINE))
    shapes.append(rect(lo_x, hi_y - LINE, hi_x, hi_y))
    shapes.append(rect(lo_x, lo_y, lo_x + LINE, hi_y))
    shapes.append(rect(hi_x - LINE, lo_y, hi_x, hi_y))
    # ticks across the top and bottom (pointing inward), longer at the middle
    for x in range(TICK_STEP_X, EYE_W, TICK_STEP_X):
        n = TICK_MID if x == EYE_W // 2 else TICK
        shapes.append(rect(x - LINE // 2, lo_y, x - LINE // 2 + LINE, lo_y + n))
        shapes.append(rect(x - LINE // 2, hi_y - n, x - LINE // 2 + LINE, hi_y))
    # ticks down the left and right sides
    for y in range(TICK_STEP_Y, H, TICK_STEP_Y):
        n = TICK_MID if y == H // 2 else TICK
        shapes.append(rect(lo_x, y - LINE // 2, lo_x + n, y - LINE // 2 + LINE))
        shapes.append(rect(hi_x - n, y - LINE // 2, hi_x, y - LINE // 2 + LINE))
    # grid of small crosses
    for cx in CROSS_XS:
        for cy in CROSS_YS:
            shapes.append(rect(cx - CROSS_ARM, cy - LINE // 2, cx + CROSS_ARM, cy - LINE // 2 + LINE))
            shapes.append(rect(cx - LINE // 2, cy - CROSS_ARM, cx - LINE // 2 + LINE, cy + CROSS_ARM))
    return shapes


def render_eye(background, letter_shapes, mark_shapes):
    """One eye image as a list of H rows, each EYE_W * 3 bytes (RGB)."""
    bg = bytes(background) * EYE_W
    white = bytes(WHITE)
    rows = []
    for y in range(H):
        row = bytearray(bg)
        # marks first, letters on top (they do not overlap anyway: the letters keep 190 px away from the frame)
        for shape in mark_shapes:
            for a, b in shape(y):
                a, b = max(0, a), min(EYE_W, b)
                if b > a:
                    row[a * 3:b * 3] = white * (b - a)
        for shape in letter_shapes:
            for a, b in shape(y):
                a, b = max(0, a), min(EYE_W, b)
                if b > a:
                    row[a * 3:b * 3] = white * (b - a)
        rows.append(bytes(row))
    return rows


# ---------------------------------------------------------------------------------------------------------------------
# PNG writing and reading (8-bit RGB, no interlace, filter type 0 on every row)
# ---------------------------------------------------------------------------------------------------------------------

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def chunk(kind, data):
    body = kind + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def build_png():
    marks = alignment_marks()
    left = render_eye(RED, LETTER_L, marks)
    right = render_eye(BLUE, LETTER_R, marks)
    raw = bytearray()
    for y in range(H):
        raw += b"\x00" + left[y] + right[y]           # filter type 0 (None), then the 3840 pixels of the row
    ihdr = struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0)  # 8 bit, colour type 2 = RGB, deflate, adaptive filtering, no interlace
    text = chunk(b"tEXt", b"Title\x00" + b"SBS test 3840x1080: left half red L, right half blue R (spike M-S1)")
    return (PNG_SIGNATURE + chunk(b"IHDR", ihdr) + text
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def read_png(path):
    """Minimal reader for the PNGs this script writes. Returns (width, height, rows) with rows as bytes of RGB."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:8] != PNG_SIGNATURE:
        raise ValueError("not a PNG file")
    pos, width, height, idat = 8, None, None, b""
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        kind = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        (crc,) = struct.unpack(">I", data[pos + 8 + length:pos + 12 + length])
        if zlib.crc32(kind + body) & 0xFFFFFFFF != crc:
            raise ValueError("bad CRC in chunk %r" % kind)
        if kind == b"IHDR":
            width, height, depth, ctype, comp, filt, interlace = struct.unpack(">IIBBBBB", body)
            if (depth, ctype, comp, filt, interlace) != (8, 2, 0, 0, 0):
                raise ValueError("this checker only reads 8-bit RGB, non-interlaced PNGs")
        elif kind == b"IDAT":
            idat += body
        pos += 12 + length
    raw = zlib.decompress(idat)
    stride = 1 + width * 3
    if len(raw) != stride * height:
        raise ValueError("unexpected decoded size %d (expected %d)" % (len(raw), stride * height))
    rows = []
    for y in range(height):
        if raw[y * stride] != 0:
            raise ValueError("row %d uses filter %d; this checker only reads filter 0" % (y, raw[y * stride]))
        rows.append(raw[y * stride + 1:(y + 1) * stride])
    return width, height, rows


def pixel(rows, x, y):
    return tuple(rows[y][x * 3:x * 3 + 3])


def check(path):
    """Reads the PNG back and asserts the size and a set of sample colours. Returns the list of report lines."""
    width, height, rows = read_png(path)
    report = ["file: %s" % path, "size: %d x %d" % (width, height)]
    assert (width, height) == (W, H), "size is %dx%d, expected %dx%d" % (width, height, W, H)
    samples = [
        ("left half background (200, 300)", (200, 300), RED),
        ("left half, last column (1919, 300)", (EYE_W - 1, 300), RED),
        ("right half, first column (1920, 300)", (EYE_W, 300), BLUE),
        ("right half background (3640, 300)", (W - 200, 300), BLUE),
        ("L stem (780, 500)", (780, 500), WHITE),
        ("L foot (1000, 820)", (1000, 820), WHITE),
        ("inside the L's corner (1000, 500)", (1000, 500), RED),
        ("R stem (EYE_W + 780, 500)", (EYE_W + 780, 500), WHITE),
        ("R top bar (EYE_W + 950, 260)", (EYE_W + 950, 260), WHITE),
        ("R counter (EYE_W + 950, 400)", (EYE_W + 950, 400), BLUE),
        ("R leg (EYE_W + 1100, 800)", (EYE_W + 1100, 800), WHITE),
        ("left frame, top edge (500, 6)", (500, FRAME_INSET), WHITE),
        ("right frame, bottom edge (EYE_W + 500, 1073)", (EYE_W + 500, H - FRAME_INSET - 1), WHITE),
        ("left cross at (480, 540), upper arm (480, 500)", (480, 500), WHITE),
        ("left cross at (1440, 810), left arm (1400, 810)", (1400, 810), WHITE),
        ("right cross at (480, 540), upper arm (EYE_W + 480, 500)", (EYE_W + 480, 500), WHITE),
        ("right cross at (1440, 270), lower arm (EYE_W + 1440, 300)", (EYE_W + 1440, 300), WHITE),
        ("between the crosses (EYE_W + 300, 400)", (EYE_W + 300, 400), BLUE),
        ("outside the frame (2, 2)", (2, 2), RED),
        ("outside the frame (3837, 1077)", (W - 3, H - 3), BLUE),
    ]
    for label, (x, y), want in samples:
        got = pixel(rows, x, y)
        status = "ok" if got == want else "WRONG (expected %s)" % (want,)
        report.append("  %-48s %s  %s" % (label, got, status))
        assert got == want, "%s: got %s, expected %s" % (label, got, want)
    # the whole left half must contain no blue and the whole right half no red (that is the point of the test)
    for y in range(0, H, 9):
        row = rows[y]
        left = row[:EYE_W * 3]
        right = row[EYE_W * 3:]
        assert not any(left[i] == 0 and left[i + 1] == 0 and left[i + 2] == 255 for i in range(0, len(left), 3)), "blue pixel in the left half, row %d" % y
        assert not any(right[i] == 255 and right[i + 1] == 0 and right[i + 2] == 0 for i in range(0, len(right), 3)), "red pixel in the right half, row %d" % y
    report.append("no blue in the left half and no red in the right half (every 9th row)")
    report.append("OK")
    return report


def main(argv):
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, DEFAULT_NAME)
    args = argv[1:]
    if args and args[0] == "--check":
        path = args[1] if len(args) > 1 else out
        print("\n".join(check(path)))
        return 0
    if args and args[0] == "--out":
        if len(args) < 2:
            print("--out needs a file name", file=sys.stderr)
            return 2
        out = args[1]
    elif args:
        print(__doc__)
        return 2
    png = build_png()
    with open(out, "wb") as f:
        f.write(png)
    print("wrote %s (%d bytes)" % (out, len(png)))
    print("\n".join(check(out)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
