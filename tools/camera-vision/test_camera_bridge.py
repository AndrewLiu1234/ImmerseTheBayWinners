"""Tests for tools/camera-vision (no sockets, no camera hardware).

Run from the repo root:  python3 -m unittest discover -s tools/camera-vision -p 'test_*.py'
"""
import base64
import json
import math
import unittest

import camera_bridge as cb


def datagram(fid, part, n, chunk):
    return {"f": fid, "p": part, "n": n, "b64": base64.b64encode(chunk).decode("ascii")}


class ChunkingTest(unittest.TestCase):
    def test_split_join_round_trip_single_and_multi_part(self):
        for size in (1, 40000, 40001, 120000):
            data = bytes((i * 7) % 256 for i in range(size))
            parts = cb.split_jpeg(data)
            self.assertEqual(cb.join_frame(list(enumerate(parts))), data)

    def test_join_refuses_gaps_and_bad_indexes(self):
        parts = [(0, b"a"), (1, b"b"), (2, b"c")]
        self.assertIsNone(cb.join_frame(parts[:2], expected=3))      # incomplete
        self.assertIsNone(cb.join_frame([(0, b"a"), (5, b"b")], expected=2))  # index out of range
        self.assertIsNone(cb.join_frame([]))
        self.assertIsNone(cb.join_frame([(0, b"a"), (1, b"a"), (1, b"b")], expected=3))  # duplicate index


class FrameAssemblerTest(unittest.TestCase):
    def test_reassembles_out_of_order_and_delivers_once(self):
        asm = cb.FrameAssembler()
        chunks = cb.split_jpeg(b"\x01\x02\x03" * 30000)
        n = len(chunks)
        self.assertGreater(n, 1)
        last = False
        for i in reversed(range(n)):          # parts arrive out of order; feed returns True only on completion
            last = asm.feed(datagram(7, i, n, chunks[i]))
        self.assertTrue(last)
        item = asm.take()
        self.assertIsNotNone(item)
        self.assertEqual(item[0], 7)
        self.assertEqual(item[1], b"\x01\x02\x03" * 30000)
        self.assertIsNone(asm.take())         # delivered exactly once

    def test_stale_frame_is_dropped(self):
        asm = cb.FrameAssembler(stale_s=0.01)
        chunks = cb.split_jpeg(b"x" * 80000)
        n = len(chunks)
        asm.feed(datagram(1, 0, n, chunks[0]))
        import time
        time.sleep(0.03)
        asm.feed(datagram(2, 0, 2, chunks[0]))   # triggers stale sweep of frame 1
        for i in range(1, n):
            asm.feed(datagram(1, i, n, chunks[i]))
        self.assertIsNone(asm.take())         # frame 1 was dropped as stale
        # frame 2 finishes and delivers
        self.assertTrue(asm.feed(datagram(2, 1, 2, chunks[1])))
        self.assertIsNotNone(asm.take())

    def test_rejects_bad_datagrams(self):
        asm = cb.FrameAssembler()
        self.assertFalse(asm.feed({"f": "x", "p": 0, "n": 1, "b64": "a"}))
        self.assertFalse(asm.feed(datagram(1, 0, 1000, b"x")))   # too many parts


class QuaternionTest(unittest.TestCase):
    def test_identity(self):
        import numpy as np
        q = cb.rotation_matrix_to_quat(np.eye(3))
        for a, b in zip(q, (0.0, 0.0, 0.0, 1.0)):
            self.assertAlmostEqual(a, b, places=6)

    def test_90_degrees_about_z(self):
        import numpy as np
        c, s = 0.0, 1.0
        r = np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]], dtype=np.float64)
        q = cb.rotation_matrix_to_quat(r)
        # 90 deg about z: quaternion (0, 0, sin(45), cos(45))
        for a, b in zip(q, (0.0, 0.0, math.sqrt(0.5), math.sqrt(0.5))):
            self.assertAlmostEqual(a, b, places=5)

    def test_round_trip_via_rodrigues(self):
        import cv2
        import numpy as np
        rvec = np.array([[0.3], [-0.2], [0.8]])
        rmat, _ = cv2.Rodrigues(rvec)
        q = cb.rotation_matrix_to_quat(rmat)
        rmat2 = cb.quat_to_rmat(q)
        for a, b in zip(rmat.flatten(), rmat2.flatten()):
            self.assertAlmostEqual(a, b, places=4)


