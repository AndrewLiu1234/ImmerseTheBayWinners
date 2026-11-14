#!/usr/bin/env python3
# Camera-vision bridge (SL12): ArUco marker pose for the camera-anchor.
#
#   Unity -> UDP 127.0.0.1:47801  JSON datagrams carrying JPEG chunks: {"f": frame_id, "p": part, "n": parts, "b64": chunk}
#   bridge -> UDP 127.0.0.1:47802  pose JSON: {"t": ts, "marker": id, "pos_mm": [x,y,z], "quat": [x,y,z,w], "det": n}
#                                  no detection: {"t": ts, "det": 0}
#
# Pose convention: marker origin in the CAMERA frame, mm; quaternion x,y,z,w (cv2 Rodrigues output, not flipped).
# The Unity adapter (Runtime/CameraAnchor/MarkerToNeck.cs) converts camera frame -> neck frame.
#
# Needs: python3 -m pip install --user opencv-python   (same pattern as the IMU bridge's hidapi)
# Run from the repo root:  python3 tools/camera-vision/camera_bridge.py
# Unit tests (no sockets, no camera):  python3 -m unittest discover -s tools/camera-vision -p 'test_*.py'
import argparse
import base64
import json
import math
import socket
import sys
import time

IN_PORT = 47801
OUT_PORT = 47802
# Datagram ceiling measured at 9216 bytes on this Mac's loopback (sandbox-imposed): raw part 6000 -> base64
# 8000 + ~100 JSON wrapper = ~8100, safely under. Unity's sender uses the same constant.
PART_BYTES = 6000
STATUS_EVERY_S = 5.0
MAX_FRAME_PARTS = 64
STALE_FRAME_S = 2.0


def _cv2():
    import cv2  # lazy: the module imports without cv2 installed (tests run synthetic frames without a camera)
    return cv2


def _aruco_dict(cv2):
    # opencv-python >= 4.7 removed Dictionary_get; 4.11 removed the whole old API. Use the new one when present.
    if hasattr(cv2.aruco, "getPredefinedDictionary"):
        return cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
    return cv2.aruco.Dictionary_get(cv2.aruco.DICT_4X4_50)


def _aruco_detect(cv2, img, aruco_dict):
    if hasattr(cv2.aruco, "ArucoDetector"):
        det = cv2.aruco.ArucoDetector(aruco_dict, cv2.aruco.DetectorParameters())
        corners, ids, _ = det.detectMarkers(img)
        if ids is not None:
            ids = ids.reshape(-1, 1)   # normalize to the old (N, 1) shape
        return corners, ids
    corners, ids, _ = cv2.aruco.detectMarkers(img, aruco_dict)
    return corners, ids


def _aruco_draw(cv2, aruco_dict, marker_id, size):
    if hasattr(cv2.aruco, "generateImageMarker"):
        return cv2.aruco.generateImageMarker(aruco_dict, marker_id, size)
    return cv2.aruco.drawMarker(aruco_dict, marker_id, size)


# ------------------------------------------------------------------------------------------------
# Detection (pure functions; unit-tested)
# ------------------------------------------------------------------------------------------------

DEFAULT_FX = 754.0          # 640 px wide, ~46 deg HFOV -> f = 320 / tan(23 deg)
DEFAULT_CX = 320.0
DEFAULT_CY = 120.0


