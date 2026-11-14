using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UntitledGuitarApp.Runtime.Logging;
using UntitledGuitarApp.Runtime.Render;

namespace UntitledGuitarApp.Editor
{
    /// <summary>
    /// Menu item "UGA/Glasses Game View": opens a borderless Game view set to "Display 2" (Camera.targetDisplay 1) at 3840x1080 on the
    /// monitor the glasses are plugged into, so Play Mode renders the side-by-side frame straight onto the glasses.
    ///
    /// What is documented and what is not (full excerpts and sources: docs/apis/unity/editorwindow-popup.md):
    ///  - EditorWindow.ShowPopup, EditorWindow.position, EditorWindow.Close, EditorGUIUtility.GetMainWindowPosition, SessionState,
    ///    EditorUtility.InstanceIDToObject are documented Unity 2021.3 API.
    ///  - UnityEditor.GameView is an INTERNAL class (a known community technique creates it with ScriptableObject.CreateInstance and
    ///    reflection). Its internals used here were read in Unity's C# reference source at tag 2021.3.3f1 and confirmed in this
    ///    editor's UnityEditor.CoreModule.dll: the private property GameView.showToolbar, the internal method
    ///    GameView.SetTargetDisplay(int) and the protected property PlayModeView.targetDisplay. Every reflective lookup below is
    ///    null-checked and every call is inside a try/catch; if one fails, a step-by-step manual procedure is logged instead of throwing.
    ///    The menu items never throw: every failure (monitor discovery included) ends in that logged manual procedure.
    ///  - Finding the glasses' monitor on macOS: InternalEditorUtility.GetBoundsOfDesktopAtPoint throws "ArgumentException: Not
    ///    implemented on OSX" there (seen in the user's editor log, Sat 11:06), Display.displays has length 1 in the Editor and
    ///    Screen.GetDisplayLayout is documented "only supported on Linux, macOS and Windows Standalone". So the editor asks AppKit:
    ///    /usr/bin/osascript -l JavaScript prints NSScreen.screens' frames (3 s timeout), Runtime/Render/MonitorLayout parses them
    ///    and flips y to the editor's screen space. Other editors keep the GetBoundsOfDesktopAtPoint scan, wrapped in try/catch.
    ///  - Manual fallback: menu "UGA/Glasses Game View At Clipboard Rect" opens the view at the rectangle on the clipboard
    ///    ("x y width height", or Unity's own Rect text); every success and failure logs the rectangle to copy.
    ///
    /// Not verified without a person at the Mac: nobody has opened the view on the glasses yet (docs/WORKSPLIT.md: L5). It compiles
    /// against Unity 2021.3.3f1's own assemblies (cs-check --editor); MonitorLayout runs in the EditMode tests (HardeningTests).
    /// </summary>
    public static class GlassesGameView
    {
        public const string MenuPath = "UGA/Glasses Game View";
        public const string CloseMenuPath = "UGA/Close Glasses Game View";
        public const string ClipboardMenuPath = "UGA/Glasses Game View At Clipboard Rect";

        /// <summary>AppKit's screens, read with JavaScript for Automation (see MonitorLayout.NsScreenScript).</summary>
        const string OsascriptPath = "/usr/bin/osascript";
        const int OsascriptTimeoutMs = 3000;

        /// <summary>The Game view dropdown entry "Display 2" is Camera.targetDisplay 1 (render.glassesDisplay).</summary>
        public const int GlassesDisplayIndex = 1;
        public const int GlassesWidth = 3840;
        public const int GlassesHeight = 1080;

        const string SessionKeyInstanceId = "UGA.GlassesGameView.InstanceId";
        const string GameViewTypeName = "UnityEditor.GameView";

        // Coarse grid used to discover the monitors (points): finer than any monitor we care about is tall (1080), wider than any desk.
        const int ScanMinX = -12000, ScanMaxX = 18000, ScanStepX = 400;
        const int ScanMinY = -6000, ScanMaxY = 8000, ScanStepY = 270;

        [MenuItem(MenuPath)]
        static void MenuOpen()
        {
            try { Open(); }
            catch (Exception e) { Fail("an unexpected exception occurred (" + e.GetType().Name + ": " + e.Message + ")", null); }
        }

