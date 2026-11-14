using NUnit.Framework;

namespace UntitledGuitarApp.Tests.EditMode
{
    /// <summary>
    /// Proves that Unity compiles this assembly and that scripts/unity-tests.sh can run an EditMode test and read its result.
    /// Deliberately trivial; real tests live in their own files next to this one.
    /// </summary>
    public class BootstrapSmokeTests
    {
        [Test]
        public void Harness_RunsAnEditModeTest()
        {
            Assert.That(1 + 1, Is.EqualTo(2));
        }
    }
}
