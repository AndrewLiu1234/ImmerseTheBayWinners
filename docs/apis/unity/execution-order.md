# Unity 2021.3: Editor-folder MonoBehaviours and script execution order (EditorSimHead)

Used by `Runtime/Head/EditorSimHead.cs` and `Editor/EditorSimRig.cs` (menu `UGA/Editor Sim Rig`). Read on 2026-11-14 from the
online 2021.3 docs (pages say "Version: 2021.3").

## Editor-assembly MonoBehaviours cannot be attached

https://docs.unity3d.com/2021.3/Documentation/Manual/SpecialFolders.html, section "Editor":
> Note: Unity doesn't allow components derived from MonoBehaviour to be assigned to GameObjects if the scripts are in the
> Editor folder.
> ... Scripts in an Editor folder run as Editor scripts, not runtime scripts.

Our Editor code is the `UntitledGuitarApp.Editor` assembly (`includePlatforms: ["Editor"]`), the same rule. So the mouse-look
component lives in the Runtime assembly inside `#if UNITY_EDITOR` (absent from players) and the Editor file keeps only the
menu. `GameObject.AddComponent` is checked for null before the menu says "attached". NOT FOUND in the docs: that
AddComponent returns null (rather than throwing) when Unity refuses a component; the code handles both.

## Execution order

https://docs.unity3d.com/2021.3/Documentation/ScriptReference/DefaultExecutionOrder.html:
> Specifies the script execution order for a MonoBehaviour-derived class relative to other MonoBehaviour-derived types.
> ... Scripts are executed in order from lowest first to highest last, for example: -200, -100, -50, 50, 100, 200.
> ... If you define an execution order for a MonoBehaviour-derived type in code with DefaultExecutionOrder but define a
> different value for the same type in the Editor's Project settings window, Unity uses the value defined in the Editor UI.
Property: `order` "Integer which defines the execution priority order for a MonoBehaviour-derived class."

No script `.meta` under `unity/Assets` sets a non-zero `executionOrder` (checked 2026-11-14), so the code values hold:
ImuHeadPose -100 (writes the head in Update) < **EditorSimHead -90 (Update)** < SpectatorCam, FloatingNeck and every other
script 0 < GazeDwell 500 < Reticle 1000 < NeckHudCard 9000 < LearnHud, StringMarks 20000. The EditMode test
`EditorSimHeadTests.SimHead_RunsAfterImuHeadPose_AndBeforeEveryOtherRuntimeBehaviour` checks it by reflection.
