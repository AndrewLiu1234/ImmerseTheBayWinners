# Unity 2021.3: the APIs behind HudText's size and its calibration render

Used by `Runtime/Hud/HudText.cs` (size from a measured constant) and `Editor/HudTextCalibration.cs` (measures that constant
in glasses-eye renders). Copied on 2026-11-14 from Unity's own offline documentation for **Unity 2021.3.3f1**:
`/Applications/Unity/Hub/Editor/2021.3.3f1/Documentation/en/ScriptReference/<page>.html` (page footer "Publication Date:
2022-05-27"; online twin `https://docs.unity3d.com/2021.3/Documentation/ScriptReference/<page>.html`). Nothing from memory.
The preview-scene APIs (NewPreviewScene, ClosePreviewScene, Camera.scene, MoveGameObjectToScene, Camera.Render, ReadPixels,
EncodeToPNG, ShaderUtil.allowAsyncCompilation, EditorApplication.Exit) are the ones `Editor/NeckPreview.cs` already quotes.

## What the documentation does NOT say (why the size is measured)

- `TextMesh-characterSize.html`: "The size of each character (This scales the whole text)". No formula from
  fontSize / characterSize to world units exists in the 2021.3 docs. So HudText's world size comes from
  `HudText.CapUnitsPerFontSize`, a number measured in Unity's renders by `Editor/HudTextCalibration.cs`.
- `TextMesh-fontSize.html`: "The font size to use (for dynamic fonts). If this is set to a non-zero value, the font size
  specified in the font importer is overriden with a custom size."
- `Renderer-localBounds.html`: "The bounding box of the renderer in local space. This is the axis-aligned bounding box fully
  enclosing the object in local space." It does not say which box a TextMesh reports (glyph ink, advance, or line height);
  the old HudText relied on it and drew text at about 0.8 of the asked size. Not used by HudText any more (only by the
  calibration's copy of the old code, to show what it did).

## Camera.projectionMatrix (Camera-projectionMatrix.html)

> Set a custom projection matrix. If you change this matrix, the camera no longer updates its rendering based on its
> fieldOfView. This lasts until you call ResetProjectionMatrix.

The calibration camera gets `StereoMath.EyeProjection(...)`, the matrix StereoRig gives each eye; its focal length in pixels
is `m11 * eyeHeight / 2` (m11 = 2n / (top - bottom) = 1 / tan(vertical FOV / 2), see StereoMath.ProjectionFromFrustum).

## Camera.worldToCameraMatrix (Camera-worldToCameraMatrix.html)

> Matrix that transforms from world to camera space. ... Note that camera space matches OpenGL convention: camera's forward is
> the negative Z axis.

So an element's depth in front of the camera is `-worldToCameraMatrix.MultiplyPoint(position).z`.

## RenderTexture.GetTemporary (RenderTexture.GetTemporary.html)

> public static RenderTexture GetTemporary (int width, int height, int depthBuffer = 0, RenderTextureFormat format =
> RenderTextureFormat.Default, RenderTextureReadWrite readWrite = RenderTextureReadWrite.Default, int antiAliasing = ...

## RenderTexture.antiAliasing (RenderTexture-antiAliasing.html)

> The antialiasing level for the RenderTexture. Anti-aliasing value indicates the number of samples per pixel. If unsupported
> by the hardware or rendering API, the greatest supported number of samples less than the indicated number is used. When a
> RenderTexture is using anti-aliasing, then any rendering into it will happen into the multi-sampled texture, which will be
> "resolved" into a regular texture when switching to another render target. To the rest of the system only this "resolved"
> surface is visible.

The calibration asks for 4 samples (so the control quad's edges are anti-aliased, like text edges), reads the value the
texture really got back, and widens the control tolerance to a whole pixel when it got 1.

## Camera.allowMSAA (Camera-allowMSAA.html)

> MSAA rendering. Should this camera use a MSAA render target? Will only use MSAA if supported by the current quality settings
> MSAA level.

## QualitySettings.activeColorSpace (QualitySettings-activeColorSpace.html)

> Active color space (Read Only). This is the active color space based on player settings (PlayerSettings.colorSpace) and
> hardware support.

The project is in Gamma (`unity/ProjectSettings/ProjectSettings.asset`: `m_ActiveColorSpace: 0`); the calibration decodes
sRGB to linear light only when this reports Linear.

## Font.RequestCharactersInTexture (Font.RequestCharactersInTexture.html)

> Request characters to be added to the font texture (dynamic fonts only). ... You can supply a custom font size and style for
> the characters. If size is zero (the default), it will use the default size for that font. RequestCharactersInTexture may
> cause the font texture to be regenerated if it does not have space to add all the requested characters.

## Font.GetCharacterInfo (Font.GetCharacterInfo.html) - old HudText path only

> public bool GetCharacterInfo (char ch, out CharacterInfo info, int size = 0, FontStyle style = FontStyle.Normal);
> Get rendering info for a specific character. ... If the character ch with the specified size and style is present in the
> font texture, then this method will return true, and info will contain the texture placement information

## Shader.Find (Shader.Find.html) - editor script only

> Finds a shader with the given name. ... Note that a shader might be not included into the player build if nothing references
> it. In that case, Shader.Find will work only in the Editor, and will result in the pink error shader in a build.

Used only in `Editor/HudTextCalibration.cs`, for the white control quad ("Unlit/Color", then "Hidden/Internal-Colored",
then "Sprites/Default"). Runtime code must keep using Resources.Load (COMMON.md, Unity rules).

## MenuItem (MenuItem.html)

> The MenuItem attribute allows you to add menu items to the main menu and inspector context menus. The MenuItem attribute turns
> any static function into a menu command. Only static functions can use the MenuItem attribute.

## Font metrics used for the provisional constant (not a Unity API)

`/System/Library/Fonts/Supplemental/Arial Bold.ttf`, read with fontTools 4.25.0 on 2026-11-14 (unitsPerEm 2048, 'glyf' boxes):
H 0..1466, 1 0..1472, G -25..1491, C -25..1491; hhea ascent 1854, descent -434, lineGap 67; OS/2 capHeight 1466.
So H : 1 = 1466 : 1472 (0.9959), and a G is 1516 / 1466 = 1.034 times a cap height (its overshoot).
