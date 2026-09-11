using System;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AgentForUnity.Editor.Application
{
    [Serializable]
    internal sealed class AgentForUnityPersistedState
    {
        internal const int CurrentSchemaVersion = 5;

        public int schemaVersion = CurrentSchemaVersion;
        public string projectPath;
        public string threadId;
        public string turnId;
        public string selectedModelId;
        public string selectedReasoningEffort;
        public string permissionMode;
        public string requestedToolingBackend;
        public string activeToolingBackend;
        public bool projectContextSent;
        public List<AgentContextDraftState> contextDrafts = new List<AgentContextDraftState>();
        public string lastDiff;
        public string lastDiffTurnId;
        public bool compilationPending;
        public string compilationTurnId;
        public string compilationState;
        public string compilationSummary;
        public string compilationDetails;
        public string compilationCompletedAt;
        public List<AgentThreadContextUsageState> threadContextUsages = new List<AgentThreadContextUsageState>();
    }

    [Serializable]
    internal sealed class AgentContextDraftState
    {
        public string id;
        public string kind;
        public string label;
        public string source;
        public string content;
        public string capturedAt;
    }

    [Serializable]
    internal sealed class AgentThreadContextUsageState
    {
        public string threadId;
        public long inputTokens;
        public long modelContextWindow;
    }

    internal static class AgentForUnityStateStore
    {
        private const string StateDirectoryName = "AgentForUnity";
        private const string StateFileName = "state.json";

        internal static AgentForUnityPersistedState Load(string projectRoot, out string error)
        {
            error = null;
            var statePath = GetStatePath(projectRoot);
            if (!File.Exists(statePath))
            {
                return CreateEmpty(projectRoot);
            }

            try
            {
                var state = JsonConvert.DeserializeObject<AgentForUnityPersistedState>(File.ReadAllText(statePath));
                if (state == null || state.schemaVersion < 1 || state.schemaVersion > AgentForUnityPersistedState.CurrentSchemaVersion)
                {
                    error = "Ignored persisted state with an unsupported schema version.";
                    return CreateEmpty(projectRoot);
                }

                if (!PathsEqual(state.projectPath, projectRoot))
                {
                    error = "Ignored persisted state belonging to a different Unity project.";
                    return CreateEmpty(projectRoot);
                }

                state.schemaVersion = AgentForUnityPersistedState.CurrentSchemaVersion;
                state.contextDrafts = state.contextDrafts ?? new List<AgentContextDraftState>();
                state.threadContextUsages = state.threadContextUsages ?? new List<AgentThreadContextUsageState>();

                return state;
            }
            catch (Exception exception)
            {
                error = $"Could not read persisted state: {exception.Message}";
                return CreateEmpty(projectRoot);
            }
        }

        internal static void Save(string projectRoot, AgentForUnityPersistedState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            state.schemaVersion = AgentForUnityPersistedState.CurrentSchemaVersion;
            state.projectPath = NormalizePath(projectRoot);

            var statePath = GetStatePath(projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath) ?? projectRoot);
            var temporaryPath = statePath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(state, Formatting.Indented));
                if (File.Exists(statePath))
                {
                    File.Replace(temporaryPath, statePath, null);
                }
                else
                {
                    File.Move(temporaryPath, statePath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        internal static string GetStatePath(string projectRoot)
        {
            return Path.Combine(NormalizePath(projectRoot), "Library", StateDirectoryName, StateFileName);
        }

        private static AgentForUnityPersistedState CreateEmpty(string projectRoot)
        {
            return new AgentForUnityPersistedState { projectPath = NormalizePath(projectRoot) };
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            var comparison = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(NormalizePath(left), NormalizePath(right), comparison);
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
