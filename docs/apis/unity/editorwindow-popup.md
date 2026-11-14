# Unity 2021.3: a borderless Game view on a second monitor (EditorWindow.ShowPopup + internal GameView)

Used by `Editor/GlassesGameView.cs` (menu `UGA/Glasses Game View`). Read on 2026-11-14 for **Unity 2021.3.3f1**. Short names (docs, csref, dll) are explained in `docs/apis/unity/multi-display.md`; csref = https://github.com/Unity-Technologies/UnityCsReference at tag `2021.3.3f1` (commit `279865b9a35f4a3a675497772b99a2226e324c5f`).

## 1. Documented API

`EditorWindow.ShowPopup`, https://docs.unity3d.com/2021.3/Documentation/ScriptReference/EditorWindow.ShowPopup.html: `public void ShowPopup();`
> Shows an Editor window using popup-style framing. This means the window has no frame, and is not draggable. It is intended for showing something like a popup menu within an existing window. Opening a window using this method will not give it the functionality of a popup window, only the styling. For full popup functionality (eg, auto closing when the window loses focus), use PopupWindow.

Its example sets the position first: `window.position = new Rect(Screen.width / 2, Screen.height / 2, 250, 150); window.ShowPopup();`.
`EditorWindow.position`, https://docs.unity3d.com/2021.3/Documentation/ScriptReference/EditorWindow-position.html: `public Rect position;` "The desired position of the window in screen space. Setting this value will undock the window if it is docked."
Also used: `EditorWindow.Close()` ("Close the editor window. This will destroy the editor window."), `EditorGUIUtility.GetMainWindowPosition()` ("Returns position of Unity Editor's main window"), `SessionState.GetInt/SetInt`, `EditorUtility.InstanceIDToObject`, `EditorApplication.delayCall`, `BuildPipeline.GetBuildTargetGroup`. They compile against 2021.3.3f1 (cs-check --editor).
csref `Editor/Mono/EditorWindow.cs`: `ShowPopup()` is `ShowPopupWithMode(ShowMode.PopupMenu, true)` (L516-518); `ShowPopupWithMode` (L552-568) makes a `ContainerWindow` at the window's current `position` and shows it; the `position` setter (L1145-1181) on an already shown popup runs `m_Parent.window.position = value` (L1163), i.e. it moves the container window. That is why the helper sets the position before and after `ShowPopup()`.
`Screen.GetDisplayLayout` is documented as "It is only supported on Linux, macOS and Windows Standalone" (https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Screen.GetDisplayLayout.html), so it is **not** used in the Editor.

## 2. The internal UnityEditor.GameView (reflection; not documented API)

