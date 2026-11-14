// EditMode tests for GhostPose and GhostPoseLibrary (Pose JSON v1: unity/Assets/StreamingAssets/poses/README.md).
// The slice's automated acceptance criterion "every pose JSON loads" is EveryPoseJsonInStreamingAssetsLoads.
// The three Exported* strings are real outputs of art/blender/export_poses.py 1.1.0, copied in by script (never
// retyped): a throwaway 16-bone test rig posed on the default neck mock-up. They are test data, not product poses.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.GhostHand;

namespace UntitledGuitarApp.Tests.EditMode
{
    public sealed class GhostPoseLibraryTests
    {
        const string ExportedG = @"{
  ""schema"": 1,
  ""hand"": ""fretting"",
  ""name"": ""G"",
  ""units"": ""mm"",
  ""space"": ""neck-local-unity"",
  ""source"": { ""blend"": ""hand_ik.blend"", ""date"": ""2026-11-14T16:07:01Z"", ""exporter"": ""export_poses.py 1.1.0"", ""blender"": ""5.2.2 LTS"" },
  ""rootPositionMm"": { ""x"": 85.049, ""y"": 61.008, ""z"": 121.828 },
  ""bones"": [
    { ""name"": ""wrist"", ""rotation"": { ""x"": -0.055501, ""y"": 0.995095, ""z"": -0.073505, ""w"": 0.036082 } },
    { ""name"": ""thumb_proximal"", ""rotation"": { ""x"": -0.026203, ""y"": 0.238822, ""z"": -0.005911, ""w"": 0.970692 } },
    { ""name"": ""thumb_intermediate"", ""rotation"": { ""x"": -0.016354, ""y"": -0.138382, ""z"": -0.20384, ""w"": 0.969037 } },
    { ""name"": ""thumb_distal"", ""rotation"": { ""x"": 0.066133, ""y"": -0.09762, ""z"": -0.234221, ""w"": 0.965006 } },
    { ""name"": ""index_proximal"", ""rotation"": { ""x"": -0.189425, ""y"": 0.007296, ""z"": 0.0289, ""w"": 0.981443 } },
    { ""name"": ""index_intermediate"", ""rotation"": { ""x"": 0.322828, ""y"": 0.009601, ""z"": -0.025473, ""w"": 0.946066 } },
    { ""name"": ""index_distal"", ""rotation"": { ""x"": 0.297022, ""y"": 0.004375, ""z"": -0.025322, ""w"": 0.954525 } },
    { ""name"": ""middle_proximal"", ""rotation"": { ""x"": -0.193598, ""y"": -0.009942, ""z"": -0.012155, ""w"": 0.980955 } },
    { ""name"": ""middle_intermediate"", ""rotation"": { ""x"": 0.310115, ""y"": -0.013902, ""z"": -0.011574, ""w"": 0.950527 } },
    { ""name"": ""middle_distal"", ""rotation"": { ""x"": 0.281585, ""y"": -0.006087, ""z"": -0.008019, ""w"": 0.959483 } },
    { ""name"": ""ring_proximal"", ""rotation"": { ""x"": -0.048538, ""y"": 0.066655, ""z"": 0.060541, ""w"": 0.994754 } },
    { ""name"": ""ring_intermediate"", ""rotation"": { ""x"": 0.451786, ""y"": 0.046224, ""z"": 0.100097, ""w"": 0.885287 } },
    { ""name"": ""ring_distal"", ""rotation"": { ""x"": 0.356523, ""y"": 0.021056, ""z"": 0.066855, ""w"": 0.931654 } },
    { ""name"": ""pinky_proximal"", ""rotation"": { ""x"": 0.271638, ""y"": 0.009053, ""z"": -0.001516, ""w"": 0.962356 } },
    { ""name"": ""pinky_intermediate"", ""rotation"": { ""x"": 0.32612, ""y"": 0.037359, ""z"": 0.096063, ""w"": 0.939693 } },
    { ""name"": ""pinky_distal"", ""rotation"": { ""x"": 0.154026, ""y"": 0.045893, ""z"": 0.083547, ""w"": 0.983458 } }
  ],
  ""fingertips"": [
    { ""bone"": ""thumb_distal"", ""positionMm"": { ""x"": 19.271, ""y"": -4.164, ""z"": 43.82 } },
    { ""bone"": ""index_distal"", ""positionMm"": { ""x"": 62.116, ""y"": 1.5, ""z"": -11.018 } },
    { ""bone"": ""middle_distal"", ""positionMm"": { ""x"": 94.999, ""y"": 1.5, ""z"": -18.819 } },
    { ""bone"": ""ring_distal"", ""positionMm"": { ""x"": 94.999, ""y"": 1.5, ""z"": 18.819 } },
    { ""bone"": ""pinky_distal"", ""positionMm"": { ""x"": 126.005, ""y"": -0.01, ""z"": 50.936 } }
  ],
  ""bind"": {
    ""joints"": [
      { ""bone"": ""wrist"", ""positionMm"": { ""x"": 0, ""y"": 0, ""z"": 0 } },
      { ""bone"": ""thumb_proximal"", ""positionMm"": { ""x"": 32, ""y"": -6, ""z"": 28 } },
      { ""bone"": ""thumb_intermediate"", ""positionMm"": { ""x"": 56.055, ""y"": -12.599, ""z"": 56.667 } },
      { ""bone"": ""thumb_distal"", ""positionMm"": { ""x"": 73.532, ""y"": -25.277, ""z"": 77.496 } },
      { ""bone"": ""index_proximal"", ""positionMm"": { ""x"": 30, ""y"": 0, ""z"": 86 } },
      { ""bone"": ""index_intermediate"", ""positionMm"": { ""x"": 34.241, ""y"": -10.87, ""z"": 126.347 } },
      { ""bone"": ""index_distal"", ""positionMm"": { ""x"": 36.467, ""y"": -25.783, ""z"": 147.528 } },
      { ""bone"": ""middle_proximal"", ""positionMm"": { ""x"": 10, ""y"": 0, ""z"": 88 } },
      { ""bone"": ""middle_intermediate"", ""positionMm"": { ""x"": 10, ""y"": -11.906, ""z"": 132.433 } },
      { ""bone"": ""middle_distal"", ""positionMm"": { ""x"": 10, ""y"": -28.539, ""z"": 156.188 } },
      { ""bone"": ""ring_proximal"", ""positionMm"": { ""x"": -10, ""y"": 0, ""z"": 86 } },
      { ""bone"": ""ring_intermediate"", ""positionMm"": { ""x"": -13.536, ""y"": -10.87, ""z"": 126.414 } },
      { ""bone"": ""ring_distal"", ""positionMm"": { ""x"": -15.463, ""y"": -26.357, ""z"": 148.447 } },
      { ""bone"": ""pinky_proximal"", ""positionMm"": { ""x"": -28, ""y"": 0, ""z"": 80 } },
      { ""bone"": ""pinky_intermediate"", ""positionMm"": { ""x"": -34.627, ""y"": -8.541, ""z"": 111.179 } },
      { ""bone"": ""pinky_distal"", ""positionMm"": { ""x"": -38.034, ""y"": -20.013, ""z"": 127.204 } }
    ],
    ""fingertips"": [
      { ""bone"": ""thumb_distal"", ""positionMm"": { ""x"": 86.334, ""y"": -41.99, ""z"": 92.753 } },
      { ""bone"": ""index_distal"", ""positionMm"": { ""x"": 37.786, ""y"": -43.805, ""z"": 160.077 } },
      { ""bone"": ""middle_distal"", ""positionMm"": { ""x"": 10, ""y"": -47.38, ""z"": 169.38 } },
      { ""bone"": ""ring_distal"", ""positionMm"": { ""x"": -16.563, ""y"": -44.378, ""z"": 161.018 } },
      { ""bone"": ""pinky_distal"", ""positionMm"": { ""x"": -40.299, ""y"": -35.576, ""z"": 137.864 } }
    ]
  }
}
";
        const string ExportedEm = @"{
  ""schema"": 1,
  ""hand"": ""fretting"",
  ""name"": ""Em"",
  ""units"": ""mm"",
  ""space"": ""neck-local-unity"",
  ""source"": { ""blend"": ""hand_markers.blend"", ""date"": ""2026-11-14T16:07:14Z"", ""exporter"": ""export_poses.py 1.1.0"", ""blender"": ""5.2.2 LTS"" },
  ""rootPositionMm"": { ""x"": 82.597, ""y"": 61.465, ""z"": 120.815 },
  ""bones"": [
    { ""name"": ""wrist"", ""rotation"": { ""x"": -0.010685, ""y"": 0.992494, ""z"": -0.121828, ""w"": 0.000468 } },
    { ""name"": ""thumb_proximal"", ""rotation"": { ""x"": 0.068363, ""y"": 0.20978, ""z"": -0.027817, ""w"": 0.974959 } },
    { ""name"": ""thumb_intermediate"", ""rotation"": { ""x"": -0.033372, ""y"": -0.120869, ""z"": -0.10535, ""w"": 0.986498 } },
    { ""name"": ""thumb_distal"", ""rotation"": { ""x"": 0.033445, ""y"": -0.095936, ""z"": -0.123845, ""w"": 0.987087 } },
    { ""name"": ""index_proximal"", ""rotation"": { ""x"": -0.368951, ""y"": 0.026905, ""z"": 0.090023, ""w"": 0.924688 } },
    { ""name"": ""index_intermediate"", ""rotation"": { ""x"": 0.502202, ""y"": 0.039869, ""z"": -0.002632, ""w"": 0.863827 } },
    { ""name"": ""index_distal"", ""rotation"": { ""x"": 0.477296, ""y"": 0.020417, ""z"": -0.016677, ""w"": 0.878347 } },
    { ""name"": ""middle_proximal"", ""rotation"": { ""x"": -0.302072, ""y"": 0.027057, ""z"": 0.043872, ""w"": 0.95189 } },
    { ""name"": ""middle_intermediate"", ""rotation"": { ""x"": 0.394163, ""y"": 0.045052, ""z"": 0.042223, ""w"": 0.916964 } },
    { ""name"": ""middle_distal"", ""rotation"": { ""x"": 0.370727, ""y"": 0.020535, ""z"": 0.028766, ""w"": 0.928069 } },
    { ""name"": ""ring_proximal"", ""rotation"": { ""x"": -0.213348, ""y"": 0.120243, ""z"": 0.107875, ""w"": 0.963529 } },
    { ""name"": ""ring_intermediate"", ""rotation"": { ""x"": 0.293697, ""y"": 0.102065, ""z"": 0.141568, ""w"": 0.939831 } },
    { ""name"": ""ring_distal"", ""rotation"": { ""x"": 0.278867, ""y"": 0.045884, ""z"": 0.095092, ""w"": 0.954508 } },
    { ""name"": ""pinky_proximal"", ""rotation"": { ""x"": 0.122759, ""y"": -0.054637, ""z"": -0.007665, ""w"": 0.990902 } },
    { ""name"": ""pinky_intermediate"", ""rotation"": { ""x"": 0.181938, ""y"": 0.020842, ""z"": 0.053592, ""w"": 0.981627 } },
    { ""name"": ""pinky_distal"", ""rotation"": { ""x"": 0.068701, ""y"": 0.020663, ""z"": 0.041111, ""w"": 0.996576 } }
  ],
  ""fingertips"": [
    { ""bone"": ""thumb_distal"", ""positionMm"": { ""x"": -0.738, ""y"": -13.07, ""z"": 61.94 } },
    { ""bone"": ""index_distal"", ""positionMm"": { ""x"": 40, ""y"": 15, ""z"": 5 } },
    { ""bone"": ""middle_distal"", ""positionMm"": { ""x"": 62.116, ""y"": 1.5, ""z"": -11.018 } },
    { ""bone"": ""ring_distal"", ""positionMm"": { ""x"": 62.116, ""y"": 1.5, ""z"": -3.673 } },
    { ""bone"": ""pinky_distal"", ""positionMm"": { ""x"": 123.251, ""y"": -16.145, ""z"": 26.046 } }
  ],
  ""bind"": {
    ""joints"": [
      { ""bone"": ""wrist"", ""positionMm"": { ""x"": 0, ""y"": 0, ""z"": 0 } },
      { ""bone"": ""thumb_proximal"", ""positionMm"": { ""x"": 32, ""y"": -6, ""z"": 28 } },
      { ""bone"": ""thumb_intermediate"", ""positionMm"": { ""x"": 56.055, ""y"": -12.599, ""z"": 56.667 } },
      { ""bone"": ""thumb_distal"", ""positionMm"": { ""x"": 73.532, ""y"": -25.277, ""z"": 77.496 } },
      { ""bone"": ""index_proximal"", ""positionMm"": { ""x"": 30, ""y"": 0, ""z"": 86 } },
      { ""bone"": ""index_intermediate"", ""positionMm"": { ""x"": 34.241, ""y"": -10.87, ""z"": 126.347 } },
      { ""bone"": ""index_distal"", ""positionMm"": { ""x"": 36.467, ""y"": -25.783, ""z"": 147.528 } },
      { ""bone"": ""middle_proximal"", ""positionMm"": { ""x"": 10, ""y"": 0, ""z"": 88 } },
      { ""bone"": ""middle_intermediate"", ""positionMm"": { ""x"": 10, ""y"": -11.906, ""z"": 132.433 } },
      { ""bone"": ""middle_distal"", ""positionMm"": { ""x"": 10, ""y"": -28.539, ""z"": 156.188 } },
      { ""bone"": ""ring_proximal"", ""positionMm"": { ""x"": -10, ""y"": 0, ""z"": 86 } },
      { ""bone"": ""ring_intermediate"", ""positionMm"": { ""x"": -13.536, ""y"": -10.87, ""z"": 126.414 } },
      { ""bone"": ""ring_distal"", ""positionMm"": { ""x"": -15.463, ""y"": -26.357, ""z"": 148.447 } },
      { ""bone"": ""pinky_proximal"", ""positionMm"": { ""x"": -28, ""y"": 0, ""z"": 80 } },
      { ""bone"": ""pinky_intermediate"", ""positionMm"": { ""x"": -34.627, ""y"": -8.541, ""z"": 111.179 } },
      { ""bone"": ""pinky_distal"", ""positionMm"": { ""x"": -38.034, ""y"": -20.013, ""z"": 127.204 } }
    ],
    ""fingertips"": [
      { ""bone"": ""thumb_distal"", ""positionMm"": { ""x"": 86.334, ""y"": -41.99, ""z"": 92.753 } },
      { ""bone"": ""index_distal"", ""positionMm"": { ""x"": 37.786, ""y"": -43.805, ""z"": 160.077 } },
      { ""bone"": ""middle_distal"", ""positionMm"": { ""x"": 10, ""y"": -47.38, ""z"": 169.38 } },
      { ""bone"": ""ring_distal"", ""positionMm"": { ""x"": -16.563, ""y"": -44.378, ""z"": 161.018 } },
      { ""bone"": ""pinky_distal"", ""positionMm"": { ""x"": -40.299, ""y"": -35.576, ""z"": 137.864 } }
    ]
  }
}
";
        const string ExportedStrummingRest = @"{
  ""schema"": 1,
  ""hand"": ""strumming"",
  ""name"": ""rest"",
  ""units"": ""mm"",
  ""space"": ""neck-local-unity"",
  ""source"": { ""blend"": ""hand_markers.blend"", ""date"": ""2026-11-14T16:17:54Z"", ""exporter"": ""export_poses.py 1.1.0"", ""blender"": ""5.2.2 LTS"" },
  ""rootPositionMm"": { ""x"": 82.597, ""y"": 61.465, ""z"": 120.815 },
  ""bones"": [
    { ""name"": ""wrist"", ""rotation"": { ""x"": -0.010685, ""y"": 0.992494, ""z"": -0.121828, ""w"": 0.000468 } },
    { ""name"": ""thumb_proximal"", ""rotation"": { ""x"": 0.068363, ""y"": 0.20978, ""z"": -0.027817, ""w"": 0.974959 } },
    { ""name"": ""thumb_intermediate"", ""rotation"": { ""x"": -0.033372, ""y"": -0.120869, ""z"": -0.10535, ""w"": 0.986498 } },
    { ""name"": ""thumb_distal"", ""rotation"": { ""x"": 0.033445, ""y"": -0.095936, ""z"": -0.123845, ""w"": 0.987087 } },
    { ""name"": ""index_proximal"", ""rotation"": { ""x"": -0.368951, ""y"": 0.026905, ""z"": 0.090023, ""w"": 0.924688 } },
    { ""name"": ""index_intermediate"", ""rotation"": { ""x"": 0.502202, ""y"": 0.039869, ""z"": -0.002632, ""w"": 0.863827 } },
    { ""name"": ""index_distal"", ""rotation"": { ""x"": 0.477296, ""y"": 0.020417, ""z"": -0.016677, ""w"": 0.878347 } },
    { ""name"": ""middle_proximal"", ""rotation"": { ""x"": -0.302072, ""y"": 0.027057, ""z"": 0.043872, ""w"": 0.95189 } },
    { ""name"": ""middle_intermediate"", ""rotation"": { ""x"": 0.394163, ""y"": 0.045052, ""z"": 0.042223, ""w"": 0.916964 } },
    { ""name"": ""middle_distal"", ""rotation"": { ""x"": 0.370727, ""y"": 0.020535, ""z"": 0.028766, ""w"": 0.928069 } },
    { ""name"": ""ring_proximal"", ""rotation"": { ""x"": -0.213348, ""y"": 0.120243, ""z"": 0.107875, ""w"": 0.963529 } },
    { ""name"": ""ring_intermediate"", ""rotation"": { ""x"": 0.293697, ""y"": 0.102065, ""z"": 0.141568, ""w"": 0.939831 } },
    { ""name"": ""ring_distal"", ""rotation"": { ""x"": 0.278867, ""y"": 0.045884, ""z"": 0.095092, ""w"": 0.954508 } },
    { ""name"": ""pinky_proximal"", ""rotation"": { ""x"": 0.122759, ""y"": -0.054637, ""z"": -0.007665, ""w"": 0.990902 } },
    { ""name"": ""pinky_intermediate"", ""rotation"": { ""x"": 0.181938, ""y"": 0.020842, ""z"": 0.053592, ""w"": 0.981627 } },
    { ""name"": ""pinky_distal"", ""rotation"": { ""x"": 0.068701, ""y"": 0.020663, ""z"": 0.041111, ""w"": 0.996576 } }
  ],
  ""fingertips"": [
    { ""bone"": ""thumb_distal"", ""positionMm"": { ""x"": -0.738, ""y"": -13.07, ""z"": 61.94 } },
    { ""bone"": ""index_distal"", ""positionMm"": { ""x"": 40, ""y"": 15, ""z"": 5 } },
    { ""bone"": ""middle_distal"", ""positionMm"": { ""x"": 62.116, ""y"": 1.5, ""z"": -11.018 } },
    { ""bone"": ""ring_distal"", ""positionMm"": { ""x"": 62.116, ""y"": 1.5, ""z"": -3.673 } },
    { ""bone"": ""pinky_distal"", ""positionMm"": { ""x"": 123.251, ""y"": -16.145, ""z"": 26.046 } }
  ],
  ""bind"": {
    ""joints"": [
      { ""bone"": ""wrist"", ""positionMm"": { ""x"": 0, ""y"": 0, ""z"": 0 } },
      { ""bone"": ""thumb_proximal"", ""positionMm"": { ""x"": 32, ""y"": -6, ""z"": 28 } },
      { ""bone"": ""thumb_intermediate"", ""positionMm"": { ""x"": 56.055, ""y"": -12.599, ""z"": 56.667 } },
      { ""bone"": ""thumb_distal"", ""positionMm"": { ""x"": 73.532, ""y"": -25.277, ""z"": 77.496 } },
      { ""bone"": ""index_proximal"", ""positionMm"": { ""x"": 30, ""y"": 0, ""z"": 86 } },
      { ""bone"": ""index_intermediate"", ""positionMm"": { ""x"": 34.241, ""y"": -10.87, ""z"": 126.347 } },
      { ""bone"": ""index_distal"", ""positionMm"": { ""x"": 36.467, ""y"": -25.783, ""z"": 147.528 } },
      { ""bone"": ""middle_proximal"", ""positionMm"": { ""x"": 10, ""y"": 0, ""z"": 88 } },
      { ""bone"": ""middle_intermediate"", ""positionMm"": { ""x"": 10, ""y"": -11.906, ""z"": 132.433 } },
      { ""bone"": ""middle_distal"", ""positionMm"": { ""x"": 10, ""y"": -28.539, ""z"": 156.188 } },
      { ""bone"": ""ring_proximal"", ""positionMm"": { ""x"": -10, ""y"": 0, ""z"": 86 } },
      { ""bone"": ""ring_intermediate"", ""positionMm"": { ""x"": -13.536, ""y"": -10.87, ""z"": 126.414 } },
      { ""bone"": ""ring_distal"", ""positionMm"": { ""x"": -15.463, ""y"": -26.357, ""z"": 148.447 } },
      { ""bone"": ""pinky_proximal"", ""positionMm"": { ""x"": -28, ""y"": 0, ""z"": 80 } },
      { ""bone"": ""pinky_intermediate"", ""positionMm"": { ""x"": -34.627, ""y"": -8.541, ""z"": 111.179 } },
      { ""bone"": ""pinky_distal"", ""positionMm"": { ""x"": -38.034, ""y"": -20.013, ""z"": 127.204 } }
    ],
    ""fingertips"": [
      { ""bone"": ""thumb_distal"", ""positionMm"": { ""x"": 86.334, ""y"": -41.99, ""z"": 92.753 } },
      { ""bone"": ""index_distal"", ""positionMm"": { ""x"": 37.786, ""y"": -43.805, ""z"": 160.077 } },
      { ""bone"": ""middle_distal"", ""positionMm"": { ""x"": 10, ""y"": -47.38, ""z"": 169.38 } },
      { ""bone"": ""ring_distal"", ""positionMm"": { ""x"": -16.563, ""y"": -44.378, ""z"": 161.018 } },
      { ""bone"": ""pinky_distal"", ""positionMm"": { ""x"": -40.299, ""y"": -35.576, ""z"": 137.864 } }
    ]
  }
}
";

        // Where the G fingertips must be: the dots of the default mock-up (scale 648 mm, nut spread 35 mm, saddle spread
        // 53 mm, strings 1.5 mm above the board), as build_neck_mockup.py prints them, converted with
        // (x, y, z)_unity = (X, Z, Y)_blender. Test data only: runtime code takes its dots from Core.
        static readonly Vector3 DotGIndex = new Vector3(62.12f, 1.50f, -11.02f);    // finger 1: string 5, fret 2
        static readonly Vector3 DotGMiddle = new Vector3(95.00f, 1.50f, -18.82f);   // finger 2: string 6, fret 3
        static readonly Vector3 DotGRing = new Vector3(95.00f, 1.50f, 18.82f);      // finger 3: string 1, fret 3
        const float DotToleranceMm = 3f;                                            // docs/TODO.md SL4

        string _folder;

        [SetUp]
        public void CreateFolder()
        {
            _folder = Path.Combine(Path.GetTempPath(), "uga-ghost-poses-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void DeleteFolder()
        {
            try
            {
                if (_folder != null && Directory.Exists(_folder)) Directory.Delete(_folder, true);
            }
            catch (Exception)
            {
                // a leftover temp folder must not fail a test
            }
        }

        // ---- the acceptance criterion ----------------------------------------------------------------------------------
        [Test]
        public void EveryPoseJsonInStreamingAssetsLoads()
        {
            AssertEveryPoseFileLoads(GhostPoseLibrary.DefaultFolder);
        }

        /// <summary>Passes only when the folder holds pose files and every one loads without a warning.</summary>
        static void AssertEveryPoseFileLoads(string folder)
        {
            var poses = new List<GhostPose>();
            var errors = new List<string>();
            var warnings = new List<string>();
            GhostPoseLibrary.ReadFolder(folder, poses, errors, warnings);
            if (poses.Count == 0 && errors.Count == 0)
                Assert.Inconclusive("No *.pose.json file in " + folder + " yet, so nothing was checked. This is not a pass: export the poses (art/blender/README.md) and run the test again.");
            Assert.That(errors, Is.Empty, "refused pose files:\n" + string.Join("\n", errors.ToArray()));
            Assert.That(warnings, Is.Empty, "pose files that loaded with a warning:\n" + string.Join("\n", warnings.ToArray()));
            Assert.That(poses.Count, Is.GreaterThan(0));
        }

        [Test]
        public void FolderCheck_WithNoPoseFiles_IsInconclusiveNotPassed()
        {
            File.WriteAllText(Path.Combine(_folder, "README.md"), "not a pose");
            Assert.Throws<InconclusiveException>(() => AssertEveryPoseFileLoads(_folder));
        }

        [Test]
        public void FolderCheck_WithAMissingFolder_IsInconclusiveNotPassed()
        {
            Assert.Throws<InconclusiveException>(() => AssertEveryPoseFileLoads(Path.Combine(_folder, "nowhere")));
        }

        [Test]
        public void FolderCheck_WithExportedFiles_Passes()
        {
            WriteExportedSet();
            Assert.DoesNotThrow(() => AssertEveryPoseFileLoads(_folder));
        }

        [Test]
        public void FolderCheck_WithABrokenFile_Fails()
        {
            WriteExportedSet();
            File.WriteAllText(Path.Combine(_folder, "fretting-Em.pose.json"), ExportedEm.Substring(0, ExportedEm.Length / 2));
            Assert.Throws<AssertionException>(() => AssertEveryPoseFileLoads(_folder));
        }

        [Test]
        public void FolderCheck_WithOnlyABrokenFile_FailsRatherThanInconclusive()
        {
            File.WriteAllText(Path.Combine(_folder, "fretting-G.pose.json"), "{ \"schema\": 1 ");
            Assert.Throws<AssertionException>(() => AssertEveryPoseFileLoads(_folder));
        }

        [Test]
        public void FolderCheck_WithAFileThatHasNoBindBlock_Fails()
        {
            File.WriteAllText(Path.Combine(_folder, "fretting-G.pose.json"), WithoutBind(ExportedG));
            Assert.Throws<AssertionException>(() => AssertEveryPoseFileLoads(_folder));
        }

        // ---- parsing the exporter's real output --------------------------------------------------------------------------
        [Test]
        public void ExportedPose_Parses()
        {
            GhostPose pose = Parse(ExportedG);
            Assert.AreEqual("fretting", pose.Hand);
            Assert.AreEqual("G", pose.Name);
            Assert.AreEqual("hand_ik.blend", pose.SourceBlend);
            Assert.AreEqual("export_poses.py 1.1.0", pose.Exporter);
            Assert.AreEqual("2026-11-14T16:07:01Z", pose.SourceDate);
            Assert.IsTrue(pose.HasBind);
            AssertNear(new Vector3(85.049f, 61.008f, 121.828f), pose.RootPositionMm, 1e-3f, "rootPositionMm");
            AssertSameRotation(new Quaternion(-0.055501f, 0.995095f, -0.073505f, 0.036082f), pose.GetRotation(GhostPose.Wrist), 1e-5f, "wrist");
            AssertSameRotation(new Quaternion(0.356523f, 0.021056f, 0.066855f, 0.931654f), pose.GetRotation(GhostPose.RingDistal), 1e-5f, "ring_distal");
            AssertNear(new Vector3(19.271f, -4.164f, 43.82f), pose.GetFingertipMm(0), 1e-3f, "thumb tip");
            AssertNear(new Vector3(126.005f, -0.01f, 50.936f), pose.GetFingertipMm(4), 1e-3f, "pinky tip");
            AssertNear(Vector3.zero, pose.GetBindPositionMm(GhostPose.Wrist), 1e-6f, "bind wrist");
            AssertNear(new Vector3(30f, 0f, 86f), pose.GetBindPositionMm(GhostPose.IndexProximal), 1e-3f, "bind index_proximal");
            AssertNear(new Vector3(10f, -47.38f, 169.38f), pose.GetBindPositionMm(GhostPose.BoneCount + 2), 1e-3f, "bind middle tip");
        }

        [Test]
        public void ExportedPose_EveryRotationHasUnitLength()
        {
            GhostPose pose = Parse(ExportedEm);
            for (int bone = 0; bone < GhostPose.BoneCount; bone++)
                Assert.That(Length(pose.GetRotation(bone)), Is.EqualTo(1f).Within(1e-6f), GhostPose.BoneNames[bone]);
        }

        [Test]
        public void ExportedPoses_AllThreeParse()
        {
            Assert.AreEqual("Em", Parse(ExportedEm).Name);
            GhostPose strum = Parse(ExportedStrummingRest);
            Assert.AreEqual("strumming", strum.Hand);
            Assert.AreEqual("rest", strum.Name);
        }

        [Test]
        public void ExportedPose_RotationsRebuildItsFingertips()
        {
            foreach (string json in new[] { ExportedG, ExportedEm, ExportedStrummingRest })
            {
                GhostPose pose = Parse(json);
                var rotations = new Quaternion[GhostPose.BoneCount];
                var totals = new Quaternion[GhostPose.BoneCount];
                var positions = new Vector3[GhostPose.JointCount];
                Assert.IsTrue(pose.CopyRotations(rotations));
                Assert.IsTrue(pose.ComputeJointPositionsMm(rotations, pose.RootPositionMm, totals, positions));
                AssertNear(pose.RootPositionMm, positions[GhostPose.Wrist], 1e-6f, "wrist joint");
                for (int digit = 0; digit < GhostPose.DigitCount; digit++)
                    AssertNear(pose.GetFingertipMm(digit), positions[GhostPose.BoneCount + digit], 0.02f, pose.Name + " fingertip " + digit);
            }
        }

        [Test]
        public void ExportedG_FingertipsAreWithinThreeMillimetresOfTheGDots()
        {
            GhostPose pose = Parse(ExportedG);
            var rotations = new Quaternion[GhostPose.BoneCount];
            var totals = new Quaternion[GhostPose.BoneCount];
            var positions = new Vector3[GhostPose.JointCount];
            pose.CopyRotations(rotations);
            Assert.IsTrue(pose.ComputeJointPositionsMm(rotations, pose.RootPositionMm, totals, positions));
            // as authored
            AssertNear(DotGIndex, pose.GetFingertipMm(1), DotToleranceMm, "index fingertip (stored)");
            AssertNear(DotGMiddle, pose.GetFingertipMm(2), DotToleranceMm, "middle fingertip (stored)");
            AssertNear(DotGRing, pose.GetFingertipMm(3), DotToleranceMm, "ring fingertip (stored)");
            // as Unity rebuilds them from the rotations
            AssertNear(DotGIndex, positions[GhostPose.BoneCount + 1], DotToleranceMm, "index fingertip (from rotations)");
            AssertNear(DotGMiddle, positions[GhostPose.BoneCount + 2], DotToleranceMm, "middle fingertip (from rotations)");
            AssertNear(DotGRing, positions[GhostPose.BoneCount + 3], DotToleranceMm, "ring fingertip (from rotations)");
            // and this check can fail: the thumb is nowhere near a dot
            Assert.That((pose.GetFingertipMm(0) - DotGIndex).magnitude, Is.GreaterThan(DotToleranceMm));
        }

        [Test]
        public void ExportedPose_JointsKeepTheirBindDistances()
        {
            GhostPose pose = Parse(ExportedG);
            var rotations = new Quaternion[GhostPose.BoneCount];
            var totals = new Quaternion[GhostPose.BoneCount];
            var positions = new Vector3[GhostPose.JointCount];
            pose.CopyRotations(rotations);
            pose.ComputeJointPositionsMm(rotations, pose.RootPositionMm, totals, positions);
            AssertBoneLengthsMatchBind(pose, positions, 1e-3f);
        }

        // ---- the axis rule of the README ---------------------------------------------------------------------------------
        [Test]
        public void AxisRule_QuarterTurnAboutBlenderZ_CarriesUnityXToUnityZ()
        {
            // Blender (w, x, y, z) = (0.7071, 0, 0, 0.7071) carries +X to +Y. Unity (x, y, z, w) = (-x, -z, -y, w).
            const float h = 0.70710678f;
            var unity = new Quaternion(-0f, -h, -0f, h);
            AssertNear(new Vector3(0f, 0f, 1f), unity * new Vector3(1f, 0f, 0f), 1e-6f, "Blender +Y is Unity +z");
            AssertNear(new Vector3(0f, 1f, 0f), unity * new Vector3(0f, 1f, 0f), 1e-6f, "the turn axis (Blender +Z = Unity +y) stays");
        }

        // ---- validation: every refusal names its reason and nothing throws -----------------------------------------------
        [Test]
        public void TryParse_RefusesNothingnessAndGarbage()
        {
            AssertRefused(null, "empty");
            AssertRefused("", "empty");
            AssertRefused("  \n ", "empty");
            AssertRefused("this is not json", "not valid JSON");
            AssertRefused(ExportedG.Substring(0, 300), "not valid JSON");
        }

        [Test]
        public void TryParse_RefusesAnotherSchema()
        {
            AssertRefused(Swap(ExportedG, "\"schema\": 1,", "\"schema\": 2,"), "schema is 2");
            AssertRefused(Swap(ExportedG, "  \"schema\": 1,\n", ""), "schema is 0");
        }

        [Test]
        public void TryParse_RefusesAnUnknownHandOrName()
        {
            AssertRefused(Swap(ExportedG, "\"hand\": \"fretting\"", "\"hand\": \"left\""), "hand is 'left'");
            AssertRefused(Swap(ExportedG, "\"name\": \"G\"", "\"name\": \"H7\""), "'H7' is not a known fretting pose");
            AssertRefused(Swap(ExportedG, "\"name\": \"G\"", "\"name\": \"g\""), "'g' is not a known fretting pose");
            AssertRefused(Swap(ExportedG, "\"name\": \"G\"", "\"name\": \"down-start\""), "'down-start' is not a known fretting pose");
            AssertRefused(Swap(ExportedStrummingRest, "\"name\": \"rest\"", "\"name\": \"G\""), "'G' is not a known strumming pose");
            AssertRefused(Swap(ExportedG, "  \"hand\": \"fretting\",\n", ""), "hand is ''");
        }

        [Test]
        public void TryParse_RefusesOtherUnitsOrSpace()
        {
            AssertRefused(Swap(ExportedG, "\"units\": \"mm\"", "\"units\": \"m\""), "units is 'm'");
            AssertRefused(Swap(ExportedG, "\"space\": \"neck-local-unity\"", "\"space\": \"world\""), "space is 'world'");
        }

        [Test]
        public void TryParse_RefusesAMissingBone()
        {
            string line = "    { \"name\": \"ring_distal\", \"rotation\": { \"x\": 0.356523, \"y\": 0.021056, \"z\": 0.066855, \"w\": 0.931654 } },\n";
            AssertRefused(Swap(ExportedG, line, ""), "missing bones: ring_distal");
        }

        [Test]
        public void TryParse_RefusesABoneListedTwiceOrUnknown()
        {
            AssertRefused(Swap(ExportedG, "{ \"name\": \"ring_distal\", \"rotation\"", "{ \"name\": \"ring_intermediate\", \"rotation\""), "ring_intermediate is listed twice");
            AssertRefused(Swap(ExportedG, "{ \"name\": \"wrist\", \"rotation\"", "{ \"name\": \"hand\", \"rotation\""), "unknown name 'hand'");
            AssertRefused(Swap(ExportedG, "{ \"name\": \"wrist\", \"rotation\"", "{ \"name\": \"hand\", \"rotation\""), "missing bones: wrist");
        }

        [Test]
        public void TryParse_RefusesARotationThatIsNotAUnitQuaternion()
        {
            AssertRefused(Swap(ExportedG, "\"z\": -0.073505, \"w\": 0.036082", "\"z\": -0.073505, \"w\": 0.5"), "wrist: rotation is not a unit quaternion");
            AssertRefused(Swap(ExportedG, "{ \"name\": \"wrist\", \"rotation\": { \"x\": -0.055501, \"y\": 0.995095, \"z\": -0.073505, \"w\": 0.036082 } }", "{ \"name\": \"wrist\" }"),
                "wrist: rotation is not a unit quaternion (length 0)");
        }

        [Test]
        public void TryParse_AcceptsARotationOffByRoundingAndNormalisesIt()
        {
            // w 0.036082 -> 0.0365: the length changes by about 2e-5, inside the 1e-3 tolerance
            string json = Swap(ExportedG, "\"z\": -0.073505, \"w\": 0.036082", "\"z\": -0.073505, \"w\": 0.0365");
            GhostPose pose = Parse(json);
            Assert.That(Length(pose.GetRotation(GhostPose.Wrist)), Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void TryParse_RefusesBadFingertips()
        {
            string tip = "    { \"bone\": \"pinky_distal\", \"positionMm\": { \"x\": 126.005, \"y\": -0.01, \"z\": 50.936 } }\n";
            AssertRefused(Swap(ExportedG, ",\n" + tip, "\n"), "missing fingertip: pinky_distal");
            AssertRefused(Swap(ExportedG, "{ \"bone\": \"pinky_distal\", \"positionMm\": { \"x\": 126.005", "{ \"bone\": \"pinky_proximal\", \"positionMm\": { \"x\": 126.005"), "not a distal bone");
            AssertRefused(Swap(ExportedG, "{ \"bone\": \"pinky_distal\", \"positionMm\": { \"x\": 126.005", "{ \"bone\": \"ring_distal\", \"positionMm\": { \"x\": 126.005"), "ring_distal is listed twice");
        }

        [Test]
        public void TryParse_RefusesAnIncompleteBindBlock()
        {
            AssertRefused(Swap(ExportedG, "      { \"bone\": \"wrist\", \"positionMm\": { \"x\": 0, \"y\": 0, \"z\": 0 } },\n", ""), "bind must list all 16 joints and 5 fingertips (found 20 of 21)");
        }

        [Test]
        public void TryParse_RefusesRotationsThatDoNotRebuildTheFingertips()
        {
            // one finger bone straightened: still 16 unit quaternions, but the index fingertip is no longer where the file says
            string broken = Swap(ExportedG, "{ \"name\": \"index_proximal\", \"rotation\": { \"x\": -0.189425, \"y\": 0.007296, \"z\": 0.0289, \"w\": 0.981443 } }",
                "{ \"name\": \"index_proximal\", \"rotation\": { \"x\": 0, \"y\": 0, \"z\": 0, \"w\": 1 } }");
            AssertRefused(broken, "index_distal's tip");
            AssertRefused(broken, "re-export");
        }

        [Test]
        public void TryParse_RefusesAFileWrittenWithAnotherAxisConvention()
        {
            // the wrist of the exported G in Blender's own component order (w, x, y, z) -> (x, y, z, w) with no sign change:
            // a plausible exporter mistake that keeps every quaternion at unit length
            string broken = Swap(ExportedG, "{ \"x\": -0.055501, \"y\": 0.995095, \"z\": -0.073505, \"w\": 0.036082 }", "{ \"x\": 0.055501, \"y\": 0.073505, \"z\": -0.995095, \"w\": 0.036082 }");
            AssertRefused(broken, "mm from the stored fingertip");
        }

        [Test]
        public void TryParse_ReportsEveryProblemAtOnce()
        {
            string json = Swap(Swap(ExportedG, "\"schema\": 1,", "\"schema\": 7,"), "\"units\": \"mm\"", "\"units\": \"cm\"");
            GhostPose pose;
            string error;
            Assert.IsFalse(GhostPose.TryParse(json, out pose, out error));
            StringAssert.Contains("schema is 7", error);
            StringAssert.Contains("units is 'cm'", error);
        }

        [Test]
        public void TryParse_AcceptsAFileWithoutBind_ButItCannotRebuildJoints()
        {
            GhostPose pose = Parse(WithoutBind(ExportedG));
            Assert.IsFalse(pose.HasBind);
            Assert.AreEqual("G", pose.Name);
            var rotations = new Quaternion[GhostPose.BoneCount];
            Assert.IsTrue(pose.CopyRotations(rotations));
            Assert.IsFalse(pose.ComputeJointPositionsMm(rotations, pose.RootPositionMm, new Quaternion[GhostPose.BoneCount], new Vector3[GhostPose.JointCount]));
            Assert.That(pose.MaxBindDistanceMm(Parse(ExportedG)), Is.EqualTo(float.PositiveInfinity));
        }

        [Test]
        public void TryParse_IgnoresKeysItDoesNotKnow()
        {
            GhostPose pose = Parse(Swap(ExportedG, "\"units\": \"mm\",", "\"units\": \"mm\",\n  \"comment\": \"added later\",\n  \"extra\": { \"a\": [1, 2, 3] },"));
            Assert.AreEqual("G", pose.Name);
        }

        [Test]
        public void TryParse_AcceptsBonesInAnyOrder()
        {
            string first = "    { \"name\": \"wrist\", \"rotation\": { \"x\": -0.055501, \"y\": 0.995095, \"z\": -0.073505, \"w\": 0.036082 } },\n";
            string second = "    { \"name\": \"thumb_proximal\", \"rotation\": { \"x\": -0.026203, \"y\": 0.238822, \"z\": -0.005911, \"w\": 0.970692 } },\n";
            GhostPose swapped = Parse(Swap(ExportedG, first + second, second + first));
            GhostPose original = Parse(ExportedG);
            for (int bone = 0; bone < GhostPose.BoneCount; bone++)
                AssertSameRotation(original.GetRotation(bone), swapped.GetRotation(bone), 1e-6f, GhostPose.BoneNames[bone]);
        }

        // ---- the bone table ----------------------------------------------------------------------------------------------
        [Test]
        public void BoneTable_IsSixteenBonesWithParentsFirst()
        {
            Assert.AreEqual(16, GhostPose.BoneCount);
            Assert.AreEqual(GhostPose.BoneCount, GhostPose.BoneNames.Count);
            Assert.AreEqual(GhostPose.BoneCount, GhostPose.BoneParents.Count);
            Assert.AreEqual("wrist", GhostPose.BoneNames[GhostPose.Wrist]);
            Assert.AreEqual(-1, GhostPose.BoneParents[GhostPose.Wrist]);
            var seen = new HashSet<string>();
            for (int bone = 0; bone < GhostPose.BoneCount; bone++)
            {
                Assert.IsTrue(seen.Add(GhostPose.BoneNames[bone]), "repeated name " + GhostPose.BoneNames[bone]);
                Assert.That(GhostPose.BoneParents[bone], Is.LessThan(bone), "the parent of " + GhostPose.BoneNames[bone] + " must come first");
                Assert.AreEqual(bone, GhostPose.BoneIndex(GhostPose.BoneNames[bone]));
            }
            Assert.AreEqual(-1, GhostPose.BoneIndex("palm"));
            Assert.AreEqual(-1, GhostPose.BoneIndex(null));
            string[] digits = { "thumb", "index", "middle", "ring", "pinky" };
            for (int digit = 0; digit < GhostPose.DigitCount; digit++)
            {
                int proximal = GhostPose.ProximalBone(digit), distal = GhostPose.DistalBone(digit);
                Assert.AreEqual(digits[digit] + "_proximal", GhostPose.BoneNames[proximal]);
                Assert.AreEqual(digits[digit] + "_intermediate", GhostPose.BoneNames[proximal + 1]);
                Assert.AreEqual(digits[digit] + "_distal", GhostPose.BoneNames[distal]);
                Assert.AreEqual(GhostPose.Wrist, GhostPose.BoneParents[proximal]);
                Assert.AreEqual(proximal, GhostPose.BoneParents[proximal + 1]);
                Assert.AreEqual(proximal + 1, GhostPose.BoneParents[distal]);
            }
            Assert.AreEqual(GhostPose.IndexDistal, GhostPose.DistalBone(1));
            Assert.AreEqual(GhostPose.PinkyProximal, GhostPose.ProximalBone(4));
            Assert.AreEqual(21, GhostPose.JointCount);
        }

        [Test]
        public void BoneTableAndPoseNames_MatchTheBlenderExporter()
        {
            string script = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "art", "blender", "export_poses.py"));
            if (!File.Exists(script)) Assert.Inconclusive("art/blender/export_poses.py not found at " + script + ", so the two bone tables were not compared.");
            string text = File.ReadAllText(script);

            Match bones = Regex.Match(text, @"^BONES = \[(.*?)^\]", RegexOptions.Singleline | RegexOptions.Multiline);
            Assert.IsTrue(bones.Success, "BONES = [ ... ] not found in export_poses.py");
            MatchCollection pairs = Regex.Matches(bones.Groups[1].Value, "\\(\"([a-z_]+)\",\\s*(-?\\d+)\\)");
            Assert.AreEqual(GhostPose.BoneCount, pairs.Count, "number of bones in export_poses.py");
            for (int bone = 0; bone < pairs.Count; bone++)
            {
                Assert.AreEqual(pairs[bone].Groups[1].Value, GhostPose.BoneNames[bone], "bone " + bone);
                Assert.AreEqual(int.Parse(pairs[bone].Groups[2].Value, CultureInfo.InvariantCulture), GhostPose.BoneParents[bone], "parent of " + GhostPose.BoneNames[bone]);
            }

            AssertNamesMatch(text, "fretting", GhostPose.FrettingPoseNames);
            AssertNamesMatch(text, "strumming", GhostPose.StrummingPoseNames);
            StringAssert.Contains("SCHEMA = " + GhostPose.SchemaVersion + "\n", text);
            StringAssert.Contains("FK_LIMIT_MM = " + GhostPose.FkToleranceMm.ToString("0.0", CultureInfo.InvariantCulture) + " ", text);
        }

        static void AssertNamesMatch(string script, string hand, IList<string> expected)
        {
            Match line = Regex.Match(script, "\"" + hand + "\": \\(([^)]*)\\)");
            Assert.IsTrue(line.Success, "KNOWN_POSES entry for " + hand + " not found in export_poses.py");
            var names = new List<string>();
            foreach (Match m in Regex.Matches(line.Groups[1].Value, "\"([^\"]+)\"")) names.Add(m.Groups[1].Value);
            Assert.AreEqual(string.Join(",", new List<string>(expected).ToArray()), string.Join(",", names.ToArray()), hand + " pose names");
        }

        [Test]
        public void KnownPoses_AreCaseSensitiveAndPerHand()
        {
            Assert.IsTrue(GhostPose.IsKnownPose("fretting", "Em"));
            Assert.IsFalse(GhostPose.IsKnownPose("fretting", "em"));
            Assert.IsFalse(GhostPose.IsKnownPose("fretting", "down-start"));
            Assert.IsTrue(GhostPose.IsKnownPose("strumming", "down-start"));
            Assert.IsTrue(GhostPose.IsKnownPose("strumming", "rest"));
            Assert.IsFalse(GhostPose.IsKnownPose("left", "rest"));
            Assert.IsFalse(GhostPose.IsKnownPose("fretting", null));
            Assert.IsFalse(GhostPose.IsKnownPose(null, "G"));
            Assert.AreEqual("fretting-G.pose.json", GhostPose.FileNameFor("fretting", "G"));
            foreach (string must in new[] { "rest", "G", "C", "D", "Em", "Am" })   // docs/GHOST_HANDS.md section 5
                Assert.IsTrue(GhostPose.IsKnownPose(GhostPose.HandFretting, must), must);
            foreach (string must in new[] { "rest", "down-start", "down-end", "up-start", "up-end", "pick-ready" })
                Assert.IsTrue(GhostPose.IsKnownPose(GhostPose.HandStrumming, must), must);
        }

        // ---- blending ----------------------------------------------------------------------------------------------------
        [Test]
        public void Ease_IsSmoothstepClampedToZeroOne()
        {
            Assert.AreEqual(0f, GhostPose.Ease(0f));
            Assert.AreEqual(1f, GhostPose.Ease(1f));
            Assert.AreEqual(0f, GhostPose.Ease(-3f));
            Assert.AreEqual(1f, GhostPose.Ease(7f));
            Assert.AreEqual(0f, GhostPose.Ease(float.NaN));
            Assert.That(GhostPose.Ease(0.5f), Is.EqualTo(0.5f).Within(1e-7f));
            Assert.That(GhostPose.Ease(0.25f), Is.EqualTo(0.15625f).Within(1e-7f));
            Assert.That(GhostPose.Ease(0.75f), Is.EqualTo(0.84375f).Within(1e-7f));
            float previous = 0f;
            for (int i = 1; i <= 100; i++)
            {
                float now = GhostPose.Ease(i / 100f);
                Assert.That(now, Is.GreaterThanOrEqualTo(previous), "Ease must never go backwards (t = " + i / 100f + ")");
                previous = now;
            }
        }

        [Test]
        public void Slerp_HalfwayToAQuarterTurn_IsAnEighthTurn()
        {
            var quarter = new Quaternion(Mathf.Sin(Mathf.PI / 4f), 0f, 0f, Mathf.Cos(Mathf.PI / 4f));
            var eighth = new Quaternion(Mathf.Sin(Mathf.PI / 8f), 0f, 0f, Mathf.Cos(Mathf.PI / 8f));
            AssertSameRotation(eighth, GhostPose.Slerp(Quaternion.identity, quarter, 0.5f), 1e-6f, "halfway");
            AssertSameRotation(Quaternion.identity, GhostPose.Slerp(Quaternion.identity, quarter, 0f), 1e-6f, "start");
            AssertSameRotation(quarter, GhostPose.Slerp(Quaternion.identity, quarter, 1f), 1e-6f, "end");
            // a third of the way is a third of the angle: 30 degrees of the 90
            var third = new Quaternion(Mathf.Sin(Mathf.PI / 12f), 0f, 0f, Mathf.Cos(Mathf.PI / 12f));
            AssertSameRotation(third, GhostPose.Slerp(Quaternion.identity, quarter, 1f / 3f), 1e-6f, "a third");
        }

        [Test]
        public void Slerp_TakesTheShortWayRound()
        {
            var a = new Quaternion(0f, 0f, 0f, 1f);
            var b = new Quaternion(0f, Mathf.Sin(0.3f), 0f, Mathf.Cos(0.3f));          // 0.6 rad about y
            var minusB = new Quaternion(-b.x, -b.y, -b.z, -b.w);                        // the same rotation
            Quaternion viaB = GhostPose.Slerp(a, b, 0.5f);
            Quaternion viaMinusB = GhostPose.Slerp(a, minusB, 0.5f);
            AssertSameRotation(viaB, viaMinusB, 1e-6f, "q and -q");
            AssertSameRotation(new Quaternion(0f, Mathf.Sin(0.15f), 0f, Mathf.Cos(0.15f)), viaMinusB, 1e-6f, "half of 0.6 rad, not half of the long way");
        }

        [Test]
        public void Slerp_AlwaysReturnsUnitLength()
        {
            var a = new Quaternion(0.1f, -0.2f, 0.3f, 0.927362f);
            var nearlyA = new Quaternion(0.1001f, -0.2f, 0.3f, 0.927351f);
            var far = new Quaternion(0.9f, 0.3f, -0.2f, 0.244949f);
            for (int i = 0; i <= 10; i++)
            {
                Assert.That(Length(GhostPose.Slerp(a, nearlyA, i / 10f)), Is.EqualTo(1f).Within(1e-6f), "nearly equal, t = " + i / 10f);
                Assert.That(Length(GhostPose.Slerp(a, far, i / 10f)), Is.EqualTo(1f).Within(1e-6f), "far apart, t = " + i / 10f);
            }
        }

        [Test]
        public void Slerp_MatchesUnitysOwn()
        {
            var a = new Quaternion(0.1f, -0.2f, 0.3f, 0.927362f);
            var b = new Quaternion(-0.5f, 0.5f, 0.5f, 0.5f);
            for (int i = 0; i <= 8; i++)
                AssertSameRotation(Quaternion.Slerp(a, b, i / 8f), GhostPose.Slerp(a, b, i / 8f), 1e-5f, "t = " + i / 8f);
        }

        [Test]
        public void Blend_AtTheEnds_GivesTheTwoPoses()
        {
            GhostPose g = Parse(ExportedG), em = Parse(ExportedEm);
            var rotations = new Quaternion[GhostPose.BoneCount];
            Vector3 root;
            Assert.IsTrue(GhostPose.Blend(g, em, 0f, rotations, out root));
            AssertNear(g.RootPositionMm, root, 1e-6f, "root at 0");
            for (int bone = 0; bone < GhostPose.BoneCount; bone++) AssertSameRotation(g.GetRotation(bone), rotations[bone], 1e-6f, GhostPose.BoneNames[bone] + " at 0");
            Assert.IsTrue(GhostPose.Blend(g, em, 1f, rotations, out root));
            AssertNear(em.RootPositionMm, root, 1e-6f, "root at 1");
            for (int bone = 0; bone < GhostPose.BoneCount; bone++) AssertSameRotation(em.GetRotation(bone), rotations[bone], 1e-6f, GhostPose.BoneNames[bone] + " at 1");
            Assert.IsTrue(GhostPose.Blend(g, em, -2f, rotations, out root));
            AssertNear(g.RootPositionMm, root, 1e-6f, "t below 0 clamps to the first pose");
            Assert.IsTrue(GhostPose.Blend(g, em, 9f, rotations, out root));
            AssertNear(em.RootPositionMm, root, 1e-6f, "t above 1 clamps to the second pose");
        }

        [Test]
        public void Blend_IsEased_AndBlendLinearIsNot()
        {
            GhostPose g = Parse(ExportedG), em = Parse(ExportedEm);
            var eased = new Quaternion[GhostPose.BoneCount];
            var linear = new Quaternion[GhostPose.BoneCount];
            Vector3 easedRoot, linearRoot;
            Assert.IsTrue(GhostPose.Blend(g, em, 0.25f, eased, out easedRoot));
            Assert.IsTrue(GhostPose.BlendLinear(g, em, 0.15625f, linear, out linearRoot));   // Ease(0.25)
            AssertNear(linearRoot, easedRoot, 1e-5f, "root");
            for (int bone = 0; bone < GhostPose.BoneCount; bone++) AssertSameRotation(linear[bone], eased[bone], 1e-6f, GhostPose.BoneNames[bone]);
            // the root moves on a straight line: at Ease(0.25) it has covered 15.625% of the way
            AssertNear(g.RootPositionMm + (em.RootPositionMm - g.RootPositionMm) * 0.15625f, easedRoot, 1e-4f, "root on the line");
            // and an un-eased quarter is somewhere else
            Assert.IsTrue(GhostPose.BlendLinear(g, em, 0.25f, linear, out linearRoot));
            Assert.That((linearRoot - easedRoot).magnitude, Is.GreaterThan(0.1f));
        }

        [Test]
        public void Blend_Halfway_IsHalfTheAngleOnEveryBone()
        {
            GhostPose g = Parse(ExportedG), em = Parse(ExportedEm);
            var half = new Quaternion[GhostPose.BoneCount];
            Vector3 root;
            Assert.IsTrue(GhostPose.Blend(g, em, 0.5f, half, out root));   // Ease(0.5) = 0.5
            for (int bone = 0; bone < GhostPose.BoneCount; bone++)
            {
                float whole = AngleBetween(g.GetRotation(bone), em.GetRotation(bone));
                Assert.That(AngleBetween(g.GetRotation(bone), half[bone]), Is.EqualTo(whole / 2f).Within(2e-3f), GhostPose.BoneNames[bone] + " from the first pose");
                Assert.That(AngleBetween(half[bone], em.GetRotation(bone)), Is.EqualTo(whole / 2f).Within(2e-3f), GhostPose.BoneNames[bone] + " to the second pose");
                Assert.That(Length(half[bone]), Is.EqualTo(1f).Within(1e-6f));
            }
        }

        [Test]
        public void Blend_KeepsTheHandInOnePiece()
        {
            GhostPose g = Parse(ExportedG), em = Parse(ExportedEm);
            var rotations = new Quaternion[GhostPose.BoneCount];
            var totals = new Quaternion[GhostPose.BoneCount];
            var positions = new Vector3[GhostPose.JointCount];
            for (int step = 0; step <= 4; step++)
            {
                Vector3 root;
                Assert.IsTrue(GhostPose.Blend(g, em, step / 4f, rotations, out root));
                Assert.IsTrue(g.ComputeJointPositionsMm(rotations, root, totals, positions));
                AssertBoneLengthsMatchBind(g, positions, 2e-3f);
            }
        }

        [Test]
        public void Blend_RefusesBadArguments_AndWritesNothing()
        {
            GhostPose g = Parse(ExportedG), em = Parse(ExportedEm), strum = Parse(ExportedStrummingRest);
            var marker = new Quaternion(9f, 9f, 9f, 9f);
            var rotations = new Quaternion[GhostPose.BoneCount];
            for (int i = 0; i < rotations.Length; i++) rotations[i] = marker;
            Vector3 root;
            Assert.IsFalse(GhostPose.Blend(null, em, 0.5f, rotations, out root));
            Assert.IsFalse(GhostPose.Blend(g, null, 0.5f, rotations, out root));
            Assert.IsFalse(GhostPose.Blend(g, em, 0.5f, null, out root));
            Assert.IsFalse(GhostPose.Blend(g, em, 0.5f, new Quaternion[GhostPose.BoneCount - 1], out root));
            Assert.IsFalse(GhostPose.Blend(g, strum, 0.5f, rotations, out root), "poses of two different hands");
            Assert.AreEqual(Vector3.zero, root);
            for (int i = 0; i < rotations.Length; i++) Assert.AreEqual(marker, rotations[i], "the buffer must be untouched after a refusal");
            Assert.IsTrue(GhostPose.Blend(g, em, 0.5f, new Quaternion[GhostPose.BoneCount + 4], out root), "a longer buffer is fine");
        }

        [Test]
        public void AccumulateRotations_MultipliesDownTheHierarchy_AlsoInPlace()
        {
            GhostPose g = Parse(ExportedG);
            var rotations = new Quaternion[GhostPose.BoneCount];
            var totals = new Quaternion[GhostPose.BoneCount];
            g.CopyRotations(rotations);
            Assert.IsTrue(GhostPose.AccumulateRotations(rotations, totals));
            AssertSameRotation(rotations[GhostPose.Wrist], totals[GhostPose.Wrist], 1e-6f, "wrist");
            AssertSameRotation(rotations[GhostPose.Wrist] * rotations[GhostPose.IndexProximal], totals[GhostPose.IndexProximal], 1e-6f, "index_proximal");
            AssertSameRotation(rotations[GhostPose.Wrist] * rotations[GhostPose.IndexProximal] * rotations[GhostPose.IndexIntermediate] * rotations[GhostPose.IndexDistal],
                totals[GhostPose.IndexDistal], 1e-6f, "index_distal");
            var inPlace = new Quaternion[GhostPose.BoneCount];
            g.CopyRotations(inPlace);
            Assert.IsTrue(GhostPose.AccumulateRotations(inPlace, inPlace));
            for (int bone = 0; bone < GhostPose.BoneCount; bone++) AssertSameRotation(totals[bone], inPlace[bone], 1e-6f, GhostPose.BoneNames[bone] + " in place");
            Assert.IsFalse(GhostPose.AccumulateRotations(null, totals));
            Assert.IsFalse(GhostPose.AccumulateRotations(rotations, new Quaternion[3]));
        }

        [Test]
        public void ComputeJointPositions_OnAStraightTestRig_GivesTheExpectedPoints()
        {
            // every rotation identity: the hand is its bind pose, moved so the wrist is at rootPositionMm
            GhostPose rest = Parse(SyntheticRestPose("fretting", "rest", 30f, new Vector3(100f, 20f, -5f)));
            var rotations = new Quaternion[GhostPose.BoneCount];
            var totals = new Quaternion[GhostPose.BoneCount];
            var positions = new Vector3[GhostPose.JointCount];
            rest.CopyRotations(rotations);
            Assert.IsTrue(rest.ComputeJointPositionsMm(rotations, rest.RootPositionMm, totals, positions));
            AssertNear(new Vector3(100f, 20f, -5f), positions[GhostPose.Wrist], 1e-5f, "wrist");
            AssertNear(new Vector3(100f + 10f, 20f, -5f + 30f), positions[GhostPose.IndexProximal], 1e-4f, "index knuckle");
            AssertNear(new Vector3(100f + 10f, 20f, -5f + 120f), positions[GhostPose.BoneCount + 1], 1e-4f, "index fingertip");

            // now turn the whole hand a quarter turn about +y at the wrist: +z offsets become +x offsets
            rotations[GhostPose.Wrist] = new Quaternion(0f, Mathf.Sin(Mathf.PI / 4f), 0f, Mathf.Cos(Mathf.PI / 4f));
            Assert.IsTrue(rest.ComputeJointPositionsMm(rotations, rest.RootPositionMm, totals, positions));
            AssertNear(new Vector3(100f + 30f, 20f, -5f - 10f), positions[GhostPose.IndexProximal], 1e-3f, "index knuckle after the turn");
            AssertNear(new Vector3(100f + 120f, 20f, -5f - 10f), positions[GhostPose.BoneCount + 1], 1e-3f, "index fingertip after the turn");

            // and curl only the index finger's last joint a quarter turn about +x: its 30 mm tip segment swings from +z to -y
            rest.CopyRotations(rotations);
            rotations[GhostPose.IndexDistal] = new Quaternion(Mathf.Sin(Mathf.PI / 4f), 0f, 0f, Mathf.Cos(Mathf.PI / 4f));
            Assert.IsTrue(rest.ComputeJointPositionsMm(rotations, rest.RootPositionMm, totals, positions));
            AssertNear(new Vector3(100f + 10f, 20f, -5f + 90f), positions[GhostPose.IndexDistal], 1e-3f, "index last joint stays");
            AssertNear(new Vector3(100f + 10f, 20f - 30f, -5f + 90f), positions[GhostPose.BoneCount + 1], 1e-3f, "index fingertip swings down");
            AssertNear(new Vector3(100f + 20f, 20f, -5f + 120f), positions[GhostPose.BoneCount + 2], 1e-3f, "middle fingertip does not move");

            Assert.IsFalse(rest.ComputeJointPositionsMm(rotations, rest.RootPositionMm, totals, new Vector3[GhostPose.JointCount - 1]));
            Assert.IsFalse(rest.ComputeJointPositionsMm(null, rest.RootPositionMm, totals, positions));
        }

        [Test]
        public void HotPaths_DoNotAllocate()
        {
            WriteExportedSet();
            var library = new GhostPoseLibrary(_folder);
            Assert.AreEqual(3, library.Reload());
            var rotations = new Quaternion[GhostPose.BoneCount];
            var totals = new Quaternion[GhostPose.BoneCount];
            var positions = new Vector3[GhostPose.JointCount];
            GhostPose g, em;
            Vector3 root;
            Func<int, bool> frame = count =>
            {
                bool ok = true;
                for (int i = 0; i < count; i++)
                {
                    ok &= library.TryGet("fretting", "G", out g) & library.TryGet("fretting", "Em", out em);
                    ok &= GhostPose.Blend(g, em, i / (float)count, rotations, out root);
                    ok &= g.ComputeJointPositionsMm(rotations, root, totals, positions);
                    ok &= library.TryBlend("fretting", "Em", "G", 0.3f, rotations, out root);
                    ok &= !library.TryGet("fretting", "nope", out em);
                }
                return ok;
            };
            Assert.IsTrue(frame(8), "warm-up");

            long before = GC.GetAllocatedBytesForCurrentThread();
            var probe = new byte[4096];
            long counted = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(probe);
            if (counted < 4096) Assert.Inconclusive("This runtime does not report allocations per thread (a 4096-byte array counted as " + counted + "), so nothing was measured.");

            before = GC.GetAllocatedBytesForCurrentThread();
            bool allOk = frame(500);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.IsTrue(allOk);
            Assert.AreEqual(0L, allocated, "bytes allocated by 500 frames of TryGet + Blend + ComputeJointPositionsMm + TryBlend");
        }

        // ---- the library -------------------------------------------------------------------------------------------------
        [Test]
        public void Library_LoadsAFolder_AndFindsPosesByHandAndName()
        {
            WriteExportedSet();
            File.WriteAllText(Path.Combine(_folder, "README.md"), "# not a pose");
            File.WriteAllText(Path.Combine(_folder, "fretting-G.pose.json.meta"), "guid: 0");
            File.WriteAllText(Path.Combine(_folder, ".fretting-C.pose.json.tmp"), "half a fi");
            File.WriteAllText(Path.Combine(_folder, "notes.json"), "{}");
            var library = new GhostPoseLibrary(_folder);
            Assert.AreEqual(_folder, library.Folder);
            Assert.AreEqual(0, library.Count, "nothing is loaded before Reload or the first TryGet");
            Assert.AreEqual(3, library.Reload());
            Assert.AreEqual(3, library.Count);
            Assert.AreEqual(3, library.Poses.Count);
            Assert.That(library.Errors, Is.Empty);
            Assert.That(library.Warnings, Is.Empty);

            GhostPose pose;
            Assert.IsTrue(library.TryGet("fretting", "G", out pose));
            Assert.AreEqual("G", pose.Name);
            Assert.AreEqual("fretting", pose.Hand);
            Assert.IsTrue(library.TryGet("fretting", "Em", out pose));
            Assert.AreEqual("Em", pose.Name);
            Assert.IsTrue(library.TryGet("strumming", "rest", out pose));
            Assert.AreEqual("strumming", pose.Hand);

            Assert.IsFalse(library.TryGet("fretting", "g", out pose), "names are case-sensitive");
            Assert.IsNull(pose);
            Assert.IsFalse(library.TryGet("fretting", "rest", out pose), "rest was exported for the strumming hand only");
            Assert.IsFalse(library.TryGet("strumming", "G", out pose));
            Assert.IsFalse(library.TryGet("fretting", "C", out pose), "the half-written temp file is not a pose");
            Assert.IsFalse(library.TryGet(null, "G", out pose));
            Assert.IsFalse(library.TryGet("fretting", null, out pose));
            Assert.IsFalse(library.TryGet("", "", out pose));
        }

        [Test]
        public void Library_TryGet_LoadsTheFolderOnFirstUse()
        {
            WriteExportedSet();
            var library = new GhostPoseLibrary(_folder);
            GhostPose pose;
            Assert.IsTrue(library.TryGet("fretting", "G", out pose));
            Assert.AreEqual(3, library.Count);
        }

        [Test]
        public void Library_Reload_FollowsTheFolder()
        {
            File.WriteAllText(Path.Combine(_folder, "fretting-G.pose.json"), ExportedG);
            var library = new GhostPoseLibrary(_folder);
            Assert.AreEqual(1, library.Reload());
            GhostPose pose;
            Assert.IsFalse(library.TryGet("fretting", "Em", out pose));

            File.WriteAllText(Path.Combine(_folder, "fretting-Em.pose.json"), ExportedEm);
            Assert.IsFalse(library.TryGet("fretting", "Em", out pose), "a new file is not seen until Reload");
            Assert.AreEqual(2, library.Reload());
            Assert.IsTrue(library.TryGet("fretting", "Em", out pose));

            File.Delete(Path.Combine(_folder, "fretting-G.pose.json"));
            Assert.AreEqual(1, library.Reload());
            Assert.IsFalse(library.TryGet("fretting", "G", out pose));
            Assert.IsTrue(library.TryGet("fretting", "Em", out pose));
        }

        [Test]
        public void Library_MissingFolder_LoadsNothingAndDoesNotThrow()
        {
            var library = new GhostPoseLibrary(Path.Combine(_folder, "nowhere"));
            Assert.AreEqual(0, library.Reload());
            Assert.That(library.Errors, Is.Empty);
            Assert.AreEqual(1, library.Warnings.Count);
            StringAssert.Contains("not found", library.Warnings[0]);
            GhostPose pose;
            Assert.IsFalse(library.TryGet("fretting", "G", out pose));
            Vector3 root;
            Assert.IsFalse(library.TryBlend("fretting", "G", "Em", 0.5f, new Quaternion[GhostPose.BoneCount], out root));
        }

        [Test]
        public void Library_TryBlend_BlendsByName()
        {
            WriteExportedSet();
            var library = new GhostPoseLibrary(_folder);
            library.Reload();
            GhostPose g, em;
            Assert.IsTrue(library.TryGet("fretting", "G", out g));
            Assert.IsTrue(library.TryGet("fretting", "Em", out em));
            var byName = new Quaternion[GhostPose.BoneCount];
            var direct = new Quaternion[GhostPose.BoneCount];
            Vector3 rootByName, rootDirect;
            Assert.IsTrue(library.TryBlend("fretting", "G", "Em", 0.4f, byName, out rootByName));
            Assert.IsTrue(GhostPose.Blend(g, em, 0.4f, direct, out rootDirect));
            AssertNear(rootDirect, rootByName, 1e-6f, "root");
            for (int bone = 0; bone < GhostPose.BoneCount; bone++) AssertSameRotation(direct[bone], byName[bone], 1e-6f, GhostPose.BoneNames[bone]);
            Assert.IsFalse(library.TryBlend("fretting", "G", "C", 0.4f, byName, out rootByName), "C is not loaded");
            Assert.IsFalse(library.TryBlend("fretting", "G", "Em", 0.4f, new Quaternion[4], out rootByName), "buffer too small");
        }

        [Test]
        public void ReadFolder_SkipsABrokenFileAndKeepsTheRest()
        {
            WriteExportedSet();
            File.WriteAllText(Path.Combine(_folder, "fretting-Em.pose.json"), Swap(ExportedEm, "\"schema\": 1,", "\"schema\": 3,"));
            var poses = new List<GhostPose>();
            var errors = new List<string>();
            var warnings = new List<string>();
            GhostPoseLibrary.ReadFolder(_folder, poses, errors, warnings);
            Assert.AreEqual(2, poses.Count);
            Assert.AreEqual(1, errors.Count);
            StringAssert.StartsWith("fretting-Em.pose.json: ", errors[0]);
            StringAssert.Contains("schema is 3", errors[0]);
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void ReadFolder_RefusesAFileWhoseNameDisagreesWithItsContent()
        {
            File.WriteAllText(Path.Combine(_folder, "fretting-C.pose.json"), ExportedG);
            var poses = new List<GhostPose>();
            var errors = new List<string>();
            var warnings = new List<string>();
            GhostPoseLibrary.ReadFolder(_folder, poses, errors, warnings);
            Assert.AreEqual(0, poses.Count);
            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains("must be named fretting-G.pose.json", errors[0]);
        }

        [Test]
        public void ReadFolder_RefusesTheSamePoseTwice()
        {
            Directory.CreateDirectory(Path.Combine(_folder, "old"));
            File.WriteAllText(Path.Combine(_folder, "fretting-G.pose.json"), ExportedG);
            File.WriteAllText(Path.Combine(_folder, "old", "fretting-G.pose.json"), ExportedG);
            var poses = new List<GhostPose>();
            var errors = new List<string>();
            var warnings = new List<string>();
            GhostPoseLibrary.ReadFolder(_folder, poses, errors, warnings);
            Assert.AreEqual(1, poses.Count);
            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains("already loaded", errors[0]);
        }

        [Test]
        public void ReadFolder_WarnsAboutAFileWithoutBind()
        {
            File.WriteAllText(Path.Combine(_folder, "fretting-G.pose.json"), WithoutBind(ExportedG));
            var poses = new List<GhostPose>();
            var errors = new List<string>();
            var warnings = new List<string>();
            GhostPoseLibrary.ReadFolder(_folder, poses, errors, warnings);
            Assert.AreEqual(1, poses.Count, "it still loads");
            Assert.That(errors, Is.Empty);
            Assert.AreEqual(1, warnings.Count);
            StringAssert.Contains("no bind block", warnings[0]);
        }

        [Test]
        public void ReadFolder_WarnsWhenOneHandsPosesComeFromTwoRigs()
        {
            File.WriteAllText(Path.Combine(_folder, "fretting-rest.pose.json"), SyntheticRestPose("fretting", "rest", 30f, Vector3.zero));
            File.WriteAllText(Path.Combine(_folder, "fretting-G.pose.json"), SyntheticRestPose("fretting", "G", 33f, Vector3.zero));       // longer bones
            File.WriteAllText(Path.Combine(_folder, "strumming-rest.pose.json"), SyntheticRestPose("strumming", "rest", 40f, Vector3.zero)); // another hand: no comparison
            var poses = new List<GhostPose>();
            var errors = new List<string>();
            var warnings = new List<string>();
            GhostPoseLibrary.ReadFolder(_folder, poses, errors, warnings);
            Assert.That(errors, Is.Empty);
            Assert.AreEqual(3, poses.Count, "all three still load");
            Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings.ToArray()));
            StringAssert.Contains("different rigs", warnings[0]);
            StringAssert.StartsWith("fretting-rest.pose.json", warnings[0]);

            // the same rig twice is quiet
            File.WriteAllText(Path.Combine(_folder, "fretting-G.pose.json"), SyntheticRestPose("fretting", "G", 30f, new Vector3(5f, 6f, 7f)));
            poses.Clear();
            errors.Clear();
            warnings.Clear();
            GhostPoseLibrary.ReadFolder(_folder, poses, errors, warnings);
            Assert.That(warnings, Is.Empty);
            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void ReadFolder_ToleratesNullLists()
        {
            Assert.DoesNotThrow(() => GhostPoseLibrary.ReadFolder(_folder, null, null, null));
        }

        // ---- helpers -----------------------------------------------------------------------------------------------------
        void WriteExportedSet()
        {
            File.WriteAllText(Path.Combine(_folder, "fretting-G.pose.json"), ExportedG);
            File.WriteAllText(Path.Combine(_folder, "fretting-Em.pose.json"), ExportedEm);
            File.WriteAllText(Path.Combine(_folder, "strumming-rest.pose.json"), ExportedStrummingRest);
        }

        static GhostPose Parse(string json)
        {
            GhostPose pose;
            string error;
            bool ok = GhostPose.TryParse(json, out pose, out error);
            Assert.IsTrue(ok, "expected the pose to parse, but: " + error);
            Assert.IsNull(error);
            Assert.IsNotNull(pose);
            return pose;
        }

        static void AssertRefused(string json, string reason)
        {
            GhostPose pose = null;
            string error = null;
            bool ok = true;
            Assert.DoesNotThrow(() => ok = GhostPose.TryParse(json, out pose, out error));
            Assert.IsFalse(ok, "expected a refusal mentioning '" + reason + "'");
            Assert.IsNull(pose);
            Assert.IsNotNull(error);
            StringAssert.Contains(reason, error);
        }

        /// <summary>Replace exactly one occurrence, and fail if the text to replace is not there: no test may run on an unchanged sample by accident.</summary>
        static string Swap(string json, string find, string replacement)
        {
            int at = json.IndexOf(find, StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThanOrEqualTo(0), "test bug: the sample does not contain: " + find);
            Assert.That(json.IndexOf(find, at + find.Length, StringComparison.Ordinal), Is.LessThan(0), "test bug: the sample contains this more than once: " + find);
            return json.Substring(0, at) + replacement + json.Substring(at + find.Length);
        }

        static string WithoutBind(string json)
        {
            int at = json.IndexOf(",\n  \"bind\": {", StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThan(0), "test bug: the sample has no bind block");
            return json.Substring(0, at) + "\n}\n";
        }

        /// <summary>
        /// A self-consistent pose made in the test: every rotation is identity and the hand is a rake of straight fingers.
        /// Model space: digit d sits at x = 10 * d mm; its joints are at z = 1, 2, 3 bone lengths and its tip at 4.
        /// </summary>
        static string SyntheticRestPose(string hand, string name, float boneLengthMm, Vector3 rootMm)
        {
            var bind = new Vector3[GhostPose.JointCount];
            for (int digit = 0; digit < GhostPose.DigitCount; digit++)
            {
                for (int k = 0; k < 3; k++) bind[GhostPose.ProximalBone(digit) + k] = new Vector3(10f * digit, 0f, boneLengthMm * (k + 1));
                bind[GhostPose.BoneCount + digit] = new Vector3(10f * digit, 0f, boneLengthMm * 4f);
            }
            var sb = new StringBuilder();
            sb.Append("{\n  \"schema\": 1,\n  \"hand\": \"").Append(hand).Append("\",\n  \"name\": \"").Append(name).Append("\",\n");
            sb.Append("  \"units\": \"mm\",\n  \"space\": \"neck-local-unity\",\n");
            sb.Append("  \"source\": { \"blend\": \"synthetic\", \"date\": \"2026-11-14T00:00:00Z\", \"exporter\": \"test\", \"blender\": \"none\" },\n");
            sb.Append("  \"rootPositionMm\": ").Append(Xyz(rootMm)).Append(",\n  \"bones\": [\n");
            for (int bone = 0; bone < GhostPose.BoneCount; bone++)
                sb.Append("    { \"name\": \"").Append(GhostPose.BoneNames[bone]).Append("\", \"rotation\": { \"x\": 0, \"y\": 0, \"z\": 0, \"w\": 1 } }").Append(bone < GhostPose.BoneCount - 1 ? ",\n" : "\n");
            sb.Append("  ],\n  \"fingertips\": [\n");
            for (int digit = 0; digit < GhostPose.DigitCount; digit++)
                sb.Append("    { \"bone\": \"").Append(GhostPose.BoneNames[GhostPose.DistalBone(digit)]).Append("\", \"positionMm\": ").Append(Xyz(rootMm + bind[GhostPose.BoneCount + digit])).Append(" }").Append(digit < GhostPose.DigitCount - 1 ? ",\n" : "\n");
            sb.Append("  ],\n  \"bind\": {\n    \"joints\": [\n");
            for (int bone = 0; bone < GhostPose.BoneCount; bone++)
                sb.Append("      { \"bone\": \"").Append(GhostPose.BoneNames[bone]).Append("\", \"positionMm\": ").Append(Xyz(bind[bone])).Append(" }").Append(bone < GhostPose.BoneCount - 1 ? ",\n" : "\n");
            sb.Append("    ],\n    \"fingertips\": [\n");
            for (int digit = 0; digit < GhostPose.DigitCount; digit++)
                sb.Append("      { \"bone\": \"").Append(GhostPose.BoneNames[GhostPose.DistalBone(digit)]).Append("\", \"positionMm\": ").Append(Xyz(bind[GhostPose.BoneCount + digit])).Append(" }").Append(digit < GhostPose.DigitCount - 1 ? ",\n" : "\n");
            sb.Append("    ]\n  }\n}\n");
            return sb.ToString();
        }

        static string Xyz(Vector3 v)
        {
            return "{ \"x\": " + v.x.ToString("0.###", CultureInfo.InvariantCulture) + ", \"y\": " + v.y.ToString("0.###", CultureInfo.InvariantCulture)
                + ", \"z\": " + v.z.ToString("0.###", CultureInfo.InvariantCulture) + " }";
        }

        static void AssertBoneLengthsMatchBind(GhostPose pose, Vector3[] positions, float toleranceMm)
        {
            for (int bone = 1; bone < GhostPose.BoneCount; bone++)
            {
                int parent = GhostPose.BoneParents[bone];
                float bind = (pose.GetBindPositionMm(bone) - pose.GetBindPositionMm(parent)).magnitude;
                Assert.That((positions[bone] - positions[parent]).magnitude, Is.EqualTo(bind).Within(toleranceMm), "distance " + GhostPose.BoneNames[parent] + " -> " + GhostPose.BoneNames[bone]);
            }
            for (int digit = 0; digit < GhostPose.DigitCount; digit++)
            {
                int bone = GhostPose.DistalBone(digit);
                float bind = (pose.GetBindPositionMm(GhostPose.BoneCount + digit) - pose.GetBindPositionMm(bone)).magnitude;
                Assert.That((positions[GhostPose.BoneCount + digit] - positions[bone]).magnitude, Is.EqualTo(bind).Within(toleranceMm), "length of the last segment of digit " + digit);
            }
        }

        static void AssertNear(Vector3 expected, Vector3 actual, float tolerance, string what)
        {
            float off = (expected - actual).magnitude;
            Assert.That(off, Is.LessThanOrEqualTo(tolerance), what + ": expected " + expected.ToString("0.0000") + " but was " + actual.ToString("0.0000") + " (" + off + " apart)");
        }

        /// <summary>q and -q are the same rotation, so compare by |dot|.</summary>
        static void AssertSameRotation(Quaternion expected, Quaternion actual, float tolerance, string what)
        {
            float dot = Mathf.Abs(expected.x * actual.x + expected.y * actual.y + expected.z * actual.z + expected.w * actual.w);
            Assert.That(dot, Is.GreaterThanOrEqualTo(1f - tolerance), what + ": expected " + expected.ToString("0.000000") + " but was " + actual.ToString("0.000000"));
        }

        static float Length(Quaternion q)
        {
            return Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        }

        /// <summary>Angle in radians between two rotations.</summary>
        static float AngleBetween(Quaternion a, Quaternion b)
        {
            float dot = Mathf.Min(1f, Mathf.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w));
            return 2f * Mathf.Acos(dot);
        }
    }
}
