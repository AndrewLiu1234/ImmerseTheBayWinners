// MarkerToNeck (SL12): the camera-vision bridge's marker pose -> the neck's AlignedPose, as pure math.
//
// Frames (see docs/apis/glasses/imu-protocol.md 7-8 for the IMU/Unity axes; the camera frame is cv2's):
//   camera frame (cv2):     +x right, +y DOWN, +z forward   (pos_mm, quat from the bridge)
//   Unity camera frame:     +x right, +y up,   +z forward
//   head frame (Unity):     the IMU head's frame (head.rotation); the glasses camera is rigidly attached to it
//   neck frame (Unity):     nut origin, +x along the neck toward the bridge, +y up out of the fretboard,
//                           +z across toward string 1 (high E) - CoreNeckLayout's frame
//
// IMPORTANT: cv2 axes -> Unity axes is a REFLECTION (y flips), not a rotation, so no single quaternion can do it.
// The quaternion is converted through its matrix: R_unity = B R_cv2 B, B = diag(1, -1, 1) (negate the y row and
// column). Vectors flip the y component directly. The matrix stays a proper rotation (similarity preserves det).
//
// The camera pose in the world = head.position + head.rotation * (camToHead + markerCamUnity).
// The marker sits at a fixed offset from the nut in the NECK frame (flags cameraAnchor.markerToNut*), so
//   nutWorld = markerWorld + markerRotWorld * markerToNut
//   neckRotWorld = markerRotWorld * q(markerToNeckDeg*)
// All offsets are flags; the defaults are guesses tuned on the device (the SL12 L5 calibration step).
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Neck;

namespace UntitledGuitarApp.Runtime.CameraAnchor
{
    public static class MarkerToNeck
    {
        const float Mm = 0.001f;

        /// <summary>
        /// Marker pose in the bridge's camera frame (metres, quaternion) -> the neck's AlignedPose in the world.
        /// <paramref name="headPos"/>/<paramref name="headRot"/> = the head transform at this moment (world).
        /// </summary>
        public static AlignedPose ToAlignedPose(CameraPose markerCam, Vector3 headPos, Quaternion headRot,
                                                CameraAnchorFlags f)
        {
            Vector3 camToHead = new Vector3(f.camToHeadX, f.camToHeadY, f.camToHeadZ) * Mm;
            Vector3 markerToNut = new Vector3(f.markerToNutX, f.markerToNutY, f.markerToNutZ) * Mm;
            Quaternion markerToNeck = Quaternion.Euler(f.markerToNeckDegX, f.markerToNeckDegY, f.markerToNeckDegZ);

            Vector3 markerCamUnity = new Vector3(markerCam.Pos.x, -markerCam.Pos.y, markerCam.Pos.z);
            Quaternion markerRotUnity = FlipCameraQuaternion(markerCam.Rot);

            Vector3 markerWorld = headPos + headRot * (camToHead + markerCamUnity);
            Quaternion markerRotWorld = headRot * markerRotUnity;

            Vector3 nutWorld = markerWorld + markerRotWorld * markerToNut;
            Quaternion neckRotWorld = markerRotWorld * markerToNeck;
            return new AlignedPose(nutWorld, neckRotWorld);
        }

        /// <summary>
        /// cv2 camera quaternion -> Unity axes quaternion: R_unity = B R_cv2 B with B = diag(1, -1, 1) (negate the y
        /// row and column). The result is a proper rotation matrix, converted back to a quaternion (port of
        /// rotation_matrix_to_quat in tools/camera-vision/camera_bridge.py).
        /// </summary>
        public static Quaternion FlipCameraQuaternion(Quaternion q)
        {
            Matrix4x4 r = Matrix4x4.Rotate(q);
            r[0, 1] = -r[0, 1];
            r[2, 1] = -r[2, 1];
            r[1, 0] = -r[1, 0];
            r[1, 2] = -r[1, 2];
            return QuaternionFromMatrix(r);
        }

        public static Quaternion QuaternionFromMatrix(Matrix4x4 r)
        {
            float tr = r[0, 0] + r[1, 1] + r[2, 2];
            if (tr > 0f)
            {
                float s = Mathf.Sqrt(tr + 1f) * 2f;
                return new Quaternion((r[2, 1] - r[1, 2]) / s, (r[0, 2] - r[2, 0]) / s,
                                      (r[1, 0] - r[0, 1]) / s, 0.25f * s);
            }
            if (r[0, 0] > r[1, 1] && r[0, 0] > r[2, 2])
            {
                float s = Mathf.Sqrt(1f + r[0, 0] - r[1, 1] - r[2, 2]) * 2f;
                return new Quaternion(0.25f * s, (r[0, 1] + r[1, 0]) / s, (r[0, 2] + r[2, 0]) / s,
                                      (r[2, 1] - r[1, 2]) / s);
            }
            if (r[1, 1] > r[2, 2])
            {
                float s = Mathf.Sqrt(1f + r[1, 1] - r[0, 0] - r[2, 2]) * 2f;
                return new Quaternion((r[0, 1] + r[1, 0]) / s, 0.25f * s, (r[1, 2] + r[2, 1]) / s,
                                      (r[0, 2] - r[2, 0]) / s);
            }
            float s2 = Mathf.Sqrt(1f + r[2, 2] - r[0, 0] - r[1, 1]) * 2f;
            return new Quaternion((r[0, 2] + r[2, 0]) / s2, (r[1, 2] + r[2, 1]) / s2, 0.25f * s2,
                                  (r[1, 0] - r[0, 1]) / s2);
        }
    }
}
