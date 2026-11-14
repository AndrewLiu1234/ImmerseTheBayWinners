// FlagSet.cs: the runtime flag data model.
//
// CONTRACT. Other workers compile against these exact public names, field names, field types and default values
// (the copy they use is SeamStubs.cs). You may ADD members; never rename, retype, remove or re-default one.
//
// Everything is a public field of a [Serializable] class because UnityEngine.JsonUtility only (de)serializes public fields
// (and [SerializeField] privates) of [Serializable] types: no properties, no readonly, no dictionaries, no polymorphism.
// https://docs.unity3d.com/2021.3/Documentation/Manual/JSONSerialization.html
//
// Treat a FlagSet you got from FlagStore as READ-ONLY. FlagStore publishes a brand-new instance on every reload, so a
// reference you cached is a snapshot; re-read FlagStore.Current or handle FlagStore.Changed to follow hot reloads.
using System;

namespace UntitledGuitarApp.Runtime.Flags
{
    [Serializable] public sealed class AudioFlags
    {
        public string device = "MacBook";      // substring of the microphone device name to prefer
        public float noiseGateDb = 12f;
        public float pluckCentsTol = 45f;
        public float strumMinMargin = 0.15f;
        public float latencyOffsetMs = 0f;
    }

    [Serializable] public sealed class GhostFlags
    {
        public string color = "#4FC3F7";
        public float fill = 0.2f;
        public bool fingerColors = false;
        public float fadeAfterSuccess = 0.4f;
    }

    [Serializable] public sealed class RenderFlags
    {
        public bool stereo = true;             // false = 2D fallback: one camera, same image for both eyes
        public int glassesDisplay = 1;         // Unity display index of the glasses (0 = the Mac screen)
        public float ipdMm = 63f;
        public float fovDiagonalDeg = 52f;     // XREAL Air 2 Ultra
        public int eyeWidth = 1920;
        public int eyeHeight = 1080;
        public bool swapEyes = false;
        public float nearM = 0.1f;
        public float farM = 20f;
        public float convergenceM = 0f;        // 0 = parallel eye axes; > 0 = off-axis frusta converging at this distance
    }

    [Serializable] public sealed class ImuFlags
    {
        public int udpPort = 47800;
        public float staleMs = 250f;           // no packet for this long -> headLocked
        public float predictionMs = 10f;
        public bool invertYaw = false;
        public bool invertPitch = false;
        public bool invertRoll = false;
    }

    [Serializable] public sealed class FlagSet
    {
        public string profile = "dev";
        public string anchorMode = "aligned";  // "aligned" | "headLocked"
        public string audioSource = "mic";     // "mic" | "wav" (wav shows the SIMULATED INPUT badge)
        public string wavReplayFile = "fixtures-demo/learn-g.wav";   // relative to StreamingAssets
        public AudioFlags audio = new AudioFlags();
        public GhostFlags ghost = new GhostFlags();
        public RenderFlags render = new RenderFlags();
        public ImuFlags imu = new ImuFlags();
        public bool waitModeDefault = true;
        public float[] tempoPresets = { 0.5f, 0.6f, 0.75f, 0.9f, 1.0f };
        public bool leftHanded = false;
        public string spectator = "hostScreen";
        public bool debugOverlay = false;
        public string coach = "off";
        public string[] chords = null;         // optional (judge profile)
        public bool largeText = false;         // optional (judge profile)
    }
}
