namespace UntitledGuitarApp.Runtime.Render
{
    /// <summary>
    /// What StereoRig does with the glasses' display at each once-a-second poll in a player, for the WHOLE run (plain C#, tested).
    /// Display.displays is updated by the player when a display is connected or disconnected (docs/apis/unity/multi-display.md
    /// section 2), so after the glasses' cable is re-seated the glasses come back as an inactive display that needs a new
    /// Display.Activate(); before this, StereoRig activated once and stopped polling, and the glasses stayed black until a restart.
    /// </summary>
    public sealed class DisplayWatch
    {
        public enum Action
        {
            /// <summary>Nothing to do (primary display, or the glasses' display is there and active, or the missing warning was given).</summary>
            None,
            /// <summary>The display is there but not active: call Display.Activate() on it.</summary>
            Activate,
            /// <summary>The display is not in Display.displays: warn (once per absence).</summary>
            WarnMissing,
        }

        int _lastIndex = -1;
        bool _missingWarned;

        /// <summary>How many times <see cref="Poll"/> has answered <see cref="Action.Activate"/> (1 = first start; more = re-plugs).</summary>
        public int Activations { get; private set; }

        /// <summary>True while the target display is missing (from the first poll that found it absent until it is back).</summary>
        public bool Missing { get; private set; }

        /// <summary>
        /// One poll. <paramref name="targetIndex"/> = render.glassesDisplay, <paramref name="displayCount"/> = Display.displays.Length,
        /// <paramref name="targetActive"/> = Display.displays[targetIndex].active (ignored when the display is missing).
        /// A change of <paramref name="targetIndex"/> starts over.
        /// </summary>
        public Action Poll(int targetIndex, int displayCount, bool targetActive)
        {
            if (targetIndex != _lastIndex)
            {
                _lastIndex = targetIndex;
                _missingWarned = false;
                Missing = false;
            }
            if (targetIndex <= 0) return Action.None;         // display 0 is the primary display: always on
            if (targetIndex >= displayCount)
            {
                Missing = true;
                if (_missingWarned) return Action.None;
                _missingWarned = true;
                return Action.WarnMissing;
            }
            Missing = false;
            _missingWarned = false;                            // a later unplug warns again
            if (targetActive) return Action.None;
            Activations++;
            return Action.Activate;
        }
    }
}
