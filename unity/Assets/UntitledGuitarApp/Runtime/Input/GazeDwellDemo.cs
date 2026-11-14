// GazeDwellDemo: OPTIONAL. A way to try gaze-dwell in the editor with a mouse, and a worked example of the API.
// Not used by the app; delete the file if you do not want it.
//
// Use it in an empty scratch scene (not Main.unity: it takes over GazeDwell.Head): create an empty GameObject, add
// Component > UntitledGuitarApp > Gaze Dwell Demo, press Play. The mouse pointer is your gaze: the head turns to point at it.
// Three balls hang 4 m ahead. Hold the reticle on one until the arc closes (1.2 s) or press Space while on it: it turns green
// and "Fired" shows on screen. Space with nothing focused shows "Confirmed". Leave early and the arc resets.
using System.Collections.Generic;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Gaze
{
    [AddComponentMenu("UntitledGuitarApp/Gaze Dwell Demo")]
    public sealed class GazeDwellDemo : MonoBehaviour
    {
        const float TargetDistanceM = 4f;
        const float TargetRadiusDeg = 3f;

        sealed class Ball
        {
            public DwellTarget Target;
            public Transform Body;
            public Material Material;
            public float BaseScale;
        }

        readonly List<Ball> balls = new List<Ball>();
        Camera cam;
        Transform head;
        GazeDwell gaze;
        string focusLine = "";    // what the gaze is on right now
        string resultLine = "";   // the last fire or confirm

        void Start()
        {
            cam = Camera.main;
            if (cam == null)
            {
                var camObject = new GameObject("Gaze Demo Camera");
                camObject.tag = "MainCamera";
                camObject.transform.position = new Vector3(0f, 1.5f, 0f);
                cam = camObject.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;   // black is invisible on the glasses' additive display
            }

            head = new GameObject("Gaze Demo Head").transform;
            head.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);

            gaze = GazeDwell.GetOrCreate();
            gaze.Head = head;
            // A fired target leaves the focus one frame later (it is in cooldown), so the result has its own line.
            gaze.TargetEntered += t => focusLine = "Looking at: " + t.Label;
            gaze.TargetExited += t => focusLine = "";
            gaze.TargetFired += t => resultLine = "Fired: " + t.Label;
            gaze.Confirmed += () => resultLine = "Confirmed (Space with nothing focused)";

            var reticle = new GameObject("Gaze Demo Reticle").AddComponent<Reticle>();
            reticle.Gaze = gaze;

            string[] names = { "Left", "Middle", "Right" };
            float[] yawDeg = { -14f, 0f, 14f };
            for (int i = 0; i < names.Length; i++)
            {
                Vector3 direction = Quaternion.Euler(0f, yawDeg[i], 0f) * cam.transform.forward;
                DwellTarget target = DwellTarget.Create(names[i], head.position + direction * TargetDistanceM, TargetRadiusDeg);

                // A sphere that subtends exactly the target's angular radius, so what you see is what the gaze test uses.
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(sphere.GetComponent<Collider>());      // gaze-dwell is an angle test; no physics needed
                sphere.transform.SetParent(target.transform, false);
                float scale = 2f * GazeMath.AngularRadiusToWorld(TargetDistanceM, TargetRadiusDeg);
                sphere.transform.localScale = Vector3.one * scale;

                balls.Add(new Ball
                {
                    Target = target,
                    Body = sphere.transform,
                    Material = sphere.GetComponent<Renderer>().material,
                    BaseScale = scale,
                });
            }
        }

        void Update()
        {
            // The pointer is the gaze: point the head along the camera ray through it.
            Vector3 mouse = Input.mousePosition;
            mouse.x = Mathf.Clamp(mouse.x, 0f, Screen.width);
            mouse.y = Mathf.Clamp(mouse.y, 0f, Screen.height);
            head.rotation = Quaternion.LookRotation(cam.ScreenPointToRay(mouse).direction, Vector3.up);

            for (int i = 0; i < balls.Count; i++)
            {
                Ball ball = balls[i];
                bool done = ball.Target.State == DwellState.Fired || ball.Target.State == DwellState.Cooldown;
                float progress = ball.Target.Progress;
                ball.Body.localScale = Vector3.one * (ball.BaseScale * (1f + 0.25f * progress));
                ball.Material.color = done
                    ? new Color(0.2f, 1f, 0.35f)
                    : Color.Lerp(new Color(0.25f, 0.4f, 0.6f), new Color(1f, 0.95f, 0.5f), progress);
            }
        }

        void OnGUI()
        {
            GUI.Label(new Rect(12f, 8f, 1000f, 24f), "Gaze-dwell demo: the mouse is your gaze. Hold the ring on a ball for 1.2 s, or press Space on it. Space on empty space = Confirmed.");
            GUI.Label(new Rect(12f, 30f, 1000f, 24f), focusLine);
            GUI.Label(new Rect(12f, 52f, 1000f, 24f), resultLine);
        }
    }
}