def detect_pose(jpeg_bytes, marker_mm=120.0, marker_id=None, fx=DEFAULT_FX, cx=DEFAULT_CX, cy=DEFAULT_CY):
    """Decode one JPEG frame and return (marker_id, pos_mm, quat_xyzw) for the best marker, or None.
    pos_mm = marker origin in the camera frame (+x right, +y down, +z forward)."""
    import numpy as np
    cv2 = _cv2()
    arr = cv2.imdecode(np.frombuffer(jpeg_bytes, dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
    if arr is None:
        return None
    aruco_dict = _aruco_dict(cv2)
    corners, ids = _aruco_detect(cv2, arr, aruco_dict)
    if ids is None or len(ids) == 0:
        return None
    cam = numpy_camera_matrix(fx, cx, cy)
    half = marker_mm / 2.0
    objp = np.array([[-half, half, 0], [half, half, 0], [half, -half, 0], [-half, -half, 0]],
                    dtype=np.float64)
    best = None
    for i in range(len(ids)):
        mid = int(ids[i][0])
        if marker_id is not None and mid != marker_id:
            continue
        ok, rvec, tvec = cv2.solvePnP(objp, corners[i][0].astype(np.float64), cam, None)
        if not ok:
            continue
        rmat, _ = cv2.Rodrigues(rvec)
        q = rotation_matrix_to_quat(rmat)
        pos = (float(tvec[0][0]), float(tvec[1][0]), float(tvec[2][0]))
        # larger marker area in pixels = closer / more confident; keep the best
        area = marker_area_px(corners[i][0])
        if best is None or area > best[0]:
            best = (area, mid, pos, q)
    if best is None:
        return None
    return best[1], best[2], best[3]


def numpy_camera_matrix(fx, cx, cy):
    import numpy as np
    return np.array([[fx, 0, cx], [0, fx, cy], [0, 0, 1]], dtype=np.float64)


def rotation_matrix_to_quat(rmat):
    # 3x3 rotation -> (x, y, z, w)
    tr = rmat[0, 0] + rmat[1, 1] + rmat[2, 2]
    if tr > 0:
        s = math.sqrt(tr + 1.0) * 2
        w = 0.25 * s
        x = (rmat[2, 1] - rmat[1, 2]) / s
        y = (rmat[0, 2] - rmat[2, 0]) / s
        z = (rmat[1, 0] - rmat[0, 1]) / s
    elif rmat[0, 0] > rmat[1, 1] and rmat[0, 0] > rmat[2, 2]:
        s = math.sqrt(1.0 + rmat[0, 0] - rmat[1, 1] - rmat[2, 2]) * 2
        w = (rmat[2, 1] - rmat[1, 2]) / s
        x = 0.25 * s
        y = (rmat[0, 1] + rmat[1, 0]) / s
        z = (rmat[0, 2] + rmat[2, 0]) / s
    elif rmat[1, 1] > rmat[2, 2]:
        s = math.sqrt(1.0 + rmat[1, 1] - rmat[0, 0] - rmat[2, 2]) * 2
        w = (rmat[0, 2] - rmat[2, 0]) / s
        x = (rmat[0, 1] + rmat[1, 0]) / s
        y = 0.25 * s
        z = (rmat[1, 2] + rmat[2, 1]) / s
    else:
        s = math.sqrt(1.0 + rmat[2, 2] - rmat[0, 0] - rmat[1, 1]) * 2
        w = (rmat[1, 0] - rmat[0, 1]) / s
        x = (rmat[0, 2] + rmat[2, 0]) / s
        y = (rmat[1, 2] + rmat[2, 1]) / s
        z = 0.25 * s
    return (float(x), float(y), float(z), float(w))


def marker_area_px(corner):
    (ax, ay), (bx, by), (cx, cy), (dx, dy) = [tuple(float(v) for v in p) for p in corner]
    return 0.5 * abs(ax * by + bx * cy + cx * dy + dx * ay - bx * ay - cx * by - dx * cy - ax * dy)


def split_jpeg(data, part_bytes=PART_BYTES):
    """Split JPEG bytes into <= part_bytes parts: list of byte strings."""
    return [data[i:i + part_bytes] for i in range(0, len(data), part_bytes)]


def join_frame(parts, expected=None):
    """parts: list of (part_index, bytes). Returns joined bytes, or None if incomplete/wrong.
    expected = the total number of parts (from the frame header); derived from len(parts) when omitted."""
    if not parts:
        return None
    n = expected if expected is not None else len(parts)
    if n < 1 or any(p[0] >= n or p[0] < 0 for p in parts):
        return None
    if len(parts) != n:
        return None
    parts = sorted(parts)
    if [p[0] for p in parts] != list(range(n)):
        return None
    return b"".join(p[1] for p in parts)


def quat_to_rmat(q):
    """(x, y, z, w) quaternion -> 3x3 rotation matrix (numpy)."""
    import numpy as np
    x, y, z, w = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]], dtype=np.float64)


