using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AgentForUnity.Editor.Application;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace AgentForUnity.Editor.UI
{
    [InitializeOnLoad]
    internal static class AgentForUnityContextMenus
    {
        private const string EnglishAddLabel = "Add to AgentForUnity";
        private const string ChineseAddLabel = "添加到 AgentForUnity";
        private static string AddLabel => AgentForUnityWindow.T(EnglishAddLabel, ChineseAddLabel);
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type ConsoleType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ConsoleWindow");
        private static IMGUIContainer _consoleContainer;
        private static Action _originalConsoleGui;
        private static Action _consoleGuiHandler;
        private static EditorWindow _consoleWindow;
        private static readonly MethodInfo RebuildMenus = typeof(Menu).GetMethod("RebuildAllMenus", StaticFlags);
        private static readonly MethodInfo RemoveMenu = typeof(Menu).GetMethod("RemoveMenuItem", StaticFlags);
        private static readonly MethodInfo MenuExists = typeof(Menu).GetMethod("MenuItemExists", StaticFlags);
        private static readonly EventInfo MenusChanged = typeof(Menu).GetEvent("menuChanged", StaticFlags);
        private static readonly Action MenusChangedHandler = QueueMenuFilter;
        private static bool _menuRefreshQueued;
        private static bool _rebuildMenusPending;
        private static bool _refreshingMenus;

        static AgentForUnityContextMenus()
        {
            EditorApplication.update += AttachConsoleMenu;
            AgentForUnityWindow.InterfaceLanguageChanged += RefreshMenuLanguage;
            MenusChanged?.GetAddMethod(true)?.Invoke(null, new object[] { MenusChangedHandler });
            RefreshMenuLanguage();
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        [MenuItem("GameObject/" + EnglishAddLabel, false, 49)]
        [MenuItem("GameObject/" + ChineseAddLabel, false, 49)]
        private static void AddHierarchySelection(MenuCommand command)
        {
            AddObjects(GetContextObjects(command));
        }

        [MenuItem("GameObject/" + EnglishAddLabel, true)]
        [MenuItem("GameObject/" + ChineseAddLabel, true)]
        private static bool CanAddHierarchySelection()
        {
            return Selection.gameObjects.Length > 0;
        }

        [MenuItem("Assets/" + EnglishAddLabel, false, 2000)]
        [MenuItem("Assets/" + ChineseAddLabel, false, 2000)]
        private static void AddProjectSelection()
        {
            AddObjects(Selection.objects.Where(EditorUtility.IsPersistent).ToArray());
        }

        [MenuItem("Assets/" + EnglishAddLabel, true)]
        [MenuItem("Assets/" + ChineseAddLabel, true)]
        private static bool CanAddProjectSelection()
        {
            return Selection.objects.Any(EditorUtility.IsPersistent);
        }

        [MenuItem("CONTEXT/Component/" + EnglishAddLabel)]
        [MenuItem("CONTEXT/Component/" + ChineseAddLabel)]
        private static void AddComponent(MenuCommand command)
        {
            AddObjects(new[] { command.context });
        }

        private static void RefreshMenuLanguage()
        {
            _rebuildMenusPending = true;
            QueueMenuFilter();
        }

        private static void QueueMenuFilter()
        {
            if (_refreshingMenus || _menuRefreshQueued)
                return;
            _menuRefreshQueued = true;
            EditorApplication.delayCall += ApplyMenuLanguage;
        }

        private static void ApplyMenuLanguage()
        {
            _menuRefreshQueued = false;
            _refreshingMenus = true;
            try
            {
                // Keep attribute-backed commands so Unity still passes the clicked MenuCommand.context.
                // Rebuild restores the previously hidden language; remove only our inactive variants.
                if (_rebuildMenusPending)
                {
                    _rebuildMenusPending = false;
                    RebuildMenus?.Invoke(null, null);
                }

                var hiddenLabel = AgentForUnityWindow.IsChinese ? EnglishAddLabel : ChineseAddLabel;
                foreach (var prefix in new[] { "GameObject/", "Assets/", "CONTEXT/Component/" })
                {
                    var path = prefix + hiddenLabel;
                    if (MenuExists?.Invoke(null, new object[] { path }) is bool exists && exists)
                        RemoveMenu?.Invoke(null, new object[] { path });
                }
            }
            finally
            {
                _refreshingMenus = false;
            }
        }

        private static Object[] GetContextObjects(MenuCommand command)
        {
            var selected = Selection.objects;
            // Right-clicking an unselected object must not attach the previous selection.
            return command.context != null && !selected.Contains(command.context)
                ? new[] { command.context }
                : selected;
        }

        private static void AddObjects(Object[] objects)
        {
            // Snapshot before opening/focusing the Agent window can change Editor selection.
            if (AgentForUnityService.Instance.TryAddSelectionContext(objects, out var error))
                AgentForUnityWindow.OpenForContext();
            else
                EditorUtility.DisplayDialog("Agent for Unity", error, "OK");
        }

        private static void AddLogs(IReadOnlyList<AgentConsoleLogEntry> entries)
        {
            if (AgentForUnityService.Instance.TryAddConsoleContext(entries, out var error))
                AgentForUnityWindow.OpenForContext();
            else
                EditorUtility.DisplayDialog("Agent for Unity", error, "OK");
        }

        private static void AttachConsoleMenu()
        {
            var window = ConsoleType?.GetField("ms_ConsoleWindow", StaticFlags)?.GetValue(null) as EditorWindow;
            var panel = window == null ? null : window.rootVisualElement.panel;
            // The backend's main IMGUI container is a sibling of the window's rootVisualElement.
            // Dockarea identifies the host container, excluding notification/overlay IMGUI containers.
            var container = panel?.visualTree.Children().OfType<IMGUIContainer>()
                .FirstOrDefault(item => item.viewDataKey == "Dockarea");
            if (container == _consoleContainer && window == _consoleWindow &&
                (container == null || container.onGUIHandler == _consoleGuiHandler))
                return;

            DetachConsoleMenu();
            if (container == null || container.onGUIHandler == null)
                return;

            _consoleWindow = window;
            _consoleContainer = container;
            _originalConsoleGui = container.onGUIHandler;
            // Capture the original delegate locally so backend replacement cannot create recursive wrappers.
            var original = _originalConsoleGui;
            _consoleGuiHandler = () => OnConsoleGui(window, container, original);
            container.onGUIHandler = _consoleGuiHandler;
        }

        private static void OnConsoleGui(EditorWindow window, IMGUIContainer container, Action original)
        {
            var evt = Event.current;
            if (window != null && EditorWindow.mouseOverWindow == window && evt != null &&
                evt.type == EventType.MouseDown && (evt.button == 1 ||
                (UnityEngine.Application.platform == RuntimePlatform.OSXEditor && evt.button == 0 && evt.control)))
            {
                var clientRect = window.rootVisualElement.worldBound;
                var pointer = container.LocalToWorld(evt.mousePosition);
                // Leave dock tabs and Console toolbar controls to Unity.
                clientRect.yMin += EditorStyles.toolbar.fixedHeight;
                if (clientRect.Contains(pointer))
                {
                    var entries = ReadSelectedLogs(window, out var error);
                    var menu = new GenericMenu();
                    if (entries.Count > 0)
                    {
                        menu.AddItem(new GUIContent(AgentForUnityWindow.T("Copy", "复制")), false, () => CopyLogs(entries));
                        menu.AddSeparator(string.Empty);
                        menu.AddItem(new GUIContent(AddLabel), false, () => AddLogs(entries));
                    }
                    else
                    {
                        menu.AddDisabledItem(new GUIContent(AddLabel +
                            AgentForUnityWindow.T(" (select a log first)", "（请先选中日志）")));
                        if (!string.IsNullOrEmpty(error))
                            menu.AddDisabledItem(new GUIContent(AgentForUnityWindow.T("Could not read logs: ", "无法读取日志：") + error));
                    }

                    // Snapshot and release the log lock before opening the menu or the Agent window.
                    menu.ShowAsContext();
                    evt.Use();
                }
            }

            original();
        }

        private static List<AgentConsoleLogEntry> ReadSelectedLogs(EditorWindow window, out string error)
        {
            error = null;
            var result = new List<AgentConsoleLogEntry>();
            if (window == null)
                return result;

            try
            {
                var state = ConsoleType.GetField("m_ListView", InstanceFlags)?.GetValue(window);
                if (state == null)
                    return result;
                var stateType = state.GetType();
                var selected = stateType.GetField("selectedItems", InstanceFlags)?.GetValue(state) as bool[];
                var row = stateType.GetField("row", InstanceFlags)?.GetValue(state);
                // Clear/filter operations invalidate row before selectedItems is necessarily reset.
                if (!(row is int currentRow) || currentRow < 0)
                    return result;
                var rows = selected == null
                    ? new List<int>()
                    : Enumerable.Range(0, selected.Length).Where(index => selected[index]).ToList();
                if (rows.Count == 0)
                    rows.Add(currentRow);

                var entriesType = ConsoleType.Assembly.GetType("UnityEditor.LogEntries");
                var entryType = ConsoleType.Assembly.GetType("UnityEditor.LogEntry");
                var start = entriesType?.GetMethod("StartGettingEntries", StaticFlags);
                var end = entriesType?.GetMethod("EndGettingEntries", StaticFlags);
                var getEntry = entriesType?.GetMethod("GetEntryInternal", StaticFlags);
                if (entryType == null || start == null || end == null || getEntry == null)
                    return result;

                var count = Convert.ToInt32(start.Invoke(null, null));
                try
                {
                    foreach (var index in rows)
                    {
                        if (index >= count)
                            continue;
                        var arguments = new[] { (object)index, Activator.CreateInstance(entryType) };
                        if (getEntry.Invoke(null, arguments) is bool found && found)
                            result.Add(AgentForUnityContextCollector.ReadUnityConsoleEntry(arguments[1], index + 1L));
                    }
                }
                finally
                {
                    end.Invoke(null, null);
                }
            }
            catch (Exception exception)
            {
                // Console internals vary between Editor versions. Never substitute unrelated recent logs.
                result.Clear();
                error = exception.GetBaseException().Message;
            }

            return result;
        }

        private static void CopyLogs(IEnumerable<AgentConsoleLogEntry> entries)
        {
            EditorGUIUtility.systemCopyBuffer = string.Join("\n\n",
                entries.Select(entry => entry.Message + "\n" + entry.StackTrace));
        }

        private static void DetachConsoleMenu()
        {
            // A docked host is shared with other tabs. Restore only the handler installed by this class.
            if (_consoleContainer != null && _consoleContainer.onGUIHandler == _consoleGuiHandler)
                _consoleContainer.onGUIHandler = _originalConsoleGui;
            _consoleWindow = null;
            _consoleContainer = null;
            _originalConsoleGui = null;
            _consoleGuiHandler = null;
        }

        private static void Shutdown()
        {
            EditorApplication.update -= AttachConsoleMenu;
            AgentForUnityWindow.InterfaceLanguageChanged -= RefreshMenuLanguage;
            MenusChanged?.GetRemoveMethod(true)?.Invoke(null, new object[] { MenusChangedHandler });
            EditorApplication.delayCall -= ApplyMenuLanguage;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            EditorApplication.quitting -= Shutdown;
            DetachConsoleMenu();
        }
    }
}
