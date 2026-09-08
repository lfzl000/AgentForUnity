using System;
using System.Collections.Generic;
using AgentForUnity.Editor.Application;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentForUnity.Editor.UI
{
    internal sealed class AgentForUnityWindow : EditorWindow
    {
        private const string UxmlPath = "Packages/com.zlr.agentforunity/Editor/UI/AgentForUnityWindow.uxml";
        private const string UssPath = "Packages/com.zlr.agentforunity/Editor/UI/AgentForUnityWindow.uss";
        private const float CompactWidth = 690f;

        private readonly List<string> _modelIds = new List<string>();
        private readonly List<MessageRow> _messageRows = new List<MessageRow>();

        private AgentForUnityService _service;
        private VisualElement _windowRoot;
        private VisualElement _statusDot;
        private Label _connectionState;
        private Label _statusText;
        private Label _projectSummary;
        private Label _turnState;
        private Label _threadValue;
        private Label _cliPathValue;
        private Label _cliVersionValue;
        private Label _accountValue;
        private Label _projectValue;
        private DropdownField _modelField;
        private DropdownField _reasoningField;
        private ScrollView _messagesScroll;
        private VisualElement _messagesList;
        private VisualElement _diagnosticsList;
        private Foldout _connectionFoldout;
        private Foldout _diagnosticsFoldout;
        private TextField _promptField;
        private Button _reconnectButton;
        private Button _newThreadButton;
        private Button _interruptButton;
        private Button _sendButton;
        private bool _isRefreshing;
        private bool _layoutInitialized;
        private bool _isCompact;
        private int _lastDiagnosticsVersion = -1;
        private int _lastMessageCount;
        private int _lastMessageTextLength;

        [MenuItem("Window/Agent for Unity")]
        private static void Open()
        {
            var window = GetWindow<AgentForUnityWindow>();
            window.titleContent = new GUIContent("Agent for Unity");
            window.minSize = new Vector2(380f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Agent for Unity");
            _service = AgentForUnityService.Instance;
            _service.Changed -= OnServiceChanged;
            _service.Changed += OnServiceChanged;
            _service.EnsureStarted();
        }

        private void OnDisable()
        {
            if (_service != null)
            {
                _service.Changed -= OnServiceChanged;
            }
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            _messageRows.Clear();
            _lastDiagnosticsVersion = -1;
            _lastMessageCount = 0;
            _lastMessageTextLength = 0;
            _layoutInitialized = false;

            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (visualTree != null)
            {
                visualTree.CloneTree(rootVisualElement);
            }
            else
            {
                BuildFallbackUi(rootVisualElement);
            }

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (styleSheet != null)
            {
                rootVisualElement.styleSheets.Add(styleSheet);
            }

            if (!BindVisualElements())
            {
                rootVisualElement.Clear();
                BuildFallbackUi(rootVisualElement);
                BindVisualElements();
            }

            if (styleSheet == null)
            {
                ApplyEssentialFallbackStyles();
            }

            RegisterUiCallbacks();
            rootVisualElement.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            RefreshFromService();
        }

        private bool BindVisualElements()
        {
            _windowRoot = rootVisualElement.Q<VisualElement>("window-root");
            _statusDot = rootVisualElement.Q<VisualElement>("status-dot");
            _connectionState = rootVisualElement.Q<Label>("connection-state");
            _statusText = rootVisualElement.Q<Label>("status-text");
            _projectSummary = rootVisualElement.Q<Label>("project-summary");
            _turnState = rootVisualElement.Q<Label>("turn-state");
            _threadValue = rootVisualElement.Q<Label>("thread-value");
            _cliPathValue = rootVisualElement.Q<Label>("cli-path-value");
            _cliVersionValue = rootVisualElement.Q<Label>("cli-version-value");
            _accountValue = rootVisualElement.Q<Label>("account-value");
            _projectValue = rootVisualElement.Q<Label>("project-value");
            _modelField = rootVisualElement.Q<DropdownField>("model-field");
            _reasoningField = rootVisualElement.Q<DropdownField>("reasoning-field");
            _messagesScroll = rootVisualElement.Q<ScrollView>("messages-scroll");
            _messagesList = rootVisualElement.Q<VisualElement>("messages-list");
            _diagnosticsList = rootVisualElement.Q<VisualElement>("diagnostics-list");
            _connectionFoldout = rootVisualElement.Q<Foldout>("connection-foldout");
            _diagnosticsFoldout = rootVisualElement.Q<Foldout>("diagnostics-foldout");
            _promptField = rootVisualElement.Q<TextField>("prompt-field");
            _reconnectButton = rootVisualElement.Q<Button>("reconnect-button");
            _newThreadButton = rootVisualElement.Q<Button>("new-thread-button");
            _interruptButton = rootVisualElement.Q<Button>("interrupt-button");
            _sendButton = rootVisualElement.Q<Button>("send-button");

            return _windowRoot != null
                   && _statusDot != null
                   && _connectionState != null
                   && _statusText != null
                   && _projectSummary != null
                   && _turnState != null
                   && _threadValue != null
                   && _cliPathValue != null
                   && _cliVersionValue != null
                   && _accountValue != null
                   && _projectValue != null
                   && _modelField != null
                   && _reasoningField != null
                   && _messagesScroll != null
                   && _messagesList != null
                   && _diagnosticsList != null
                   && _connectionFoldout != null
                   && _diagnosticsFoldout != null
                   && _promptField != null
                   && _reconnectButton != null
                   && _newThreadButton != null
                   && _interruptButton != null
                   && _sendButton != null;
        }

        private void RegisterUiCallbacks()
        {
            _promptField.multiline = true;
            _promptField.RegisterValueChangedCallback(_ => UpdateActionAvailability());
            _reconnectButton.clicked += () => _service.Reconnect();
            _newThreadButton.clicked += () => _service.NewThread();
            _interruptButton.clicked += () => _service.Interrupt();
            _sendButton.clicked += SendPrompt;
            _modelField.RegisterValueChangedCallback(OnModelChanged);
            _reasoningField.RegisterValueChangedCallback(OnReasoningChanged);
        }

        private void OnServiceChanged()
        {
            RefreshFromService();
        }

        private void RefreshFromService()
        {
            if (_service == null || _windowRoot == null)
            {
                return;
            }

            _isRefreshing = true;
            try
            {
                var connectionState = Convert.ToString(_service.ConnectionState) ?? string.Empty;
                _connectionState.text = DisplayValue(connectionState, "Not checked");
                _statusText.text = DisplayValue(_service.StatusText, "Waiting for Codex");
                _projectSummary.text = ShortProjectName(_service.ProjectRoot);
                _projectSummary.tooltip = DisplayValue(_service.ProjectRoot, "Project unavailable");
                _turnState.text = DisplayValue(_service.TurnStateLabel, "Idle");

                SetLabelValue(_threadValue, _service.ThreadId, "Not started");
                SetLabelValue(_cliPathValue, _service.CliPath, "Not found");
                SetLabelValue(_cliVersionValue, _service.CliVersion, "Unknown");
                SetLabelValue(_accountValue, _service.AccountLabel, "Unknown");
                SetLabelValue(_projectValue, _service.ProjectRoot, "Unavailable");

                RefreshConnectionTone(connectionState);
                RefreshModels(_service.Models, _service.SelectedModelId);
                RefreshReasoningEfforts(_service.ReasoningEfforts, _service.SelectedReasoningEffort);
                RefreshMessages(_service.Messages);
                RefreshDiagnostics(_service.Diagnostics, _service.DiagnosticsVersion);
                UpdateActionAvailability();
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private void RefreshModels(IReadOnlyList<AgentModelInfo> models, string selectedModelId)
        {
            var choices = new List<string>();
            _modelIds.Clear();

            if (models != null)
            {
                for (var i = 0; i < models.Count; i++)
                {
                    var model = models[i];
                    if (model == null || string.IsNullOrWhiteSpace(model.Id))
                    {
                        continue;
                    }

                    _modelIds.Add(model.Id);
                    var label = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName;
                    if (choices.Contains(label))
                    {
                        label = label + " (" + model.Id + ")";
                    }

                    choices.Add(label);
                }
            }

            if (choices.Count == 0)
            {
                choices.Add("Unavailable");
            }

            _modelField.choices = choices;
            var selectedIndex = _modelIds.IndexOf(selectedModelId);
            _modelField.SetValueWithoutNotify(selectedIndex >= 0 ? choices[selectedIndex] : choices[0]);
            _modelField.SetEnabled(_modelIds.Count > 0 && _service.CanSend);
        }

        private void RefreshReasoningEfforts(IReadOnlyList<string> efforts, string selectedEffort)
        {
            var choices = new List<string>();
            if (efforts != null)
            {
                for (var i = 0; i < efforts.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(efforts[i]))
                    {
                        choices.Add(efforts[i]);
                    }
                }
            }

            if (choices.Count == 0)
            {
                choices.Add("Unavailable");
            }

            _reasoningField.choices = choices;
            var selectedIndex = choices.IndexOf(selectedEffort);
            _reasoningField.SetValueWithoutNotify(selectedIndex >= 0 ? choices[selectedIndex] : choices[0]);
            _reasoningField.SetEnabled(efforts != null && efforts.Count > 0 && _service.CanSend);
        }

        private void RefreshMessages(IReadOnlyList<AgentChatMessage> messages)
        {
            var messageCount = messages == null ? 0 : messages.Count;
            if (messageCount == 0)
            {
                if (_messageRows.Count != 0 || _messagesList.childCount == 0)
                {
                    _messageRows.Clear();
                    _messagesList.Clear();
                    var empty = new Label("No messages in this thread");
                    empty.AddToClassList("afu-empty-state");
                    _messagesList.Add(empty);
                }

                _lastMessageCount = 0;
                _lastMessageTextLength = 0;
                return;
            }

            if (_messageRows.Count != messageCount)
            {
                _messageRows.Clear();
                _messagesList.Clear();
                for (var i = 0; i < messageCount; i++)
                {
                    var row = CreateMessageRow();
                    _messageRows.Add(row);
                    _messagesList.Add(row.Root);
                }
            }

            for (var i = 0; i < messageCount; i++)
            {
                var message = messages[i];
                var row = _messageRows[i];
                var role = message == null ? string.Empty : Convert.ToString(message.Role);
                var normalizedRole = NormalizeRole(role);

                row.Root.EnableInClassList("afu-message--user", normalizedRole == "User");
                row.Root.EnableInClassList("afu-message--agent", normalizedRole == "Agent");
                row.Role.text = normalizedRole;
                row.Body.text = message == null ? string.Empty : message.Text ?? string.Empty;
                row.Streaming.style.display = message != null && message.IsStreaming
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }

            var lastMessage = messages[messageCount - 1];
            var lastTextLength = lastMessage == null || lastMessage.Text == null ? 0 : lastMessage.Text.Length;
            if (_lastMessageCount != messageCount || _lastMessageTextLength != lastTextLength)
            {
                _lastMessageCount = messageCount;
                _lastMessageTextLength = lastTextLength;
                var lastRow = _messageRows[_messageRows.Count - 1].Root;
                _messagesScroll.schedule.Execute(() => _messagesScroll.ScrollTo(lastRow));
            }
        }

        private void RefreshDiagnostics(IReadOnlyList<string> diagnostics, int version)
        {
            if (_lastDiagnosticsVersion == version)
            {
                return;
            }

            _lastDiagnosticsVersion = version;
            _diagnosticsList.Clear();
            var count = diagnostics == null ? 0 : diagnostics.Count;
            _diagnosticsFoldout.text = count == 0 ? "Diagnostics" : "Diagnostics (" + count + ")";

            if (count == 0)
            {
                var empty = new Label("No diagnostics");
                empty.AddToClassList("afu-diagnostic");
                empty.AddToClassList("afu-diagnostic--empty");
                _diagnosticsList.Add(empty);
                return;
            }

            for (var i = 0; i < count; i++)
            {
                var diagnostic = new Label(diagnostics[i] ?? string.Empty);
                diagnostic.enableRichText = false;
                diagnostic.AddToClassList("afu-diagnostic");
                _diagnosticsList.Add(diagnostic);
            }
        }

        private void UpdateActionAvailability()
        {
            if (_service == null || _promptField == null)
            {
                return;
            }

            var hasPrompt = !string.IsNullOrWhiteSpace(_promptField.value);
            _sendButton.SetEnabled(_service.CanSend && hasPrompt);
            _interruptButton.SetEnabled(_service.CanInterrupt);
            _newThreadButton.SetEnabled(_service.CanSend);
        }

        private void SendPrompt()
        {
            var prompt = _promptField.value == null ? string.Empty : _promptField.value.Trim();
            if (!_service.CanSend || prompt.Length == 0)
            {
                return;
            }

            _service.Send(prompt);
            _promptField.SetValueWithoutNotify(string.Empty);
            UpdateActionAvailability();
        }

        private void OnModelChanged(ChangeEvent<string> change)
        {
            if (_isRefreshing)
            {
                return;
            }

            var index = _modelField.choices.IndexOf(change.newValue);
            if (index >= 0 && index < _modelIds.Count)
            {
                _service.SelectedModelId = _modelIds[index];
            }
        }

        private void OnReasoningChanged(ChangeEvent<string> change)
        {
            if (!_isRefreshing && _reasoningField.enabledSelf && !string.IsNullOrWhiteSpace(change.newValue))
            {
                _service.SelectedReasoningEffort = change.newValue;
            }
        }

        private void RefreshConnectionTone(string state)
        {
            _statusDot.RemoveFromClassList("afu-status-dot--ready");
            _statusDot.RemoveFromClassList("afu-status-dot--working");
            _statusDot.RemoveFromClassList("afu-status-dot--warning");
            _statusDot.RemoveFromClassList("afu-status-dot--error");

            if (ContainsIgnoreCase(state, "ready"))
            {
                _statusDot.AddToClassList("afu-status-dot--ready");
            }
            else if (ContainsIgnoreCase(state, "connecting") || ContainsIgnoreCase(state, "recovering"))
            {
                _statusDot.AddToClassList("afu-status-dot--working");
            }
            else if (ContainsIgnoreCase(state, "fault") || ContainsIgnoreCase(state, "missing"))
            {
                _statusDot.AddToClassList("afu-status-dot--error");
            }
            else if (ContainsIgnoreCase(state, "signedout"))
            {
                _statusDot.AddToClassList("afu-status-dot--warning");
            }
        }

        private void OnGeometryChanged(GeometryChangedEvent geometryEvent)
        {
            var compact = geometryEvent.newRect.width < CompactWidth;
            _windowRoot.EnableInClassList("afu--compact", compact);

            if (compact && (!_layoutInitialized || !_isCompact))
            {
                _connectionFoldout.SetValueWithoutNotify(false);
                _diagnosticsFoldout.SetValueWithoutNotify(false);
            }

            _isCompact = compact;
            _layoutInitialized = true;
        }

        private static MessageRow CreateMessageRow()
        {
            var root = new VisualElement();
            root.AddToClassList("afu-message");

            var header = new VisualElement();
            header.AddToClassList("afu-message__header");

            var role = new Label();
            role.AddToClassList("afu-message__role");
            header.Add(role);

            var streaming = new Label("Streaming");
            streaming.AddToClassList("afu-message__streaming");
            header.Add(streaming);

            var body = new Label();
            body.enableRichText = false;
            body.AddToClassList("afu-message__body");

            root.Add(header);
            root.Add(body);
            return new MessageRow(root, role, streaming, body);
        }

        private static string NormalizeRole(string role)
        {
            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "agent", StringComparison.OrdinalIgnoreCase))
            {
                return "Agent";
            }

            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                return "User";
            }

            return string.IsNullOrWhiteSpace(role) ? "System" : role;
        }

        private static string ShortProjectName(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return "Project unavailable";
            }

            var normalized = projectRoot.TrimEnd('/', '\\');
            var slash = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf('\\'));
            return slash >= 0 && slash < normalized.Length - 1 ? normalized.Substring(slash + 1) : normalized;
        }

        private static void SetLabelValue(Label label, string value, string fallback)
        {
            label.text = DisplayValue(value, fallback);
            label.tooltip = label.text;
        }

        private static string DisplayValue(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static bool ContainsIgnoreCase(string value, string search)
        {
            return value != null && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void BuildFallbackUi(VisualElement host)
        {
            var windowRoot = Element("window-root", "afu-root");
            host.Add(windowRoot);

            var header = Element(null, "afu-header");
            var identity = Element(null, "afu-header__identity");
            identity.Add(Label(null, "Agent for Unity", "afu-title"));
            var connection = Element(null, "afu-connection");
            connection.Add(Element("status-dot", "afu-status-dot"));
            connection.Add(Label("connection-state", "Not checked", "afu-connection__label"));
            identity.Add(connection);
            header.Add(identity);

            var headerActions = Element(null, "afu-header__actions");
            headerActions.Add(Button("reconnect-button", "Reconnect", "Restart connection detection"));
            var newThread = Button("new-thread-button", "New Thread", "Start a new project-scoped thread");
            newThread.AddToClassList("afu-primary-button");
            headerActions.Add(newThread);
            header.Add(headerActions);
            windowRoot.Add(header);

            var statusBar = Element(null, "afu-status-bar");
            statusBar.Add(Label("status-text", "Checking Codex...", "afu-status-text"));
            statusBar.Add(Label("project-summary", string.Empty, "afu-project-summary"));
            windowRoot.Add(statusBar);

            var workspace = Element(null, "afu-workspace");
            var chatPane = Element(null, "afu-chat-pane");
            var toolbar = Element(null, "afu-turn-toolbar");
            var modelField = new DropdownField("Model") { name = "model-field" };
            modelField.AddToClassList("afu-select");
            toolbar.Add(modelField);
            var reasoningField = new DropdownField("Reasoning") { name = "reasoning-field" };
            reasoningField.AddToClassList("afu-select");
            reasoningField.AddToClassList("afu-select--reasoning");
            toolbar.Add(reasoningField);
            toolbar.Add(Label("turn-state", "Idle", "afu-turn-state"));
            chatPane.Add(toolbar);

            var threadBar = Element(null, "afu-thread-bar");
            threadBar.Add(Label(null, "Thread", "afu-field-caption"));
            threadBar.Add(Label("thread-value", "Not started", "afu-thread-value"));
            chatPane.Add(threadBar);

            var messageScroll = new ScrollView { name = "messages-scroll" };
            messageScroll.AddToClassList("afu-messages");
            messageScroll.Add(Element("messages-list", "afu-messages__list"));
            chatPane.Add(messageScroll);

            var composer = Element(null, "afu-composer");
            var prompt = new TextField { name = "prompt-field", multiline = true };
            prompt.AddToClassList("afu-prompt");
            composer.Add(prompt);
            var composerActions = Element(null, "afu-composer__actions");
            composerActions.Add(Button("interrupt-button", "Stop", "Interrupt the active turn"));
            var send = Button("send-button", "Send", "Send this prompt to the current thread");
            send.AddToClassList("afu-primary-button");
            composerActions.Add(send);
            composer.Add(composerActions);
            chatPane.Add(composer);
            workspace.Add(chatPane);

            var detailsPane = Element(null, "afu-details-pane");
            var connectionFoldout = new Foldout { name = "connection-foldout", text = "Connection", value = true };
            connectionFoldout.AddToClassList("afu-foldout");
            connectionFoldout.Add(DetailRow("CLI", "cli-path-value"));
            connectionFoldout.Add(DetailRow("Version", "cli-version-value"));
            connectionFoldout.Add(DetailRow("Account", "account-value"));
            connectionFoldout.Add(DetailRow("Project", "project-value"));
            detailsPane.Add(connectionFoldout);

            var diagnosticsFoldout = new Foldout { name = "diagnostics-foldout", text = "Diagnostics", value = true };
            diagnosticsFoldout.AddToClassList("afu-foldout");
            diagnosticsFoldout.AddToClassList("afu-diagnostics-foldout");
            var diagnosticsScroll = new ScrollView();
            diagnosticsScroll.AddToClassList("afu-diagnostics-scroll");
            diagnosticsScroll.Add(Element("diagnostics-list", "afu-diagnostics-list"));
            diagnosticsFoldout.Add(diagnosticsScroll);
            detailsPane.Add(diagnosticsFoldout);
            workspace.Add(detailsPane);
            windowRoot.Add(workspace);
        }

        private void ApplyEssentialFallbackStyles()
        {
            _windowRoot.style.flexGrow = 1f;
            var workspace = rootVisualElement.Q<VisualElement>(className: "afu-workspace");
            var chatPane = rootVisualElement.Q<VisualElement>(className: "afu-chat-pane");
            var detailsPane = rootVisualElement.Q<VisualElement>(className: "afu-details-pane");
            workspace.style.flexGrow = 1f;
            workspace.style.flexDirection = FlexDirection.Row;
            chatPane.style.flexGrow = 1f;
            _messagesScroll.style.flexGrow = 1f;
            _promptField.style.minHeight = 70f;
            detailsPane.style.width = 280f;
            detailsPane.style.paddingLeft = 8f;
            detailsPane.style.paddingRight = 8f;
        }

        private static VisualElement DetailRow(string caption, string valueName)
        {
            var row = Element(null, "afu-detail-row");
            row.Add(Label(null, caption, "afu-field-caption"));
            row.Add(Label(valueName, string.Empty, "afu-detail-value"));
            return row;
        }

        private static VisualElement Element(string name, string className)
        {
            var element = new VisualElement { name = name };
            if (!string.IsNullOrEmpty(className))
            {
                element.AddToClassList(className);
            }

            return element;
        }

        private static Label Label(string name, string text, string className)
        {
            var label = new Label(text) { name = name };
            if (!string.IsNullOrEmpty(className))
            {
                label.AddToClassList(className);
            }

            return label;
        }

        private static Button Button(string name, string text, string tooltip)
        {
            return new Button { name = name, text = text, tooltip = tooltip };
        }

        private sealed class MessageRow
        {
            internal MessageRow(VisualElement root, Label role, Label streaming, Label body)
            {
                Root = root;
                Role = role;
                Streaming = streaming;
                Body = body;
            }

            internal VisualElement Root { get; }
            internal Label Role { get; }
            internal Label Streaming { get; }
            internal Label Body { get; }
        }
    }
}
