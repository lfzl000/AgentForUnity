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
        Stopped
    }

    internal enum AgentTurnState
    {
        Idle,
        Starting,
        Running,
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

    internal sealed class AgentChatMessage
    {
        internal AgentChatMessage(AgentChatRole role, string text, string itemId = null)
        {
            Role = role;
            Text = text ?? string.Empty;
            ItemId = itemId;
        }

        internal AgentChatRole Role { get; }
        internal string Text { get; set; }
        internal string ItemId { get; }
        internal bool IsStreaming { get; set; }
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

    internal sealed class AgentForUnityService : IDisposable
    {
        private const int MaxDiagnostics = 200;
        private const int MaxRestoredMessages = 100;
        private const int MaxReconnectAttempts = 3;
        private const double ReconnectStabilitySeconds = 30d;

        private readonly string _projectRoot;
        private readonly List<AgentModelInfo> _models = new List<AgentModelInfo>();
        private readonly List<string> _reasoningEfforts = new List<string>();
        private readonly List<AgentChatMessage> _messages = new List<AgentChatMessage>();
        private readonly List<string> _diagnostics = new List<string>();
        private readonly Dictionary<string, AgentChatMessage> _streamingMessages =
            new Dictionary<string, AgentChatMessage>(StringComparer.Ordinal);
        private readonly HashSet<string> _reportedUnknownNotifications =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly AgentForUnityPersistedState _persistedState;

        private CodexAppServerClient _client;
        private bool _started;
        private bool _connecting;
        private bool _disposed;
        private bool _operationInProgress;
        private bool _threadReady;
        private bool _changePending;
        private bool _interruptWhenStarted;
        private int _connectionGeneration;
        private int _diagnosticsVersion;
        private int _operationGeneration;
        private int _reconnectAttempts;
        private double _nextReconnectTime = -1d;
        private double _connectionStableSince = -1d;
        private CodexAppServerClient _pendingDisconnectedClient;
        private string _pendingDisconnect;
        private string _selectedModelId;
        private string _selectedReasoningEffort;
        private string _threadId;
        private string _turnId;

        internal AgentForUnityService()
        {
            _projectRoot = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
            _persistedState = AgentForUnityStateStore.Load(_projectRoot, out var loadError);
            _threadId = _persistedState.threadId;
            _turnId = _persistedState.turnId;
            _selectedModelId = _persistedState.selectedModelId;
            _selectedReasoningEffort = _persistedState.selectedReasoningEffort;
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
        internal string ProjectRoot => _projectRoot;
        internal string ThreadId => _threadId;
        internal string TurnId => _turnId;
        internal IReadOnlyList<AgentModelInfo> Models => _models;
        internal IReadOnlyList<string> ReasoningEfforts => _reasoningEfforts;
        internal IReadOnlyList<AgentChatMessage> Messages => _messages;
        internal IReadOnlyList<string> Diagnostics => _diagnostics;
        internal int DiagnosticsVersion => _diagnosticsVersion;
        internal bool CanSend => ConnectionState == AgentConnectionState.Ready &&
                                 !IsTurnActive &&
                                 !_operationInProgress;
        internal bool CanInterrupt => ConnectionState == AgentConnectionState.Ready &&
                                      (TurnState == AgentTurnState.Running || TurnState == AgentTurnState.Starting) &&
                                      TurnState != AgentTurnState.Interrupting;
        internal bool IsTurnActive => TurnState == AgentTurnState.Starting ||
                                      TurnState == AgentTurnState.Running ||
                                      TurnState == AgentTurnState.Interrupting;

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
            if (_disposed || _started)
            {
                return;
            }

            _started = true;
            ConnectInternal(true);
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

        internal async void NewThread()
        {
            if (!CanSend)
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

        internal async void Send(string prompt)
        {
            prompt = prompt?.Trim();
            if (!CanSend || string.IsNullOrEmpty(prompt))
            {
                return;
            }

            var operation = BeginUserOperation();
            var client = _client;
            var connectionGeneration = _connectionGeneration;
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

                _messages.Add(new AgentChatMessage(AgentChatRole.User, prompt));
                TurnState = AgentTurnState.Starting;
                StatusText = "Starting turn";
                _turnId = null;
                _streamingMessages.Clear();
                SaveState();
                MarkChanged();

                var parameters = new JObject
                {
                    ["threadId"] = _threadId,
                    ["input"] = new JArray(new JObject
                    {
                        ["type"] = "text",
                        ["text"] = prompt
                    }),
                    ["cwd"] = _projectRoot,
                    ["approvalPolicy"] = "never"
                };
                AddOptional(parameters, "model", _selectedModelId);
                AddOptional(parameters, "effort", _selectedReasoningEffort);

                var result = await client.SendRequestAsync("turn/start", parameters);
                EnsureCurrentUserOperation(operation, client, connectionGeneration);

                var turn = result["turn"] as JObject;
                _turnId = turn?.Value<string>("id") ?? _turnId;
                if (TurnState == AgentTurnState.Starting)
                {
                    TurnState = AgentTurnState.Running;
                    StatusText = "Working";
                }

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

            _client?.Pump(128);
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

            if (_changePending)
            {
                _changePending = false;
                Changed?.Invoke();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelUserOperation();
            _connectionGeneration++;
            SaveState();
            DisposeClient();
            ConnectionState = AgentConnectionState.Stopped;
            StatusText = "Stopped";
        }

        private async void ConnectInternal(bool userInitiated)
        {
            if (_disposed || _connecting)
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
                    catch (Exception exception)
                    {
                        if (!IsCurrentClient(newClient, generation))
                        {
                            return;
                        }

                        _threadReady = false;
                        if (IsTurnActive)
                        {
                            foreach (var streaming in _streamingMessages.Values)
                            {
                                streaming.IsStreaming = false;
                            }

                            _streamingMessages.Clear();
                            _turnId = null;
                            TurnState = AgentTurnState.Failed;
                            SaveState();
                        }

                        ConnectionState = AgentConnectionState.Ready;
                        StatusText = "Connected - thread recovery failed";
                        AddDiagnostic(exception.Message);
                    }
                }
            }
            catch (Exception exception)
            {
                if (IsCurrentConnection(generation))
                {
                    DisposeClient();
                    ConnectionState = AgentConnectionState.Faulted;
                    StatusText = "Connection failed";
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

        private void ReadAccount(JObject result)
        {
            if (!(result["account"] is JObject account))
            {
                AccountLabel = (result.Value<bool?>("requiresOpenaiAuth") ?? true) ? "Signed out" : "Not required";
                return;
            }

            var type = account.Value<string>("type") ?? "Unknown";
            var email = account.Value<string>("email");
            var plan = account.Value<string>("planType");
            AccountLabel = string.Join(" - ", new[] { type, email, plan }.Where(value => !string.IsNullOrEmpty(value)));
        }

        private async System.Threading.Tasks.Task StartThreadAsync(
            CodexAppServerClient client,
            int generation,
            bool clearMessages)
        {
            var parameters = new JObject
            {
                ["cwd"] = _projectRoot,
                ["sandbox"] = "read-only",
                ["approvalPolicy"] = "never"
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
            _turnId = null;
            _threadReady = true;
            if (clearMessages)
            {
                TurnState = AgentTurnState.Idle;
                StatusText = "New thread ready";
                _messages.Clear();
                _streamingMessages.Clear();
            }

            SaveState();
            MarkChanged();
        }

        private async System.Threading.Tasks.Task ResumeThreadAsync(CodexAppServerClient client, int generation)
        {
            if (string.IsNullOrEmpty(_threadId))
            {
                return;
            }

            var result = await client.SendRequestAsync(
                "thread/resume",
                new JObject
                {
                    ["threadId"] = _threadId,
                    ["cwd"] = _projectRoot,
                    ["sandbox"] = "read-only",
                    ["approvalPolicy"] = "never"
                });
            EnsureCurrentClient(client, generation);

            var thread = result["thread"] as JObject;
            var resumedId = thread?.Value<string>("id");
            if (string.IsNullOrEmpty(resumedId))
            {
                throw new FormatException("thread/resume returned no thread id.");
            }

            _threadId = resumedId;
            _threadReady = true;
            RebuildMessages(thread);
            SaveState();
            MarkChanged();
        }

        private void RebuildMessages(JObject thread)
        {
            _messages.Clear();
            _streamingMessages.Clear();
            _turnId = null;
            TurnState = AgentTurnState.Idle;

            if (!(thread?["turns"] is JArray turns))
            {
                return;
            }

            foreach (var turn in turns.OfType<JObject>())
            {
                var turnId = turn.Value<string>("id");
                var status = turn.Value<string>("status");
                if (turn["items"] is JArray items)
                {
                    foreach (var item in items.OfType<JObject>())
                    {
                        RestoreItem(item);
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
        }

        private void RestoreItem(JObject item)
        {
            var type = item.Value<string>("type");
            if (string.Equals(type, "agentMessage", StringComparison.Ordinal))
            {
                _messages.Add(new AgentChatMessage(
                    AgentChatRole.Agent,
                    item.Value<string>("text") ?? string.Empty,
                    item.Value<string>("id")));
                return;
            }

            if (!string.Equals(type, "userMessage", StringComparison.Ordinal) || !(item["content"] is JArray content))
            {
                return;
            }

            var text = string.Join(
                "\n",
                content.OfType<JObject>()
                    .Where(value => string.Equals(value.Value<string>("type"), "text", StringComparison.Ordinal))
                    .Select(value => value.Value<string>("text"))
                    .Where(value => !string.IsNullOrEmpty(value)));
            if (!string.IsNullOrEmpty(text))
            {
                _messages.Add(new AgentChatMessage(AgentChatRole.User, text, item.Value<string>("id")));
            }
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
                    break;
                default:
                    if (_reportedUnknownNotifications.Add(message.Method))
                    {
                        AddDiagnostic($"Ignored unknown notification: {message.Method}");
                    }

                    break;
            }
        }

        private void HandleTurnStarted(JObject parameters)
        {
            if (!MatchesThread(parameters))
            {
                return;
            }

            var turn = parameters["turn"] as JObject;
            _turnId = turn?.Value<string>("id") ?? _turnId;
            TurnState = AgentTurnState.Running;
            StatusText = "Working";
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
                    chatMessage = new AgentChatMessage(AgentChatRole.Agent, string.Empty, itemId);
                    _messages.Add(chatMessage);
                }

                chatMessage.IsStreaming = true;
                _streamingMessages.Add(itemId, chatMessage);
            }

            chatMessage.Text += delta;
            MarkChanged();
        }

        private void HandleItemCompleted(JObject parameters)
        {
            if (!MatchesThread(parameters) || !MatchesCurrentTurn(parameters) || !(parameters["item"] is JObject item))
            {
                return;
            }

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
                    chatMessage = new AgentChatMessage(AgentChatRole.Agent, finalText, itemId);
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

            MarkChanged();
        }

        private void HandleTurnCompleted(JObject parameters)
        {
            if (!MatchesThread(parameters) || !(parameters["turn"] is JObject turn))
            {
                return;
            }

            var completedTurnId = turn.Value<string>("id");
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
            StatusText = TurnState == AgentTurnState.Completed
                ? "Completed"
                : TurnState == AgentTurnState.Interrupted
                    ? "Interrupted"
                    : "Failed";

            if (turn["error"] is JObject error)
            {
                AddDiagnostic(error.Value<string>("message") ?? error.ToString());
            }

            SaveState();
            MarkChanged();
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
            client.DiagnosticReceived += AddDiagnostic;
            client.Disconnected += QueueDisconnected;
        }

        private void DisposeClient()
        {
            var client = _client;
            _client = null;
            if (client == null)
            {
                return;
            }

            client.NotificationReceived -= HandleNotification;
            client.DiagnosticReceived -= AddDiagnostic;
            client.Disconnected -= QueueDisconnected;
            client.Dispose();
            _threadReady = false;
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

        private void SaveState()
        {
            if (_disposed && ConnectionState == AgentConnectionState.Stopped)
            {
                return;
            }

            _persistedState.threadId = _threadId;
            _persistedState.turnId = _turnId;
            _persistedState.selectedModelId = _selectedModelId;
            _persistedState.selectedReasoningEffort = _selectedReasoningEffort;
            try
            {
                AgentForUnityStateStore.Save(_projectRoot, _persistedState);
            }
            catch (Exception exception)
            {
                AddDiagnostic($"Could not persist session state: {exception.Message}");
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
