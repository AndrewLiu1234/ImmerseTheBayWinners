# Unity 2021.3: rendering an Editor preview off-screen (GhostHandPreview pass 1 and 2)

Source: the Unity 2021.3.3f1 offline Scripting Reference installed with the editor,
`/Applications/Unity/Hub/Editor/2021.3.3f1/Documentation/en/ScriptReference/<page>.html` ("Version: 2021.3",
"Publication Date: 2022-05-27"), online at `https://docs.unity3d.com/2021.3/Documentation/ScriptReference/<page>.html`.
Copied 2026-11-14 by the ghost-strum worker. Quotes are verbatim; lines marked *use* say how we use them.

## A preview scene, so the editor's own scene is never touched

- `SceneManagement.EditorSceneManager.NewPreviewScene.html`
  `public static SceneManagement.Scene NewPreviewScene ();` "Returns Scene The new preview Scene."
  "Creates a new preview Scene. Any object added to a preview Scene will only be rendered in that Scene."
- `SceneManagement.EditorSceneManager.ClosePreviewScene.html`
  `public static bool ClosePreviewScene ( SceneManagement.Scene scene );` "Closes a preview Scene created by NewPreviewScene."
- `SceneManagement.SceneManager.MoveGameObjectToScene.html`
  `public static void MoveGameObjectToScene ( GameObject go , SceneManagement.Scene scene );` "Move a GameObject from its
  current Scene to a new Scene. You can only move root GameObjects from one Scene to another. This means the GameObject to
  move must not be a child of any other GameObject in its Scene."
  *use*: every root is built in the active scene, then moved (with its whole hierarchy) into the preview scene, as
  Editor/NeckPreview.cs does. Replaces `EditorSceneManager.NewScene(EmptyScene, Additive)`, which threw in the user's
  editor at 11:50 ("Cannot create a new scene additively with an untitled scene unsaved").
- `Camera-scene.html` "If not null, the camera will only render the contents of the specified Scene. Currently, only Scenes
  created by EditorSceneManager.NewPreviewScene are supported."
- `CameraType.Preview.html` "Used to indicate a camera that is used for rendering previews in the Editor."

## The glasses' eye and the pictures

- `Camera-projectionMatrix.html` "Set a custom projection matrix. If you change this matrix, the camera no longer updates its
  rendering based on its fieldOfView . This lasts until you call ResetProjectionMatrix ."
  *use*: the left eye gets `StereoMath.EyeProjection(...)`, exactly as `StereoRig.ConfigureEye` sets it.
- `Camera.ResetProjectionMatrix.html` `public void ResetProjectionMatrix ();` "Make the projection reflect normal camera's
  parameters. Call this to end the effect of setting projectionMatrix ." *use*: before the close-up, which uses fieldOfView.
- `Texture2D.ReadPixels.html` `public void ReadPixels ( Rect source , int destX , int destY , bool recalculateMipMaps = true);`
  "destX The horizontal pixel position in the texture to write the pixels to. destY The vertical pixel position in the
  texture to write the pixels to." "Reads the pixels from the current render target (the screen, or a RenderTexture ), and
  writes them to the texture." *use*: each 960 x 540 render is read straight into its cell of the contact sheet (texture
  y runs upward, so the top row has the larger destY).
- `ShaderUtil-allowAsyncCompilation.html` "When true, asynchronous Shader compilation is allowed at the current call site.
  ... You can also force synchronous compilation on parts of rendering where asynchronous compilation would be used by
  default. This property takes effect immediately." *use*: false while rendering (no placeholder shader in a picture),
  restored afterwards.
- `SkinnedMeshRenderer-updateWhenOffscreen.html` "If enabled, the Skinned Mesh will be updated when offscreen. If disabled,
  this also disables updating animations." *use*: switched on for the two posed hands in the preview only, so a hand
  that the pose driver moved far from the FBX's bind position is skinned whatever its stale bounds say (our reading of
  the sentence, not a quote).

Already quoted elsewhere and used unchanged: `Camera.Render`, `RenderTexture.active`, `ImageConversion.EncodeToPNG`,
`EditorApplication.Exit`, `Application.isBatchMode` (Editor/GhostHandPreview.cs and Editor/NeckPreview.cs headers).
NOT FOUND: nothing.
