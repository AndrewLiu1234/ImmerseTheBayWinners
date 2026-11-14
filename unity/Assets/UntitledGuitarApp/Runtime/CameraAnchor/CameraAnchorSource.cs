// CameraAnchorSource (SL12): WebCamTexture capture -> chunked JPEG over UDP to tools/camera-vision/camera_bridge.py
// -> pose JSON back -> PoseFilter -> NeckAnchor.SetAlignment (the same AlignedPose the gaze aligner produces).
//
//   Unity (this)  --JPEG chunks, UDP 47801-->  camera_bridge.py (cv2 ArUco + solvePnP)  --pose JSON, UDP 47802-->  Unity
//
// Threading follows Head/ImuUdpReceiver.cs: the receive thread owns its socket from bind to close and publishes RAW BYTES
// into a latest-value mailbox (JsonUtility must run on the main thread). The capture is throttled to ~30 Hz
// (flags.cameraAnchor.sendIntervalMs): JPEG encoding allocates by nature, so it never runs on the HUD hot path.
// The UVC camera on this hardware opens flakily (see STATUS 2026-11-14): retry every 2 s, log state changes only.
// Marker lost -> the last alignment freezes (NeckAnchor keeps it); the gaze aligner (A) still works and is overwritten
// by the next fresh camera pose.
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;
using UntitledGuitarApp.Runtime.Neck;

namespace UntitledGuitarApp.Runtime.CameraAnchor
{
    [DisallowMultipleComponent]
    public sealed class CameraAnchorSource : MonoBehaviour
    {
        /// <summary>Raw JPEG chunk size; base64 of this + the JSON wrapper stays under the 9216-byte loopback ceiling (STATUS).</summary>
        public const int PartBytes = 6000;
        const float OpenRetrySeconds = 2f;
        const int OpenMaxTries = 15;

        [Serializable] public sealed class PoseMessage
        {
            public double t;
            public int det;
            public int marker;
            public float[] pos_mm;
            public float[] quat;
        }

        /// <summary>Requested WebCamTexture mode, resolved from flags with safe fallbacks (pure logic; tested).</summary>
        public struct RequestedMode
        {
            public int width, height, fps;
            public RequestedMode(int width, int height, int fps) { this.width = width; this.height = height; this.fps = fps; }
        }

        /// <summary>Working mode for this device: 640x241 (the device default) delivers streak garbage on this setup;
        /// 1760x1328 is real footage (STATUS 2026-11-14, verified by direct ffmpeg grab vs Unity stream).</summary>
        public static readonly RequestedMode DefaultMode = new RequestedMode(1760, 1328, 30);

        /// <summary>Pure: flags -> requested mode; non-positive fields fall back to the default.</summary>
        public static RequestedMode ResolveRequestedMode(CameraAnchorFlags f)
        {
            return new RequestedMode(
                f.requestWidth > 0 ? f.requestWidth : DefaultMode.width,
                f.requestHeight > 0 ? f.requestHeight : DefaultMode.height,
                f.requestFps > 0 ? f.requestFps : DefaultMode.fps);
        }

        /// <summary>Latest-bytes mailbox (producer = receive thread, consumer = main thread); no waits, no growth.</summary>
        sealed class ByteSlot
        {
            const int DirtyBit = 4;
            readonly byte[][] _slots = { new byte[4096], new byte[4096], new byte[4096] };
            int _write, _read = 1, _middle = 2;

            public void Publish(byte[] data, int length)
            {
                byte[] target = _slots[_write];
                Array.Copy(data, 0, target, 0, Math.Min(length, target.Length));
                int previous = Interlocked.Exchange(ref _middle, _write | DirtyBit);
                _write = previous & 3;
            }

            /// <summary>Copies the newest payload into <paramref name="into"/>; false when nothing new.</summary>
            public bool TryTake(byte[] into, out int length)
            {
                if ((Volatile.Read(ref _middle) & DirtyBit) == 0) { length = 0; return false; }
                int previous = Interlocked.Exchange(ref _middle, _read);
                _read = previous & 3;
                Array.Copy(_slots[_read], into, _slots[_read].Length);
                length = _slots[_read].Length;
                return true;
            }
        }

