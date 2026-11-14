// Camera mode request tests (SL12): pure logic, runs offline (kit/mono-tests.sh) and in EditMode.
using NUnit.Framework;
using UntitledGuitarApp.Runtime.CameraAnchor;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Tests.EditMode.CameraAnchor
{
    public class CameraModeTests
    {
        [Test]
        public void Defaults_RequestTheWorkingHighResMode()
        {
            // the device's 640x241 default delivers streak garbage on this setup; 1760x1328 is the working mode (STATUS 2026-11-14)
            var m = CameraAnchorSource.ResolveRequestedMode(new CameraAnchorFlags());
            Assert.AreEqual(1760, m.width);
            Assert.AreEqual(1328, m.height);
            Assert.AreEqual(30, m.fps);
        }

        [Test]
        public void ExplicitFlags_PassThrough()
        {
            var f = new CameraAnchorFlags { requestWidth = 640, requestHeight = 480, requestFps = 60 };
            var m = CameraAnchorSource.ResolveRequestedMode(f);
            Assert.AreEqual(640, m.width);
            Assert.AreEqual(480, m.height);
            Assert.AreEqual(60, m.fps);
        }

        [Test]
        public void NonPositiveFields_FallBackToDefault()
        {
            var f = new CameraAnchorFlags { requestWidth = 0, requestHeight = -5, requestFps = 0 };
            var m = CameraAnchorSource.ResolveRequestedMode(f);
            Assert.AreEqual(1760, m.width);
            Assert.AreEqual(1328, m.height);
            Assert.AreEqual(30, m.fps);
        }
    }
}