Where the technique comes from (community, all three do the same four things: `Type.GetType("UnityEditor.GameView,UnityEditor")`, read the non-public property `showToolbar`, `ScriptableObject.CreateInstance`, `ShowPopup()` then `position = ...` then `Focus()`):
- https://gist.github.com/fnuecke/d4275087cc7969257eae0f939fac3d2f ("FullscreenGameView.cs"; "Confirmed to work in Unity 2019 and 2020. May work in earlier and later versions. No promises."; opens on "whatever Unity thinks is the main monitor"). Core of it:
```csharp
static readonly Type GameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
static readonly PropertyInfo ShowToolbarProperty = GameViewType.GetProperty("showToolbar", BindingFlags.Instance | BindingFlags.NonPublic);
...
instance = (EditorWindow) ScriptableObject.CreateInstance(GameViewType);
ShowToolbarProperty?.SetValue(instance, False);
var fullscreenRect = new Rect(Vector2.zero, new Vector2(Screen.currentResolution.width, Screen.currentResolution.height));
instance.ShowPopup(); instance.position = fullscreenRect; instance.Focus();
```
- https://gist.github.com/dnnkeeper/5cf0a6eafe0c1aaaf2c8a1437ad0bf85 ("Unity Fullscreen Game Window F11", same code) and https://gist.github.com/Chillu1/4c209308dc81104776718b1735c639f7 (MIT licence, "Confirmed to work in Unity 2020"; adds opening on entering Play Mode, and notes that a second Game view shows Display 1 unless you pick another display).
- A Unity issue tracker entry titled "It is not possible to hide the toolbar of the Game window when observed in 2022.2 or later streams" (https://issuetracker.unity3d.com/issues/it-is-not-possible-to-hide-the-toolbar-of-the-game-window-when-observed-in-2022-dot-2-or-later-streams) was seen only as a search-result summary (the page itself was not opened): the property disappeared in 2022.2+ and came back in 6000.0.31f1 / 6000.1.0a7. We are pinned to 2021.3.3f1 where it exists (below). **Do not upgrade Unity and expect this to keep working.**
- Commercial alternative that does the same job: "Fullscreen Editor" on the Asset Store, https://assetstore.unity.com/packages/tools/utilities/fullscreen-editor-69534 (not used: no new packages).

What those scripts rely on, checked in csref 2021.3.3f1 `Editor/Mono/GameView/GameView.cs` and `Editor/Mono/PlayModeView/PlayModeView.cs`, then confirmed in `UnityEditor.CoreModule.dll` of this editor with a reflection probe run under Unity's own Mono (it replays the exact lookups of `GlassesGameView.cs`):

| What | csref excerpt | dll result |
|---|---|---|
| the type | L38: `internal class GameView : PlayModeView, IHasCustomMenu, IGameViewSizeMenuUser`; PlayModeView.cs L26: `internal abstract class PlayModeView : EditorWindow` | `UnityEditor.GameView` found, non-public, base `UnityEditor.PlayModeView` |
| hide the toolbar | L293: `private bool showToolbar { get; set; }`; L317 (in `OnEnable`): `showToolbar = ModeService.HasCapability(ModeCapability.GameViewToolbar, true);`; L193 and L208: `if (showToolbar) return new Rect(0, EditorGUI.kWindowToolbarHeight, pos.width, pos.height - EditorGUI.kWindowToolbarHeight); return new Rect(0, 0, pos.width, pos.height);` (so with `false` the view fills the whole window); L839 (`OnEditorModeChanged`) sets it again | `Boolean showToolbar`, getter and setter private, found with `Instance | NonPublic` |
| choose "Display 2" | L178-185: `//This has been added to accomodate the test in PR for case 991291.` `internal void SetTargetDisplay(int id) { if (id < 0 \|\| id > 7) return; targetDisplay = id; SetDisplayViewSize(id, targetSize); }`; PlayModeView.cs L65-76: `protected int targetDisplay { get { return m_TargetDisplay; } set { if (m_TargetDisplay != value) SetDisplayViewSize(value, m_TargetSize); m_TargetDisplay = value; } }`; GameView.cs L571-576 (toolbar dropdown): `targetDisplay = display; Display.activeEditorGameViewTarget = targetDisplay;` | `Void SetTargetDisplay(Int32)` internal on GameView; `Int32 targetDisplay` protected, read/write, declared on PlayModeView (so the lookup walks base types) |
| size of the picture | `GetViewInWindow` and `viewInWindow` (L190-215): the view is the window minus the toolbar; the render target is that rectangle in pixels (`GameViewSizes.GetRenderTargetSize`, "Free Aspect" by default) | not probed (needs a window) |
| the monitor | `InternalEditorUtility.bindings.cs` L317-318: `[FreeFunction("InternalEditorUtilityBindings::GetBoundsOfDesktopAtPoint")] extern public static Rect GetBoundsOfDesktopAtPoint(Vector2 pos);` (a public static method of the public class `UnityEditorInternal.InternalEditorUtility`, not in the docs) | found, `Rect(Vector2)` public static |

How `GlassesGameView.Open()` uses them (each step null-checked, each call inside try/catch; a failure logs the manual steps in section 4 and opens nothing):
1. Refuse unless the active build target is a Standalone one (otherwise the Game view cannot show Display 2: `ModuleManager.ShouldShowMultiDisplayOption()`, see multi-display.md section 5).
2. Find the glasses' monitor: ask `GetBoundsOfDesktopAtPoint` for the monitor at every point of a coarse grid (x -12000..18000 step 400, y -6000..8000 step 270, plus the centre of the main editor window), de-duplicate, and take the one that is 3840 wide and 1000-1202 high. If none matches, the log lists every monitor Unity found.
3. `ScriptableObject.CreateInstance(gameViewType)`; `SetTargetDisplay(1)` (fallback: the `targetDisplay` property) and read it back; `showToolbar = false` (a failure is only a warning).
4. `position = rect; ShowPopup(); position = rect;` with `rect` = 3840 x 1080 at the monitor's top-left corner (if Unity reports the monitor shorter than 1080, the bottoms are lined up instead); remember the instance id in `SessionState`; `Close()` (menu `UGA/Close Glasses Game View`) and a repeated `Open()` close the previous window first; after a script recompile `OnEnable` runs again and resets the toolbar, so an `[InitializeOnLoadMethod]` hides it again.

## 3. Behaviour that is NOT verified (nobody has run it in the editor yet; L5)

- NOT FOUND in any source: whether the monitor rect returned by `GetBoundsOfDesktopAtPoint` is the full frame or the work area (without the menu bar) on macOS, and whether it is in points (the coordinates `EditorWindow.position` uses) or pixels on a Retina Mac. The helper tolerates a height of 1000-1202 and lines up the bottom edge, and the glasses are a 1x display, but if the window lands in the wrong place, `GlassesGameView.OpenOn(rect)` takes the monitor rectangle by hand.
- Whether macOS keeps a popup-style window above the Dock and menu bar on the glasses' monitor, and whether "Displays have separate Spaces" changes that.
- Whether the popup survives a domain reload (the code copes either way: it re-hides the toolbar if the window is still there, otherwise run the menu item again).
- Keyboard focus: `ShowPopup()` gives the popup focus. Click the Game view on the Mac screen once before using keys or the mouse-look.
- Frame rate with two Game views (3840x1080 popup plus the Mac screen's Game view): that is spike M-S4.

## 4. By hand (what the helper logs when it fails)

1. Plug the glasses in; System Settings > Displays: **extended**, not mirrored. Hold the glasses' brightness-up button about 3 s until it beeps (side-by-side 3D) and pick 3840 x 1080 (`docs/apis/glasses/display-modes.md`).
2. Unity: Window > General > Game. Drag the Game tab out of the main window and drop it on the glasses' monitor.
3. In that Game view choose **Display 2** and **Free Aspect**. A floating window keeps its title bar and toolbar (about 50 px), which both eyes will see. That is fine for the fps and left/right checks, not for judging layout.
4. For a clean full-screen picture run the standalone build (`BuildScript.BuildMac`): the player activates Display 2 itself.
