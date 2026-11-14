// DemoKeys: the operator's keys on the Mac keyboard -> DemoCommand. One command per frame, first match in table order.
//
// The project uses the legacy Input Manager (ProjectSettings activeInputHandler: 0), so UnityEngine.Input.GetKeyDown is the
// API (Documentation/en/ScriptReference/Input.GetKeyDown.html: "Returns true during the frame the user starts pressing down
// the key identified by the key KeyCode enum parameter"). The table and the lookup are pure so they can be unit-tested; only
// Poll touches the engine. Main thread only (it is called from Bootstrap.Update).
using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime
{
    public static class DemoKeys
    {
        /// <summary>Key -> command, checked in this order. Shown to the operator by <see cref="Legend"/>.</summary>
        public static readonly KeyCode[] Keys =
        {
            KeyCode.L, KeyCode.P, KeyCode.I, KeyCode.Escape, KeyCode.N, KeyCode.LeftBracket, KeyCode.RightBracket, KeyCode.S, KeyCode.R,
        };

        public static readonly DemoCommand[] Commands =
        {
            DemoCommand.Learn, DemoCommand.Play, DemoCommand.Idle, DemoCommand.Idle, DemoCommand.Next, DemoCommand.TempoDown,
            DemoCommand.TempoUp, DemoCommand.Skip, DemoCommand.Recenter,
        };

        /// <summary>One line for the operator (spectator screen or log): what each key does.</summary>
        public const string Legend = "L learn · P play · N next · [ ] tempo · S skip · R recenter · I/Esc idle";

        /// <summary>The first command whose key <paramref name="pressedThisFrame"/> reports as pressed; None if none is.</summary>
        public static DemoCommand CommandFor(Func<KeyCode, bool> pressedThisFrame)
        {
            if (pressedThisFrame == null) return DemoCommand.None;
            for (int i = 0; i < Keys.Length; i++)
            {
                if (pressedThisFrame(Keys[i])) return Commands[i];
            }
            return DemoCommand.None;
        }

        static readonly Func<KeyCode, bool> s_GetKeyDown = UnityEngine.Input.GetKeyDown;

        /// <summary>This frame's command from the real keyboard. No allocation (the delegate is cached).</summary>
        public static DemoCommand Poll()
        {
            return CommandFor(s_GetKeyDown);
        }
    }
}