class FrameAssembler(object):
    """Reassembles chunked frames from datagrams; drops stale/incomplete frames."""

    def __init__(self, max_parts=MAX_FRAME_PARTS, stale_s=STALE_FRAME_S):
        self.max_parts = max_parts
        self.stale_s = stale_s
        self.pending = {}      # frame_id -> (last_seen, {part_index: bytes})
        self.completed = []    # (frame_id, jpeg_bytes) FIFO for the consumer

    def feed(self, obj):
        try:
            fid, part, n = int(obj["f"]), int(obj["p"]), int(obj["n"])
            chunk = base64.b64decode(obj["b64"])
        except (KeyError, ValueError, TypeError):
            return False
        if n < 1 or n > self.max_parts or part < 0 or part >= n:
            return False
        now = time.monotonic()
        # drop stale frames
        for k in list(self.pending):
            if now - self.pending[k][0] > self.stale_s:
                del self.pending[k]
        if fid not in self.pending:
            self.pending[fid] = (now, {})
        self.pending[fid][1][part] = chunk
        if len(self.pending[fid][1]) == n:
            data = join_frame(list(self.pending[fid][1].items()), expected=n)
            del self.pending[fid]
            if data:
                self.completed.append((fid, data))
                if len(self.completed) > 16:
                    self.completed.pop(0)
            return True
        return False

    def take(self):
        return self.completed.pop(0) if self.completed else None


def make_marker_image(marker_id=7, marker_mm=120.0, dpi=300, margin_mm=20.0):
    """Draw the ArUco marker (DICT_4X4_50) with a white quiet zone, sized for print."""
    cv2 = _cv2()
    aruco_dict = _aruco_dict(cv2)
    img = _aruco_draw(cv2, aruco_dict, marker_id, 800)
    total_mm = marker_mm + 2 * margin_mm
    canvas_px = int(round(total_mm / 25.4 * dpi))
    canvas = numpy_full(canvas_px, canvas_px, 255)
    m = int(round(marker_mm / 25.4 * dpi))
    import numpy as np
    resized = cv2.resize(img, (m, m), interpolation=cv2.INTER_NEAREST)
    off = (canvas_px - m) // 2
    canvas[off:off + m, off:off + m] = resized
    return canvas


def numpy_full(w, h, v):
    import numpy as np
    return np.full((h, w), v, dtype=np.uint8)


# ------------------------------------------------------------------------------------------------
# Bridge loop
# ------------------------------------------------------------------------------------------------

def run_bridge(args):
    cv2 = _cv2()  # fail fast with a clear message if cv2 is missing
    sock_in = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock_in.bind(("127.0.0.1", args.in_port))
    sock_in.settimeout(0.5)
    sock_out = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    asm = FrameAssembler()
    stats = {"frames": 0, "det": 0, "sent": 0, "last_det": 0.0, "bad": 0}
    last_status = time.monotonic()
    last_pose = None

    print("camera-bridge: listening on UDP 127.0.0.1:%d -> 127.0.0.1:%d (cv2 %s, marker %d mm, fx=%.1f)"
          % (args.in_port, args.out_port, cv2.__version__, args.marker_mm, args.fx))
    try:
        while True:
            try:
                data, _ = sock_in.recvfrom(65535)
            except socket.timeout:
                data = None
            if data:
                try:
                    obj = json.loads(data.decode("utf-8"))
                    if asm.feed(obj):
                        pass
                    else:
                        stats["bad"] += 1
                except (ValueError, UnicodeDecodeError):
                    stats["bad"] += 1
            # drain completed frames
            while True:
                item = asm.take()
                if item is None:
                    break
                fid, jpeg = item
                stats["frames"] += 1
                res = detect_pose(jpeg, marker_mm=args.marker_mm, marker_id=args.marker_id,
                                  fx=args.fx, cx=args.cx, cy=args.cy)
                if res is not None:
                    mid, pos, quat = res
                    stats["det"] += 1
                    stats["last_det"] = time.monotonic()
                    out = {"t": round(time.time(), 3), "marker": mid, "pos_mm": [round(v, 2) for v in pos],
                           "quat": [round(v, 6) for v in quat], "det": 1}
                    last_pose = out
                else:
                    out = {"t": round(time.time(), 3), "det": 0}
                sock_out.sendto(json.dumps(out).encode("utf-8"), ("127.0.0.1", args.out_port))
                stats["sent"] += 1
            now = time.monotonic()
            if now - last_status >= STATUS_EVERY_S:
                since = now - last_status
                fps = stats["frames"] / max(since, 1e-9)
                seen = "yes" if (last_pose and time.monotonic() - stats["last_det"] < 2.0) else "no"
                print("[status] frames %.1f/s | detections %d | marker seen: %s | bad datagrams %d | sent %d"
                      % (fps, stats["det"], seen, stats["bad"], stats["sent"]))
                stats["frames"] = stats["det"] = stats["bad"] = stats["sent"] = 0
                last_status = now
    except KeyboardInterrupt:
        print("camera-bridge: stopped.")