        [MenuItem(CloseMenuPath)]
        static void MenuClose()
        {
            try { Close(); }
            catch (Exception e) { Debug.LogWarning("[UGA] Close Glasses Game View: " + e.GetType().Name + ": " + e.Message); }
        }

        [MenuItem(ClipboardMenuPath)]
        static void MenuOpenFromClipboard()
        {
            try { OpenFromClipboard(); }
            catch (Exception e) { Fail("an unexpected exception occurred (" + e.GetType().Name + ": " + e.Message + ")", null); }
        }

        /// <summary>
        /// Opens the glasses Game view (closing a previous one first) on the monitor that is 3840 wide. Returns true when the window
        /// was opened. On any failure it opens nothing and logs the steps for doing it by hand. Never throws.
        /// </summary>
        public static bool Open()
        {
            try
            {
                Close();

                string monitorReport;
                Rect monitor;
                if (!TryFindGlassesMonitor(out monitor, out monitorReport))
                {
                    Fail("no " + GlassesWidth + " x " + GlassesHeight + " monitor was found", monitorReport);
                    return false;
                }
                Debug.Log("[UGA] Glasses Game View: " + monitorReport);
                return OpenOn(monitor);
            }
            catch (Exception e)
            {
                Fail("an exception occurred while looking for the glasses (" + e.GetType().Name + ": " + e.Message + ")", null);
                return false;
            }
        }

        /// <summary>
        /// The manual route: opens the glasses Game view at the rectangle on the clipboard (EditorGUIUtility.systemCopyBuffer):
        /// four numbers "x y width height" in points (editor screen space), or Unity's own "(x:..., y:..., width:..., height:...)".
        /// Copy one of the lines this class logs, edit it if the window landed in the wrong place, and run the menu item.
        /// </summary>
        public static bool OpenFromClipboard()
        {
            string text = EditorGUIUtility.systemCopyBuffer;
            double x, y, w, h;
            if (!MonitorLayout.TryParseRect(text, out x, out y, out w, out h))
            {
                string shown = text == null ? "(empty)" : (text.Length > 80 ? text.Substring(0, 80) + "..." : text);
                Fail("the clipboard does not hold a rectangle (four numbers: x y width height); it holds: " + shown, null);
                return false;
            }
            // The clipboard rect is the WINDOW itself (normally 3840 x 1080), not a monitor.
            return OpenWindowAt(new Rect((float)x, (float)y, (float)w, (float)h));
        }

        /// <summary>
        /// Opens the glasses Game view on a monitor you name yourself (screen coordinates in points, as EditorWindow.position uses):
        /// for example when the automatic search does not find the glasses. Never throws.
        /// </summary>
        public static bool OpenOn(Rect monitor)
        {
            // The window must be exactly 3840 x 1080 and start at the monitor's top-left corner. If the monitor rect is shorter
            // than 1080 (a work area without the menu bar), line the bottoms up instead.
            Rect rect = new Rect(monitor.x, monitor.height >= GlassesHeight ? monitor.y : monitor.yMax - GlassesHeight, GlassesWidth, GlassesHeight);
            return OpenWindowAt(rect);
        }

