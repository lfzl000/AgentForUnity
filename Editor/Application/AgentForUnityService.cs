using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentForUnity.Editor.Codex;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentForUnity.Editor.Application
{
    internal enum AgentConnectionState
    {
        NotChecked,
        Checking,
        CliMissing,
        Connecting,
        SignedOut,
        Ready,
        Recovering,
        Faulted,
        Disconnected,
        Stopped
    }

    internal enum AgentTurnState
    {
        Idle,
        Starting,
        Running,
        WaitingForApproval,
        WaitingForUserInput,
        Interrupting,
        Completed,
        Failed,
        Interrupted
    }

    internal enum AgentChatRole
    {
        User,
        Agent,
        System
    }

    internal enum AgentPermissionMode
    {
        AskApproval,
        CodexDecides,
        FullAccess
    }

    internal static class AgentPermissionPolicy
    {
        internal static string GetApprovalPolicy(AgentPermissionMode mode)
        {
            return mode == AgentPermissionMode.FullAccess ? "never" : "on-request";
        }

        internal static string GetApprovalsReviewer(AgentPermissionMode mode)
        {
            return mode == AgentPermissionMode.CodexDecides ? "auto_review" : "user";
        }

        internal static string GetThreadSandboxMode(AgentPermissionMode mode)
        {
            return mode == AgentPermissionMode.FullAccess
                ? "danger-full-access"
                : "workspace-write";
        }

        internal static JObject CreateSandboxPolicy(AgentPermissionMode mode, string projectRoot)
        {
            if (mode == AgentPermissionMode.FullAccess)
            {
                return new JObject { ["type"] = "dangerFullAccess" };
            }

            return new JObject
            {
                ["type"] = "workspaceWrite",
                ["writableRoots"] = new JArray(projectRoot),
                ["networkAccess"] = false
            };
        }
    }

    internal sealed class AgentChatMessage
    {
        internal AgentChatMessage(
            AgentChatRole role,
            string text,
            string itemId = null,
            IReadOnlyList<AgentChatAttachment> attachments = null,
            string turnId = null)
        {
            Role = role;
            Text = text ?? string.Empty;
            ItemId = itemId;
            Attachments = attachments ?? Array.Empty<AgentChatAttachment>();
            TurnId = turnId;
        }

        internal AgentChatRole Role { get; }
        internal string Text { get; set; }
        internal string ItemId { get; }
        internal IReadOnlyList<AgentChatAttachment> Attachments { get; }
        internal string TurnId { get; set; }
        internal TimeSpan? TurnDuration { get; set; }
        internal bool IsTurnCompleted { get; set; }
        internal bool IsPendingTurnStart { get; set; }
        internal IReadOnlyList<AgentActivityItem> Activities { get; private set; } = Array.Empty<AgentActivityItem>();
        internal bool IsStreaming { get; set; }

        internal void SetActivities(IReadOnlyList<AgentActivityItem> activities)
        {
            Activities = activities == null
                ? Array.Empty<AgentActivityItem>()
                : new List<AgentActivityItem>(activities);
        }
    }

    internal sealed class AgentModelInfo
    {
        internal AgentModelInfo(
            string id,
            string displayName,
            bool isDefault,
            string defaultReasoningEffort,
            IReadOnlyList<string> supportedReasoningEfforts)
        {
            Id = id;
            DisplayName = string.IsNullOrEmpty(displayName) ? id : displayName;
            IsDefault = isDefault;
            DefaultReasoningEffort = defaultReasoningEffort;
            SupportedReasoningEfforts = supportedReasoningEfforts ?? Array.Empty<string>();
        }

        internal string Id { get; }
        internal string DisplayName { get; }
        internal bool IsDefault { get; }
        internal string DefaultReasoningEffort { get; }
        internal IReadOnlyList<string> SupportedReasoningEfforts { get; }
    }

    internal sealed class AgentThreadInfo
    {
        internal AgentThreadInfo(string id, string name, string preview, long updatedAt, string status)
        {
            Id = id;
            Name = name;
            Preview = preview;
            UpdatedAt = updatedAt;
            Status = status;
        }

        internal string Id { get; }
        internal string Name { get; }
        internal string Preview { get; }
        internal long UpdatedAt { get; }
        internal string Status { get; }
    }

    internal sealed class AgentContextUsageSnapshot
    {
        internal AgentContextUsageSnapshot(long inputTokens, long modelContextWindow)
        {
            InputTokens = inputTokens;
            ModelContextWindow = modelContextWindow;
        }

        internal long InputTokens { get; }
        internal long ModelContextWindow { get; }
    }

    internal sealed partial class AgentForUnityService : IDisposable
    {
        private const int MaxDiagnostics = 200;
        private const int MaxRestoredMessages = 100;
        private const int MaxPersistedContextUsageEntries = 100;
        private const int ThreadPageSize = 20;
        private const int MaxAutomaticThreadTitleLength = 48;
        private const int MaxReconnectAttempts = 3;
        private const double ReconnectStabilitySeconds = 30d;
        private const double ProjectChangesRefreshSeconds = 2d;
        private const string UnityCompilationDeveloperInstructions =
            "You are operating through Agent for Unity. Do not trigger Unity script compilation or perform " +
            "compilation validation while a turn is active. Complete the requested work and end the turn first. " +
            "After a completed turn with file changes, the plugin requests Unity script compilation. Do not claim " +
            "that compilation validation ran during this turn; report it as pending until the plugin provides a result.";

        private readonly string _projectRoot;
        private readonly List<AgentModelInfo> _models = new List<AgentModelInfo>();
        private readonly List<AgentThreadInfo> _threads = new List<AgentThreadInfo>();
        private readonly List<string> _reasoningEfforts = new List<string>();
        private readonly List<AgentChatMessage> _messages = new List<AgentChatMessage>();
        private readonly List<string> _diagnostics = new List<string>();
        private readonly List<AgentProjectChange> _projectChanges = new List<AgentProjectChange>();
        private readonly Dictionary<string, AgentChatMessage> _streamingMessages =
            new Dictionary<string, AgentChatMessage>(StringComparer.Ordinal);
        private readonly Dictionary<string, AgentContextUsageSnapshot> _contextUsageByThreadId =
            new Dictionary<string, AgentContextUsageSnapshot>(StringComparer.Ordinal);
        private readonly HashSet<string> _reportedUnknownNotifications =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly AgentForUnityPersistedState _persistedState;

        private CodexAppServerClient _client;
        private bool _started;
        private bool _connecting;
        private bool _disposed;
        private bool _operationInProgress;
        private bool _threadReady;
        private bool _threadReadOnly;
        private bool _threadsLoading;
        private bool _turnStartPending;
        private bool _changePending;
        private bool _interruptWhenStarted;
        private bool _domainReloadLockedForTurn;
        private int _connectionGeneration;
        private int _diagnosticsVersion;
        private int _operationGeneration;
        private int _reconnectAttempts;
        private double _nextReconnectTime = -1d;
        private double _connectionStableSince = -1d;
        private double _nextProjectChangesRefreshTime;
        private string _projectBranch = string.Empty;
        private CodexAppServerClient _pendingDisconnectedClient;
        private string _pendingDisconnect;
        private AgentPermissionMode _permissionMode;
        private string _selectedModelId;
        private string _selectedReasoningEffort;
        private string _threadId;
        private string _threadAwaitingAutomaticTitle;
        private string _nextThreadsCursor;
        private string _turnId;
        private AgentChatMessage _pendingUserMessage;
        private DateTime _turnStartedAtUtc;
        private TimeSpan? _lastTurnDuration;

        internal AgentForUnityService()
        {
            _projectRoot = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
            _persistedState = AgentForUnityStateStore.Load(_projectRoot, out var loadError);
            _threadId = _persistedState.threadId;
            _turnId = _persistedState.turnId;
            _selectedModelId = _persistedState.selectedModelId;
            _selectedReasoningEffort = _persistedState.selectedReasoningEffort;
            foreach (var usage in _persistedState.threadContextUsages)
            {
                if (!string.IsNullOrEmpty(usage?.threadId) && usage.modelContextWindow > 0)
                {
                    _contextUsageByThreadId[usage.threadId] = new AgentContextUsageSnapshot(
                        usage.inputTokens,
                        usage.modelContextWindow);
                }
            }
            if (!Enum.TryParse(_persistedState.permissionMode, true, out _permissionMode))
            {
                _permissionMode = AgentPermissionMode.CodexDecides;
            }
            InitializeM1State();
            InitializeUnityToolingState();
            if (!string.IsNullOrEmpty(loadError))
            {
                AddDiagnostic(loadError);
            }
        }

        internal event Action Changed;

        internal static AgentForUnityService Instance => AgentForUnityBootstrap.Service;

        internal AgentConnectionState ConnectionState { get; private set; } = AgentConnectionState.NotChecked;
        internal AgentTurnState TurnState { get; private set; } = AgentTurnState.Idle;
        internal string TurnStateLabel => TurnState.ToString();
        internal string StatusText { get; private set; } = "Not connected";
        internal string CliPath { get; private set; }
        internal string CliVersion { get; private set; }
        internal string AccountLabel { get; private set; } = "Unknown";
        internal string AccountUsageLabel { get; private set; } = "Unavailable";
        internal string ContextUsageLabel { get; private set; } = string.Empty;
        internal string ContextUsageTooltip { get; private set; } = string.Empty;
        internal string ProjectRoot => _projectRoot;
        internal string ThreadId => _threadId;
        internal string CurrentThreadTitle => GetThreadTitle(
            _threads.FirstOrDefault(thread => string.Equals(thread.Id, _threadId, StringComparison.Ordinal)));
        internal string TurnId => _turnId;
        internal TimeSpan? LastTurnDuration => _lastTurnDuration;
        internal TimeSpan? ActiveTurnDuration => IsTurnStarting && _turnStartedAtUtc != default
            ? DateTime.UtcNow - _turnStartedAtUtc
            : (TimeSpan?)null;
        internal bool IsTurnStarting => _turnStartPending || IsTurnActive;
        internal IReadOnlyList<AgentModelInfo> Models => _models;
        internal IReadOnlyList<AgentThreadInfo> Threads => _threads;
        internal IReadOnlyList<string> ReasoningEfforts => _reasoningEfforts;
        internal IReadOnlyList<AgentChatMessage> Messages => _messages;
        internal IReadOnlyList<string> Diagnostics => _diagnostics;
        internal IReadOnlyList<AgentProjectChange> ProjectChanges => _projectChanges;
        internal string ProjectBranch => _projectBranch;
        internal AgentGitAvailability ProjectGitAvailability { get; private set; } = AgentGitAvailability.Checking;
        internal string ProjectGitError { get; private set; } = string.Empty;
        internal int DiagnosticsVersion => _diagnosticsVersion;
        internal bool CanSend => ConnectionState == AgentConnectionState.Ready &&
                                 !GitBusy &&
                                 !PackageUpdating &&
                                 !IsTurnActive &&
                                 !_operationInProgress &&
                                 !ToolingBlocksNewTurns &&
                                 !_threadReadOnly;
        internal bool CanStartThread => ConnectionState == AgentConnectionState.Ready &&
                                        !GitBusy &&
                                        !PackageUpdating &&
                                        !IsTurnActive &&
                                        !_operationInProgress &&
                                        !ToolingBlocksNewTurns;
        internal bool CanInterrupt => ConnectionState == AgentConnectionState.Ready &&
                                      (TurnState == AgentTurnState.Running ||
                                       TurnState == AgentTurnState.Starting ||
                                       TurnState == AgentTurnState.WaitingForApproval ||
                                       TurnState == AgentTurnState.WaitingForUserInput) &&
                                      TurnState != AgentTurnState.Interrupting;
        internal bool CanDisconnect => !_disposed && _started;
        internal bool IsTurnActive => TurnState == AgentTurnState.Starting ||
                                      TurnState == AgentTurnState.Running ||
                                      TurnState == AgentTurnState.WaitingForApproval ||
                                      TurnState == AgentTurnState.WaitingForUserInput ||
                                      TurnState == AgentTurnState.Interrupting;
        internal bool CanChangePermissionMode => !_disposed && !IsTurnActive && !_operationInProgress;
        internal bool CanSwitchThread => ConnectionState == AgentConnectionState.Ready &&
                                         !GitBusy &&
                                         !PackageUpdating &&
                                         !IsTurnActive &&
                                         !_operationInProgress &&
                                         !ToolingBlocksNewTurns;
        internal bool CanRefreshThreads => ConnectionState == AgentConnectionState.Ready && !_threadsLoading;
        internal bool ThreadsLoading => _threadsLoading;
        internal bool HasMoreThreads => !string.IsNullOrEmpty(_nextThreadsCursor);
        internal bool CanLoadMoreThreads => CanRefreshThreads && HasMoreThreads;

        internal static bool EnterPlayModeReloadsDomain()
        {
            return !EditorSettings.enterPlayModeOptionsEnabled ||
                   (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
        }

        internal AgentPermissionMode PermissionMode
        {
            get => _permissionMode;
            set
            {
                if (_permissionMode == value || !CanChangePermissionMode)
                {
                    return;
                }

                _permissionMode = value;
                SaveState();
                MarkChanged();
            }
        }

        internal string SelectedModelId
        {
            get => _selectedModelId;
            set
            {
                if (string.Equals(_selectedModelId, value, StringComparison.Ordinal))
                {
                    return;
                }

                _selectedModelId = value;
                RefreshReasoningEfforts();
                SaveState();
                MarkChanged();
            }
        }

        internal string SelectedReasoningEffort
        {
            get => _selectedReasoningEffort;
            set
            {
                if (string.Equals(_selectedReasoningEffort, value, StringComparison.Ordinal))
                {
                    return;
                }

                _selectedReasoningEffort = value;
                SaveState();
                MarkChanged();
            }
        }

        internal void EnsureStarted()
        {
            if (_disposed || _started || ConnectionState == AgentConnectionState.Disconnected)
            {
                return;
            }

            _started = true;
            ConnectInternal(true);
        }

        internal void HandlePlayModeEntryBlocked()
        {
            if (_disposed || (!IsTurnStarting && !GitBusy))
            {
                return;
            }

            if (GitBusy)
            {
                SetGitStatus("Wait for the Git action before entering Play Mode", "请等待 Git 操作完成后再进入 Play Mode", GitDetails);
                return;
            }
            StatusText = "Play Mode blocked - Domain Reload would interrupt the conversation";
            AddDiagnostic(
                "Cancelled Play Mode entry because Domain Reload would interrupt the active conversation. " +
                "Enter Play Mode again after the turn finishes.");
            MarkChanged();
        }

        internal void Reconnect()
        {
            if (_disposed)
            {
                return;
            }

            _started = true;
            _reconnectAttempts = 0;
            _nextReconnectTime = -1d;
            _connectionStableSince = -1d;
            CancelUserOperation();
            _interruptWhenStarted = false;
            _pendingDisconnectedClient = null;
            _pendingDisconnect = null;
            _connectionGeneration++;
            DisposeClient();
            _connecting = false;
            ConnectInternal(true);
        }

        internal void Disconnect()
        {
            if (_disposed || !_started)
            {
                return;
            }

            _started = false;
            _reconnectAttempts = 0;
            _nextReconnectTime = -1d;
            _connectionStableSince = -1d;
            CancelUserOperation();
            _interruptWhenStarted = false;
            _pendingDisconnectedClient = null;
            _pendingDisconnect = null;
            _connectionGeneration++;
            _connecting = false;
            DisposeClient();
            ReleaseDomainReloadLock();
            ConnectionState = AgentConnectionState.Disconnected;
            StatusText = "Disconnected";
            MarkChanged();
        }

        internal async void NewThread()
        {
            if (!CanStartThread)
            {
                return;
            }

            var operation = BeginUserOperation();
            var client = _client;
            var connectionGeneration = _connectionGeneration;
            StatusText = "Creating thread";
            MarkChanged();
            try
            {
                await StartThreadAsync(client, connectionGeneration, true);
            }
            catch (Exception exception)
            {
                if (!IsCurrentUserOperation(operation, client, connectionGeneration))
                {
                    return;
                }

                AddDiagnostic(exception.Message);
                StatusText = "Could not create a thread";
                MarkChanged();
            }
            finally
            {
                EndUserOperation(operation);
            }
        }

        internal async void SwitchThread(string threadId)
        {
            if (!CanSwitchThread || string.IsNullOrEmpty(threadId) ||
                string.Equals(threadId, _threadId, StringComparison.Ordinal))
            {
                return;
            }

            var operation = BeginUserOperation();
            var client = _client;
            var connectionGeneration = _connectionGeneration;
            StatusText = "Switching conversation";
            MarkChanged();
            try
            {
                await ResumeThreadAsync(client, connectionGeneration, threadId);
                EnsureCurrentUserOperation(operation, client, connectionGeneration);
                StatusText = IsTurnActive ? "Active conversation restored" : "Conversation restored";
            }
            catch (CodexProtocolException exception) when (IsActiveWriterConflict(exception))
            {
                if (!IsCurrentUserOperation(operation, client, connectionGeneration))
                {
                    return;
                }

                try
                {
                    await ReadThreadForDisplayAsync(client, connectionGeneration, threadId);
                    EnsureCurrentUserOperation(operation, client, connectionGeneration);
                    StatusText = "Conversation opened read-only - active in another Codex client";
                }
                catch (Exception readException)
                {
                    if (!IsCurrentUserOperation(operation, client, connectionGeneration))
                    {
                        return;
                    }

                    AddDiagnostic($"Could not open the active conversation read-only: {readException.Message}");
                    StatusText = "Could not switch conversation";
                }
            }
            catch (Exception exception)
            {
                if (!IsCurrentUserOperation(operation, client, connectionGeneration))
                {
                    return;
                }

                AddDiagnostic(exception.Message);
                StatusText = "Could not switch conversation";
            }
            finally
            {
                EndUserOperation(operation);
                MarkChanged();
            }
        }

        internal void RefreshThreads()
        {
            if (!CanRefreshThreads)
            {
                return;
            }

            _nextThreadsCursor = null;
            RefreshThreadsInBackground(false);
        }

        internal void LoadMoreThreads()
        {
            if (!CanLoadMoreThreads)
            {
                return;
            }

            RefreshThreadsInBackground(true);
        }

        internal async void Send(string prompt)
        {
            prompt = prompt?.Trim();
            if (!CanSend || (string.IsNullOrEmpty(prompt) && !HasContextAttachments))
            {
                return;
            }

            IReadOnlyList<AgentContextItem> submittedContexts;
            try
            {
                submittedContexts = PrepareContextsForTurn();
            }
            catch (Exception exception)
            {
                AddDiagnostic(exception.Message);
                StatusText = "Could not prepare context";
                MarkChanged();
                return;
            }

            var operation = BeginUserOperation();
            var client = _client;
            var connectionGeneration = _connectionGeneration;
            _turnStartPending = true;
            _turnStartedAtUtc = DateTime.UtcNow;
            StatusText = "Preparing turn";
            _pendingUserMessage = new AgentChatMessage(
                AgentChatRole.User,
                prompt,
                attachments: CreateChatAttachments(submittedContexts))
            {
                IsPendingTurnStart = true
            };
            _messages.Add(_pendingUserMessage);
            MarkChanged();
            try
            {
                if (!_threadReady)
                {
                    if (!string.IsNullOrEmpty(_threadId))
                    {
                        await ResumeThreadAsync(client, connectionGeneration);
                    }
                    else
                    {
                        await StartThreadAsync(client, connectionGeneration, false);
                    }
                }

                EnsureCurrentUserOperation(operation, client, connectionGeneration);
                if (!_threadReady)
                {
                    throw new InvalidOperationException("No active thread is available.");
                }

                if (IsTurnActive)
                {
                    StatusText = "The restored thread still has an active turn";
                    MarkChanged();
                    return;
                }

                TurnState = AgentTurnState.Starting;
                LockDomainReloadForActiveTurn();
                StatusText = "Starting turn";
                _turnId = null;
                _lastTurnDuration = null;
                _streamingMessages.Clear();
                SaveState();
                MarkChanged();

                var parameters = new JObject
                {
                    ["threadId"] = _threadId,
                    ["input"] = BuildTurnInput(
                        prompt,
                        submittedContexts,
                        EnterPlayModeReloadsDomain()),
                    ["cwd"] = _projectRoot,
                    ["approvalPolicy"] = AgentPermissionPolicy.GetApprovalPolicy(_permissionMode),
                    ["approvalsReviewer"] = AgentPermissionPolicy.GetApprovalsReviewer(_permissionMode),
                    ["sandboxPolicy"] = AgentPermissionPolicy.CreateSandboxPolicy(_permissionMode, _projectRoot)
                };
                AddOptional(parameters, "model", _selectedModelId);
                AddOptional(parameters, "effort", _selectedReasoningEffort);

                var result = await client.SendRequestAsync("turn/start", parameters);
                EnsureCurrentUserOperation(operation, client, connectionGeneration);

                var turn = result["turn"] as JObject;
                _turnId = turn?.Value<string>("id") ?? _turnId;
                AssignTurnToPendingUserMessage(_turnId);
                if (TurnState == AgentTurnState.Starting)
                {
                    TurnState = AgentTurnState.Running;
                    StatusText = "Working";
                }
                _turnStartPending = false;

                CompleteContextSubmission(submittedContexts);
                SaveState();
                MarkChanged();

                var shouldInterrupt = _interruptWhenStarted &&
                                      (TurnState == AgentTurnState.Starting || TurnState == AgentTurnState.Running);
                _interruptWhenStarted = false;
                if (shouldInterrupt)
                {
                    Interrupt();
                }
            }
            catch (Exception exception)
            {
                if (!IsCurrentUserOperation(operation, client, connectionGeneration))
                {
                    return;
                }

                var turnWasConfirmed = !string.IsNullOrEmpty(_turnId) && IsTurnActive;
                var shouldInterrupt = turnWasConfirmed && _interruptWhenStarted;
                _interruptWhenStarted = false;
                if (TurnState == AgentTurnState.Starting && !turnWasConfirmed)
                {
                    TurnState = AgentTurnState.Failed;
                    _turnStartPending = false;
                    UnlockDomainReloadForInactiveTurn();
                    StatusText = "Turn failed to start";
                }
                else if (TurnState == AgentTurnState.Starting)
                {
                    TurnState = AgentTurnState.Running;
                    StatusText = "Working";
                }

                AddDiagnostic(exception.Message);
                MarkChanged();
                if (shouldInterrupt)
                {
                    Interrupt();
                }
            }
            finally
            {
                if (!IsTurnActive)
                {
                    _turnStartPending = false;
                    if (_pendingUserMessage != null)
                    {
                        _pendingUserMessage.IsPendingTurnStart = false;
                        _pendingUserMessage = null;
                    }
                }
                EndUserOperation(operation);
            }
        }

        internal async void Interrupt()
        {
            if (!CanInterrupt)
            {
                return;
            }

            if (string.IsNullOrEmpty(_turnId))
            {
                _interruptWhenStarted = true;
                StatusText = "Stop queued";
                MarkChanged();
                return;
            }

            TurnState = AgentTurnState.Interrupting;
            StatusText = "Stopping";
            MarkChanged();

            var interruptClient = _client;
            var connectionGeneration = _connectionGeneration;
            var threadId = _threadId;
            var turnId = _turnId;
            try
            {
                await interruptClient.SendRequestAsync(
                    "turn/interrupt",
                    new JObject
                    {
                        ["threadId"] = threadId,
                        ["turnId"] = turnId
                    });
                if (IsCurrentTurnOperation(interruptClient, connectionGeneration, threadId, turnId) &&
                    TurnState == AgentTurnState.Interrupting)
                {
                    StatusText = "Stop requested";
                    MarkChanged();
                }
            }
            catch (Exception exception)
            {
                if (!IsCurrentTurnOperation(interruptClient, connectionGeneration, threadId, turnId))
                {
                    return;
                }

                if (TurnState == AgentTurnState.Interrupting && ConnectionState == AgentConnectionState.Ready)
                {
                    TurnState = AgentTurnState.Running;
                    StatusText = "Stop request failed";
                }

                AddDiagnostic(exception.Message);
                MarkChanged();
            }
        }

        internal void Update()
        {
            if (_disposed)
            {
                return;
            }

            UpdateUnityToolingSetup();
            _client?.Pump(128);
            _gitMessageClient?.Pump(128);
            if (!string.IsNullOrEmpty(_pendingDisconnect))
            {
                var disconnectedClient = _pendingDisconnectedClient;
                var reason = _pendingDisconnect;
                _pendingDisconnectedClient = null;
                _pendingDisconnect = null;
                HandleDisconnected(disconnectedClient, reason);
            }

            if (!_connecting && _nextReconnectTime >= 0d && EditorApplication.timeSinceStartup >= _nextReconnectTime)
            {
                _nextReconnectTime = -1d;
                ConnectInternal(false);
            }

            if (ConnectionState == AgentConnectionState.Ready &&
                _connectionStableSince >= 0d &&
                EditorApplication.timeSinceStartup - _connectionStableSince >= ReconnectStabilitySeconds)
            {
                _reconnectAttempts = 0;
                _connectionStableSince = -1d;
            }

            UpdateCompilationVerificationRequest();
            RefreshProjectChanges();

            if (_changePending)
            {
                _changePending = false;
                Changed?.Invoke();
            }
        }

        private void RefreshProjectChanges()
        {
            if (GitBusy) return;
            if (EditorApplication.timeSinceStartup < _nextProjectChangesRefreshTime)
            {
                return;
            }

            _nextProjectChangesRefreshTime = EditorApplication.timeSinceStartup + ProjectChangesRefreshSeconds;
            AgentProjectChangesSnapshot snapshot;
            var error = string.Empty;
            try
            {
                snapshot = AgentForUnityContextCollector.GetProjectChanges(_projectRoot);
            }
            catch (Exception exception)
            {
                var missing = exception is System.ComponentModel.Win32Exception nativeError && nativeError.NativeErrorCode == 2;
                snapshot = new AgentProjectChangesSnapshot(string.Empty, Array.Empty<AgentProjectChange>(),
                    missing ? AgentGitAvailability.GitMissing : AgentGitAvailability.Error);
                error = AgentForUnityContextCollector.Redact(exception.Message);
            }

            if (ProjectGitAvailability == snapshot.Availability && ProjectGitError == error &&
                _projectBranch == snapshot.Branch &&
                _projectChanges.Count == snapshot.Changes.Count &&
                _projectChanges.Zip(snapshot.Changes, (current, next) =>
                    current.Path == next.Path && current.ChangeType == next.ChangeType).All(matches => matches))
            {
                return;
            }

            _projectBranch = snapshot.Branch;
            ProjectGitAvailability = snapshot.Availability;
            ProjectGitError = error;
            _projectChanges.Clear();
            _projectChanges.AddRange(snapshot.Changes);
            MarkChanged();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposePackageUpdate();
            DisposeGit();
            CancelUserOperation();
            _connectionGeneration++;
            ReleaseDomainReloadLock();
            SaveState();
            DisposeClient();
            ConnectionState = AgentConnectionState.Stopped;
            StatusText = "Stopped";
        }

        private async void ConnectInternal(bool userInitiated)
        {
            if (_disposed || !_started || _connecting)
            {
                return;
            }

            _connecting = true;
            var generation = ++_connectionGeneration;
            ConnectionState = AgentConnectionState.Checking;
            StatusText = "Detecting Codex CLI";
            MarkChanged();

            CodexAppServerClient newClient = null;
            try
            {
                var cliInfo = await CodexCliLocator.DetectAsync();
                if (!IsCurrentConnection(generation))
                {
                    return;
                }

                CliPath = cliInfo.Path;
                CliVersion = cliInfo.Version;
                if (!cliInfo.IsAvailable)
                {
                    ConnectionState = AgentConnectionState.CliMissing;
                    StatusText = cliInfo.Error;
                    AddDiagnostic(cliInfo.Error);
                    return;
                }

                ConnectionState = AgentConnectionState.Connecting;
                StatusText = "Starting Codex App Server";
                MarkChanged();

                var process = new CodexAppServerProcess();
                process.Start(cliInfo.Path, _projectRoot);
                if (!string.IsNullOrEmpty(process.StartupDiagnostic))
                {
                    AddDiagnostic(process.StartupDiagnostic);
                }
                newClient = new CodexAppServerClient(process);
                AttachClient(newClient);
                _client = newClient;

                await newClient.InitializeAsync();
                if (!IsCurrentConnection(generation))
                {
                    return;
                }

                var account = await newClient.SendRequestAsync(
                    "account/read",
                    new JObject { ["refreshToken"] = false });
                EnsureCurrentClient(newClient, generation);
                ReadAccount(account);
                try
                {
                    var rateLimits = await newClient.SendRequestAsync(
                        "account/rateLimits/read",
                        JValue.CreateNull());
                    EnsureCurrentClient(newClient, generation);
                    ReadRateLimits(rateLimits);
                }
                catch (Exception)
                {
                    AccountUsageLabel = "Unavailable";
                }
                await LoadModelsAsync(newClient, generation);
                EnsureCurrentClient(newClient, generation);

                var requiresOpenAiAuth = account.Value<bool?>("requiresOpenaiAuth") ?? true;
                var accountToken = account["account"];
                if ((accountToken == null || accountToken.Type == JTokenType.Null) && requiresOpenAiAuth)
                {
                    ConnectionState = AgentConnectionState.SignedOut;
                    StatusText = "Codex is signed out";
                    _threadReady = false;
                    return;
                }

                ConnectionState = AgentConnectionState.Ready;
                StatusText = "Connected";
                _nextReconnectTime = -1d;
                _connectionStableSince = EditorApplication.timeSinceStartup;

                if (!string.IsNullOrEmpty(_threadId))
                {
                    ConnectionState = AgentConnectionState.Recovering;
                    StatusText = "Restoring thread";
                    MarkChanged();
                    try
                    {
                        await ResumeThreadAsync(newClient, generation);
                        if (IsCurrentConnection(generation))
                        {
                            ConnectionState = AgentConnectionState.Ready;
                            StatusText = IsTurnActive
                                ? "Connected - active turn restored"
                                : "Connected - thread restored";
                        }
                    }
                    catch (CodexProtocolException exception) when (IsActiveWriterConflict(exception))
                    {
                        if (!IsCurrentClient(newClient, generation))
                        {
                            return;
                        }

                        try
                        {
                            await ReadThreadForDisplayAsync(newClient, generation, _threadId);
                            EnsureCurrentClient(newClient, generation);
                            ConnectionState = AgentConnectionState.Ready;
                            StatusText = "Conversation opened read-only - active in another Codex client";
                        }
                        catch (Exception readException)
                        {
                            if (!IsCurrentClient(newClient, generation))
                            {
                                return;
                            }

                            HandleThreadRecoveryFailure(readException);
                        }
                    }
                    catch (Exception exception)
                    {
                        if (!IsCurrentClient(newClient, generation))
                        {
                            return;
                        }

                        HandleThreadRecoveryFailure(exception);
                    }
                }

                RefreshThreadsInBackground();
            }
            catch (Exception exception)
            {
                if (IsCurrentConnection(generation))
                {
                    DisposeClient();
                    ConnectionState = AgentConnectionState.Faulted;
                    StatusText = "Connection failed - update Agent for Unity if Codex was recently updated";
                    AddDiagnostic(exception.Message);
                    if (!userInitiated || _started)
                    {
                        ScheduleReconnect();
                    }
                }
                else
                {
                    newClient?.Dispose();
                }
            }
            finally
            {
                if (IsCurrentConnection(generation))
                {
                    _connecting = false;
                    MarkChanged();
                }
            }
        }

        private async System.Threading.Tasks.Task LoadModelsAsync(CodexAppServerClient client, int generation)
        {
            var loadedModels = new List<AgentModelInfo>();
            string cursor = null;
            do
            {
                var parameters = new JObject
                {
                    ["limit"] = 100,
                    ["includeHidden"] = false
                };
                if (!string.IsNullOrEmpty(cursor))
                {
                    parameters["cursor"] = cursor;
                }

                var result = await client.SendRequestAsync("model/list", parameters);
                EnsureCurrentClient(client, generation);
                if (result["data"] is JArray data)
                {
                    foreach (var token in data.OfType<JObject>())
                    {
                        if (token.Value<bool?>("hidden") == true)
                        {
                            continue;
                        }

                        var id = token.Value<string>("id") ?? token.Value<string>("model");
                        if (string.IsNullOrEmpty(id))
                        {
                            continue;
                        }

                        var efforts = new List<string>();
                        if (token["supportedReasoningEfforts"] is JArray effortTokens)
                        {
                            foreach (var effortToken in effortTokens.OfType<JObject>())
                            {
                                var effort = effortToken.Value<string>("reasoningEffort");
                                if (!string.IsNullOrEmpty(effort))
                                {
                                    efforts.Add(effort);
                                }
                            }
                        }

                        loadedModels.Add(new AgentModelInfo(
                            id,
                            token.Value<string>("displayName"),
                            token.Value<bool?>("isDefault") == true,
                            token.Value<string>("defaultReasoningEffort"),
                            efforts));
                    }
                }

                cursor = result.Value<string>("nextCursor");
            }
            while (!string.IsNullOrEmpty(cursor));

            EnsureCurrentClient(client, generation);
            _models.Clear();
            _models.AddRange(loadedModels);
            if (_models.All(model => !string.Equals(model.Id, _selectedModelId, StringComparison.Ordinal)))
            {
                _selectedModelId = _models.FirstOrDefault(model => model.IsDefault)?.Id ?? _models.FirstOrDefault()?.Id;
            }

            RefreshReasoningEfforts();
            SaveState();
        }

        private void HandleThreadRecoveryFailure(Exception exception)
        {
            _threadReady = false;
            _threadReadOnly = false;
            // Forget an unusable persisted thread so the next prompt can create a fresh one.
            _threadId = null;
            ClearContextUsage();
            if (IsTurnActive)
            {
                foreach (var streaming in _streamingMessages.Values)
                {
                    streaming.IsStreaming = false;
                }

                _streamingMessages.Clear();
                _turnId = null;
                TurnState = AgentTurnState.Failed;
                UnlockDomainReloadForInactiveTurn();
            }

            SaveState();
            ConnectionState = AgentConnectionState.Ready;
            StatusText = "Connected - thread recovery failed";
            AddDiagnostic(exception.Message);
        }

        private void ReadAccount(JObject result)
        {
            if (!(result["account"] is JObject account))
            {
                AccountLabel = (result.Value<bool?>("requiresOpenaiAuth") ?? true) ? "Signed out" : "Not required";
                return;
            }

            var email = account.Value<string>("email");
            var plan = account.Value<string>("planType");
            var type = account.Value<string>("type") ?? "Unknown";
            var userName = string.IsNullOrEmpty(email) ? null : email.Split('@')[0];
            var planLabel = string.IsNullOrEmpty(plan)
                ? string.Empty
                : char.ToUpperInvariant(plan[0]) + plan.Substring(1);
            AccountLabel = string.IsNullOrEmpty(userName)
                ? type
                : string.IsNullOrEmpty(planLabel) ? userName : userName + "\n" + planLabel;
        }

        private void ReadRateLimits(JObject result)
        {
            var buckets = result?["rateLimitsByLimitId"] as JObject;
            if (buckets == null || !buckets.Properties().Any())
            {
                buckets = new JObject { ["Codex"] = result?["rateLimits"] };
            }

            var bucketProperties = buckets.Properties().ToList();
            var lines = bucketProperties
                .Select(property => FormatRateLimit(property.Value as JObject))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            AccountUsageLabel = lines.Count == 0 ? "Unavailable" : string.Join("\n", lines);
        }

        private static string FormatRateLimit(JObject rateLimit)
        {
            if (rateLimit == null)
            {
                return string.Empty;
            }

            var windows = new[]
            {
                FormatRateLimitWindow(rateLimit["primary"] as JObject),
                FormatRateLimitWindow(rateLimit["secondary"] as JObject)
            }.Where(value => !string.IsNullOrEmpty(value)).ToList();
            return string.Join("\n", windows);
        }

        private static string FormatRateLimitWindow(JObject window)
        {
            var used = window?.Value<int?>("usedPercent");
            if (!used.HasValue)
            {
                return string.Empty;
            }

            var remaining = Math.Max(0, Math.Min(100, 100 - used.Value));
            var durationMinutes = window.Value<long?>("windowDurationMins");
            var resetAt = window.Value<long?>("resetsAt");
            var label = FormatRateLimitDuration(durationMinutes);
            if (!resetAt.HasValue)
            {
                return label + "  " + remaining + "%";
            }

            var reset = DateTimeOffset.FromUnixTimeSeconds(resetAt.Value).LocalDateTime;
            var resetLabel = reset.Date == DateTime.Now.Date ? reset.ToString("HH:mm") : reset.ToString("MMM d");
            return label + "  " + remaining + "%  " + resetLabel;
        }

        private static string FormatRateLimitDuration(long? durationMinutes)
        {
            if (durationMinutes == 300)
            {
                return "5 hours";
            }

            if (durationMinutes == 10080)
            {
                return "1 week";
            }

            if (!durationMinutes.HasValue)
            {
                return "Usage";
            }

            return durationMinutes.Value % 60 == 0
                ? (durationMinutes.Value / 60) + " hours"
                : durationMinutes.Value + " minutes";
        }

        private async System.Threading.Tasks.Task StartThreadAsync(
            CodexAppServerClient client,
            int generation,
            bool clearMessages)
        {
            var parameters = new JObject
            {
                ["cwd"] = _projectRoot,
                ["sandbox"] = AgentPermissionPolicy.GetThreadSandboxMode(_permissionMode),
                ["approvalPolicy"] = AgentPermissionPolicy.GetApprovalPolicy(_permissionMode),
                ["approvalsReviewer"] = AgentPermissionPolicy.GetApprovalsReviewer(_permissionMode),
                ["developerInstructions"] = BuildDeveloperInstructions()
            };
            AddOptional(parameters, "model", _selectedModelId);

            var result = await client.SendRequestAsync("thread/start", parameters);
            EnsureCurrentClient(client, generation);
            var thread = result["thread"] as JObject;
            var threadId = thread?.Value<string>("id");
            if (string.IsNullOrEmpty(threadId))
            {
                throw new FormatException("thread/start returned no thread id.");
            }

            _threadId = threadId;
            ClearContextUsage();
            _threadAwaitingAutomaticTitle = threadId;
            _turnId = null;
            _threadReady = true;
            _threadReadOnly = false;
            ResetM1ForNewThread();
            if (clearMessages)
            {
                TurnState = AgentTurnState.Idle;
                StatusText = "New thread ready";
                _messages.Clear();
                _streamingMessages.Clear();
            }

            AddOrUpdateThread(thread);

            SaveState();
            MarkChanged();
            RefreshThreadsInBackground();
        }

        private async System.Threading.Tasks.Task ResumeThreadAsync(CodexAppServerClient client, int generation)
        {
            await ResumeThreadAsync(client, generation, _threadId);
        }

        private async System.Threading.Tasks.Task ResumeThreadAsync(
            CodexAppServerClient client,
            int generation,
            string threadId)
        {
            if (string.IsNullOrEmpty(threadId))
            {
                return;
            }

            var result = await client.SendRequestAsync(
                "thread/resume",
                new JObject
                {
                    ["threadId"] = threadId,
                    ["cwd"] = _projectRoot,
                    ["sandbox"] = AgentPermissionPolicy.GetThreadSandboxMode(_permissionMode),
                    ["approvalPolicy"] = AgentPermissionPolicy.GetApprovalPolicy(_permissionMode),
                    ["approvalsReviewer"] = AgentPermissionPolicy.GetApprovalsReviewer(_permissionMode),
                    ["developerInstructions"] = BuildDeveloperInstructions()
                });
            EnsureCurrentClient(client, generation);

            var thread = result["thread"] as JObject;
            var resumedId = thread?.Value<string>("id");
            if (string.IsNullOrEmpty(resumedId))
            {
                throw new FormatException("thread/resume returned no thread id.");
            }

            _threadId = resumedId;
            RestoreContextUsage(resumedId);
            _threadReady = true;
            _threadReadOnly = false;
            KeepRecentTurnsForDisplay(thread);
            RebuildMessages(thread);
            if (IsTurnActive)
            {
                LockDomainReloadForActiveTurn();
            }
            else
            {
                UnlockDomainReloadForInactiveTurn();
            }
            AddOrUpdateThread(thread);
            SaveState();
            MarkChanged();
        }

        private async System.Threading.Tasks.Task ReadThreadForDisplayAsync(
            CodexAppServerClient client,
            int generation,
            string threadId)
        {
            var readResult = await client.SendRequestAsync(
                "thread/read",
                new JObject
                {
                    ["threadId"] = threadId,
                    ["includeTurns"] = false
                });
            EnsureCurrentClient(client, generation);

            var thread = readResult["thread"] as JObject;
            if (thread == null)
            {
                throw new FormatException("thread/read returned no thread.");
            }

            var turnsResult = await client.SendRequestAsync(
                "thread/turns/list",
                new JObject
                {
                    ["threadId"] = threadId,
                    ["limit"] = MaxRestoredMessages,
                    ["sortDirection"] = "desc",
                    ["itemsView"] = "summary"
                });
            EnsureCurrentClient(client, generation);

            var turns = turnsResult["data"] as JArray ?? new JArray();
            thread["turns"] = new JArray(turns.Reverse().Select(turn => turn.DeepClone()));
            _threadId = threadId;
            RestoreContextUsage(threadId);
            _threadReady = false;
            _threadReadOnly = true;
            RebuildMessages(thread);
            AddOrUpdateThread(thread);
            MarkChanged();
        }

        private static bool IsActiveWriterConflict(CodexProtocolException exception)
        {
            return exception != null &&
                   exception.Code == -32600 &&
                   exception.Message.IndexOf("already has an active writer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async void RefreshThreadsInBackground(bool append = false)
        {
            if (_threadsLoading || _client == null || ConnectionState != AgentConnectionState.Ready ||
                (append && string.IsNullOrEmpty(_nextThreadsCursor)))
            {
                return;
            }

            _threadsLoading = true;
            var client = _client;
            var generation = _connectionGeneration;
            var loadedThreads = append
                ? new List<AgentThreadInfo>(_threads)
                : new List<AgentThreadInfo>();
            var cursor = append ? _nextThreadsCursor : null;
            MarkChanged();
            try
            {
                var parameters = new JObject
                {
                    ["limit"] = ThreadPageSize,
                    ["cwd"] = _projectRoot,
                    ["archived"] = false,
                    // App Server otherwise defaults to CLI and VS Code threads only.
                    ["sourceKinds"] = new JArray("cli", "vscode", "appServer"),
                    ["sortKey"] = "updated_at",
                    ["sortDirection"] = "desc"
                };
                if (!string.IsNullOrEmpty(cursor))
                {
                    parameters["cursor"] = cursor;
                }

                var result = await client.SendRequestAsync("thread/list", parameters);
                EnsureCurrentClient(client, generation);
                if (result["data"] is JArray data)
                {
                    foreach (var thread in data.OfType<JObject>())
                    {
                        var info = ReadThreadInfo(thread);
                        if (info != null && loadedThreads.All(value => !string.Equals(value.Id, info.Id, StringComparison.Ordinal)))
                        {
                            loadedThreads.Add(info);
                        }
                    }
                }

                _nextThreadsCursor = result.Value<string>("nextCursor");

                EnsureCurrentClient(client, generation);
                // A just-started thread is not guaranteed to be persisted before its first turn completes.
                // Keep its locally created entry visible until App Server includes it in thread/list.
                if (!append)
                {
                    var activeThread = _threads.FirstOrDefault(thread =>
                        string.Equals(thread.Id, _threadId, StringComparison.Ordinal));
                    if (activeThread != null && loadedThreads.All(thread =>
                        !string.Equals(thread.Id, activeThread.Id, StringComparison.Ordinal)))
                    {
                        loadedThreads.Insert(0, activeThread);
                    }
                }

                _threads.Clear();
                _threads.AddRange(loadedThreads);
            }
            catch (CodexProtocolException exception) when (append &&
                exception.Message.IndexOf("invalid cursor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (IsCurrentClient(client, generation))
                {
                    _nextThreadsCursor = null;
                    AddDiagnostic(
                        "Could not load older conversations because Codex rejected its continuation cursor.");
                }
            }
            catch (Exception exception)
            {
                if (IsCurrentClient(client, generation))
                {
                    AddDiagnostic($"Could not load conversations: {exception.Message}");
                }
            }
            finally
            {
                if (IsCurrentClient(client, generation))
                {
                    _threadsLoading = false;
                    MarkChanged();
                }
            }
        }

        private void AddOrUpdateThread(JObject thread)
        {
            var info = ReadThreadInfo(thread);
            if (info == null)
            {
                return;
            }

            _threads.RemoveAll(value => string.Equals(value.Id, info.Id, StringComparison.Ordinal));
            _threads.Insert(0, info);
        }

        private void UpdateThreadName(string threadId, string threadName)
        {
            var index = _threads.FindIndex(thread => string.Equals(thread.Id, threadId, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            var existing = _threads[index];
            _threads[index] = new AgentThreadInfo(
                existing.Id,
                threadName,
                existing.Preview,
                existing.UpdatedAt,
                existing.Status);
        }

        private static string GetThreadTitle(AgentThreadInfo thread)
        {
            var title = string.IsNullOrWhiteSpace(thread?.Name) ? thread?.Preview : thread.Name;
            return string.IsNullOrWhiteSpace(title)
                ? "New conversation"
                : title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static AgentThreadInfo ReadThreadInfo(JObject thread)
        {
            var id = thread?.Value<string>("id");
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            var historyMode = thread.Value<string>("historyMode");
            if (!string.IsNullOrEmpty(historyMode) &&
                !string.Equals(historyMode, "legacy", StringComparison.Ordinal) &&
                !string.Equals(historyMode, "paginated", StringComparison.Ordinal))
            {
                return null;
            }

            var status = (thread["status"] as JObject)?.Value<string>("type") ?? string.Empty;
            return new AgentThreadInfo(
                id,
                thread.Value<string>("name"),
                thread.Value<string>("preview"),
                thread.Value<long?>("updatedAt") ?? thread.Value<long?>("createdAt") ?? 0L,
                status);
        }

        private void RebuildMessages(JObject thread)
        {
            _messages.Clear();
            _streamingMessages.Clear();
            ResetM1ForRestoredThread();
            _turnId = null;
            TurnState = AgentTurnState.Idle;

            if (!(thread?["turns"] is JArray turns))
            {
                if (_pendingUserMessage != null)
                {
                    _messages.Add(_pendingUserMessage);
                }

                return;
            }

            foreach (var turn in turns.OfType<JObject>())
            {
                var turnId = turn.Value<string>("id");
                var status = turn.Value<string>("status");
                var isCompleted = !string.Equals(status, "inProgress", StringComparison.Ordinal);
                var duration = ReadTurnDuration(turn);
                if (turn["items"] is JArray items)
                {
                    foreach (var item in items.OfType<JObject>())
                    {
                        RestoreItem(item, turnId, duration, isCompleted);
                    }
                }

                if (string.Equals(status, "inProgress", StringComparison.Ordinal))
                {
                    _turnId = turnId;
                    TurnState = AgentTurnState.Running;
                }
                else if (!string.IsNullOrEmpty(status))
                {
                    TurnState = MapTurnState(status);
                }
            }

            if (_messages.Count > MaxRestoredMessages)
            {
                _messages.RemoveRange(0, _messages.Count - MaxRestoredMessages);
            }

            if (_pendingUserMessage != null && !_messages.Contains(_pendingUserMessage))
            {
                _messages.Add(_pendingUserMessage);
            }
        }

        private static void KeepRecentTurnsForDisplay(JObject thread)
        {
            if (!(thread?["turns"] is JArray turns) || turns.Count <= MaxRestoredMessages)
            {
                return;
            }

            thread["turns"] = new JArray(
                turns.Skip(turns.Count - MaxRestoredMessages).Select(turn => turn.DeepClone()));
        }

        private void RestoreItem(
            JObject item,
            string turnId,
            TimeSpan? turnDuration,
            bool isTurnCompleted)
        {
            var type = item.Value<string>("type");
            if (string.Equals(type, "agentMessage", StringComparison.Ordinal))
            {
                var message = new AgentChatMessage(
                    AgentChatRole.Agent,
                    item.Value<string>("text") ?? string.Empty,
                    item.Value<string>("id"),
                    turnId: turnId)
                {
                    TurnDuration = turnDuration,
                    IsTurnCompleted = isTurnCompleted
                };
                _messages.Add(message);
                return;
            }

            if (!string.Equals(type, "userMessage", StringComparison.Ordinal) || !(item["content"] is JArray content))
            {
                RestoreM1Item(item);
                return;
            }

            var text = ExtractRestoredUserMessage(content, out var attachments);
            if (!string.IsNullOrEmpty(text) || attachments.Count > 0)
            {
                var message = new AgentChatMessage(
                    AgentChatRole.User,
                    text,
                    item.Value<string>("id"),
                    attachments,
                    turnId)
                {
                    TurnDuration = turnDuration,
                    IsTurnCompleted = isTurnCompleted
                };
                _messages.Add(message);
            }
        }

        private static TimeSpan? ReadTurnDuration(JObject turn)
        {
            var durationMs = turn?.Value<long?>("durationMs");
            if (durationMs.HasValue && durationMs.Value >= 0)
            {
                return TimeSpan.FromMilliseconds(durationMs.Value);
            }

            var startedAt = turn?.Value<long?>("startedAt");
            var completedAt = turn?.Value<long?>("completedAt");
            if (startedAt.HasValue && completedAt.HasValue && completedAt.Value >= startedAt.Value)
            {
                return TimeSpan.FromSeconds(completedAt.Value - startedAt.Value);
            }

            return null;
        }

        private void HandleNotification(CodexMessage message)
        {
            switch (message.Method)
            {
                case "turn/started":
                    HandleTurnStarted(message.Params);
                    break;
                case "item/agentMessage/delta":
                    HandleAgentMessageDelta(message.Params);
                    break;
                case "item/completed":
                    HandleItemCompleted(message.Params);
                    break;
                case "turn/completed":
                    HandleTurnCompleted(message.Params);
                    break;
                case "error":
                    HandleErrorNotification(message.Params);
                    break;
                case "thread/started":
                case "thread/status/changed":
                case "mcpServer/startupStatus/updated":
                    break;
                case "thread/tokenUsage/updated":
                    HandleThreadTokenUsage(message.Params);
                    break;
                case "account/rateLimits/updated":
                    RefreshRateLimitsAsync();
                    break;
                case "skills/changed":
                case "thread/goal/cleared":
                    break;
                case "thread/name/updated":
                    HandleThreadNameUpdated(message.Params);
                    break;
                default:
                    if (TryHandleM1Notification(message))
                    {
                        break;
                    }

                    if (_reportedUnknownNotifications.Add(message.Method))
                    {
                        AddDiagnostic($"Ignored unknown notification: {message.Method}");
                    }

                    break;
            }
        }

        private async void RefreshRateLimitsAsync()
        {
            var client = _client;
            var generation = _connectionGeneration;
            if (client == null || !IsCurrentClient(client, generation))
            {
                return;
            }

            try
            {
                var rateLimits = await client.SendRequestAsync("account/rateLimits/read", JValue.CreateNull());
                EnsureCurrentClient(client, generation);
                ReadRateLimits(rateLimits);
                MarkChanged();
            }
            catch (Exception)
            {
                // Rate-limit refresh is informational and must not interrupt an active conversation.
            }
        }

        private void HandleThreadTokenUsage(JObject parameters)
        {
            if (!MatchesThread(parameters))
            {
                return;
            }

            var threadId = parameters?.Value<string>("threadId") ?? _threadId;
            var tokenUsage = parameters?["tokenUsage"] as JObject;
            var contextWindow = tokenUsage?.Value<long?>("modelContextWindow");
            var inputTokens = (tokenUsage?["last"] as JObject)?.Value<long?>("inputTokens");
            if (!contextWindow.HasValue || contextWindow.Value <= 0 || !inputTokens.HasValue)
            {
                ClearContextUsage();
                MarkChanged();
                return;
            }

            var used = Math.Max(0L, Math.Min(inputTokens.Value, contextWindow.Value));
            if (!string.IsNullOrEmpty(threadId))
            {
                _contextUsageByThreadId[threadId] = new AgentContextUsageSnapshot(used, contextWindow.Value);
            }

            ApplyContextUsage(used, contextWindow.Value);
            MarkChanged();
        }

        private void RestoreContextUsage(string threadId)
        {
            if (!string.IsNullOrEmpty(threadId) && _contextUsageByThreadId.TryGetValue(threadId, out var usage))
            {
                ApplyContextUsage(usage.InputTokens, usage.ModelContextWindow);
                return;
            }

            ClearContextUsage();
        }

        private void ApplyContextUsage(long inputTokens, long contextWindow)
        {
            var used = Math.Max(0L, Math.Min(inputTokens, contextWindow));
            var usedPercent = (int)Math.Round(used * 100d / contextWindow, MidpointRounding.AwayFromZero);
            ContextUsageLabel = usedPercent + "%";
            ContextUsageTooltip = "Context: " + FormatTokenCount(used) + " / " + FormatTokenCount(contextWindow) +
                                  " tokens (" + usedPercent + "% used)";
        }

        private void ClearContextUsage()
        {
            ContextUsageLabel = string.Empty;
            ContextUsageTooltip = string.Empty;
        }

        private static string FormatTokenCount(long tokens)
        {
            return tokens >= 1000 ? Math.Round(tokens / 1000d, 1).ToString("0.#") + "k" : tokens.ToString();
        }

        private void HandleTurnStarted(JObject parameters)
        {
            if (!MatchesThread(parameters))
            {
                return;
            }

            var turn = parameters["turn"] as JObject;
            _turnId = turn?.Value<string>("id") ?? _turnId;
            AssignTurnToPendingUserMessage(_turnId);
            if (_turnStartedAtUtc == default)
            {
                _turnStartedAtUtc = DateTime.UtcNow;
            }
            TurnState = AgentTurnState.Running;
            StatusText = "Working";
            HandleM1TurnStarted(_turnId);
            SaveState();
            MarkChanged();
            if (_interruptWhenStarted)
            {
                _interruptWhenStarted = false;
                Interrupt();
            }
        }

        private void HandleAgentMessageDelta(JObject parameters)
        {
            if (!MatchesThread(parameters) || !MatchesCurrentTurn(parameters))
            {
                return;
            }

            var itemId = parameters.Value<string>("itemId");
            var delta = parameters.Value<string>("delta");
            if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(delta))
            {
                return;
            }

            if (!_streamingMessages.TryGetValue(itemId, out var chatMessage))
            {
                chatMessage = _messages.LastOrDefault(value =>
                    value.Role == AgentChatRole.Agent &&
                    string.Equals(value.ItemId, itemId, StringComparison.Ordinal));
                if (chatMessage == null)
                {
                    chatMessage = new AgentChatMessage(AgentChatRole.Agent, string.Empty, itemId, turnId: _turnId);
                    _messages.Add(chatMessage);
                }

                chatMessage.IsStreaming = true;
                _streamingMessages.Add(itemId, chatMessage);
            }

            chatMessage.Text += delta;
            AttachActivitiesToAgentMessage(chatMessage);
            MarkChanged();
        }

        private void HandleItemCompleted(JObject parameters)
        {
            if (!MatchesThread(parameters) || !MatchesCurrentTurn(parameters) || !(parameters["item"] is JObject item))
            {
                return;
            }

            HandleM1ItemCompleted(item);

            if (!string.Equals(item.Value<string>("type"), "agentMessage", StringComparison.Ordinal))
            {
                return;
            }

            var itemId = item.Value<string>("id");
            var finalText = item.Value<string>("text") ?? string.Empty;
            if (!_streamingMessages.TryGetValue(itemId ?? string.Empty, out var chatMessage))
            {
                chatMessage = _messages.LastOrDefault(value =>
                    value.Role == AgentChatRole.Agent &&
                    string.Equals(value.ItemId, itemId, StringComparison.Ordinal));
                if (chatMessage == null)
                {
                    chatMessage = new AgentChatMessage(AgentChatRole.Agent, finalText, itemId, turnId: _turnId);
                    _messages.Add(chatMessage);
                }
                else
                {
                    chatMessage.Text = finalText;
                    chatMessage.IsStreaming = false;
                }
            }
            else
            {
                chatMessage.Text = finalText;
                chatMessage.IsStreaming = false;
                _streamingMessages.Remove(itemId);
            }

            AttachActivitiesToAgentMessage(chatMessage);
            MarkChanged();
        }

        private void HandleTurnCompleted(JObject parameters)
        {
            if (!MatchesThread(parameters) || !(parameters["turn"] is JObject turn))
            {
                return;
            }

            var completedTurnId = turn.Value<string>("id") ?? _turnId;
            if (!string.IsNullOrEmpty(_turnId) &&
                !string.IsNullOrEmpty(completedTurnId) &&
                !string.Equals(_turnId, completedTurnId, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var streaming in _streamingMessages.Values)
            {
                streaming.IsStreaming = false;
            }

            _streamingMessages.Clear();
            var status = turn.Value<string>("status") ?? "failed";
            TurnState = MapTurnState(status);
            _lastTurnDuration = ReadTurnDuration(turn);
            if (!_lastTurnDuration.HasValue && _turnStartedAtUtc != default)
            {
                _lastTurnDuration = DateTime.UtcNow - _turnStartedAtUtc;
            }
            RecordTurnDuration(completedTurnId);
            StatusText = TurnState == AgentTurnState.Completed
                ? "Completed"
                : TurnState == AgentTurnState.Interrupted
                    ? "Interrupted"
                    : "Failed";

            if (turn["error"] is JObject error)
            {
                AddDiagnostic(error.Value<string>("message") ?? error.ToString());
            }

            HandleM1TurnCompleted(completedTurnId, TurnState);
            UnlockDomainReloadForInactiveTurn();
            RequestCompilationVerificationAfterCompletedTurn(completedTurnId, TurnState);
            SetAutomaticTitleAfterFirstCompletedTurn(completedTurnId);

            SaveState();
            MarkChanged();
            RefreshThreadsInBackground();
        }

        private void HandleThreadNameUpdated(JObject parameters)
        {
            var threadId = parameters?.Value<string>("threadId");
            if (string.IsNullOrEmpty(threadId))
            {
                return;
            }

            UpdateThreadName(threadId, parameters.Value<string>("threadName"));
            MarkChanged();
        }

        private void SetAutomaticTitleAfterFirstCompletedTurn(string completedTurnId)
        {
            if (TurnState != AgentTurnState.Completed ||
                !string.Equals(_threadAwaitingAutomaticTitle, _threadId, StringComparison.Ordinal))
            {
                return;
            }

            _threadAwaitingAutomaticTitle = null;
            var currentThread = _threads.FirstOrDefault(thread =>
                string.Equals(thread.Id, _threadId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(currentThread?.Name))
            {
                return;
            }

            var firstUserMessage = _messages.FirstOrDefault(message =>
                message.Role == AgentChatRole.User &&
                string.Equals(message.TurnId, completedTurnId, StringComparison.Ordinal));
            var title = BuildAutomaticThreadTitle(firstUserMessage?.Text);
            if (string.IsNullOrEmpty(title) || _client == null)
            {
                return;
            }

            SetThreadNameInBackground(_client, _connectionGeneration, _threadId, title);
        }

        private async void SetThreadNameInBackground(
            CodexAppServerClient client,
            int generation,
            string threadId,
            string title)
        {
            try
            {
                await client.SendRequestAsync(
                    "thread/name/set",
                    new JObject
                    {
                        ["threadId"] = threadId,
                        ["name"] = title
                    });
                if (!IsCurrentClient(client, generation))
                {
                    return;
                }

                UpdateThreadName(threadId, title);
                MarkChanged();
            }
            catch (Exception exception)
            {
                if (!IsCurrentClient(client, generation))
                {
                    return;
                }

                AddDiagnostic($"Could not set conversation title: {exception.Message}");
                MarkChanged();
            }
        }

        private static string BuildAutomaticThreadTitle(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return null;
            }

            var title = string.Join(" ", prompt
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            if (title.Length <= MaxAutomaticThreadTitleLength)
            {
                return title;
            }

            return title.Substring(0, MaxAutomaticThreadTitleLength - 3).TrimEnd() + "...";
        }

        private void AssignTurnToPendingUserMessage(string turnId)
        {
            if (string.IsNullOrEmpty(turnId))
            {
                return;
            }

            var userMessage = _messages.LastOrDefault(message =>
                message.Role == AgentChatRole.User &&
                string.IsNullOrEmpty(message.TurnId));
            if (userMessage != null)
            {
                userMessage.TurnId = turnId;
                userMessage.IsPendingTurnStart = false;
                if (ReferenceEquals(userMessage, _pendingUserMessage))
                {
                    _pendingUserMessage = null;
                }
            }
        }

        private void RecordTurnDuration(string turnId)
        {
            if (string.IsNullOrEmpty(turnId))
            {
                return;
            }

            foreach (var message in _messages.Where(message => string.Equals(message.TurnId, turnId, StringComparison.Ordinal)))
            {
                if (_lastTurnDuration.HasValue)
                {
                    message.TurnDuration = _lastTurnDuration;
                }

                message.IsTurnCompleted = true;
            }
        }

        private void HandleErrorNotification(JObject parameters)
        {
            if (!MatchesThread(parameters) || !MatchesCurrentTurn(parameters))
            {
                return;
            }

            var error = parameters["error"] as JObject;
            AddDiagnostic(error?.Value<string>("message") ?? "The App Server reported an error.");
            if (parameters.Value<bool?>("willRetry") != true)
            {
                StatusText = "Turn error";
            }

            MarkChanged();
        }

        private bool MatchesThread(JObject parameters)
        {
            var notificationThreadId = parameters?.Value<string>("threadId");
            return string.IsNullOrEmpty(notificationThreadId) ||
                   string.Equals(notificationThreadId, _threadId, StringComparison.Ordinal);
        }

        private bool MatchesCurrentTurn(JObject parameters)
        {
            var notificationTurnId = parameters?.Value<string>("turnId");
            if (string.IsNullOrEmpty(notificationTurnId))
            {
                return true;
            }

            if (string.IsNullOrEmpty(_turnId) && TurnState == AgentTurnState.Starting)
            {
                _turnId = notificationTurnId;
                SaveState();
                return true;
            }

            return string.Equals(notificationTurnId, _turnId, StringComparison.Ordinal);
        }

        private static AgentTurnState MapTurnState(string status)
        {
            switch (status)
            {
                case "completed":
                    return AgentTurnState.Completed;
                case "interrupted":
                    return AgentTurnState.Interrupted;
                case "inProgress":
                    return AgentTurnState.Running;
                default:
                    return AgentTurnState.Failed;
            }
        }

        private void RefreshReasoningEfforts()
        {
            _reasoningEfforts.Clear();
            var model = _models.FirstOrDefault(value => string.Equals(value.Id, _selectedModelId, StringComparison.Ordinal));
            if (model == null)
            {
                _selectedReasoningEffort = null;
                return;
            }

            _reasoningEfforts.AddRange(model.SupportedReasoningEfforts);
            if (!_reasoningEfforts.Contains(_selectedReasoningEffort))
            {
                _selectedReasoningEffort = model.DefaultReasoningEffort;
            }
        }

        private void AttachClient(CodexAppServerClient client)
        {
            client.NotificationReceived += HandleNotification;
            client.ServerRequestReceived += HandleServerRequest;
            client.DiagnosticReceived += HandleDiagnostic;
            client.Disconnected += QueueDisconnected;
        }

        private void DisposeClient()
        {
            var client = _client;
            _client = null;
            _threadsLoading = false;
            if (client == null)
            {
                return;
            }

            client.NotificationReceived -= HandleNotification;
            client.ServerRequestReceived -= HandleServerRequest;
            client.DiagnosticReceived -= HandleDiagnostic;
            client.Disconnected -= QueueDisconnected;
            client.Dispose();
            _threadReady = false;
            _threadReadOnly = false;
        }

        private void QueueDisconnected(CodexAppServerClient client, string reason)
        {
            _pendingDisconnectedClient = client;
            _pendingDisconnect = reason;
        }

        private void HandleDisconnected(CodexAppServerClient client, string reason)
        {
            if (!ReferenceEquals(client, _client))
            {
                return;
            }

            _connectionGeneration++;
            _connecting = false;
            _connectionStableSince = -1d;
            CancelUserOperation();
            _interruptWhenStarted = false;
            DisposeClient();
            if (_disposed)
            {
                return;
            }

            ConnectionState = AgentConnectionState.Faulted;
            StatusText = "App Server disconnected";
            AddDiagnostic(reason);
            ScheduleReconnect();
        }

        private void ScheduleReconnect()
        {
            if (!_started)
            {
                _nextReconnectTime = -1d;
                return;
            }

            if (_reconnectAttempts >= MaxReconnectAttempts)
            {
                _nextReconnectTime = -1d;
                StatusText = "Connection failed - reconnect manually";
                return;
            }

            var delay = Math.Pow(2d, _reconnectAttempts);
            _reconnectAttempts++;
            _nextReconnectTime = EditorApplication.timeSinceStartup + delay;
            StatusText = $"Connection failed - retrying in {delay:0}s";
            MarkChanged();
        }

        private bool IsCurrentConnection(int generation)
        {
            return !_disposed && generation == _connectionGeneration;
        }

        private bool IsCurrentClient(CodexAppServerClient client, int generation)
        {
            return IsCurrentConnection(generation) && ReferenceEquals(client, _client);
        }

        private void EnsureCurrentClient(CodexAppServerClient client, int generation)
        {
            if (!IsCurrentClient(client, generation))
            {
                throw new OperationCanceledException("The Codex connection changed while the operation was running.");
            }
        }

        private int BeginUserOperation()
        {
            _operationInProgress = true;
            var operation = ++_operationGeneration;
            MarkChanged();
            return operation;
        }

        private void EndUserOperation(int operation)
        {
            if (operation != _operationGeneration)
            {
                return;
            }

            _operationInProgress = false;
            MarkChanged();
        }

        private void CancelUserOperation()
        {
            _operationGeneration++;
            _operationInProgress = false;
            _turnStartPending = false;
        }

        private bool IsCurrentUserOperation(
            int operation,
            CodexAppServerClient client,
            int connectionGeneration)
        {
            return operation == _operationGeneration && IsCurrentClient(client, connectionGeneration);
        }

        private void EnsureCurrentUserOperation(
            int operation,
            CodexAppServerClient client,
            int connectionGeneration)
        {
            if (!IsCurrentUserOperation(operation, client, connectionGeneration))
            {
                throw new OperationCanceledException("The Codex connection changed while the operation was running.");
            }
        }

        private bool IsCurrentTurnOperation(
            CodexAppServerClient client,
            int connectionGeneration,
            string threadId,
            string turnId)
        {
            return IsCurrentClient(client, connectionGeneration) &&
                   string.Equals(threadId, _threadId, StringComparison.Ordinal) &&
                   string.Equals(turnId, _turnId, StringComparison.Ordinal);
        }

        private string BuildDeveloperInstructions()
        {
            return UnityCompilationDeveloperInstructions + "\n\n" +
                   UnityToolingInstaller.AgentsGuideInstruction + "\n\n" +
                   GetUnityToolingDeveloperInstructions();
        }

        private bool SaveState()
        {
            if (_disposed && ConnectionState == AgentConnectionState.Stopped)
            {
                return false;
            }

            SaveUnityToolingState();
            _persistedState.threadId = _threadId;
            _persistedState.turnId = _turnId;
            _persistedState.selectedModelId = _selectedModelId;
            _persistedState.selectedReasoningEffort = _selectedReasoningEffort;
            _persistedState.permissionMode = _permissionMode.ToString();
            _persistedState.threadContextUsages = _contextUsageByThreadId
                .Take(MaxPersistedContextUsageEntries)
                .Select(pair => new AgentThreadContextUsageState
                {
                    threadId = pair.Key,
                    inputTokens = pair.Value.InputTokens,
                    modelContextWindow = pair.Value.ModelContextWindow
                })
                .ToList();
            SaveM1State();
            try
            {
                AgentForUnityStateStore.Save(_projectRoot, _persistedState);
                return true;
            }
            catch (Exception exception)
            {
                AddDiagnostic($"Could not persist session state: {exception.Message}");
                return false;
            }
        }

        private void AddDiagnostic(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            _diagnostics.Add($"[{DateTime.Now:HH:mm:ss}] {message.Trim()}");
            if (_diagnostics.Count > MaxDiagnostics)
            {
                _diagnostics.RemoveRange(0, _diagnostics.Count - MaxDiagnostics);
            }

            _diagnosticsVersion++;
            MarkChanged();
        }

        private void HandleDiagnostic(string message)
        {
            if (!string.IsNullOrEmpty(message) &&
                message.IndexOf("already has an active writer", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            AddDiagnostic(message);
        }

        // Keep the App Server process alive while it owns a streaming turn. A queued script
        // compilation may still run, but Unity defers the Domain Reload until this is released.
        private void LockDomainReloadForActiveTurn()
        {
            if (_domainReloadLockedForTurn)
            {
                return;
            }

            EditorApplication.LockReloadAssemblies();
            _domainReloadLockedForTurn = true;
        }

        private void UnlockDomainReloadForInactiveTurn()
        {
            if (!_domainReloadLockedForTurn || IsTurnActive)
            {
                return;
            }

            ReleaseDomainReloadLock();
        }

        private void ReleaseDomainReloadLock()
        {
            if (!_domainReloadLockedForTurn)
            {
                return;
            }

            EditorApplication.UnlockReloadAssemblies();
            _domainReloadLockedForTurn = false;
        }

        private void MarkChanged()
        {
            _changePending = true;
        }

        private static void AddOptional(JObject target, string propertyName, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                target[propertyName] = value;
            }
        }
    }
}
