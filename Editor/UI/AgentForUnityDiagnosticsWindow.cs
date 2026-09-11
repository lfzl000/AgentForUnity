using System;
using AgentForUnity.Editor.Application;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentForUnity.Editor.UI
{
    internal sealed class AgentForUnityDiagnosticsWindow : EditorWindow
    {
        private AgentForUnityService _service;
        private VisualElement _diagnosticsList;
        private Label _title;
        private Label _summary;
        private int _lastDiagnosticsVersion = -1;

        internal static void Open()
        {
            var window = GetWindow<AgentForUnityDiagnosticsWindow>();
            window.titleContent = new GUIContent(T("Agent Diagnostics", "Agent 诊断"));
            window.minSize = new Vector2(480f, 320f);
            window.Show();
        }

        private void OnEnable()
        {
            _service = AgentForUnityService.Instance;
            _service.Changed -= RefreshFromService;
            _service.Changed += RefreshFromService;
            AgentForUnityWindow.InterfaceLanguageChanged -= RefreshLanguage;
            AgentForUnityWindow.InterfaceLanguageChanged += RefreshLanguage;
        }

        private void OnDisable()
        {
            if (_service != null)
            {
                _service.Changed -= RefreshFromService;
            }

            AgentForUnityWindow.InterfaceLanguageChanged -= RefreshLanguage;
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1f;
            root.style.minHeight = 0;
            root.style.paddingTop = 8f;
            root.style.paddingRight = 8f;
            root.style.paddingBottom = 8f;
            root.style.paddingLeft = 8f;

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            _title = new Label(T("Diagnostics", "诊断"));
            _title.style.fontSize = 14f;
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _title.style.flexGrow = 1f;
            header.Add(_title);
            _summary = new Label();
            _summary.style.opacity = 0.65f;
            header.Add(_summary);
            root.Add(header);

            var diagnosticsScroll = new ScrollView(ScrollViewMode.Vertical);
            diagnosticsScroll.style.flexGrow = 1f;
            diagnosticsScroll.style.flexShrink = 1f;
            diagnosticsScroll.style.minHeight = 0;
            diagnosticsScroll.style.marginTop = 8f;
            diagnosticsScroll.style.borderTopWidth = 1f;
            diagnosticsScroll.style.borderRightWidth = 1f;
            diagnosticsScroll.style.borderBottomWidth = 1f;
            diagnosticsScroll.style.borderLeftWidth = 1f;
            var borderColor = new Color(0.5f, 0.5f, 0.5f, 0.35f);
            diagnosticsScroll.style.borderTopColor = borderColor;
            diagnosticsScroll.style.borderRightColor = borderColor;
            diagnosticsScroll.style.borderBottomColor = borderColor;
            diagnosticsScroll.style.borderLeftColor = borderColor;
            _diagnosticsList = new VisualElement();
            _diagnosticsList.style.paddingTop = 5f;
            _diagnosticsList.style.paddingRight = 6f;
            _diagnosticsList.style.paddingBottom = 5f;
            _diagnosticsList.style.paddingLeft = 6f;
            diagnosticsScroll.Add(_diagnosticsList);
            root.Add(diagnosticsScroll);

            _lastDiagnosticsVersion = -1;
            RefreshFromService();
        }

        private void RefreshLanguage()
        {
            titleContent = new GUIContent(T("Agent Diagnostics", "Agent 诊断"));
            if (_title != null)
            {
                _title.text = T("Diagnostics", "诊断");
            }

            _lastDiagnosticsVersion = -1;
            RefreshFromService();
        }

        private void RefreshFromService()
        {
            if (_service == null || _diagnosticsList == null ||
                _lastDiagnosticsVersion == _service.DiagnosticsVersion)
            {
                return;
            }

            _lastDiagnosticsVersion = _service.DiagnosticsVersion;
            var diagnostics = _service.Diagnostics ?? Array.Empty<string>();
            _summary.text = diagnostics.Count == 0
                ? T("No diagnostics", "暂无诊断记录")
                : AgentForUnityWindow.IsChinese ? diagnostics.Count + " 条记录" : diagnostics.Count + " entries";
            _diagnosticsList.Clear();
            if (diagnostics.Count == 0)
            {
                var empty = new Label(T("No diagnostics have been recorded.", "尚未记录诊断信息。"));
                empty.style.opacity = 0.6f;
                _diagnosticsList.Add(empty);
                return;
            }

            foreach (var entry in diagnostics)
            {
                var diagnostic = new Label(LocalizeDiagnosticEntry(entry)) { enableRichText = false };
                diagnostic.tooltip = entry ?? string.Empty;
                diagnostic.style.whiteSpace = WhiteSpace.Normal;
                diagnostic.style.marginBottom = 5f;
                diagnostic.style.paddingTop = 5f;
                diagnostic.style.paddingRight = 6f;
                diagnostic.style.paddingBottom = 5f;
                diagnostic.style.paddingLeft = 6f;
                diagnostic.style.borderLeftWidth = 2f;
                diagnostic.style.borderLeftColor = new Color(0.85f, 0.6f, 0.15f, 0.9f);
                diagnostic.style.backgroundColor = new Color(0f, 0f, 0f, 0.055f);
                _diagnosticsList.Add(diagnostic);
            }
        }

        private static string T(string english, string chinese)
        {
            return AgentForUnityWindow.T(english, chinese);
        }

        private static string LocalizeDiagnosticEntry(string entry)
        {
            const string englishPrefix = "Ignored unknown notification: ";
            var value = entry ?? string.Empty;
            var prefixIndex = value.IndexOf(englishPrefix, StringComparison.Ordinal);
            if (!AgentForUnityWindow.IsChinese || prefixIndex < 0)
            {
                return value;
            }

            return value.Substring(0, prefixIndex) + "已忽略未知通知：" +
                   value.Substring(prefixIndex + englishPrefix.Length);
        }
    }
}