class DetectPoseTest(unittest.TestCase):
    def test_synthetic_marker_is_detected_and_posed(self):
        import cv2
        import numpy as np
        aruco_dict = cb._aruco_dict(cv2)
        marker = cb._aruco_draw(cv2, aruco_dict, 7, 400)
        canvas = np.full((800, 1200), 255, dtype=np.uint8)
        src = np.float32([[0, 0], [399, 0], [399, 399], [0, 399]])
        dst = np.float32([[700, 250], [1020, 210], [1080, 600], [640, 640]])
        H = cv2.getPerspectiveTransform(src, dst)
        warped = cv2.warpPerspective(marker, H, (1200, 800))
        full = np.full(marker.shape, 255, dtype=np.uint8)
        mask = cv2.warpPerspective(full, H, (1200, 800))
        canvas[mask > 0] = warped[mask > 0]
        ok, enc = cv2.imencode(".jpg", canvas, [int(cv2.IMWRITE_JPEG_QUALITY), 80])
        res = cb.detect_pose(enc.tobytes(), marker_mm=120.0, marker_id=7, fx=754.0, cx=600.0, cy=400.0)
        self.assertIsNotNone(res)
        mid, pos, quat = res
        self.assertEqual(mid, 7)
        self.assertGreater(pos[2], 100.0)     # plausible depth, camera looks down +z
        self.assertLess(abs(pos[2]), 5000.0)
        # quaternion is unit
        self.assertAlmostEqual(sum(c * c for c in quat), 1.0, places=4)

    def test_marker_id_filter(self):
        import cv2
        aruco_dict = cb._aruco_dict(cv2)
        marker = cb._aruco_draw(cv2, aruco_dict, 7, 400)
        marker = cv2.copyMakeBorder(marker, 60, 60, 60, 60, cv2.BORDER_CONSTANT, value=255)
        ok, enc = cv2.imencode(".jpg", marker)
        self.assertIsNotNone(cb.detect_pose(enc.tobytes(), marker_id=7))
        self.assertIsNone(cb.detect_pose(enc.tobytes(), marker_id=11))

    def test_no_marker_returns_none(self):
        import cv2
        import numpy as np
        ok, enc = cv2.imencode(".jpg", np.full((400, 600), 128, dtype=np.uint8))
        self.assertIsNone(cb.detect_pose(enc.tobytes()))

    def test_garbage_bytes_return_none(self):
        self.assertIsNone(cb.detect_pose(b"not a jpeg"))


class MarkerImageTest(unittest.TestCase):
    def test_marker_image_size_and_quiet_zone(self):
        import cv2
        img = cb.make_marker_image(marker_id=7, marker_mm=120.0, dpi=300, margin_mm=20.0)
        total_mm = 160.0
        want = int(round(total_mm / 25.4 * 300))
        self.assertEqual(img.shape, (want, want))
        # corners are white (quiet zone) and the centre is the marker pattern
        self.assertEqual(img[2, 2], 255)
        centre = img[want // 2, want // 2]
        self.assertIn(int(centre), (0, 255))
        # the drawn marker is itself detectable
        aruco_dict = cb._aruco_dict(cv2)
        corners, ids = cb._aruco_detect(cv2, img, aruco_dict)
        self.assertIsNotNone(ids)
        self.assertIn(7, [int(i[0]) for i in ids])


if __name__ == "__main__":
    unittest.main()
