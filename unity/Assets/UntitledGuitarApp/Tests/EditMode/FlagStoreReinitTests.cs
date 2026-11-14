// FlagStoreReinitTests.cs: self-healing flag init after a domain reload (statics reset to built-in defaults
// while the restored scene objects keep running - see STATUS 2026-11-14 camera hunt). No Play Mode, no network.
using System;
using System.IO;
using NUnit.Framework;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Tests.EditMode
{
    public class FlagStoreReinitTests
    {
        static string TempFile(string name, string text)
        {
            string p = Path.Combine(Path.GetTempPath(), "uga-flags-" + Guid.NewGuid().ToString("N") + "-" + name);
            File.WriteAllText(p, text);
            return p;
        }

        [TearDown]
        public void TearDown()
        {
            FlagStore.ResetForTests();
        }

        [Test]
        public void ResetForTests_ReportsNeedsInitialization_AndDefaults()
        {
            FlagStore.ResetForTests();
            Assert.IsTrue(FlagStore.NeedsInitialization);
            Assert.IsFalse(FlagStore.Current.cameraAnchor.enabled, "built-in defaults must keep the camera anchor off");
        }

        [Test]
        public void Initialize_FromTempPaths_ClearsNeedsInitialization()
        {
            string d = TempFile("defaults.json", "{}");
            string o = TempFile("override.json", "{ 'cameraAnchor': { 'enabled': true, 'markerMm': 60 } }".Replace('\'', '"'));
            FlagStore.Initialize(d, o);
            Assert.IsFalse(FlagStore.NeedsInitialization);
            Assert.IsTrue(FlagStore.IsInitialized);
            Assert.IsNotNull(FlagStore.Current);
            // (merge CONTENT assertions run in the real editor only: JsonUtility is an engine call and no-ops offline)
        }

        [Test]
        public void EnsureInitialized_WhenLoaded_IsANoOp()
        {
            string d = TempFile("defaults.json", "{}");
            string o = TempFile("override.json", "{}");
            FlagStore.Initialize(d, o);
            var before = FlagStore.Current;
            FlagStore.EnsureInitialized();
            Assert.AreSame(before, FlagStore.Current, "a loaded store must not be reloaded by EnsureInitialized");
        }
    }
}
