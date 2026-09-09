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
                selectedReasoningEffort = "medium",
                permissionMode = AgentPermissionMode.CodexDecides.ToString()
            };

            AgentForUnityStateStore.Save(_projectRoot, state);
            var loaded = AgentForUnityStateStore.Load(_projectRoot, out var error);
            var persistedText = File.ReadAllText(AgentForUnityStateStore.GetStatePath(_projectRoot));

            Assert.That(error, Is.Null);
            Assert.That(loaded.threadId, Is.EqualTo("thread-1"));
            Assert.That(loaded.turnId, Is.EqualTo("turn-2"));
            Assert.That(loaded.selectedModelId, Is.EqualTo("model-3"));
            Assert.That(loaded.permissionMode, Is.EqualTo(AgentPermissionMode.CodexDecides.ToString()));
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

    internal sealed class AgentPermissionPolicyTests
    {
        [TestCase(AgentPermissionMode.AskApproval, "untrusted", "workspace-write", "workspaceWrite")]
        [TestCase(AgentPermissionMode.CodexDecides, "on-request", "workspace-write", "workspaceWrite")]
        [TestCase(AgentPermissionMode.FullAccess, "never", "danger-full-access", "dangerFullAccess")]
        public void PermissionMode_MapsToCodexProtocol(
            AgentPermissionMode mode,
            string approvalPolicy,
            string threadSandbox,
            string turnSandbox)
        {
            var sandboxPolicy = AgentPermissionPolicy.CreateSandboxPolicy(mode, "/project");

            Assert.That(AgentPermissionPolicy.GetApprovalPolicy(mode), Is.EqualTo(approvalPolicy));
            Assert.That(AgentPermissionPolicy.GetThreadSandboxMode(mode), Is.EqualTo(threadSandbox));
            Assert.That(sandboxPolicy.Value<string>("type"), Is.EqualTo(turnSandbox));

            if (mode == AgentPermissionMode.FullAccess)
            {
                Assert.That(sandboxPolicy["networkAccess"], Is.Null);
                Assert.That(sandboxPolicy["writableRoots"], Is.Null);
            }
            else
            {
                Assert.That(sandboxPolicy.Value<bool>("networkAccess"), Is.False);
                Assert.That((string)sandboxPolicy["writableRoots"]?[0], Is.EqualTo("/project"));
            }
        }
    }
}