        /// <summary>"x y width height" in the invariant culture: the text the clipboard menu item reads back.</summary>
        public static string RectText(Rect r)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} {1} {2} {3}", r.x, r.y, r.width, r.height);
        }

        static bool OpenWindowAt(Rect rect)
        {
            try { Close(); }
            catch (Exception e) { Debug.LogWarning("[UGA] Glasses Game View: closing the previous window failed: " + e.Message); }

            EditorWindow window = null;
            try
            {
                if (BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget) != BuildTargetGroup.Standalone)
                {
                    Fail("the active build target is " + EditorUserBuildSettings.activeBuildTarget + ", and the Game view only offers 'Display 2' for Standalone targets",
                         "File > Build Settings > macOS > Switch Platform, then run this again.");
                    return false;
                }

                Type gameViewType = typeof(EditorWindow).Assembly.GetType(GameViewTypeName, false);
                if (gameViewType == null)
                {
                    Fail("the internal type " + GameViewTypeName + " does not exist in this Unity version (it was checked in 2021.3.3f1)", null);
                    return false;
                }

                window = ScriptableObject.CreateInstance(gameViewType) as EditorWindow;
                if (window == null)
                {
                    Fail("ScriptableObject.CreateInstance(" + GameViewTypeName + ") did not return an EditorWindow", null);
                    return false;
                }

                string displayError;
                if (!TrySetTargetDisplay(window, gameViewType, GlassesDisplayIndex, out displayError))
                {
                    UnityEngine.Object.DestroyImmediate(window);
                    Fail("could not select 'Display 2' on the new Game view: " + displayError, null);
                    return false;
                }

                bool toolbarHidden = TryHideToolbar(window, gameViewType);

                window.position = rect;       // documented example order: position first ...
                window.ShowPopup();           // ... then ShowPopup (borderless, no title bar)
                window.position = rect;       // and again afterwards: a shown window is moved through its container window

                SessionState.SetInt(SessionKeyInstanceId, window.GetInstanceID());

                string note = toolbarHidden ? "" : " The Game view toolbar could not be hidden (about 21 px of toolbar will show in both eyes).";
                Debug.Log("[UGA] Glasses Game View opened on Display 2 at " + rect + "." + note
                    + " Click the Game view on the Mac screen once so keyboard input goes there. Run it again after a script recompile if the glasses go black."
                    + " If it landed in the wrong place: copy \"" + RectText(rect) + "\", correct the numbers, and use " + ClipboardMenuPath + ".");
                UGALog.Event("glasses_view.open", "rect", rect.ToString(), "toolbarHidden", toolbarHidden, "displayIndex", GlassesDisplayIndex);
                return true;
            }
            catch (Exception e)
            {
                if (window != null)
                    UnityEngine.Object.DestroyImmediate(window);
                Fail("an exception occurred (" + e.GetType().Name + ": " + e.Message + ")", null);
                return false;
            }
        }

        /// <summary>Closes the glasses Game view if this class opened one and it is still open.</summary>
        public static void Close()
        {
            int id = SessionState.GetInt(SessionKeyInstanceId, 0);
            if (id == 0)
                return;
            SessionState.SetInt(SessionKeyInstanceId, 0);
            EditorWindow window = EditorUtility.InstanceIDToObject(id) as EditorWindow;
            if (window != null)
                window.Close();
        }

        /// <summary>True while the window this class opened is still open.</summary>
        public static bool IsOpen
        {
            get
            {
                int id = SessionState.GetInt(SessionKeyInstanceId, 0);
                return id != 0 && EditorUtility.InstanceIDToObject(id) as EditorWindow != null;
            }
        }

        // After a script recompile the window's OnEnable runs again and the toolbar comes back; hide it again.
        [InitializeOnLoadMethod]
        static void AfterDomainReload()
        {
            EditorApplication.delayCall += () =>
            {
                int id = SessionState.GetInt(SessionKeyInstanceId, 0);
                if (id == 0)
                    return;
                EditorWindow window = EditorUtility.InstanceIDToObject(id) as EditorWindow;
                if (window == null)
                {
                    SessionState.SetInt(SessionKeyInstanceId, 0);
                    return;
                }
                TryHideToolbar(window, window.GetType());
                window.Repaint();
            };
        }

        // ------------------------------------------------------------------------------------------------------------
        // Finding the glasses' monitor
        // ------------------------------------------------------------------------------------------------------------

        // Never throws: every failure comes back as false plus a report for the log.
        static bool TryFindGlassesMonitor(out Rect monitor, out string report)
        {
            monitor = default(Rect);
            List<MonitorFrame> monitors;
            string source, error;
            bool listed;
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                source = "AppKit NSScreen.screens via " + OsascriptPath + " -l JavaScript";
                listed = TryListMacScreens(out monitors, out error);
            }
            else
            {
                source = "InternalEditorUtility.GetBoundsOfDesktopAtPoint";
                listed = TryScanDesktops(out monitors, out error);
            }
            if (!listed)
            {
                report = "Listing the monitors failed (" + source + "): " + error;
                return false;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("monitors from ").Append(source).Append(" (editor screen space, points): ").Append(MonitorLayout.Describe(monitors));
            int pick = MonitorLayout.ChooseGlasses(monitors, GlassesWidth, GlassesHeight);
            if (pick < 0)
            {
                // Help for the usual causes: the glasses in 2D mode (1920 wide) or mirrored; give the rectangle to type by hand.
                for (int i = 0; i < monitors.Count; i++)
                {
                    if (monitors[i].IsPrimary) continue;
                    MonitorFrame m = monitors[i];
                    sb.Append("\n  Monitor #").Append(m.Index).Append(" is not ").Append(GlassesWidth).Append(" x ").Append(GlassesHeight)
                      .Append(": if it is the glasses, switch them to side-by-side 3D (hold brightness-up about 3 s) and run the menu again,")
                      .Append(" or copy \"").Append(RectText(new Rect((float)m.X, (float)m.Y, GlassesWidth, GlassesHeight)))
                      .Append("\" and use ").Append(ClipboardMenuPath).Append('.');
                }
                if (monitors.Count < 2)
                    sb.Append("\n  Only one monitor: the glasses are unplugged, asleep, or set to mirror the Mac screen.");
                report = sb.ToString();
                return false;
            }
            MonitorFrame g = monitors[pick];
            sb.Append("\n  Glasses = monitor #").Append(g.Index).Append('.');
            report = sb.ToString();
            monitor = new Rect((float)g.X, (float)g.Y, (float)g.Width, (float)g.Height);
            return true;
        }

        // macOS: AppKit's NSScreen.screens through JavaScript for Automation (script on standard input; osascript(1): "Passed in
        // using standard input. This works only if there are no filename arguments"), then Cocoa -> editor space.
        static bool TryListMacScreens(out List<MonitorFrame> monitors, out string error)
        {
            monitors = null;
            error = null;
            System.Diagnostics.Process p = null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(OsascriptPath, "-l JavaScript")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                p = System.Diagnostics.Process.Start(psi);
                if (p == null) { error = "could not start " + OsascriptPath; return false; }
                p.StandardInput.Write(MonitorLayout.NsScreenScript);
                p.StandardInput.Close();
                if (!p.WaitForExit(OsascriptTimeoutMs))
                {
                    try { p.Kill(); } catch (Exception) { }
                    error = OsascriptPath + " did not answer within " + (OsascriptTimeoutMs / 1000) + " s";
                    return false;
                }
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                if (p.ExitCode != 0)
                {
                    error = OsascriptPath + " exited with " + p.ExitCode + ": " + Trim(stderr);
                    return false;
                }
                List<MonitorFrame> cocoa = MonitorLayout.ParseNsScreenLines(stdout);
                monitors = MonitorLayout.ToEditorSpace(cocoa);
                if (monitors.Count == 0)
                {
                    error = "no screen (or no primary screen) in its output: \"" + Trim(stdout) + "\" " + Trim(stderr);
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                if (p != null) p.Dispose();
            }
        }

        static string Trim(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
        }

        // Other editors: every distinct monitor rectangle Unity reports, found by asking for the monitor at each point of a coarse
        // grid (plus the centre of the main editor window, which becomes entry 0 = "primary"). The first exception ends the scan.
        static bool TryScanDesktops(out List<MonitorFrame> monitors, out string error)
        {
            monitors = new List<MonitorFrame>();
            error = null;
            try
            {
                List<Rect> found = new List<Rect>();
                AddMonitorAt(found, EditorGUIUtility.GetMainWindowPosition().center);
                for (int y = ScanMinY; y <= ScanMaxY; y += ScanStepY)
                    for (int x = ScanMinX; x <= ScanMaxX; x += ScanStepX)
                        AddMonitorAt(found, new Vector2(x, y));
                for (int i = 0; i < found.Count; i++)
                    monitors.Add(new MonitorFrame(i, found[i].x, found[i].y, found[i].width, found[i].height, 0, ""));
                if (monitors.Count == 0) { error = "no monitor reported"; return false; }
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        static void AddMonitorAt(List<Rect> found, Vector2 point)
        {
            Rect r = InternalEditorUtility.GetBoundsOfDesktopAtPoint(point);
            if (!(r.width >= 1f) || !(r.height >= 1f))
                return;
            for (int i = 0; i < found.Count; i++)
            {
                Rect f = found[i];
                if (Mathf.Approximately(f.x, r.x) && Mathf.Approximately(f.y, r.y)
                    && Mathf.Approximately(f.width, r.width) && Mathf.Approximately(f.height, r.height))
                    return;
            }
            found.Add(r);
        }

        // ------------------------------------------------------------------------------------------------------------
        // The internal GameView, through reflection (each step null-checked and reported)
        // ------------------------------------------------------------------------------------------------------------

        // Preferred: the internal method GameView.SetTargetDisplay(int id) (GameView.cs, 2021.3.3f1: "if (id < 0 || id > 7) return;
        // targetDisplay = id; SetDisplayViewSize(id, targetSize);"). Fallback: the protected property PlayModeView.targetDisplay.
        static bool TrySetTargetDisplay(EditorWindow window, Type gameViewType, int displayIndex, out string error)
        {
            error = null;
            try
            {
                const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo method = gameViewType.GetMethod("SetTargetDisplay", Any, null, new[] { typeof(int) }, null);
                if (method != null)
                {
                    method.Invoke(window, new object[] { displayIndex });
                }
                else
                {
                    PropertyInfo property = FindProperty(gameViewType, "targetDisplay");
                    if (property == null || !property.CanWrite || property.PropertyType != typeof(int))
                    {
                        error = "neither GameView.SetTargetDisplay(int) nor a writable int property 'targetDisplay' exists";
                        return false;
                    }
                    property.SetValue(window, displayIndex, null);
                }

                // Read it back when we can.
                PropertyInfo readBack = FindProperty(gameViewType, "targetDisplay");
                if (readBack != null && readBack.CanRead && readBack.PropertyType == typeof(int))
                {
                    int now = (int)readBack.GetValue(window, null);
                    if (now != displayIndex)
                    {
                        error = "targetDisplay reads back " + now + " instead of " + displayIndex;
                        return false;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + (e.InnerException != null ? e.InnerException.Message : e.Message);
                return false;
            }
        }

        // GameView.showToolbar: "private bool showToolbar { get; set; }" (GameView.cs, 2021.3.3f1). When false, the view fills the
        // whole window instead of leaving 21 px for the toolbar. This is the well-known trick of community "fullscreen Game view" scripts.
        static bool TryHideToolbar(EditorWindow window, Type gameViewType)
        {
            try
            {
                PropertyInfo property = FindProperty(gameViewType, "showToolbar");
                if (property == null || !property.CanWrite || property.PropertyType != typeof(bool))
                    return false;
                property.SetValue(window, false, null);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Property lookup that also finds non-public properties declared on a base class (PlayModeView.targetDisplay is protected).
        static PropertyInfo FindProperty(Type type, string name)
        {
            const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, Declared);
                if (p != null)
                    return p;
            }
            return null;
        }

        // ------------------------------------------------------------------------------------------------------------
        // Failure: say why, and give the manual procedure
        // ------------------------------------------------------------------------------------------------------------

        static void Fail(string why, string detail)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[UGA] Glasses Game View: ").Append(why).Append(".\n");
            if (!string.IsNullOrEmpty(detail))
                sb.Append(detail).Append('\n');
            sb.Append("If you know where the glasses' monitor is: copy \"x y 3840 1080\" (points, y down from the top of the Mac's\n");
            sb.Append("  main screen; a monitor to the right of a 1512-wide Mac screen, tops aligned, is \"1512 0 3840 1080\") and use ")
              .Append(ClipboardMenuPath).Append(".\n");
            sb.Append("Or do it by hand instead (about a minute):\n");
            sb.Append("  1. Plug the glasses in. System Settings > Displays: the glasses must be an EXTENDED display, not mirrored.\n");
            sb.Append("  2. Hold the glasses' brightness-up button about 3 s until it beeps (side-by-side 3D), then pick 3840 x 1080 for that display\n");
            sb.Append("     (docs/HOW_IT_WORKS.md section 4, docs/apis/glasses/display-modes.md).\n");
            sb.Append("  3. In Unity: Window > General > Game. Drag the Game tab out of the main window and drop it on the glasses' monitor.\n");
            sb.Append("  4. In that Game view choose Display 2 and Free Aspect. A floating window keeps its title bar and toolbar (about 50 px),\n");
            sb.Append("     which both eyes will see: fine for the fps and left/right checks, not for judging layout.\n");
            sb.Append("  5. For a clean full-screen picture run the standalone build instead (BuildScript.BuildMac): it activates Display 2 by itself.\n");
            sb.Append("  Details and sources: docs/apis/unity/editorwindow-popup.md");
            Debug.LogWarning(sb.ToString());
            UGALog.Warn("glasses_view.manual", why);
        }
    }
}