def self_test():
    """Generate a synthetic marker frame and prove detect_pose recovers the pose (needs cv2)."""
    cv2 = _cv2()
    import numpy as np
    aruco_dict = _aruco_dict(cv2)
    marker = _aruco_draw(cv2, aruco_dict, 7, 400)
    canvas = np.full((800, 1200), 255, dtype=np.uint8)
    # place the marker with a known perspective warp (composite through a mask so black cells survive)
    src = np.float32([[0, 0], [399, 0], [399, 399], [0, 399]])
    dst = np.float32([[700, 250], [1020, 210], [1080, 600], [640, 640]])
    H = cv2.getPerspectiveTransform(src, dst)
    warped = cv2.warpPerspective(marker, H, (1200, 800))
    full = np.full(marker.shape, 255, dtype=np.uint8)
    mask = cv2.warpPerspective(full, H, (1200, 800))
    canvas[mask > 0] = warped[mask > 0]
    ok, enc = cv2.imencode(".jpg", canvas, [int(cv2.IMWRITE_JPEG_QUALITY), 80])
    res = detect_pose(enc.tobytes(), marker_mm=120.0, marker_id=7, fx=754.0, cx=600.0, cy=400.0)
    if res is None:
        print("self-test: FAILED (no detection)")
        return 1
    mid, pos, quat = res
    print("self-test: marker %d pos_mm=(%.1f, %.1f, %.1f) quat=(%.3f, %.3f, %.3f, %.3f)"
          % (mid, pos[0], pos[1], pos[2], quat[0], quat[1], quat[2], quat[3]))
    print("self-test: OK (marker detected and posed)")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description="ArUco marker pose bridge for the SL12 camera anchor")
    ap.add_argument("--in-port", type=int, default=IN_PORT, help="UDP port Unity sends JPEG chunks to (default 47801)")
    ap.add_argument("--out-port", type=int, default=OUT_PORT, help="UDP port pose JSON goes to (default 47802)")
    ap.add_argument("--marker-mm", type=float, default=120.0, help="printed marker side length in mm (default 120)")
    ap.add_argument("--marker-id", type=int, default=None, help="only accept this marker id (default: any)")
    ap.add_argument("--fx", type=float, default=DEFAULT_FX, help="camera focal length px (default %.1f)" % DEFAULT_FX)
    ap.add_argument("--cx", type=float, default=DEFAULT_CX, help="camera principal point x (default %.1f)" % DEFAULT_CX)
    ap.add_argument("--cy", type=float, default=DEFAULT_CY, help="camera principal point y (default %.1f)" % DEFAULT_CY)
    ap.add_argument("--self-test", action="store_true", help="synthetic-marker round trip, then exit")
    ap.add_argument("--write-marker", metavar="PNG", help="write the marker PNG (300 DPI, with quiet zone) and exit")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    if args.write_marker:
        img = make_marker_image(marker_id=args.marker_id if args.marker_id is not None else 7,
                                marker_mm=args.marker_mm)
        _cv2().imwrite(args.write_marker, img)
        print("marker written: %s (%d mm, id %s)" % (args.write_marker, args.marker_mm,
                                                     args.marker_id if args.marker_id is not None else 7))
        return 0
    return run_bridge(args)


if __name__ == "__main__":
    sys.exit(main())
