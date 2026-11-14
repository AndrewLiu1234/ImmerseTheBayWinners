# Unity APIs used by Editor/HudPlayPreview.cs (play-along HUD preview and overlap check)

Source: the offline scripting reference shipped with Unity 2021.3.3f1 on this machine,
`/Applications/Unity/Hub/Editor/2021.3.3f1/Documentation/en/ScriptReference/<page>` (publication date 2022-05-27).
Quoted 2026-11-14. APIs already quoted in the headers of Editor/HudLearnPreview.cs and Editor/NeckPreview.cs
(NewPreviewScene, ClosePreviewScene, Camera.scene, MoveGameObjectToScene, Camera.Render, Texture2D.ReadPixels,
ShaderUtil.allowAsyncCompilation, EditorApplication.Exit, worldToCameraMatrix / projectionMatrix) are used the same way.

## New in this script

### Texture2D.GetRawTextureData (Texture2D.GetRawTextureData.html)
> Declaration `public NativeArray<T> GetRawTextureData ();` Returns `NativeArray<T>` Raw texture data view.
> "This function returns a direct "view" into the texture pixel data as a Unity.Collections.NativeArray. ... For example,
> a 16x8 texture of RGBA32 format with no mipmaps will result in a 512-byte array (16x8x4), or a 128-element array if
> Color32 is used as a type. ... GetRawTextureData does not allocate memory; the returned NativeArray directly points to
> the texture system memory data buffer. ... Note: The returned array can become invalid ... if modifications or uploads
> happen to the texture after you call this method. Therefore the recommended way to use this method is to get the data,
> and use or modify it immediately."

Used as: after `ReadPixels` into an RGBA32 texture, `GetRawTextureData<Color32>()` is copied out at once (below), so the
per-part renders of the overlap check do not allocate an 8 MB array each (GetPixels32 allocates).

### NativeArray<T>.CopyTo (Unity.Collections.NativeArray_1.CopyTo.html)
> Declaration `public void CopyTo (T[] array );` "Copy all elements to another NativeArray or managed array of the same length."

### MenuItem (MenuItem.html)
Used exactly as the existing previews use it: `[MenuItem("Untitled Guitar App/HUD/Render Play HUD Previews", false, 220)]`
on a public static method with no arguments.

## Not a Unity API
`System.Reflection.MethodInfo.Invoke` (.NET) calls HudText's private `LateUpdate` in Edit Mode, where Unity does not call
it, so a text whose glyph measurement is pending can retry after a render. If the method is renamed the call is skipped
and the report counts the text as unmeasured.
