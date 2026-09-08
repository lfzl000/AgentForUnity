using AgentForUnity.Editor.Codex;
using NUnit.Framework;

namespace AgentForUnity.Editor.Tests
{
    internal sealed class CodexCliLocatorTests
    {
        [TestCase("codex-cli 0.144.0")]
        [TestCase("codex-cli 0.144.6")]
        [TestCase("codex 0.144.99")]
        public void IsVersionSupported_AcceptsValidatedMinorRelease(string versionText)
        {
            Assert.That(CodexCliLocator.IsVersionSupported(versionText, out var error), Is.True);
            Assert.That(error, Is.Null);
        }

        [TestCase("codex-cli 0.143.9")]
        [TestCase("codex-cli 0.145.0")]
        [TestCase("not-a-version")]
        public void IsVersionSupported_RejectsUnknownOrIncompatibleRelease(string versionText)
        {
            Assert.That(CodexCliLocator.IsVersionSupported(versionText, out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }
    }
}
