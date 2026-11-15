// WebcamBackdrop (webcam AR demo): a fullscreen quad on the spectator camera, textured with the live WebCamTexture
// from CameraAnchorSource, so the neck, ghost hands and HUD render over the Mac camera footage. Created by Bootstrap
// only when flags.cameraAnchor.webcamOverlay is on. The quad sits just inside the far clip plane (resized to cover the
// frustum whenever the camera view changes) so the whole virtual scene draws in front of it and it never clips.
//
// NOTE: the quad is on the default layer, so the glasses' eye cameras would also see it. The webcam demo runs without
// the glasses (spectator-only mode); if the two are ever combined, put the quad on a layer the eye cameras cull.
using UnityEngine;
using UntitledGuitarApp.Runtime.CameraAnchor;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Spectator
{
    /// <summary>Live webcam backdrop for the spectator camera (webcam AR demo). Main thread only.</summary>
    [DisallowMultipleComponent]
    public sealed class WebcamBackdrop : MonoBehaviour
    {
        Camera _cam;
        CameraAnchorSource _src;
        MeshRenderer _quad;
        Material _mat;
        Texture _shown;
        float _fovY = -1f;
        float _aspect = -1f;
        float _far = -1f;

        /// <summary>
        /// Half-extents (x = width, y = height) of a pinhole camera's frustum at <paramref name="distance"/> in front of
        /// the camera: halfH = tan(fovY / 2) * distance, halfW = halfH * aspect. Pure, tested.
        /// </summary>
        public static Vector2 HalfExtents(float fovYDeg, float aspect, float distance)
        {
            float halfH = Mathf.Tan(fovYDeg * 0.5f * Mathf.Deg2Rad) * distance;
            return new Vector2(halfH * aspect, halfH);
        }

        /// <summary>Creates the backdrop as a child of <paramref name="spectatorCam"/>; samples <paramref name="src"/>.LiveTexture.
        /// Never throws: a missing material (e.g. shader stripped from the build) just leaves the quad disabled.</summary>
        public static WebcamBackdrop Create(Camera spectatorCam, CameraAnchorSource src)
        {
            var go = new GameObject("WebcamBackdrop");
            go.transform.SetParent(spectatorCam.transform, false);
            var bd = go.AddComponent<WebcamBackdrop>();
            bd._cam = spectatorCam;
            bd._src = src;

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            var mr = go.AddComponent<MeshRenderer>();
            bd._quad = mr;
            Material template = Resources.Load<Material>("Backdrop");   // Assets/.../Resources/Backdrop.mat references UGA/Backdrop, so it is never stripped
            if (template != null)
            {
                bd._mat = new Material(template);
                mr.sharedMaterial = bd._mat;
            }
            else
            {
                UGALog.Warn("webcam_backdrop", "Backdrop material not found; quad stays disabled");
                mr.enabled = false;
            }

            go.transform.localPosition = new Vector3(0f, 0f, -2f);
            go.transform.localRotation = Quaternion.identity;
            bd.ApplyQuad();
            return bd;
        }

        void LateUpdate()
        {
            if (_cam == null || _quad == null) return;

            if (Mathf.Abs(_cam.fieldOfView - _fovY) > 0.01f ||
                Mathf.Abs(_cam.aspect - _aspect) > 0.001f ||
                Mathf.Abs(_cam.farClipPlane - _far) > 0.01f)
            {
                _fovY = _cam.fieldOfView;
                _aspect = _cam.aspect;
                _far = _cam.farClipPlane;
                ApplyQuad();
            }

            if (_src != null)
            {
                Texture tex = _src.LiveTexture;
                if (!ReferenceEquals(tex, _shown))
                {
                    _shown = tex;
                    _mat.mainTexture = tex;
                    _quad.enabled = tex != null;
                }
            }
        }

        void ApplyQuad()
        {
            if (_cam == null) return;
            float far = Mathf.Max(_cam.farClipPlane - 1f, 2f);
            Vector2 half = HalfExtents(_fovY, _aspect, far);
            transform.localPosition = new Vector3(0f, 0f, -far);
            transform.localScale = new Vector3(half.x * 2f, half.y * 2f, 1f);
        }
    }
}