        NeckAnchor _anchor;
        Transform _head;
        CameraAnchorFlags _f;
        WebCamTexture _tex;
        Texture2D _sendTex;
        Color32[] _pixels;
        UdpClient _sender;
        Thread _thread;
        Socket _receive;
        volatile bool _stop;
        ByteSlot _slot = new ByteSlot();
        byte[] _recvBuf = new byte[4096];
        PoseFilter _filter;
        bool _wasAligned;
        bool _openLogged;
        bool _gaveUpLogged;
        int _openTries;
        float _nextOpenAt;
        float _nextSendAt;
        float _nextSetAt;
        double _nowSec;

        /// <summary>Creates the camera anchor on a new child (flags.cameraAnchor.enabled gates this).</summary>
        public static CameraAnchorSource Create(Transform parent, Transform head, NeckAnchor anchor)
        {
            var go = new GameObject("CameraAnchor");
            if (parent != null) go.transform.SetParent(parent, false);
            var src = go.AddComponent<CameraAnchorSource>();
            src._head = head;
            src._anchor = anchor;
            src._f = FlagStore.Current != null ? FlagStore.Current.cameraAnchor : new CameraAnchorFlags();
            src._filter = new PoseFilter(src._f.posAlpha, src._f.rotAlpha, src._f.maxJumpMm, src._f.maxJumpDeg, src._f.lostTimeoutS);
            return src;
        }

        void Start()
        {
            _sender = new UdpClient();
            _stop = false;
            _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "camera-anchor-rx" };
            _thread.Start();
        }

        void Update()
        {
            FlagSet flags = FlagStore.Current;
            if (flags == null || !flags.cameraAnchor.enabled) { ShutDown(); return; }
            _f = flags.cameraAnchor;
            _nowSec = Time.unscaledTimeAsDouble;
            UpdateCameraOpen();
            if (_tex != null && _tex.isPlaying && _tex.didUpdateThisFrame && Time.unscaledTime >= _nextSendAt)
            {
                _nextSendAt = Time.unscaledTime + Mathf.Max(_f.sendIntervalMs, 16) / 1000f;
                SendFrame();
            }
            DrainPoses();
            bool lost = _filter.Lost(_nowSec);
            if (_wasAligned && lost)
            {
                _wasAligned = false;
                UGALog.Event("camera_anchor_lost", "rejected", _filter.Rejected);
            }
        }

        void UpdateCameraOpen()
        {
            if (_tex != null && _tex.isPlaying) return;
            if (_openTries >= OpenMaxTries && !_gaveUpLogged)
            {
                _gaveUpLogged = true;
                UGALog.Warn("camera_anchor", "gave up opening the camera after " + OpenMaxTries + " tries; gaze anchor only");
                return;
            }
            if (Time.unscaledTime < _nextOpenAt) return;
            _nextOpenAt = Time.unscaledTime + OpenRetrySeconds;
            _openTries++;
            try
            {
                if (_tex == null)
                {
                    string name = PickDevice(_f.cameraName);
                    if (name == null)
                    {
                        if (!_openLogged) { _openLogged = true; UGALog.Warn("camera_anchor", "no WebCamTexture device matches \"" + _f.cameraName + "\""); }
                        return;
                    }
                    RequestedMode mode = ResolveRequestedMode(_f);
                    _tex = new WebCamTexture(name, mode.width, mode.height, mode.fps);
                }
                if (!_tex.isPlaying) _tex.Play();
                if (!_tex.isPlaying && !_openLogged)
                {
                    _openLogged = true;
                    UGALog.Warn("camera_anchor", "WebCamTexture.Play did not start the stream; retrying every 2 s");
                }
                else if (_tex.isPlaying && _openLogged)
                {
                    _openLogged = false;
                    UGALog.Event("camera_anchor", "open", true, "dim", _tex.width + "x" + _tex.height);
                }
            }
            catch (Exception e)
            {
                if (!_openLogged) { _openLogged = true; UGALog.Warn("camera_anchor", "open threw: " + e.Message); }
            }
        }

