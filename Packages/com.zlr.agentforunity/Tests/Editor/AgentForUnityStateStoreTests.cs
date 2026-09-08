using System;
using System.IO;
using AgentForUnity.Editor.Application;
using NUnit.Framework;

namespace AgentForUnity.Editor.Tests
{
    internal sealed class AgentForUnityStateStoreTests
    {
        private string _projectRoot;

        [SetUp]
        public void SetUp()
        {
            _projectRoot = Path.Combine(Path.GetTempPath(), "AgentForUnityTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_projectRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_projectRoot))
            {
                Directory.Delete(_projectRoot, true);
            }
        }

        [Test]
        public void SaveAndLoad_RoundTripsOnlySessionMetadata()
        {
            var state = new AgentForUnityPersistedState
            {
                threadId = "thread-1",
                turnId = "turn-2",
                selectedModelId = "model-3",
                selectedReasoningEffort = "medium"
            };

            AgentForUnityStateStore.Save(_projectRoot, state);
            var loaded = AgentForUnityStateStore.Load(_projectRoot, out var error);
            var persistedText = File.ReadAllText(AgentForUnityStateStore.GetStatePath(_projectRoot));

            Assert.That(error, Is.Null);
            Assert.That(loaded.threadId, Is.EqualTo("thread-1"));
            Assert.That(loaded.turnId, Is.EqualTo("turn-2"));
            Assert.That(loaded.selectedModelId, Is.EqualTo("model-3"));
            Assert.That(persistedText, Does.Not.Contain("prompt"));
            Assert.That(persistedText, Does.Not.Contain("apiKey"));
        }

        [Test]
        public void Load_CorruptStateReturnsEmptyStateAndDiagnostic()
        {
            var statePath = AgentForUnityStateStore.GetStatePath(_projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath));
            File.WriteAllText(statePath, "not-json");

            var loaded = AgentForUnityStateStore.Load(_projectRoot, out var error);

            Assert.That(error, Is.Not.Empty);
            Assert.That(loaded.threadId, Is.Null);
            Assert.That(loaded.schemaVersion, Is.EqualTo(AgentForUnityPersistedState.CurrentSchemaVersion));
        }
    }
}
