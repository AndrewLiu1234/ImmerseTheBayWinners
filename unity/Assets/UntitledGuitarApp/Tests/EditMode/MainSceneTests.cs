using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UntitledGuitarApp.Tests.EditMode
{
    /// <summary>
    /// Main.unity is the app's one scene: a single root "Bootstrap" with the Bootstrap component, first and enabled in
    /// Build Settings. Editor/SceneBuilder.cs makes it; these tests catch a scene someone broke or forgot to list.
    /// (This assembly does not reference the Editor assembly, so the expected path and name are repeated here.)
    /// </summary>
    public sealed class MainSceneTests
    {
        const string MainScenePath = "Assets/UntitledGuitarApp/Scenes/Main.unity";

        [Test]
        public void BuildSettings_FirstEnabledScene_IsMain()
        {
            EditorBuildSettingsScene first = null;
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            {
                if (!s.enabled) continue;
                first = s;
                break;
            }
            Assert.IsNotNull(first, "no enabled scene in Build Settings (run UGA > Rebuild Main Scene)");
            Assert.AreEqual(MainScenePath, first.path);
        }

        [Test]
        public void MainScene_HasExactlyOneBootstrapRoot_AndNoMissingScripts()
        {
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(MainScenePath), MainScenePath + " does not exist");
            Scene scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Additive);
            try
            {
                GameObject[] roots = scene.GetRootGameObjects();
                Assert.AreEqual(1, roots.Length, "Main.unity must hold one root object; compose everything else in Bootstrap code");
                Assert.AreEqual("Bootstrap", roots[0].name);
                Component[] components = roots[0].GetComponents<Component>();
                foreach (Component c in components)
                    Assert.IsNotNull(c, "Bootstrap object has a missing script");
                Assert.AreEqual(2, components.Length, "expected exactly Transform + Bootstrap");
                Assert.IsNotNull(roots[0].GetComponent<UntitledGuitarApp.Runtime.Bootstrap>());
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
