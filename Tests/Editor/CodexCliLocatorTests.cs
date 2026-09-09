using AgentForUnity.Editor.Codex;
using NUnit.Framework;

namespace AgentForUnity.Editor.Tests
{
    internal sealed class CodexCliLocatorTests
    {
        [TestCase("codex-cli 0.144.0")]
        [TestCase("codex-cli 0.144.6")]
        [TestCase("codex 0.144.99")]
        [TestCase("codex-cli 0.153.4")]
        [TestCase("codex-cli 0.154.0")]
        [TestCase("codex-cli 1.0.0")]
        public void IsVersionSupported_AcceptsCurrentOrNewerRelease(string versionText)
        {
            Assert.That(CodexCliLocator.IsVersionSupported(versionText, out var error), Is.True);
            Assert.That(error, Is.Null);
        }

        [TestCase("codex-cli 0.143.9")]
        [TestCase("not-a-version")]
        public void IsVersionSupported_RejectsUnknownOrTooOldRelease(string versionText)
        {
            Assert.That(CodexCliLocator.IsVersionSupported(versionText, out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }
    }
}
