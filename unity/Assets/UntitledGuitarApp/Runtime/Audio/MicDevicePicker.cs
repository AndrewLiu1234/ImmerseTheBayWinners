using System;

namespace UntitledGuitarApp.Runtime.Audio
{
    /// <summary>Pure choices MicCapture makes at start-up (no Unity types, so they are unit-tested).</summary>
    public static class MicDevicePicker
    {
        /// <summary>
        /// Index of the first entry of <paramref name="devices"/> whose name contains <paramref name="wanted"/>
        /// (case-insensitive, ordinal), or -1 meaning "use the default device": empty/blank <paramref name="wanted"/>,
        /// empty or null list, or no match.
        /// </summary>
        public static int Pick(string[] devices, string wanted)
        {
            if (devices == null || devices.Length == 0) return -1;
            if (wanted == null) return -1;
            string w = wanted.Trim();
            if (w.Length == 0) return -1;
            for (int i = 0; i < devices.Length; i++)
            {
                string d = devices[i];
                if (d != null && d.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// The rate to ask <c>Microphone.Start</c> for. <c>Microphone.GetDeviceCaps</c> reports a range; per Unity's docs
        /// "when a value of zero is returned in the minFreq and maxFreq parameters, this indicates that the device supports
        /// any frequency". A single zero side is read as "unbounded on that side". The wanted rate is clamped into the range.
        /// </summary>
        public static int ChooseSampleRate(int minFreq, int maxFreq, int wanted)
        {
            int lo = minFreq > 0 ? minFreq : 0;
            int hi = maxFreq > 0 ? maxFreq : int.MaxValue;
            if (lo > hi) { int t = lo; lo = hi; hi = t; }
            if (wanted < lo) return lo;
            if (wanted > hi) return hi;
            return wanted;
        }
    }
}
