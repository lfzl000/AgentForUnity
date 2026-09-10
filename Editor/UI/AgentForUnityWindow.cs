using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private const int MaxRenderedActivityItems = 12;
        private const int MaxRenderedActivityBodyCharacters = 512;
        private const int MaxPreviewImageBytes = 25 * 1024 * 1024;
        private const string LanguagePreferenceKey = "AgentForUnity.InterfaceLanguage";
        private static readonly string[] TurnActivityFrames = { "|", "/", "-", "\\" };

        private readonly List<string> _modelIds = new List<string>();
        private readonly List<string> _reasoningEffortIds = new List<string>();
        private readonly List<MessageRow> _messageRows = new List<MessageRow>();
        private readonly List<Texture2D> _composerPreviewTextures = new List<Texture2D>();

        private AgentForUnityService _service;
        private VisualElement _windowRoot;
        private VisualElement _statusDot;
        private Label _connectionState;
        private Label _statusText;
        private Label _turnState;
        private Label _turnActivityIndicator;
        private Label _contextUsageLabel;
        private Label _threadValue;
        private Label _cliVersionValue;
        private Label _accountValue;
        private Label _accountUsageValue;
        private Label _projectValue;
        private ScrollView _conversationsScroll;
        private VisualElement _conversationsList;
        private DropdownField _modelField;
        private DropdownField _reasoningField;
        private DropdownField _permissionField;
        private DropdownField _languageField;
        private ScrollView _messagesScroll;
        private ScrollView _detailsScroll;
        private VisualElement _messagesList;
        private VisualElement _messagesDeliveryList;
        private VisualElement _contextsList;
        private VisualElement _chatApprovalAlert;
        private VisualElement _chatApprovalActions;
        private Label _chatApprovalTitle;
        private Label _chatApprovalMessage;
        private VisualElement _diffFilesList;
        private Label _projectChangesSummary;
        private Foldout _diffFoldout;
        private TextField _promptField;
        private Button _reconnectButton;
        private Button _disconnectButton;
        private Button _diagnosticsButton;
        private Button _newThreadButton;
        private Button _refreshThreadsButton;
        private Button _interruptButton;
        private Button _sendButton;
        private Button _addSelectionButton;
        private Button _addConsoleButton;
        private Button _addFileButton;
        private Button _addSceneButton;
        private Button _addGitDiffButton;
        private Button _addScreenshotButton;
        private Button _chatAllowOnceButton;
        private Button _chatAllowSessionButton;
        private Button _chatDeclineButton;
        private Button _chatCancelTurnButton;
        private bool _isRefreshing;
        private int _lastMessageCount;
        private int _lastMessageTextLength;
        private string _lastContextSignature;
        private string _lastApprovalSignature;
        private string _lastDiff;
        private string _lastMessagePresentationSignature;
        private string _lastThreadSignature;
        private string _lastRenderedThreadId;
        private string _activeChatApprovalKey;
        private IVisualElementScheduledItem _turnActivityAnimation;
        private int _turnActivityFrame;
        private Label _activeConversationIndicator;
        private Label _activeProcessStatus;

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
            StopTurnActivityAnimation();
            DestroyAttachmentPreviewTextures();
            if (_service != null)
            {
                _service.Changed -= OnServiceChanged;
            }
        }

        public void CreateGUI()
        {
            StopTurnActivityAnimation();
            rootVisualElement.Clear();
            rootVisualElement.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            DestroyAttachmentPreviewTextures();
            _messageRows.Clear();
            _lastMessageCount = 0;
            _lastMessageTextLength = 0;
            _lastContextSignature = null;
            _lastApprovalSignature = null;
            _lastDiff = null;
            _lastMessagePresentationSignature = null;
            _lastThreadSignature = null;
            _lastRenderedThreadId = null;
            _activeChatApprovalKey = null;

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
            _turnState = rootVisualElement.Q<Label>("turn-state");
            _turnActivityIndicator = rootVisualElement.Q<Label>("turn-activity-indicator");
            _contextUsageLabel = rootVisualElement.Q<Label>("context-usage-label");
            _threadValue = rootVisualElement.Q<Label>("thread-value");
            _cliVersionValue = rootVisualElement.Q<Label>("cli-version-value");
            _accountValue = rootVisualElement.Q<Label>("account-value");
            _accountUsageValue = rootVisualElement.Q<Label>("account-usage-value");
            _projectValue = rootVisualElement.Q<Label>("project-value");
            _conversationsScroll = rootVisualElement.Q<ScrollView>("conversations-scroll");
            _conversationsList = rootVisualElement.Q<VisualElement>("conversations-list");
            _modelField = rootVisualElement.Q<DropdownField>("model-field");
            _reasoningField = rootVisualElement.Q<DropdownField>("reasoning-field");
            _permissionField = rootVisualElement.Q<DropdownField>("permission-field");
            _languageField = rootVisualElement.Q<DropdownField>("language-field");
            _messagesScroll = rootVisualElement.Q<ScrollView>("messages-scroll");
            _detailsScroll = rootVisualElement.Q<ScrollView>("details-scroll");
            _messagesList = rootVisualElement.Q<VisualElement>("messages-list");
            _messagesDeliveryList = rootVisualElement.Q<VisualElement>("messages-delivery-list");
            _contextsList = rootVisualElement.Q<VisualElement>("contexts-list");
            _chatApprovalAlert = rootVisualElement.Q<VisualElement>("chat-approval-alert");
            _chatApprovalActions = rootVisualElement.Q<VisualElement>("chat-approval-actions");
            _chatApprovalTitle = rootVisualElement.Q<Label>("chat-approval-title");
            _chatApprovalMessage = rootVisualElement.Q<Label>("chat-approval-message");
            _diffFilesList = rootVisualElement.Q<VisualElement>("diff-files-list");
            _projectChangesSummary = rootVisualElement.Q<Label>("project-changes-summary");
            _diffFoldout = rootVisualElement.Q<Foldout>("diff-foldout");
            _promptField = rootVisualElement.Q<TextField>("prompt-field");
            _reconnectButton = rootVisualElement.Q<Button>("reconnect-button");
            _disconnectButton = rootVisualElement.Q<Button>("disconnect-button");
            _diagnosticsButton = rootVisualElement.Q<Button>("diagnostics-button");
            _newThreadButton = rootVisualElement.Q<Button>("new-thread-button");
            _refreshThreadsButton = rootVisualElement.Q<Button>("refresh-threads-button");
            _interruptButton = rootVisualElement.Q<Button>("interrupt-button");
            _sendButton = rootVisualElement.Q<Button>("send-button");
            _addSelectionButton = rootVisualElement.Q<Button>("add-selection-button");
            _addConsoleButton = rootVisualElement.Q<Button>("add-console-button");
            _addFileButton = rootVisualElement.Q<Button>("add-file-button");
            _addSceneButton = rootVisualElement.Q<Button>("add-scene-button");
            _addGitDiffButton = rootVisualElement.Q<Button>("add-git-diff-button");
            _addScreenshotButton = rootVisualElement.Q<Button>("add-screenshot-button");
            _chatAllowOnceButton = rootVisualElement.Q<Button>("chat-allow-once-button");
            _chatAllowSessionButton = rootVisualElement.Q<Button>("chat-allow-session-button");
            _chatDeclineButton = rootVisualElement.Q<Button>("chat-decline-button");
            _chatCancelTurnButton = rootVisualElement.Q<Button>("chat-cancel-turn-button");

            return _windowRoot != null
                   && _statusDot != null
                   && _connectionState != null
                   && _statusText != null
                   && _turnState != null
                   && _turnActivityIndicator != null
                   && _contextUsageLabel != null
                   && _threadValue != null
                   && _cliVersionValue != null
                   && _accountValue != null
                   && _accountUsageValue != null
                   && _projectValue != null
                   && _conversationsScroll != null
                   && _conversationsList != null
                   && _modelField != null
                   && _reasoningField != null
                   && _permissionField != null
                   && _languageField != null
                   && _messagesScroll != null
                   && _detailsScroll != null
                   && _messagesList != null
                   && _messagesDeliveryList != null
                   && _contextsList != null
                   && _chatApprovalAlert != null
                   && _chatApprovalActions != null
                   && _chatApprovalTitle != null
                   && _chatApprovalMessage != null
                   && _diffFilesList != null
                   && _projectChangesSummary != null
                   && _diffFoldout != null
                   && _promptField != null
                   && _reconnectButton != null
                   && _disconnectButton != null
                   && _diagnosticsButton != null
                   && _newThreadButton != null
                   && _refreshThreadsButton != null
                   && _interruptButton != null
                   && _sendButton != null
                   && _addSelectionButton != null
                   && _addConsoleButton != null
                   && _addFileButton != null
                   && _addSceneButton != null
                   && _addGitDiffButton != null
                   && _addScreenshotButton != null
                   && _chatAllowOnceButton != null
                   && _chatAllowSessionButton != null
                   && _chatDeclineButton != null
                   && _chatCancelTurnButton != null;
        }

        private void RegisterUiCallbacks()
        {
            _promptField.multiline = true;
            _messagesScroll.mode = ScrollViewMode.Vertical;
            _messagesScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _detailsScroll.mode = ScrollViewMode.Vertical;
            _detailsScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            var detailsContent = _detailsScroll.contentContainer;
            detailsContent.style.minWidth = 0;
            detailsContent.style.width = Length.Percent(100f);
            detailsContent.style.maxWidth = Length.Percent(100f);
            var messagesContent = _messagesScroll.contentContainer;
            messagesContent.style.minWidth = 0;
            messagesContent.style.width = Length.Percent(100f);
            messagesContent.style.maxWidth = Length.Percent(100f);
            _messagesList.style.minWidth = 0;
            _messagesList.style.width = Length.Percent(100f);
            _messagesList.style.maxWidth = Length.Percent(100f);
            _promptField.RegisterValueChangedCallback(_ => UpdateActionAvailability());
            _promptField.RegisterCallback<KeyDownEvent>(OnPromptKeyDown, TrickleDown.TrickleDown);
            _reconnectButton.clicked += () => _service.Reconnect();
            _disconnectButton.clicked += () => _service.Disconnect();
            _diagnosticsButton.clicked += AgentForUnityDiagnosticsWindow.Open;
            _newThreadButton.clicked += () => _service.NewThread();
            _refreshThreadsButton.clicked += () => _service.RefreshThreads();
            _interruptButton.clicked += () => _service.Interrupt();
            _sendButton.clicked += SendPrompt;
            _addSelectionButton.clicked += () => AddContext(AgentContextKind.Selection);
            _addConsoleButton.clicked += AddConsoleContext;
            _addFileButton.clicked += AddFileContext;
            _addSceneButton.clicked += () => AddContext(AgentContextKind.Scene);
            _addGitDiffButton.clicked += () => AddContext(AgentContextKind.GitDiff);
            _addScreenshotButton.clicked += ShowScreenshotMenu;
            _chatAllowOnceButton.clicked += () => ResolveActiveChatApproval("accept");
            _chatAllowSessionButton.clicked += () => ResolveActiveChatApproval("acceptForSession");
            _chatDeclineButton.clicked += () => ResolveActiveChatApproval("decline");
            _chatCancelTurnButton.clicked += () => ResolveActiveChatApproval("cancel");
            _modelField.RegisterValueChangedCallback(OnModelChanged);
            _reasoningField.RegisterValueChangedCallback(OnReasoningChanged);
            _permissionField.RegisterValueChangedCallback(OnPermissionChanged);
            _languageField.RegisterValueChangedCallback(OnLanguageChanged);
            ApplyComposerSelectWidths();
        }

        private void ApplyComposerSelectWidths()
        {
            SetFixedWidth(_modelField, 120f);
            SetFixedWidth(_reasoningField, 60f);
            SetFixedWidth(_permissionField, 105f);
        }

        private static void SetFixedWidth(VisualElement field, float width)
        {
            field.style.width = width;
            field.style.minWidth = width;
            field.style.maxWidth = width;
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
                RefreshLanguageSelector();
                ApplyLocalizedStaticText();
                var connectionState = Convert.ToString(_service.ConnectionState) ?? string.Empty;
                _connectionState.text = LocalizeConnectionState(connectionState);
                _statusText.text = LocalizeStatusText(_service.StatusText, T("Waiting for Codex", "等待 Codex"));
                _turnState.text = LocalizeTurnState(_service.TurnStateLabel, T("Idle", "空闲"));
                RefreshTurnActivity(_service.IsTurnStarting);
                _contextUsageLabel.text = _service.ContextUsageLabel;
                _contextUsageLabel.tooltip = _service.ContextUsageTooltip;
                _contextUsageLabel.style.display = string.IsNullOrWhiteSpace(_contextUsageLabel.text)
                    ? DisplayStyle.None
                    : DisplayStyle.Flex;

                SetLabelValue(_threadValue, _service.CurrentThreadTitle, T("New conversation", "新对话"));
                _threadValue.tooltip = string.IsNullOrEmpty(_service.ThreadId)
                    ? null
                    : "Thread ID: " + _service.ThreadId;
                SetLabelValue(_cliVersionValue, _service.CliVersion, T("Unknown", "未知"));
                SetLabelValue(_accountValue, _service.AccountLabel, T("Unknown", "未知"));
                SetLabelValue(_accountUsageValue, LocalizeAccountUsage(_service.AccountUsageLabel), T("Unavailable", "不可用"));
                SetLabelValue(_projectValue, ShortProjectName(_service.ProjectRoot), T("Unavailable", "不可用"));
                _projectValue.tooltip = _service.ProjectRoot;

                RefreshConnectionTone(connectionState);
                RefreshModels(_service.Models, _service.SelectedModelId);
                RefreshReasoningEfforts(_service.ReasoningEfforts, _service.SelectedReasoningEffort);
                RefreshPermissionMode(_service.PermissionMode);
                RefreshConversations(
                    _service.Threads,
                    _service.ThreadId,
                    _service.ThreadsLoading,
                    _service.CanSwitchThread,
                    _service.IsTurnStarting,
                    _service.HasMoreThreads,
                    _service.CanLoadMoreThreads);
                if (!string.Equals(_lastRenderedThreadId, _service.ThreadId, StringComparison.Ordinal))
                {
                    _lastRenderedThreadId = _service.ThreadId;
                    _lastMessageCount = -1;
                    _lastMessageTextLength = -1;
                    _lastMessagePresentationSignature = null;
                }
                RefreshMessages(
                    _service.Messages,
                    _service.TurnState == AgentTurnState.Completed,
                    _service.LastTurnDuration);
                RefreshContexts(_service.Contexts);
                RefreshChatApproval(_service.Approvals);
                RefreshProjectChanges(_service.ProjectChanges, _service.ProjectBranch);
                UpdateActionAvailability();
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private void RefreshConversations(
            IReadOnlyList<AgentThreadInfo> threads,
            string selectedThreadId,
            bool isLoading,
            bool canSwitch,
            bool hasActiveTurn,
            bool hasMore,
            bool canLoadMore)
        {
            var signatureParts = threads == null
                ? new List<string>()
                : threads.Select(thread => string.Join(":", new[]
                {
                    thread.Id,
                    thread.Name,
                    thread.Preview,
                    Convert.ToString(thread.UpdatedAt),
                    thread.Status
                })).ToList();
            var signature = string.Join("|", signatureParts) +
                            "#" + selectedThreadId +
                            "#" + isLoading +
                            "#" + canSwitch +
                            "#" + hasActiveTurn +
                            "#" + hasMore +
                            "#" + canLoadMore;
            if (string.Equals(signature, _lastThreadSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastThreadSignature = signature;
            _activeConversationIndicator = null;
            _conversationsList.Clear();
            if (threads == null || threads.Count == 0)
            {
                var empty = new Label(isLoading ? T("Loading conversations...", "正在加载对话…") : T("No conversations yet", "暂无对话"));
                empty.AddToClassList("afu-conversations-empty");
                _conversationsList.Add(empty);
                return;
            }

            foreach (var thread in threads)
            {
                if (thread == null || string.IsNullOrEmpty(thread.Id))
                {
                    continue;
                }

                var threadId = thread.Id;
                var selected = string.Equals(threadId, selectedThreadId, StringComparison.Ordinal);
                var button = new Button(() => _service.SwitchThread(threadId))
                {
                    tooltip = ConversationTooltip(thread)
                };
                button.AddToClassList("afu-conversation");
                button.EnableInClassList("afu-conversation--selected", selected);
                button.SetEnabled(selected || canSwitch);

                var titleRow = new VisualElement();
                titleRow.AddToClassList("afu-conversation__title-row");
                var title = new Label(ConversationTitle(thread));
                title.AddToClassList("afu-conversation__title");
                titleRow.Add(title);
                if (selected && hasActiveTurn)
                {
                    _activeConversationIndicator = new Label(TurnActivityFrames[_turnActivityFrame]);
                    _activeConversationIndicator.AddToClassList("afu-conversation__activity");
                    titleRow.Add(_activeConversationIndicator);
                }

                button.Add(titleRow);

                var meta = new Label(ConversationMeta(thread, selected));
                meta.AddToClassList("afu-conversation__meta");
                button.Add(meta);
                _conversationsList.Add(button);
            }

            if (hasMore)
            {
                var loadMore = new Button(_service.LoadMoreThreads)
                {
                    text = isLoading ? T("Loading...", "正在加载…") : T("Load more", "加载更多")
                };
                loadMore.tooltip = T("Load 20 more conversations", "加载另外 20 个对话");
                loadMore.AddToClassList("afu-conversations-load-more");
                loadMore.SetEnabled(canLoadMore);
                _conversationsList.Add(loadMore);
            }
        }

        private void RefreshTurnActivity(bool isActive)
        {
            if (!isActive)
            {
                StopTurnActivityAnimation();
                return;
            }

            _turnActivityIndicator.style.display = DisplayStyle.Flex;
            if (_turnActivityAnimation != null)
            {
                return;
            }

            _turnActivityFrame = 0;
            RefreshTurnActivityIndicators();
            _turnActivityAnimation = _turnActivityIndicator.schedule.Execute(() =>
            {
                _turnActivityFrame = (_turnActivityFrame + 1) % TurnActivityFrames.Length;
                RefreshTurnActivityIndicators();
            }).Every(120);
        }

        private void StopTurnActivityAnimation()
        {
            _turnActivityAnimation?.Pause();
            _turnActivityAnimation = null;
            if (_turnActivityIndicator == null)
            {
                return;
            }

            _turnActivityIndicator.text = string.Empty;
            _turnActivityIndicator.style.display = DisplayStyle.None;
            if (_activeConversationIndicator != null)
            {
                _activeConversationIndicator.style.display = DisplayStyle.None;
            }

            _activeProcessStatus = null;
        }

        private void RefreshTurnActivityIndicators()
        {
            var frame = TurnActivityFrames[_turnActivityFrame];
            _turnActivityIndicator.text = frame;
            if (_activeConversationIndicator != null)
            {
                _activeConversationIndicator.text = frame;
            }

            if (_activeProcessStatus != null)
            {
                _activeProcessStatus.text = ActiveProcessStatus();
            }
        }

        private static string ConversationTitle(AgentThreadInfo thread)
        {
            var title = string.IsNullOrWhiteSpace(thread.Name) ? thread.Preview : thread.Name;
            if (string.IsNullOrWhiteSpace(title))
            {
                return T("New conversation", "新对话");
            }

            return title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static string ConversationMeta(AgentThreadInfo thread, bool selected)
        {
            var parts = new List<string>();
            if (selected)
            {
                parts.Add(T("Current", "当前"));
            }

            if (!string.IsNullOrWhiteSpace(thread.Status) &&
                !string.Equals(thread.Status, "notLoaded", StringComparison.Ordinal))
            {
                parts.Add(thread.Status == "active" ? T("Active", "进行中") : T("Idle", "空闲"));
            }

            if (thread.UpdatedAt > 0)
            {
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var updated = epoch.AddSeconds(thread.UpdatedAt).ToLocalTime();
                parts.Add(updated.Date == DateTime.Now.Date
                    ? updated.ToString("HH:mm")
                    : updated.ToString("MM-dd HH:mm"));
            }

            return string.Join(" · ", parts);
        }

        private static string ConversationTooltip(AgentThreadInfo thread)
        {
            var preview = string.IsNullOrWhiteSpace(thread.Preview) ? T("No messages yet", "暂无消息") : thread.Preview.Trim();
            return preview + "\n\n" + T("Thread: ", "对话：") + thread.Id;
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
                choices.Add(T("Unavailable", "不可用"));
            }

            _modelField.choices = choices;
            var selectedIndex = _modelIds.IndexOf(selectedModelId);
            _modelField.SetValueWithoutNotify(selectedIndex >= 0 ? choices[selectedIndex] : choices[0]);
            _modelField.SetEnabled(_modelIds.Count > 0 && _service.CanSend);
        }

        private void RefreshReasoningEfforts(IReadOnlyList<string> efforts, string selectedEffort)
        {
            var choices = new List<string>();
            _reasoningEffortIds.Clear();
            if (efforts != null)
            {
                for (var i = 0; i < efforts.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(efforts[i]))
                    {
                        _reasoningEffortIds.Add(efforts[i]);
                        choices.Add(DisplayReasoningEffort(efforts[i]));
                    }
                }
            }

            if (choices.Count == 0)
            {
                choices.Add(T("Unavailable", "不可用"));
            }

            _reasoningField.choices = choices;
            var selectedIndex = _reasoningEffortIds.IndexOf(selectedEffort);
            _reasoningField.SetValueWithoutNotify(selectedIndex >= 0 ? choices[selectedIndex] : choices[0]);
            _reasoningField.SetEnabled(_reasoningEffortIds.Count > 0 && _service.CanSend);
        }

        private void RefreshPermissionMode(AgentPermissionMode mode)
        {
            _permissionField.choices = PermissionChoices.ToList();
            _permissionField.SetValueWithoutNotify(PermissionLabel(mode));
            _permissionField.tooltip = PermissionDescription(mode);
            _permissionField.SetEnabled(_service.CanChangePermissionMode);
        }

        private void RefreshMessages(
            IReadOnlyList<AgentChatMessage> messages,
            bool showCompletedTurn,
            TimeSpan? turnDuration)
        {
            var messageCount = messages == null ? 0 : messages.Count;
            if (messageCount == 0)
            {
                if (_messageRows.Count != 0 || _messagesDeliveryList.childCount == 0)
                {
                    DestroyMessagePreviewTextures();
                    _messageRows.Clear();
                    _messagesDeliveryList.Clear();
                    var empty = new Label(T("No messages in this thread", "此对话中暂无消息"));
                    empty.AddToClassList("afu-empty-state");
                    _messagesDeliveryList.Add(empty);
                }

                _lastMessageCount = 0;
                _lastMessageTextLength = 0;
                _lastMessagePresentationSignature = null;
                return;
            }

            if (_messageRows.Count != messageCount)
            {
                DestroyMessagePreviewTextures();
                _messageRows.Clear();
                _messagesDeliveryList.Clear();
                _lastMessagePresentationSignature = null;
                for (var i = 0; i < messageCount; i++)
                {
                    var row = CreateMessageRow();
                    _messageRows.Add(row);
                }
            }

            for (var i = 0; i < messageCount; i++)
            {
                var message = messages[i];
                var row = _messageRows[i];
                var role = message == null ? string.Empty : Convert.ToString(message.Role);
                var normalizedRole = NormalizeRole(role);
                var isUserMessage = message != null && message.Role == AgentChatRole.User;
                var isAgentMessage = message != null && message.Role == AgentChatRole.Agent;

                row.Root.EnableInClassList("afu-message--user", isUserMessage);
                row.Root.EnableInClassList("afu-message--agent", isAgentMessage);
                row.Role.text = normalizedRole;
                row.Streaming.text = T("Streaming", "生成中");
                row.CopyButton.tooltip = T("Copy message text", "复制消息文本");
                RefreshMessageAttachments(row, message?.Attachments);
                RefreshMessageActivities(row, message);
                var text = message == null ? string.Empty : message.Text ?? string.Empty;
                row.RawText = text;
                row.CopyButton.SetEnabled(!string.IsNullOrEmpty(text));
                if (!string.Equals(row.RenderedText, text, StringComparison.Ordinal))
                {
                    row.RenderedText = text;
                    AgentMarkdownRenderer.Render(row.Body, text, OpenProjectLink);
                }
                row.Streaming.style.display = message != null && message.IsStreaming
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }

            RefreshActiveProcessPresentation(messages);
            ArrangeMessageRows(messages, showCompletedTurn, turnDuration);

            var lastMessage = messages[messageCount - 1];
            var lastTextLength = lastMessage == null || lastMessage.Text == null ? 0 : lastMessage.Text.Length;
            if (_lastMessageCount != messageCount || _lastMessageTextLength != lastTextLength)
            {
                _lastMessageCount = messageCount;
                _lastMessageTextLength = lastTextLength;
                ScrollMessagesToBottom();
            }
        }

        private void ScrollMessagesToBottom()
        {
            _messagesScroll.schedule.Execute(() =>
            {
                _messagesScroll.schedule.Execute(() =>
                {
                    var maximumOffset = Mathf.Max(
                        0f,
                        _messagesScroll.contentContainer.layout.height -
                        _messagesScroll.contentViewport.layout.height);
                    _messagesScroll.scrollOffset = new Vector2(_messagesScroll.scrollOffset.x, maximumOffset);
                });
            });
        }

        private void ArrangeMessageRows(
            IReadOnlyList<AgentChatMessage> messages,
            bool showCompletedTurn,
            TimeSpan? turnDuration)
        {
            var signature = messages.Count + ":" + showCompletedTurn + ":" + turnDuration + ":" +
                            (_service.IsTurnActive ? _service.TurnId : string.Empty) + ":" +
                            string.Join("|", messages.Select(message =>
                                (message?.TurnId ?? string.Empty) + ":" +
                                (message?.TurnDuration?.Ticks ?? 0) + ":" +
                                (message?.IsTurnCompleted == true ? "1" : "0")));
            if (string.Equals(signature, _lastMessagePresentationSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastMessagePresentationSignature = signature;
            foreach (var row in _messageRows)
            {
                if (row.ActivityFoldout.parent != row.Root)
                {
                    row.Root.Add(row.ActivityFoldout);
                }
            }

            _messagesDeliveryList.Clear();
            var displayedRows = new HashSet<int>();
            for (var i = 0; i < _messageRows.Count; i++)
            {
                if (displayedRows.Contains(i))
                {
                    continue;
                }

                var message = messages[i];
                if (message?.Role == AgentChatRole.User &&
                    !string.IsNullOrEmpty(message.TurnId) &&
                    message.IsTurnCompleted)
                {
                    var finalMessageIndex = FindFinalAgentMessageIndex(messages, i, message.TurnId);
                    var processEndIndex = finalMessageIndex;

                    if (processEndIndex > i)
                    {
                        _messagesDeliveryList.Add(_messageRows[i].Root);
                        displayedRows.Add(i);

                        var process = new Foldout
                        {
                            text = ProcessTitle(message.TurnDuration),
                            value = false
                        };
                        process.AddToClassList("afu-turn-process");

                        for (var j = i + 1; j < processEndIndex; j++)
                        {
                            if (!string.Equals(messages[j]?.TurnId, message.TurnId, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            if (messages[j]?.Role == AgentChatRole.User)
                            {
                                _messagesDeliveryList.Add(_messageRows[j].Root);
                                displayedRows.Add(j);
                                continue;
                            }

                            process.Add(_messageRows[j].Root);
                            displayedRows.Add(j);
                        }

                        if (finalMessageIndex > i)
                        {
                            var finalRow = _messageRows[finalMessageIndex];
                            if (messages[finalMessageIndex]?.Activities?.Count > 0)
                            {
                                finalRow.ActivityFoldout.RemoveFromHierarchy();
                                process.Add(finalRow.ActivityFoldout);
                            }

                            _messagesDeliveryList.Add(process);
                            _messagesDeliveryList.Add(finalRow.Root);
                            displayedRows.Add(finalMessageIndex);
                        }
                        else
                        {
                            _messagesDeliveryList.Add(process);
                        }
                        continue;
                    }
                }

                _messagesDeliveryList.Add(_messageRows[i].Root);
                displayedRows.Add(i);
            }
        }

        private static int FindFinalAgentMessageIndex(
            IReadOnlyList<AgentChatMessage> messages,
            int userMessageIndex,
            string turnId)
        {
            var finalMessageIndex = -1;
            for (var i = userMessageIndex + 1; i < messages.Count; i++)
            {
                var message = messages[i];
                if (!string.Equals(message?.TurnId, turnId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (message.Role == AgentChatRole.Agent)
                {
                    finalMessageIndex = i;
                }
            }

            return finalMessageIndex;
        }

        private void RefreshActiveProcessPresentation(IReadOnlyList<AgentChatMessage> messages)
        {
            _activeProcessStatus = null;
            for (var i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                var row = _messageRows[i];
                var isCurrentTurnMessage = _service.IsTurnStarting && message != null &&
                                           (message.IsPendingTurnStart ||
                                            (!string.IsNullOrEmpty(_service.TurnId) &&
                                             string.Equals(message.TurnId, _service.TurnId, StringComparison.Ordinal)));
                var isActiveProcessMessage = isCurrentTurnMessage && message.Role == AgentChatRole.Agent;
                row.Root.EnableInClassList("afu-message--process", isActiveProcessMessage);
                if (isCurrentTurnMessage)
                {
                    _activeProcessStatus = row.Streaming;
                }
            }

            if (_activeProcessStatus != null)
            {
                _activeProcessStatus.text = ActiveProcessStatus();
                _activeProcessStatus.style.display = DisplayStyle.Flex;
            }
        }

        private string ActiveProcessStatus()
        {
            return $"{FormatElapsed(_service.ActiveTurnDuration)} {TurnActivityFrames[_turnActivityFrame]}";
        }

        private static string ProcessTitle(TimeSpan? duration)
        {
            return T("Process conversation · Total ", "处理对话 · 总计 ") + FormatDuration(duration);
        }

        private static string FormatElapsed(TimeSpan? duration)
        {
            var value = duration.GetValueOrDefault();
            if (value.TotalHours >= 1)
            {
                return $"{(int)value.TotalHours}h {value.Minutes:D2}m {value.Seconds:D2}s";
            }

            if (value.TotalMinutes >= 1)
            {
                return $"{value.Minutes}m {value.Seconds:D2}s";
            }

            return $"{Math.Max(0, (int)value.TotalSeconds)}s";
        }

        private static string FormatDuration(TimeSpan? duration)
        {
            if (!duration.HasValue)
            {
                return T("unknown", "未知");
            }

            var value = duration.Value;
            if (value.TotalHours >= 1)
            {
                return $"{(int)value.TotalHours}h {value.Minutes:D2}m {value.Seconds:D2}s";
            }

            if (value.TotalMinutes >= 1)
            {
                return $"{value.Minutes}m {value.Seconds:D2}s";
            }

            return $"{Math.Max(0, (int)Math.Ceiling(value.TotalSeconds))}s";
        }

        private void RefreshContexts(IReadOnlyList<AgentContextItem> contexts)
        {
            var signature = contexts == null
                ? string.Empty
                : string.Join("|", contexts.Select(item => item.Id));
            if (signature == _lastContextSignature)
            {
                return;
            }

            _lastContextSignature = signature;
            DestroyComposerPreviewTextures();
            _contextsList.Clear();
            if (contexts == null)
            {
                return;
            }

            foreach (var context in contexts)
            {
                if (context.Kind == AgentContextKind.Screenshot)
                {
                    _contextsList.Add(CreateScreenshotPreview(
                        context.Label,
                        context.Source,
                        () => _service.RemoveContext(context.Id),
                        _composerPreviewTextures));
                    continue;
                }

                var chip = new VisualElement();
                chip.AddToClassList("afu-context-chip");
                var label = new Label($"{context.Label} · {context.CharacterCount:N0}");
                label.tooltip = context.Preview;
                label.AddToClassList("afu-context-chip__label");
                chip.Add(label);
                var contextId = context.Id;
                var remove = new Button(() => _service.RemoveContext(contextId)) { text = "×", tooltip = "Remove context" };
                remove.AddToClassList("afu-context-chip__remove");
                chip.Add(remove);
                _contextsList.Add(chip);
            }
        }

        private void RefreshMessageAttachments(
            MessageRow row,
            IReadOnlyList<AgentChatAttachment> attachments)
        {
            var signature = attachments == null
                ? string.Empty
                : string.Join("|", attachments.Select(item => item.Kind + ":" + item.Label + ":" + item.Source));
            if (string.Equals(signature, row.AttachmentSignature, StringComparison.Ordinal))
            {
                return;
            }

            row.AttachmentSignature = signature;
            DestroyTextures(row.PreviewTextures);
            row.Attachments.Clear();
            row.Attachments.style.display = string.IsNullOrEmpty(signature)
                ? DisplayStyle.None
                : DisplayStyle.Flex;
            if (attachments == null || attachments.Count == 0)
            {
                return;
            }

            var caption = new Label(T("Attachments", "附件"));
            caption.AddToClassList("afu-message__attachments-caption");
            row.Attachments.Add(caption);
            foreach (var attachment in attachments)
            {
                if (attachment.Kind == AgentContextKind.Screenshot)
                {
                    row.Attachments.Add(CreateScreenshotPreview(
                        attachment.Label,
                        attachment.Source,
                        null,
                        row.PreviewTextures));
                    continue;
                }

                var chip = new Label(attachment.Label)
                {
                    tooltip = string.IsNullOrWhiteSpace(attachment.Source)
                        ? attachment.Kind.ToString()
                        : attachment.Kind + ": " + attachment.Source
                };
                chip.AddToClassList("afu-message__attachment");
                row.Attachments.Add(chip);
            }
        }

        private void RefreshMessageActivities(MessageRow row, AgentChatMessage message)
        {
            var activities = message?.Activities;
            var signature = (message?.Role.ToString() ?? string.Empty) + ":" +
                            (activities == null
                                ? string.Empty
                                : string.Join("|", activities.Select(activity =>
                                    activity.Id + ":" + activity.Status + ":" + activity.IsStreaming + ":" +
                                    (activity.Body ?? string.Empty).GetHashCode())));
            if (string.Equals(signature, row.ActivitySignature, StringComparison.Ordinal))
            {
                return;
            }

            row.ActivitySignature = signature;
            var isAgentMessage = message != null && message.Role == AgentChatRole.Agent;
            var count = activities?.Count ?? 0;
            row.ActivityFoldout.style.display = isAgentMessage && count > 0
                ? DisplayStyle.Flex
                : DisplayStyle.None;
            row.ActivityList.Clear();
            if (!isAgentMessage || count == 0)
            {
                return;
            }

            var skippedCount = Math.Max(0, count - MaxRenderedActivityItems);
            var latestActivity = activities[count - 1];
            var summary = ActivitySummary(latestActivity);
            row.ActivityFoldout.text = skippedCount == 0
                ? T("Activity", "活动") + $" ({count}) · {summary}"
                : T("Activity", "活动") + $" ({count}, " + T("latest", "最近") + $" {MaxRenderedActivityItems}) · {summary}";
            if (skippedCount > 0)
            {
                AddEmptyCard(row.ActivityList, T("Showing the latest", "仅显示最近") + $" {MaxRenderedActivityItems} / {count} " + T("activity items.", "个活动项。"));
            }

            foreach (var activity in activities.Skip(skippedCount))
            {
                var detail = new Foldout { text = ActivitySummary(activity), value = false };
                detail.AddToClassList("afu-message-activity__detail");
                var status = new Label(activity.Status);
                status.AddToClassList("afu-message-activity__status");
                detail.Add(status);
                var body = new Label(TruncateForDisplay(activity.Body, MaxRenderedActivityBodyCharacters))
                {
                    enableRichText = false
                };
                body.AddToClassList("afu-card__body");
                detail.Add(body);
                row.ActivityList.Add(detail);
            }

            ScrollMessagesToBottom();
        }

        private static string ActivitySummary(AgentActivityItem activity)
        {
            var body = (activity?.Body ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            if (body.Length == 0)
            {
                body = activity?.Status ?? string.Empty;
            }

            const int maximumLength = 72;
            if (body.Length > maximumLength)
            {
                body = body.Substring(0, maximumLength) + "...";
            }

            return string.IsNullOrEmpty(activity?.Title)
                ? body
                : string.IsNullOrEmpty(body)
                    ? activity.Title
                    : activity.Title + ": " + body;
        }

        private static string TruncateForDisplay(string value, int maximumCharacters)
        {
            var text = value ?? string.Empty;
            if (text.Length <= maximumCharacters)
            {
                return text;
            }

            return text.Substring(0, maximumCharacters) + "\n[Preview truncated for editor performance.]";
        }

        private void RefreshChatApproval(IReadOnlyList<AgentApprovalRequest> approvals)
        {
            var signature = approvals == null
                ? string.Empty
                : string.Join("|", approvals.Select(item =>
                    item.Key + ":" + item.IsResolved + ":" + item.IsResponding + ":" + item.Resolution));
            signature += "|connection:" + _service.ConnectionState;
            if (signature == _lastApprovalSignature)
            {
                return;
            }

            _lastApprovalSignature = signature;
            var approval = approvals?.FirstOrDefault(item => !item.IsResolved);
            var hasPendingApproval = approval != null;
            _chatApprovalAlert.style.display = hasPendingApproval ? DisplayStyle.Flex : DisplayStyle.None;
            _activeChatApprovalKey = approval?.Key;
            if (!hasPendingApproval)
            {
                return;
            }

            var canRespond = !approval.IsResponding && _service.ConnectionState == AgentConnectionState.Ready;
            _chatAllowOnceButton.SetEnabled(canRespond);
            _chatAllowSessionButton.SetEnabled(canRespond);
            _chatDeclineButton.SetEnabled(canRespond);
            _chatCancelTurnButton.SetEnabled(canRespond);
            _chatApprovalTitle.text = T("Permission required", "需要权限");
            _chatApprovalMessage.text = T("This request: ", "本次内容：") +
                                        FormatApprovalContent(approval);
        }

        private static string FormatApprovalContent(AgentApprovalRequest approval)
        {
            var content = !string.IsNullOrWhiteSpace(approval.Command)
                ? approval.Command
                : !string.IsNullOrWhiteSpace(approval.Details)
                    ? approval.Details
                    : approval.Reason;
            content = (content ?? string.Empty).Trim();
            if (content.Length == 0)
            {
                return T("Details unavailable", "未提供详细内容");
            }

            const int maximumCharacters = 600;
            return content.Length <= maximumCharacters
                ? content
                : content.Substring(0, maximumCharacters) + "\n...";
        }

        private void ResolveActiveChatApproval(string decision)
        {
            if (!string.IsNullOrEmpty(_activeChatApprovalKey))
            {
                _service.ResolveApproval(_activeChatApprovalKey, decision);
            }
        }

        private void RefreshProjectChanges(IReadOnlyList<AgentProjectChange> changes, string branch)
        {
            var signature = (branch ?? string.Empty) + "\n" + (changes == null
                ? string.Empty
                : string.Join("|", changes.Select(change => change.Path + ":" + change.ChangeType)));
            if (string.Equals(signature, _lastDiff, StringComparison.Ordinal))
            {
                return;
            }

            _lastDiff = signature;
            _diffFilesList.Clear();
            var files = changes ?? Array.Empty<AgentProjectChange>();
            _diffFoldout.text = T("Project Changes", "当前变更");
            var summary = string.Empty;
            if (!string.IsNullOrWhiteSpace(branch))
            {
                summary = T("Branch: ", "分支：") + branch + " · ";
            }

            _projectChangesSummary.text = summary + files.Count + " " + T("files", "个文件");
            foreach (var file in files)
            {
                var capturedPath = file.Path;
                var item = new Button(() => OpenProjectPath(capturedPath))
                {
                    tooltip = capturedPath + "\n" + ProjectChangeTypeLabel(file.ChangeType)
                };
                item.AddToClassList("afu-project-change");

                var name = new Label(ProjectChangeFileName(capturedPath));
                name.AddToClassList("afu-project-change__name");
                item.Add(name);

                var status = new Label(ProjectChangeStatusLetter(file.ChangeType));
                status.AddToClassList("afu-project-change__status");
                status.AddToClassList("afu-project-change__status--" + ProjectChangeStatusClass(file.ChangeType));
                status.tooltip = ProjectChangeTypeLabel(file.ChangeType);
                item.Add(status);
                _diffFilesList.Add(item);
            }

            if (files.Count == 0)
            {
                AddEmptyCard(_diffFilesList, T("No project changes", "当前没有变更"));
            }
        }

        private void AddContext(AgentContextKind kind)
        {
            _service.TryAddContext(kind, null, out _);
        }

        private void AddConsoleContext()
        {
            AgentForUnityConsolePickerWindow.Open(
                _service.GetConsoleEntries(),
                selectedEntries =>
                {
                    if (!_service.TryAddConsoleContext(selectedEntries, out var error) &&
                        !string.IsNullOrWhiteSpace(error))
                    {
                        ShowNotification(new GUIContent(error));
                    }
                });
        }

        private void AddFileContext()
        {
            var path = EditorUtility.OpenFilePanel("Attach project file", _service.ProjectRoot, string.Empty);
            if (!string.IsNullOrWhiteSpace(path))
            {
                _service.TryAddContext(AgentContextKind.File, path, out _);
            }
        }

        private void OpenProjectPath(string path)
        {
            OpenProjectPathAtLine(path, 1);
        }

        private void OpenProjectLink(string target)
        {
            var value = (target ?? string.Empty).Trim();
            // Markdown permits angle brackets around link destinations. Keep that
            // syntax out of the filesystem path passed to Unity's file opener.
            if (value.Length >= 2 && value[0] == '<' && value[value.Length - 1] == '>')
            {
                value = value.Substring(1, value.Length - 2).Trim();
            }

            if (value.Length == 0)
            {
                return;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                {
                    UnityEngine.Application.OpenURL(value);
                    return;
                }

                if (uri.Scheme == Uri.UriSchemeFile)
                {
                    value = uri.LocalPath;
                }
            }

            var line = 1;
            var fragmentIndex = value.IndexOf("#L", StringComparison.OrdinalIgnoreCase);
            if (fragmentIndex >= 0)
            {
                int.TryParse(value.Substring(fragmentIndex + 2), out line);
                value = value.Substring(0, fragmentIndex);
            }

            var separator = value.LastIndexOf(':');
            if (separator > 0 && separator < value.Length - 1 &&
                int.TryParse(value.Substring(separator + 1), out var suffixLine))
            {
                line = suffixLine;
                value = value.Substring(0, separator);
            }

            OpenProjectPathAtLine(Uri.UnescapeDataString(value), Math.Max(1, line));
        }

        private void OpenProjectPathAtLine(string path, int line)
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/');
            if (Path.IsPathRooted(normalized))
            {
                if (File.Exists(normalized))
                {
                    UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(normalized, line);
                }

                return;
            }

            var asset = AssetDatabase.LoadMainAssetAtPath(normalized);
            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);
                AssetDatabase.OpenAsset(asset, line);
                return;
            }

            var absolute = Path.Combine(_service.ProjectRoot, normalized);
            if (File.Exists(absolute))
            {
                UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(absolute, line);
            }
        }

        private string ProjectChangeTypeLabel(AgentProjectChangeType changeType)
        {
            switch (changeType)
            {
                case AgentProjectChangeType.Added:
                    return T("Added", "新增");
                case AgentProjectChangeType.Deleted:
                    return T("Deleted", "删除");
                case AgentProjectChangeType.Renamed:
                    return T("Renamed", "重命名");
                case AgentProjectChangeType.Copied:
                    return T("Copied", "复制");
                case AgentProjectChangeType.Untracked:
                    return T("Untracked", "未跟踪");
                case AgentProjectChangeType.Conflicted:
                    return T("Conflicted", "冲突");
                default:
                    return T("Modified", "已修改");
            }
        }

        private static string ProjectChangeFileName(string path)
        {
            var normalizedPath = (path ?? string.Empty).Replace('\\', '/');
            var separatorIndex = normalizedPath.LastIndexOf('/');
            return separatorIndex < 0 ? normalizedPath : normalizedPath.Substring(separatorIndex + 1);
        }

        private static string ProjectChangeStatusLetter(AgentProjectChangeType changeType)
        {
            switch (changeType)
            {
                case AgentProjectChangeType.Added:
                    return "A";
                case AgentProjectChangeType.Deleted:
                    return "D";
                case AgentProjectChangeType.Renamed:
                    return "R";
                case AgentProjectChangeType.Copied:
                    return "C";
                case AgentProjectChangeType.Untracked:
                    return "U";
                case AgentProjectChangeType.Conflicted:
                    return "!";
                default:
                    return "M";
            }
        }

        private static string ProjectChangeStatusClass(AgentProjectChangeType changeType)
        {
            switch (changeType)
            {
                case AgentProjectChangeType.Added:
                case AgentProjectChangeType.Untracked:
                    return "added";
                case AgentProjectChangeType.Deleted:
                case AgentProjectChangeType.Conflicted:
                    return "deleted";
                case AgentProjectChangeType.Renamed:
                case AgentProjectChangeType.Copied:
                    return "renamed";
                default:
                    return "modified";
            }
        }

        private static Label CardTitle(string text)
        {
            var label = new Label(text ?? string.Empty);
            label.AddToClassList("afu-card__title");
            return label;
        }

        private static Label CardBody(string text)
        {
            var label = new Label(text ?? string.Empty) { enableRichText = false };
            label.AddToClassList("afu-card__body");
            return label;
        }

        private static void AddEmptyCard(VisualElement host, string text)
        {
            var empty = new Label(text);
            empty.AddToClassList("afu-diagnostic--empty");
            host.Add(empty);
        }

        private void UpdateActionAvailability()
        {
            if (_service == null || _promptField == null)
            {
                return;
            }

            var hasPrompt = !string.IsNullOrWhiteSpace(_promptField.value);
            var hasScreenshot = _service.HasScreenshotAttachments;
            _sendButton.text = _service.CanSteer ? T("Steer", "引导") : T("Send", "发送");
            _sendButton.SetEnabled((_service.CanSend || _service.CanSteer) && (hasPrompt || hasScreenshot));
            _interruptButton.SetEnabled(_service.CanInterrupt);
            _disconnectButton.SetEnabled(_service.CanDisconnect);
            _newThreadButton.SetEnabled(_service.CanStartThread);
            _refreshThreadsButton.SetEnabled(_service.CanRefreshThreads);
        }

        private void SendPrompt()
        {
            var prompt = _promptField.value == null ? string.Empty : _promptField.value.Trim();
            if ((!_service.CanSend && !_service.CanSteer) ||
                (prompt.Length == 0 && !_service.HasScreenshotAttachments))
            {
                return;
            }

            if (_service.CanSteer)
            {
                _service.Steer(prompt);
            }
            else
            {
                _service.Send(prompt);
            }
            _promptField.SetValueWithoutNotify(string.Empty);
            UpdateActionAvailability();
        }

        private void OnPromptKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.V || !evt.actionKey)
            {
                return;
            }

            if (TryAddClipboardScreenshot(false))
            {
                evt.PreventDefault();
                evt.StopImmediatePropagation();
            }
        }

        private void AddClipboardScreenshot()
        {
            TryAddClipboardScreenshot(true);
        }

        private void ShowScreenshotMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(
                new GUIContent(T("Clipboard", "剪贴板")),
                false,
                AddClipboardScreenshot);
            menu.AddItem(
                new GUIContent(T("Game View", "Game 视图")),
                false,
                AddGameViewScreenshot);

            menu.DropDown(_addScreenshotButton.worldBound);
        }

        private void AddGameViewScreenshot()
        {
            if (_service.TryAddGameViewScreenshot(out var error))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                ShowNotification(new GUIContent(error));
            }
        }

        private bool TryAddClipboardScreenshot(bool showMissingImageNotice)
        {
            if (_service.TryAddClipboardScreenshot(out var error))
            {
                return true;
            }

            if (showMissingImageNotice && !string.IsNullOrWhiteSpace(error))
            {
                ShowNotification(new GUIContent(error));
            }

            return false;
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
            if (_isRefreshing || !_reasoningField.enabledSelf)
            {
                return;
            }

            var index = _reasoningField.choices.IndexOf(change.newValue);
            if (index >= 0 && index < _reasoningEffortIds.Count)
            {
                _service.SelectedReasoningEffort = _reasoningEffortIds[index];
            }
        }

        private void OnPermissionChanged(ChangeEvent<string> change)
        {
            if (_isRefreshing || !_permissionField.enabledSelf)
            {
                return;
            }

            var index = _permissionField.choices.IndexOf(change.newValue);
            if (index >= 0 && index <= (int)AgentPermissionMode.FullAccess)
            {
                _service.PermissionMode = (AgentPermissionMode)index;
            }
        }

        private void OnLanguageChanged(ChangeEvent<string> change)
        {
            if (_isRefreshing)
            {
                return;
            }

            EditorPrefs.SetBool(LanguagePreferenceKey, change.newValue == "中文");
            InterfaceLanguageChanged?.Invoke();
            _lastThreadSignature = null;
            _lastContextSignature = null;
            _lastDiff = null;
            _lastMessagePresentationSignature = null;
            foreach (var row in _messageRows)
            {
                row.ActivitySignature = null;
            }

            RefreshFromService();
        }

        private void RefreshLanguageSelector()
        {
            _languageField.choices = new List<string> { "English", "中文" };
            _languageField.SetValueWithoutNotify(IsChinese ? "中文" : "English");
        }

        private void ApplyLocalizedStaticText()
        {
            SetText("cli-caption", "CLI", "CLI");
            SetText("project-caption", "Project", "项目");
            SetText("conversations-caption", "Conversations", "对话");
            SetText("remaining-caption", "Remaining", "剩余");
            SetText("thread-caption", "Thread", "对话");
            SetText("reconnect-button", "Reconnect", "重新连接", "Restart connection detection", "重新检测连接");
            SetText("disconnect-button", "Disconnect", "断开连接", "Stop the Codex App Server connection", "停止 Codex App Server 连接");
            SetText("new-thread-button", "New Thread", "新建对话", "Start a new project-scoped thread", "开始一个项目范围的新对话");
            SetText("refresh-threads-button", "↻", "↻", "Reload conversations for this Unity project", "重新加载此 Unity 项目的对话");
            SetText("add-selection-button", "+ Selection", "+ 选择");
            SetText("add-console-button", "+ Console", "+ 控制台");
            SetText("add-file-button", "+ File", "+ 文件");
            SetText("add-scene-button", "+ Scene", "+ 场景");
            SetText("add-git-diff-button", "+ Git Diff", "+ Git 差异");
            SetText(
                "add-screenshot-button",
                "+ Screenshot",
                "+ 截图",
                "Attach an image from the clipboard or Game view",
                "从剪贴板或 Game 视图附加图片");
            SetText("interrupt-button", "Stop", "停止");
            SetText("chat-allow-once-button", "Allow Once", "仅允许一次");
            SetText("chat-allow-session-button", "Allow Session", "本次会话允许");
            SetText("chat-decline-button", "Decline", "拒绝");
            SetText("chat-cancel-turn-button", "Cancel Turn", "取消本轮");
            _modelField.tooltip = T("Model", "模型");
            _reasoningField.tooltip = T("Reasoning effort", "推理强度");
            _languageField.tooltip = T("Interface language", "界面语言");
        }

        private void SetText(string name, string english, string chinese, string englishTooltip = null, string chineseTooltip = null)
        {
            var element = rootVisualElement.Q<TextElement>(name);
            if (element == null)
            {
                return;
            }

            element.text = T(english, chinese);
            if (englishTooltip != null)
            {
                element.tooltip = T(englishTooltip, chineseTooltip);
            }
        }

        private static IReadOnlyList<string> PermissionChoices => new[]
        {
            T("Ask for Approval", "请求批准"),
            T("Approve for Me", "帮我批准"),
            T("Full Access", "完全访问权限")
        };

        private static string PermissionLabel(AgentPermissionMode mode)
        {
            var index = (int)mode;
            return index >= 0 && index < PermissionChoices.Count
                ? PermissionChoices[index]
                : PermissionChoices[(int)AgentPermissionMode.CodexDecides];
        }

        private static string DisplayReasoningEffort(string effort)
        {
            if (!IsChinese)
            {
                return effort;
            }

            switch (effort)
            {
                case "none": return "无";
                case "minimal": return "极低";
                case "low": return "低";
                case "medium": return "中";
                case "high": return "高";
                case "xhigh": return "超高";
                case "max": return "最大";
                case "ultra": return "极致";
                default: return effort;
            }
        }

        private static string LocalizeConnectionState(string state)
        {
            if (string.IsNullOrWhiteSpace(state))
            {
                return T("Not checked", "未检查");
            }

            switch (state)
            {
                case "Ready":
                    return T("Ready", "已连接");
                case "Connecting":
                    return T("Connecting", "正在连接");
                case "Disconnected":
                    return T("Disconnected", "已断开");
                case "Faulted":
                    return T("Faulted", "连接失败");
                case "SignedOut":
                    return T("Signed out", "未登录");
                default:
                    return state;
            }
        }

        private static string LocalizeTurnState(string state, string fallback)
        {
            switch (state)
            {
                case "Idle": return T("Idle", "空闲");
                case "Starting": return T("Starting", "正在开始");
                case "Running": return T("Running", "进行中");
                case "WaitingForApproval": return T("Waiting for approval", "等待审批");
                case "WaitingForUserInput": return T("Waiting for user input", "等待用户输入");
                case "Interrupting": return T("Interrupting", "正在停止");
                case "Completed": return T("Completed", "已完成");
                case "Failed": return T("Failed", "失败");
                case "Interrupted": return T("Interrupted", "已中断");
                default: return DisplayValue(state, fallback);
            }
        }

        private static string LocalizeStatusText(string status, string fallback)
        {
            if (!IsChinese || string.IsNullOrWhiteSpace(status))
            {
                return DisplayValue(status, fallback);
            }

            switch (status)
            {
                case "Connected - thread restored": return "已连接 - 已恢复对话";
                case "Connected - active turn restored": return "已连接 - 已恢复进行中的回合";
                case "Connected - thread recovery failed": return "已连接 - 恢复对话失败";
                case "Conversation restored": return "已恢复对话";
                case "Active conversation restored": return "已恢复进行中的对话";
                case "Completed": return "已完成";
                case "Interrupted": return "已中断";
                case "Failed": return "失败";
                case "Working": return "处理中";
                case "Waiting for approval": return "等待审批";
                case "Waiting for command approval": return "等待命令审批";
                case "Waiting for file approval": return "等待文件审批";
                case "Waiting for user input": return "等待用户输入";
                default: return status;
            }
        }

        private static string PermissionDescription(AgentPermissionMode mode)
        {
            switch (mode)
            {
                case AgentPermissionMode.AskApproval:
                    return T("Ask before using the internet or accessing files outside this project.", "使用互联网或访问项目外文件前请求批准。");
                case AgentPermissionMode.FullAccess:
                    return T("Allow unrestricted file, command, and network access without approval.", "无需审批即可无限制访问文件、命令和网络。");
                default:
                    return T("Let Codex automatically review requests for additional access and ask only when needed.", "由 Codex 自动审阅额外访问请求，仅在需要时询问你。");
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

            var streaming = new Label(T("Streaming", "生成中"));
            streaming.AddToClassList("afu-message__streaming");
            header.Add(streaming);

            var copy = new Button { text = "⧉", tooltip = T("Copy message text", "复制消息文本") };
            copy.AddToClassList("afu-message__copy");
            header.Add(copy);

            var body = new VisualElement();
            body.AddToClassList("afu-message__body");

            var activityFoldout = new Foldout { value = false };
            activityFoldout.AddToClassList("afu-message-activity");
            var activityList = new VisualElement();
            activityList.AddToClassList("afu-message-activity__list");
            activityFoldout.Add(activityList);

            var attachments = new VisualElement();
            attachments.AddToClassList("afu-message__attachments");

            root.Add(header);
            root.Add(attachments);
            root.Add(body);
            root.Add(activityFoldout);
            var row = new MessageRow(root, role, streaming, attachments, body, copy, activityFoldout, activityList);
            copy.clicked += () => EditorGUIUtility.systemCopyBuffer = row.RawText ?? string.Empty;
            body.RegisterCallback<ContextualMenuPopulateEvent>(evt =>
            {
                evt.menu.AppendAction(
                    T("Copy message text", "复制消息文本"),
                    _ => EditorGUIUtility.systemCopyBuffer = row.RawText ?? string.Empty,
                    _ => string.IsNullOrEmpty(row.RawText)
                        ? DropdownMenuAction.Status.Disabled
                        : DropdownMenuAction.Status.Normal);
            });
            return row;
        }

        private static string NormalizeRole(string role)
        {
            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "agent", StringComparison.OrdinalIgnoreCase))
            {
                return T("Agent", "助手");
            }

            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                return T("User", "用户");
            }

            return string.IsNullOrWhiteSpace(role) ? T("System", "系统") : role;
        }

        private static string ShortProjectName(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return T("Project unavailable", "项目不可用");
            }

            var normalized = projectRoot.TrimEnd('/', '\\');
            var slash = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf('\\'));
            return slash >= 0 && slash < normalized.Length - 1 ? normalized.Substring(slash + 1) : normalized;
        }

        private static string LocalizeAccountUsage(string usage)
        {
            if (!IsChinese || string.IsNullOrWhiteSpace(usage))
            {
                return usage;
            }

            var lines = usage.Split(new[] { '\n' }, StringSplitOptions.None);
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = LocalizeAccountUsageLine(lines[i]);
            }

            return string.Join("\n", lines);
        }

        private static string LocalizeAccountUsageLine(string line)
        {
            var value = (line ?? string.Empty)
                .Replace(" hours", " 小时")
                .Replace(" minutes", " 分钟")
                .Replace(" week", " 周")
                .Replace("Usage", "额度")
                .Replace("Unavailable", "不可用");
            var parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[parts.Length - 1], out var day))
            {
                return value;
            }

            if (!TryGetMonth(parts[parts.Length - 2], out var month))
            {
                return value;
            }

            var englishDate = parts[parts.Length - 2] + " " + day;
            return value.Replace(englishDate, month + "月" + day + "日");
        }

        private static bool TryGetMonth(string value, out int month)
        {
            switch (value)
            {
                case "Jan": month = 1; return true;
                case "Feb": month = 2; return true;
                case "Mar": month = 3; return true;
                case "Apr": month = 4; return true;
                case "May": month = 5; return true;
                case "Jun": month = 6; return true;
                case "Jul": month = 7; return true;
                case "Aug": month = 8; return true;
                case "Sep": month = 9; return true;
                case "Oct": month = 10; return true;
                case "Nov": month = 11; return true;
                case "Dec": month = 12; return true;
                default: month = 0; return false;
            }
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

        internal static bool IsChinese => EditorPrefs.GetBool(LanguagePreferenceKey, false);

        internal static event Action InterfaceLanguageChanged;

        internal static string T(string english, string chinese)
        {
            return IsChinese ? chinese : english;
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
            var headerMetadata = Element(null, "afu-header__metadata");
            var cliMetadata = Element(null, "afu-header__metadata-item");
            cliMetadata.Add(Label("cli-caption", "CLI", "afu-header__metadata-caption"));
            cliMetadata.Add(Label("cli-version-value", string.Empty, "afu-header__metadata-value"));
            headerMetadata.Add(cliMetadata);
            var projectMetadata = Element(null, "afu-header__metadata-item");
            projectMetadata.Add(Label("project-caption", "Project", "afu-header__metadata-caption"));
            projectMetadata.Add(Label("project-value", string.Empty, "afu-header__metadata-value"));
            headerMetadata.Add(projectMetadata);
            identity.Add(headerMetadata);
            header.Add(identity);

            var headerActions = Element(null, "afu-header__actions");
            var languageField = new DropdownField { name = "language-field", tooltip = "Interface language" };
            languageField.AddToClassList("afu-select");
            languageField.AddToClassList("afu-select--language");
            headerActions.Add(languageField);
            headerActions.Add(Button("reconnect-button", "Reconnect", "Restart connection detection"));
            headerActions.Add(Button("disconnect-button", "Disconnect", "Stop the Codex App Server connection"));
            header.Add(headerActions);
            windowRoot.Add(header);

            var statusBar = Element(null, "afu-status-bar");
            statusBar.Add(Label("status-text", "Checking Codex...", "afu-status-text"));
            windowRoot.Add(statusBar);

            var workspace = Element(null, "afu-workspace");
            var conversationsPane = Element(null, "afu-conversations-pane");
            var conversationsHeader = Element(null, "afu-conversations-header");
            conversationsHeader.Add(Label("conversations-caption", "Conversations", "afu-conversations-title"));
            var conversationsActions = Element(null, "afu-conversations-actions");
            var newThread = Button("new-thread-button", "New Thread", "Start a new project-scoped thread");
            newThread.AddToClassList("afu-conversations-new");
            newThread.AddToClassList("afu-primary-button");
            conversationsActions.Add(newThread);
            var refreshThreads = Button("refresh-threads-button", "↻", "Reload conversations for this Unity project");
            refreshThreads.AddToClassList("afu-conversations-refresh");
            conversationsActions.Add(refreshThreads);
            conversationsHeader.Add(conversationsActions);
            conversationsPane.Add(conversationsHeader);
            var conversationsScroll = new ScrollView { name = "conversations-scroll" };
            conversationsScroll.AddToClassList("afu-conversations-scroll");
            conversationsScroll.Add(Element("conversations-list", "afu-conversations-list"));
            conversationsPane.Add(conversationsScroll);
            var accountSummary = Element(null, "afu-account-summary");
            accountSummary.Add(Label("account-value", string.Empty, "afu-account-summary__value"));
            accountSummary.Add(Label("remaining-caption", "Remaining", "afu-account-summary__caption"));
            accountSummary.Add(Label("account-usage-value", string.Empty, "afu-account-summary__value"));
            conversationsPane.Add(accountSummary);
            workspace.Add(conversationsPane);

            var chatPane = Element(null, "afu-chat-pane");
            var threadBar = Element(null, "afu-thread-bar");
            threadBar.Add(Label("thread-caption", "Thread", "afu-field-caption"));
            threadBar.Add(Label("thread-value", "New conversation", "afu-thread-value"));
            threadBar.Add(Label("turn-activity-indicator", string.Empty, "afu-turn-activity"));
            threadBar.Add(Label("turn-state", "Idle", "afu-turn-state"));
            chatPane.Add(threadBar);

            var messageScroll = new ScrollView { name = "messages-scroll" };
            messageScroll.AddToClassList("afu-messages");
            var messagesList = Element("messages-list", "afu-messages__list");
            messagesList.Add(Element("messages-delivery-list", "afu-messages__delivery-list"));
            messageScroll.Add(messagesList);
            chatPane.Add(messageScroll);

            var chatApprovalAlert = Element("chat-approval-alert", "afu-chat-approval-alert");
            var chatApprovalText = Element(null, "afu-chat-approval-alert__text");
            chatApprovalText.Add(Label("chat-approval-title", "Permission required", "afu-chat-approval-alert__title"));
            chatApprovalText.Add(Label("chat-approval-message", "Codex is waiting for your decision.", "afu-chat-approval-alert__message"));
            chatApprovalAlert.Add(chatApprovalText);
            var chatApprovalActions = Element("chat-approval-actions", "afu-chat-approval-alert__actions");
            var chatAllowOnceButton = Button("chat-allow-once-button", "Allow Once", "Allow only this request");
            chatAllowOnceButton.AddToClassList("afu-chat-approval-action");
            chatAllowOnceButton.AddToClassList("afu-chat-approval-action--allow");
            chatApprovalActions.Add(chatAllowOnceButton);
            var chatAllowSessionButton = Button("chat-allow-session-button", "Allow Session", "Allow matching requests for this session");
            chatAllowSessionButton.AddToClassList("afu-chat-approval-action");
            chatAllowSessionButton.AddToClassList("afu-chat-approval-action--allow");
            chatApprovalActions.Add(chatAllowSessionButton);
            var chatDeclineButton = Button("chat-decline-button", "Decline", "Decline this request");
            chatDeclineButton.AddToClassList("afu-chat-approval-action");
            chatDeclineButton.AddToClassList("afu-chat-approval-action--decline");
            chatApprovalActions.Add(chatDeclineButton);
            var chatCancelTurnButton = Button("chat-cancel-turn-button", "Cancel Turn", "Cancel the current turn");
            chatCancelTurnButton.AddToClassList("afu-chat-approval-action");
            chatCancelTurnButton.AddToClassList("afu-chat-approval-action--cancel");
            chatApprovalActions.Add(chatCancelTurnButton);
            chatApprovalAlert.Add(chatApprovalActions);
            chatPane.Add(chatApprovalAlert);

            var composer = Element(null, "afu-composer");
            var contextToolbar = Element(null, "afu-context-toolbar");
            contextToolbar.Add(Button("add-selection-button", "+ Selection", "Attach the current Unity selection"));
            contextToolbar.Add(Button("add-console-button", "+ Console", "Choose specific Console logs to attach"));
            contextToolbar.Add(Button("add-file-button", "+ File", "Attach a text file inside this project"));
            contextToolbar.Add(Button("add-scene-button", "+ Scene", "Attach the active scene summary"));
            contextToolbar.Add(Button("add-git-diff-button", "+ Git Diff", "Attach the current unstaged Git diff"));
            contextToolbar.Add(Button(
                "add-screenshot-button",
                "+ Screenshot",
                "Attach an image from the clipboard or Game view"));
            composer.Add(contextToolbar);
            composer.Add(Element("contexts-list", "afu-contexts-list"));
            var prompt = new TextField { name = "prompt-field", multiline = true };
            prompt.AddToClassList("afu-prompt");
            composer.Add(prompt);
            var composerActions = Element(null, "afu-composer__actions");
            var composerSettings = Element(null, "afu-composer__settings");
            var modelField = new DropdownField { name = "model-field", tooltip = "Model" };
            modelField.AddToClassList("afu-select");
            composerSettings.Add(modelField);
            var reasoningField = new DropdownField { name = "reasoning-field", tooltip = "Reasoning effort" };
            reasoningField.AddToClassList("afu-select");
            reasoningField.AddToClassList("afu-select--reasoning");
            composerSettings.Add(reasoningField);
            var permissionField = new DropdownField
            {
                name = "permission-field",
                tooltip = "Choose how Codex requests approval for file, command, and network access"
            };
            permissionField.AddToClassList("afu-select");
            permissionField.AddToClassList("afu-select--permission");
            composerSettings.Add(permissionField);
            composerActions.Add(composerSettings);
            var composerCommands = Element(null, "afu-composer__commands");
            composerCommands.Add(Label("context-usage-label", string.Empty, "afu-context-usage"));
            composerCommands.Add(Button("interrupt-button", "Stop", "Interrupt the active turn"));
            var send = Button("send-button", "Send", "Send this prompt to the current thread");
            send.AddToClassList("afu-primary-button");
            composerCommands.Add(send);
            composerActions.Add(composerCommands);
            composer.Add(composerActions);
            chatPane.Add(composer);
            workspace.Add(chatPane);

            var detailsPane = new ScrollView { name = "details-scroll" };
            detailsPane.AddToClassList("afu-details-pane");
            var diffFoldout = new Foldout { name = "diff-foldout", text = "Project Changes", value = true };
            diffFoldout.AddToClassList("afu-foldout");
            diffFoldout.Add(Label("project-changes-summary", string.Empty, "afu-project-changes-summary"));
            diffFoldout.Add(Element("diff-files-list", "afu-diff-files"));
            detailsPane.Add(diffFoldout);

            workspace.Add(detailsPane);
            windowRoot.Add(workspace);
            var diagnosticsButton = Button("diagnostics-button", "ⓘ", "Open Agent for Unity diagnostics");
            diagnosticsButton.AddToClassList("afu-diagnostics-launcher");
            windowRoot.Add(diagnosticsButton);
        }

        private void ApplyEssentialFallbackStyles()
        {
            _windowRoot.style.flexGrow = 1f;
            var workspace = rootVisualElement.Q<VisualElement>(className: "afu-workspace");
            var conversationsPane = rootVisualElement.Q<VisualElement>(className: "afu-conversations-pane");
            var conversationsHeader = rootVisualElement.Q<VisualElement>(className: "afu-conversations-header");
            var conversationsActions = rootVisualElement.Q<VisualElement>(className: "afu-conversations-actions");
            var chatPane = rootVisualElement.Q<VisualElement>(className: "afu-chat-pane");
            var detailsPane = rootVisualElement.Q<VisualElement>(className: "afu-details-pane");
            workspace.style.flexGrow = 1f;
            workspace.style.flexDirection = FlexDirection.Row;
            conversationsPane.style.width = 220f;
            conversationsHeader.style.minHeight = 64f;
            conversationsHeader.style.flexDirection = FlexDirection.Column;
            conversationsActions.style.flexDirection = FlexDirection.Row;
            _newThreadButton.style.flexGrow = 1f;
            _newThreadButton.style.minHeight = 24f;
            _refreshThreadsButton.style.width = 26f;
            _refreshThreadsButton.style.minWidth = 26f;
            _refreshThreadsButton.style.height = 24f;
            _refreshThreadsButton.style.minHeight = 24f;
            chatPane.style.flexGrow = 1f;
            _messagesScroll.style.flexGrow = 1f;
            _promptField.style.minHeight = 70f;
            detailsPane.style.width = 280f;
            detailsPane.style.paddingLeft = 8f;
            detailsPane.style.paddingRight = 8f;
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

        private static VisualElement CreateScreenshotPreview(
            string label,
            string path,
            Action removeAction,
            ICollection<Texture2D> textures)
        {
            var preview = new VisualElement { tooltip = label };
            preview.AddToClassList("afu-screenshot-preview");

            var texture = LoadPreviewTexture(path);
            if (texture != null)
            {
                textures.Add(texture);
                var image = new Image
                {
                    image = texture,
                    scaleMode = ScaleMode.ScaleToFit,
                    pickingMode = PickingMode.Ignore
                };
                image.AddToClassList("afu-screenshot-preview__image");
                preview.Add(image);
            }
            else
            {
                var unavailable = new Label(T("Image unavailable", "图片不可用"));
                unavailable.AddToClassList("afu-screenshot-preview__unavailable");
                preview.Add(unavailable);
            }

            if (removeAction != null)
            {
                var remove = new Button(removeAction) { text = "×", tooltip = T("Remove screenshot", "移除截图") };
                remove.AddToClassList("afu-screenshot-preview__remove");
                preview.Add(remove);
            }

            return preview;
        }

        private static Texture2D LoadPreviewTexture(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                    new FileInfo(path).Length > MaxPreviewImageBytes)
                {
                    return null;
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (ImageConversion.LoadImage(texture, File.ReadAllBytes(path), true))
                {
                    return texture;
                }

                UnityEngine.Object.DestroyImmediate(texture);
            }
            catch (Exception)
            {
                // A missing or invalid historical image falls back to an unavailable placeholder.
            }

            return null;
        }

        private void DestroyAttachmentPreviewTextures()
        {
            DestroyComposerPreviewTextures();
            DestroyMessagePreviewTextures();
        }

        private void DestroyComposerPreviewTextures()
        {
            DestroyTextures(_composerPreviewTextures);
        }

        private void DestroyMessagePreviewTextures()
        {
            foreach (var row in _messageRows)
            {
                DestroyTextures(row.PreviewTextures);
            }
        }

        private static void DestroyTextures(ICollection<Texture2D> textures)
        {
            foreach (var texture in textures)
            {
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }

            textures.Clear();
        }

        private sealed class MessageRow
        {
            internal MessageRow(
                VisualElement root,
                Label role,
                Label streaming,
                VisualElement attachments,
                VisualElement body,
                Button copyButton,
                Foldout activityFoldout,
                VisualElement activityList)
            {
                Root = root;
                Role = role;
                Streaming = streaming;
                Attachments = attachments;
                Body = body;
                CopyButton = copyButton;
                ActivityFoldout = activityFoldout;
                ActivityList = activityList;
            }

            internal VisualElement Root { get; }
            internal Label Role { get; }
            internal Label Streaming { get; }
            internal VisualElement Attachments { get; }
            internal VisualElement Body { get; }
            internal Button CopyButton { get; }
            internal Foldout ActivityFoldout { get; }
            internal VisualElement ActivityList { get; }
            internal string AttachmentSignature { get; set; }
            internal string ActivitySignature { get; set; }
            internal string RenderedText { get; set; }
            internal string RawText { get; set; }
            internal List<Texture2D> PreviewTextures { get; } = new List<Texture2D>();
        }
    }

    internal sealed class AgentForUnityConsolePickerWindow : EditorWindow
    {
        private static IReadOnlyList<string> LogTypeChoices => new[]
        {
            T("All levels", "所有级别"),
            T("Errors & Exceptions", "错误和异常"),
            T("Warnings", "警告"),
            T("Logs", "日志")
        };

        private readonly HashSet<long> _selectedIds = new HashSet<long>();
        private readonly List<AgentConsoleLogEntry> _filteredEntries = new List<AgentConsoleLogEntry>();
        private IReadOnlyList<AgentConsoleLogEntry> _entries = Array.Empty<AgentConsoleLogEntry>();
        private Action<IReadOnlyList<AgentConsoleLogEntry>> _onConfirm;
        private Button _addButton;
        private Label _selectionSummary;
        private Label _emptyLabel;
        private TextField _searchField;
        private DropdownField _logTypeField;
        private ListView _listView;

        internal static void Open(
            IReadOnlyList<AgentConsoleLogEntry> entries,
            Action<IReadOnlyList<AgentConsoleLogEntry>> onConfirm)
        {
            var window = CreateInstance<AgentForUnityConsolePickerWindow>();
            window.titleContent = new GUIContent(T("Select Console Logs", "选择控制台日志"));
            window.minSize = new Vector2(520f, 360f);
            window._entries = entries ?? Array.Empty<AgentConsoleLogEntry>();
            window._onConfirm = onConfirm;
            window.ShowUtility();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            _selectedIds.Clear();
            _filteredEntries.Clear();
            root.style.flexDirection = FlexDirection.Column;
            root.style.flexGrow = 1f;
            root.style.minHeight = 0;
            root.style.paddingTop = 10f;
            root.style.paddingRight = 10f;
            root.style.paddingBottom = 10f;
            root.style.paddingLeft = 10f;

            var title = new Label(T("Select Console logs to attach", "选择要附加的控制台日志"));
            title.style.flexShrink = 0f;
            title.style.fontSize = 14f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            root.Add(title);

            var description = new Label(T("Only checked logs and their stack traces will be sent as context.", "仅会将勾选的日志及其堆栈跟踪作为上下文发送。"));
            description.style.marginTop = 3f;
            description.style.marginBottom = 8f;
            description.style.whiteSpace = WhiteSpace.Normal;
            description.style.flexShrink = 0f;
            root.Add(description);

            var filters = new VisualElement();
            filters.style.flexDirection = FlexDirection.Row;
            filters.style.flexWrap = Wrap.Wrap;
            filters.style.alignItems = Align.FlexEnd;
            filters.style.flexShrink = 0f;
            _searchField = new TextField(T("Search message or stack trace", "搜索消息或堆栈跟踪"));
            _searchField.style.flexGrow = 1f;
            _searchField.style.minWidth = 200f;
            _searchField.style.marginRight = 8f;
            _searchField.RegisterValueChangedCallback(_ => RefreshFilters());
            filters.Add(_searchField);
            _logTypeField = new DropdownField(T("Level", "级别"), LogTypeChoices.ToList(), 0);
            _logTypeField.style.minWidth = 180f;
            _logTypeField.style.marginBottom = 3f;
            _logTypeField.RegisterValueChangedCallback(_ => RefreshFilters());
            filters.Add(_logTypeField);
            root.Add(filters);

            var toolbar = new VisualElement();
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.flexWrap = Wrap.Wrap;
            toolbar.style.alignItems = Align.Center;
            toolbar.style.marginTop = 7f;
            toolbar.style.flexShrink = 0f;
            toolbar.Add(new Button(SelectVisible) { text = T("Select Visible", "选择当前可见项") });
            var clearButton = new Button(ClearSelection) { text = T("Clear Selection", "清除选择") };
            clearButton.style.marginLeft = 5f;
            toolbar.Add(clearButton);
            _selectionSummary = new Label();
            _selectionSummary.style.flexGrow = 1f;
            _selectionSummary.style.minWidth = 150f;
            _selectionSummary.style.marginTop = 3f;
            _selectionSummary.style.unityTextAlign = TextAnchor.MiddleRight;
            toolbar.Add(_selectionSummary);
            root.Add(toolbar);

            _listView = new ListView
            {
                itemsSource = _filteredEntries,
                fixedItemHeight = 27f,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.None,
                makeItem = MakeLogRow,
                bindItem = BindLogRow
            };
            _listView.style.flexGrow = 1f;
            _listView.style.flexShrink = 1f;
            _listView.style.minHeight = 0;
            _listView.style.marginTop = 7f;
            _listView.style.marginBottom = 8f;
            _listView.style.borderTopWidth = 1f;
            _listView.style.borderRightWidth = 1f;
            _listView.style.borderBottomWidth = 1f;
            _listView.style.borderLeftWidth = 1f;
            var borderColor = new Color(0.5f, 0.5f, 0.5f, 0.35f);
            _listView.style.borderTopColor = borderColor;
            _listView.style.borderRightColor = borderColor;
            _listView.style.borderBottomColor = borderColor;
            _listView.style.borderLeftColor = borderColor;
            root.Add(_listView);

            _emptyLabel = new Label();
            _emptyLabel.style.flexGrow = 1f;
            _emptyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _emptyLabel.style.opacity = 0.62f;
            root.Add(_emptyLabel);

            var actions = new VisualElement();
            actions.style.flexShrink = 0f;
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.justifyContent = Justify.FlexEnd;
            actions.Add(new Button(Close) { text = T("Cancel", "取消") });
            _addButton = new Button(ConfirmSelection) { text = T("Add Selected", "添加已选项") };
            _addButton.style.marginLeft = 6f;
            actions.Add(_addButton);
            root.Add(actions);
            RefreshFilters();
        }

        private VisualElement MakeLogRow()
        {
            var toggle = new Toggle();
            toggle.style.height = 27f;
            toggle.style.paddingLeft = 7f;
            toggle.style.paddingRight = 7f;
            toggle.RegisterValueChangedCallback(change =>
            {
                if (!(toggle.userData is long entryId))
                {
                    return;
                }

                if (change.newValue)
                {
                    _selectedIds.Add(entryId);
                }
                else
                {
                    _selectedIds.Remove(entryId);
                }

                RefreshSelectionState();
            });
            return toggle;
        }

        private void BindLogRow(VisualElement element, int index)
        {
            var toggle = (Toggle)element;
            var entry = _filteredEntries[index];
            toggle.userData = entry.Id;
            toggle.SetValueWithoutNotify(_selectedIds.Contains(entry.Id));
            toggle.tooltip = string.IsNullOrWhiteSpace(entry.StackTrace)
                ? entry.Message
                : entry.Message + "\n\n" + entry.StackTrace;
            toggle.text = $"[{DisplayLogType(entry.Type)}] {FirstLine(entry.Message)}";
        }

        private void RefreshFilters()
        {
            var search = _searchField?.value?.Trim();
            var logType = _logTypeField?.value ?? LogTypeChoices[0];
            _filteredEntries.Clear();
            foreach (var entry in _entries)
            {
                if (MatchesType(entry.Type, logType) && MatchesSearch(entry, search))
                {
                    _filteredEntries.Add(entry);
                }
            }

            _listView.itemsSource = _filteredEntries;
            _listView.Rebuild();
            var hasResults = _filteredEntries.Count > 0;
            _listView.style.display = hasResults ? DisplayStyle.Flex : DisplayStyle.None;
            _emptyLabel.style.display = hasResults ? DisplayStyle.None : DisplayStyle.Flex;
            _emptyLabel.text = _entries.Count == 0
                ? T("Unity Console is empty.", "Unity 控制台为空。")
                : T("No logs match the current filters.", "没有日志符合当前筛选条件。");
            RefreshSelectionState();
        }

        private void SelectVisible()
        {
            foreach (var entry in _filteredEntries)
            {
                _selectedIds.Add(entry.Id);
            }

            _listView.RefreshItems();
            RefreshSelectionState();
        }

        private void ClearSelection()
        {
            _selectedIds.Clear();
            _listView.RefreshItems();
            RefreshSelectionState();
        }

        private void RefreshSelectionState()
        {
            var selectedCount = _selectedIds.Count;
            if (_selectionSummary != null)
            {
                _selectionSummary.text = AgentForUnityWindow.IsChinese
                    ? $"已选 {selectedCount} 项 · 显示 {_filteredEntries.Count} / 控制台共 {_entries.Count} 项"
                    : $"{selectedCount} selected · {_filteredEntries.Count} shown / {_entries.Count} in Console";
            }

            _addButton?.SetEnabled(selectedCount > 0);
        }

        private void ConfirmSelection()
        {
            var selected = _entries
                .Where(entry => _selectedIds.Contains(entry.Id))
                .OrderBy(entry => entry.Id)
                .ToList();
            if (selected.Count == 0)
            {
                return;
            }

            _onConfirm?.Invoke(selected);
            Close();
        }

        private static bool MatchesType(LogType type, string filter)
        {
            if (filter == T("Errors & Exceptions", "错误和异常"))
            {
                return type == LogType.Error || type == LogType.Assert || type == LogType.Exception;
            }

            if (filter == T("Warnings", "警告"))
            {
                return type == LogType.Warning;
            }

            return filter != T("Logs", "日志") || type == LogType.Log;
        }

        private static bool MatchesSearch(AgentConsoleLogEntry entry, string search)
        {
            return string.IsNullOrWhiteSpace(search) ||
                   entry.Message.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   entry.StackTrace.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DisplayLogType(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                    return T("Error", "错误");
                case LogType.Assert:
                    return T("Assert", "断言");
                case LogType.Warning:
                    return T("Warning", "警告");
                case LogType.Exception:
                    return T("Exception", "异常");
                default:
                    return T("Log", "日志");
            }
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return T("(empty message)", "（空消息）");
            }

            var normalized = value.Replace('\r', '\n');
            var lineEnd = normalized.IndexOf('\n');
            var line = lineEnd < 0 ? normalized : normalized.Substring(0, lineEnd);
            const int maximumLength = 140;
            return line.Length <= maximumLength ? line : line.Substring(0, maximumLength) + "…";
        }

        private static string T(string english, string chinese)
        {
            return AgentForUnityWindow.T(english, chinese);
        }
    }
}