        static string PickDevice(string wanted)
        {
            WebCamDevice[] devices = WebCamTexture.devices;
            for (int i = 0; i < devices.Length; i++)
                if (devices[i].name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                    return devices[i].name;
            return devices.Length > 1 ? devices[1].name : (devices.Length == 1 ? devices[0].name : null);
        }

        void SendFrame()
        {
            if (_sendTex == null || _sendTex.width != _tex.width || _sendTex.height != _tex.height)
            {
                _sendTex = new Texture2D(_tex.width, _tex.height, TextureFormat.RGB24, false);
                _pixels = new Color32[_tex.width * _tex.height];
            }
            _sendTex.SetPixels32(_tex.GetPixels32(_pixels));
            _sendTex.Apply(false, false);
            byte[] jpeg = _sendTex.EncodeToJPG(_f.jpegQuality);
            int parts = (jpeg.Length + PartBytes - 1) / PartBytes;
            int frameId = Time.frameCount & 0x3FFFFFFF;
            for (int i = 0; i < parts; i++)
            {
                int len = Math.Min(PartBytes, jpeg.Length - i * PartBytes);
                string b64 = Convert.ToBase64String(jpeg, i * PartBytes, len);
                string json = "{\"f\":" + frameId + ",\"p\":" + i + ",\"n\":" + parts + ",\"b64\":\"" + b64 + "\"}";
                byte[] dg = Encoding.UTF8.GetBytes(json);
                _sender.Send(dg, dg.Length, "127.0.0.1", _f.udpInPort);
            }
        }

        void DrainPoses()
        {
            if (!_slot.TryTake(_recvBuf, out int length)) return;
            string json = Encoding.UTF8.GetString(_recvBuf, 0, length).TrimEnd('\0');
            PoseMessage msg;
            try { msg = JsonUtility.FromJson<PoseMessage>(json); }
            catch { return; }
            if (msg == null || msg.det != 1 || msg.pos_mm == null || msg.quat == null ||
                msg.pos_mm.Length < 3 || msg.quat.Length < 4) return;
            var sample = new CameraPose
            {
                Pos = new Vector3(msg.pos_mm[0], msg.pos_mm[1], msg.pos_mm[2]) * 0.001f,
                Rot = new Quaternion(msg.quat[0], msg.quat[1], msg.quat[2], msg.quat[3])
            };
            if (!_filter.Feed(sample, _nowSec)) return;
            if (_filter.Lost(_nowSec) || Time.unscaledTime < _nextSetAt) return;
            if (!_filter.TryGetSmoothed(out CameraPose smoothed)) return;
            _nextSetAt = Time.unscaledTime + 0.033f;   // ~30 Hz into the anchor
            if (_anchor == null) return;
            Vector3 headPos = _head != null ? _head.position : Vector3.zero;
            Quaternion headRot = _head != null ? _head.rotation : Quaternion.identity;
            _anchor.SetAlignment(MarkerToNeck.ToAlignedPose(smoothed, headPos, headRot, _f));
            if (!_wasAligned)
            {
                _wasAligned = true;
                UGALog.Event("camera_anchor_aligned", "rejected", _filter.Rejected);
            }
        }

        void ReceiveLoop()
        {
            Socket socket = null;
            try
            {
                while (!_stop)
                {
                    if (socket == null)
                    {
                        try
                        {
                            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                            socket.Bind(new IPEndPoint(IPAddress.Loopback, _f.udpOutPort));
                            _receive = socket;
                        }
                        catch (SocketException)
                        {
                            socket = null;
                            _receive = null;
                            Thread.Sleep(2000);
                            continue;
                        }
                    }
                    if (!socket.Poll(20000, SelectMode.SelectRead)) continue;
                    int n = socket.Receive(_recvBuf);
                    if (n > 0) _slot.Publish(_recvBuf, n);
                }
            }
            catch (Exception)
            {
                // socket died with the app; the main thread shuts us down via _stop
            }
        }

        void ShutDown()
        {
            _stop = true;
            if (_tex != null) { try { _tex.Stop(); } catch {} _tex = null; }
            if (_sender != null) { try { _sender.Close(); } catch {} _sender = null; }
            if (_receive != null) { try { _receive.Close(); } catch {} _receive = null; }
        }

        void OnApplicationQuit()
        {
            ShutDown();
        }

        void OnDestroy()
        {
            ShutDown();
        }
    }
}
