# Unity APIs used by the neck alignment (SL8): NeckAligner, NeckAnchor, NeckAlignPreview

Source for every excerpt: the offline scripting reference shipped with the editor we use,
`/Applications/Unity/Hub/Editor/2021.3.3f1/Documentation/en/ScriptReference/<page>` (Unity 2021.3.3f1), read on 2026-11-14.
Everyday APIs (Transform, Vector3/Quaternion arithmetic, MonoBehaviour messages) are not listed. The preview-scene and
render-to-PNG APIs are the ones already cited in `unity/Assets/UntitledGuitarApp/Editor/NeckPreview.cs`.

## Component.GetComponent(string) - NeckAnchor finds the editor's mouse-look rig by name
`Component.GetComponent.html`:
> Declaration `public Component GetComponent (string type );` Parameters: type - The name of the type of Component to get.
> Returns Component - A Component of the matching type, otherwise null if no Component is found. Description: To improve the
> performance of your code, consider using GetComponent with a type instead of a string.

Why the string form: Runtime code cannot reference the Editor assembly's `EditorSimRig`. Called twice a second, editor only.

## GameObject.SetActive - NeckAligner hides the neck (and its ghost hands) during the glances
`GameObject.SetActive.html`:
> Declaration `public void SetActive (bool value );` ... Activates/Deactivates the GameObject, depending on the given true or
> false value. A GameObject may be inactive because a parent is not active.

## Input.GetKeyDown(KeyCode) - Space confirms a capture / retries
`Input.GetKeyDown.html`:
> Returns true during the frame the user starts pressing down the key ... Call this function from the Update function, since
> the state gets reset each frame.

(Read in NeckAligner.Update only, as GazeDwell does.)

## Preview only (Editor/NeckAlignPreview.cs)
- `GameObject.CreatePrimitive.html`: `public static GameObject CreatePrimitive ( PrimitiveType type );` "Creates a game object
  with a primitive mesh renderer and appropriate collider." (the collider is removed; amber wire + magenta target balls)
- `GL.html` (example code): `Shader shader = Shader.Find ("Hidden/Internal-Colored"); lineMaterial = new Material (shader);
  lineMaterial.hideFlags = HideFlags.HideAndDontSave;` - "a built-in shader that is useful for drawing simple colored things".
  Editor-only use (Shader.Find is fine in the editor; runtime code loads shaders with Resources.Load).
- `Quaternion.LookRotation.html`: `public static Quaternion LookRotation ( Vector3 forward , Vector3 upwards = Vector3.up);`
- `Vector3.Slerp.html`: `public static Vector3 Slerp ( Vector3 a , Vector3 b , float t );` "Spherically interpolates between
  two vectors."

## Not engine APIs, but load-bearing conventions (checked by tests, not docs)
- UnityEngine `Quaternion * Vector3` and System.Numerics `Vector3.Transform(v, q)` rotate a vector the same way for the same
  (x, y, z, w): `NeckAlignerTests.QuaternionConventionsAgree` (runs offline on Unity's Mono with the real UnityEngine.CoreModule).
- Core's `NeckPose.NormalAxis` equals `Vector3.Cross(AlongAxis, AcrossAxis)` in Unity's coordinates:
  `NeckAlignerTests.CoreNormalIsAlongCrossAcross_InUnityCoordinates`.
