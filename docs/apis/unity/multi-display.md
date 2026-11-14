# Unity 2021.3: multi-display (Display, Camera.targetDisplay, the Game view's "Display N")

Used by `Runtime/Render/StereoRig.cs` and `Editor/GlassesGameView.cs`. Everything below is copied from sources read on 2026-11-14 for **Unity 2021.3.3f1** (the project's pinned version); nothing is from memory.

## Sources and how they were read

| Short name | What | Version |
|---|---|---|
| **docs** | Unity's own offline documentation that ships with the editor: `/Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents/Documentation/en/{ScriptReference,Manual}/<page>.html`. Online twin of each page: `https://docs.unity3d.com/2021.3/Documentation/ScriptReference/<page>.html` (and `.../Manual/<page>.html`); the URLs below returned HTTP 200 on 2026-11-14 | page footer "Version: 2021.3", "Publication Date: 2022-05-27" |
| **csref** | Unity C# reference source, https://github.com/Unity-Technologies/UnityCsReference, tag `2021.3.3f1`, commit `279865b9a35f4a3a675497772b99a2226e324c5f` ("Unity 2021.3.3f1 C# reference source code", 2022-05-19). Unity Reference Only License: short excerpts here for citation only | 2021.3.3f1 |
| **dll** | `UnityEditor.CoreModule.dll` and the reference assemblies of the same editor install, loaded with Unity's own Mono (reflection probe) and used by `cs-check.sh` | 2021.3.3f1 |

## 1. Display (UnityEngine.CoreModule)

URL: https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Display.html

> Provides access to a display / screen for rendering operations.
> Multi-display rendering is available on PC (Windows/Mac/Linux), iOS and Android.
> Use the Display class to operate on the displays themselves, and Camera.targetDisplay to set up cameras for rendering to individual displays.

Members we use (all declarations from the docs): `public static Display[] displays;` · `public static Display main;` ("Synonymous with Display.displays[0]") · `public bool active;` ("Gets the state of the display and returns true if the display is active and false if otherwise. See Also: Display.Activate.") · `public int systemWidth;` / `systemHeight` ("Horizontal/Vertical native display resolution. Actual rendering resolution might be different, see Display.renderingWidth.") · `public int renderingWidth;` / `renderingHeight` ("Rendering resolution can be set up to be different from "native" display resolution") · `public void SetRenderingResolution(int w, int h);`.
Not used: `Display.SetParams` ("Set rendering size and position on screen (Windows only)"), and `Display.onDisplaysUpdated`, which exists in csref (`Runtime/Export/Graphics/Display.bindings.cs` L179-180) but is not in the docs.

## 2. Display.displays

URL: https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Display-displays.html

> public static Display[] displays;
> The list of currently connected displays.
> In a built application, Unity automatically updates this list when you connect or disconnect a display. It always contains at least one (main) display.
> **In the Unity Editor, `displays` is not supported; `displays.Length` always has a value of 1, regardless of how many displays you have connected.**

csref `Runtime/Export/Graphics/Display.bindings.cs` L151: `public static Display[] displays = new Display[1] { new Display() };`, replaced by the player through `RecreateDisplayList(IntPtr[] nativeDisplay)` (L160).
**Consequence:** in the Editor never index `Display.displays[1]`. StereoRig skips all display code when `Application.isEditor` (https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Application-isEditor.html: "Are we running inside the Unity editor? (Read Only)").

## 3. Display.Activate (standalone player, macOS included)

URL: https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Display.Activate.html

```
public void Activate();
public void Activate(int width, int height, int refreshRate);
```
> Activate an external display. Eg. Secondary Monitors connected to the System.
> The function Activate() will create a window of Screen width, Screen height and default Refresh Rates.
> **This function is available on Windows, Linux and Mac platforms.**
> The default primary Monitor is always indexed 0. Secondary Monitors range from 1 to 7. Ensure an external monitor is connected before Activating it.
>
> Parameters of the overload: `width`: "Desired Width of the Window (for Windows only. On Linux and Mac uses Screen Width)." `height`: same, "On Linux and Mac uses Screen Height". `refreshRate`: "Desired Refresh Rate."
> This overloaded function available for Windows allows specifying desired Window Width, Height and Refresh Rate.

Docs example (Display.Activate page):
```csharp
if (Display.displays.Length > 1) { Display.displays[1].Activate(); }   // "Activate the display 1 (second monitor connected to the system)."
```
csref L113-115: `public void Activate() { ActivateDisplayImpl(nativeDisplay, 0, 0, 60); }` (so the no-argument form passes width 0, height 0, refresh 60, which macOS ignores per the docs above).

Manual page "Multi-display", https://docs.unity3d.com/2021.3/Documentation/Manual/MultiDisplay.html:
> Unity supports multi-display on: Standalone platforms (Windows, macOS X, and Linux) · Android (OpenGL ES and Vulkan) · iOS
> Unity's default display mode is one monitor only. When you run your application, you need use `Display.Activate()` to explicitly activate additional displays. **Once you activate a display, you can't deactivate it.**
> ... Make sure you call `Display.Activate()` only once during startup.
> `public void Activate(int width, int height, int refreshRate)`: Windows only. ... **On Linux and macOS X, secondary displays always use the display's current resolution, if available.**
> By default, the user's computer sorts the relative positions of its display monitors based on its x, y virtual desktop. To override this ... start your application from the command line and use the `-multidisplay` command line flag.

**Consequences for the glasses:** the glasses must already be in their 3840x1080 mode (and an extended display) *before* `Activate`, because on macOS the window takes the display's current resolution and `Activate(w, h, hz)` cannot change it. StereoRig calls `Activate()` once per player run (guarded by `Display.active`), polls once a second until the display exists, and one second later logs `stereo.display_size` if `renderingWidth x renderingHeight` is not 3840x1080 (2D fallback: 1920x1080).

## 4. Camera.targetDisplay, rect, projectionMatrix (UnityEngine.CoreModule)

`Camera.targetDisplay`, https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Camera-targetDisplay.html: `public int targetDisplay;`
> Set the target display for this Camera. This setting makes a Camera render into the specified display. Maximum number of displays (eg. monitors) supported is 8. This property has no effect on Android and iOS platforms, consider using Camera.SetTargetBuffers.

`Camera.rect`, https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Camera-rect.html: `public Rect rect;`
> Where on the screen is the camera rendered in normalized coordinates. The values in `rect` range from zero (left/bottom) to one (right/top).

`Camera.targetTexture` (https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Camera-targetTexture.html): "When targetTexture is `null`, camera renders to screen. When rendering into a texture, the camera always renders into the whole texture; effectively rect and pixelRect are ignored." (so the eye cameras keep `targetTexture = null`).

`Camera.projectionMatrix`, https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Camera-projectionMatrix.html: `public Matrix4x4 projectionMatrix;`
> Set a custom projection matrix. If you change this matrix, the camera no longer updates its rendering based on its fieldOfView. This lasts until you call ResetProjectionMatrix. ... Note that projection matrix passed to shaders can be modified depending on platform and other state. If you need to calculate projection matrix for shader use from camera's projection, use GL.GetGPUProjectionMatrix.

The same page carries the off-centre example our `StereoMath.ProjectionFromFrustum` reproduces (m00 = 2n/(r-l), m02 = (r+l)/(r-l), m11 = 2n/(t-b), m12 = (t+b)/(t-b), m22 = -(f+n)/(f-n), m23 = -2fn/(f-n), m32 = -1):
```csharp
static Matrix4x4 PerspectiveOffCenter(float left, float right, float bottom, float top, float near, float far)
{
    float x = 2.0F * near / (right - left);  float y = 2.0F * near / (top - bottom);
    float a = (right + left) / (right - left);  float b = (top + bottom) / (top - bottom);
    float c = -(far + near) / (far - near);  float d = -(2.0F * far * near) / (far - near);  float e = -1.0F;
    Matrix4x4 m = new Matrix4x4();
    m[0, 0] = x; m[0, 1] = 0; m[0, 2] = a; m[0, 3] = 0;   m[1, 0] = 0; m[1, 1] = y; m[1, 2] = b; m[1, 3] = 0;
    m[2, 0] = 0; m[2, 1] = 0; m[2, 2] = c; m[2, 3] = d;   m[3, 0] = 0; m[3, 1] = 0; m[3, 2] = e; m[3, 3] = 0;
    return m;
}
```
`Matrix4x4.Perspective(float fov, float aspect, float zNear, float zFar)` (https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Matrix4x4.Perspective.html): "`fov`: Vertical field-of-view in degrees. ... Projection matrices in Unity follow OpenGL convention, i.e. clip space near plane is at `z=-1`, and far plane is at `z=1`. The returned matrix embeds a z-flip operation whose purpose is to cancel the z-flip performed by the camera view matrix." `Matrix4x4.Frustum(left, right, bottom, top, zNear, zFar)` has the same convention: "The corners of the near plane ... top-left : (left, top, zNear) ..." (https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Matrix4x4.Frustum.html). NOTE: the numbers printed in the Matrix4x4.Perspective doc example do not match the formula (its last row prints `0 0 0 1`), so they are not used as test values.
Also used: `Camera.ResetProjectionMatrix()` ("Make the projection reflect normal camera's parameters. Call this to end the effect of setting projectionMatrix."; not needed), `Camera.aspect` ("By default the aspect ratio is automatically calculated from the screen's aspect ratio ... If you modify the aspect ratio of the camera, the value will stay until you call camera.ResetAspect()"; StereoRig never relies on it because the matrix is explicit), `Camera.allowHDR` ("High dynamic range rendering. True if the camera is using HDR rendering"), `Camera.allowDynamicResolution`, `Camera.useOcclusionCulling`, `Camera.clearFlags` ("Can be CameraClearFlags.Skybox, CameraClearFlags.SolidColor, CameraClearFlags.Depth or CameraClearFlags.Nothing").

## 5. The Game view's "Display N" (Editor)

Manual "The Game view", https://docs.unity3d.com/2021.3/Documentation/Manual/GameView.html, control bar:
> **Display**: Select this to choose from a list of Cameras if you have multiple Cameras in the Scene. This is set to Display 1 by default. You can assign Displays to Cameras in the Camera module, under the Target Display drop-down menu.

Manual "Multi-display", "Previewing multiple displays in your Project": "In the Camera's Inspector, select a Target Display for that Camera. Make sure you're in the Game view. From the Display menu in the top-left corner, select the Display to Preview."
So in the Editor, a Game view whose dropdown says **"Display 2"** renders exactly the cameras with `Camera.targetDisplay == 1`, into a render target the size of that Game view; there is no `Display.Activate` in the Editor.
`Display.activeEditorGameViewTarget` (https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Display-activeEditorGameViewTarget.html): "Get the Editors active GameView display target. Returns the last selected GameView's display target. At runtime will return -1 as there is no GameView." (csref L155-157: a static int initialised to 0 with an internal setter; the docs say -1 at runtime. Not used by our code.)

csref `Editor/Mono/PlayModeView/PlayModeView.cs` L194-212 (each Game view renders the cameras of its own target display; the display option is only honoured for Standalone-style build targets):
```csharp
var currentTargetDisplay = 0;
if (ModuleManager.ShouldShowMultiDisplayOption()) { ... currentTargetDisplay = targetDisplay; }
...
EditorGUIUtility.RenderPlayModeViewCamerasInternal(m_TargetTexture, currentTargetDisplay, mousePosition, showGizmos, renderIMGUI);
```
`ModuleManager.ShouldShowMultiDisplayOption()` (`Editor/Mono/Modules/ModuleManager.cs` L484-495) is true for `BuildTargetGroup.Standalone`, WSA, iOS, Android, EmbeddedLinux, or when the platform module names its displays. **The active build target must be macOS (a Standalone target) or the Game view falls back to display 0.** `GameView.OnGUI` shows the dropdown with the names "Display 1" ... "Display 8" (`Editor/Mono/DisplayUtility.cs` L17-23), index = `Camera.targetDisplay`.
Several Game views can exist at once and each renders its own display every frame (the main one on Display 1 = spectator, the glasses one on Display 2).

## 6. What StereoRig does with this

- Eye cameras: `targetDisplay = render.glassesDisplay` (clamped 0..7), `rect` = left or right half (or full for the 2D fallback), `targetTexture = null`, explicit `projectionMatrix` (so the Game view's size or aspect cannot change the field of view, and no matrix is recomputed per frame).
- Player: `Display.displays[i].Activate()` once when `i >= 1` exists and `!active`; polled once a second while missing; never touched in the Editor.
- Editor: the glasses Game view (Display 2, 3840x1080, toolbar hidden) is made by `Editor/GlassesGameView.cs`; see `docs/apis/unity/editorwindow-popup.md`.
- `Application.runInBackground` (https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Application-runInBackground.html: "Should the player be running when the application is in the background? Default is false (application pauses when it is in the background).") is set to true in **players only** (ProjectSettings has `runInBackground: 0`, so otherwise the glasses freeze when you click another window). The docs do not say what the setter does in the Editor, so StereoRig does not call it there.

## 7. NOT verified (needs the glasses, L5)

- That `Display.Activate()` on macOS opens the second display's window borderless at 3840x1080, and the frame rate it reaches.
- That the glasses' display is index 1 in the player (it is the second display macOS reports; with more monitors set `render.glassesDisplay`).
- The Game view popup behaviour on the glasses' monitor (see editorwindow-popup.md).
- NOT FOUND: any Unity document about macOS-specific multi-display limits beyond the lines quoted above.
