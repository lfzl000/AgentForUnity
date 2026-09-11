using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AgentForUnity.Editor.Application
{
    internal sealed class AgentConsoleLogEntry
    {
        internal AgentConsoleLogEntry(long id, string message, string stackTrace, LogType type)
        {
            Id = id;
            Message = message ?? string.Empty;
            StackTrace = stackTrace ?? string.Empty;
            Type = type;
        }

        internal long Id { get; }
        internal string Message { get; }
        internal string StackTrace { get; }
        internal LogType Type { get; }
    }

    [InitializeOnLoad]
    internal static class AgentForUnityContextCollector
    {
        private const int MaxConsoleEntries = 5000;
        private const int MaxConsoleMessageCharacters = 4 * 1024;
        private const int MaxConsoleStackTraceCharacters = 12 * 1024;
        private const int MaxContextCharacters = 128 * 1024;
        private static readonly object ConsoleLock = new object();
        private static readonly Queue<AgentConsoleLogEntry> ConsoleEntries = new Queue<AgentConsoleLogEntry>();
        private static long _nextConsoleEntryId;
        private static readonly Regex SecretAssignment = new Regex(
            @"(?im)(api[_-]?key|access[_-]?token|authorization|password|secret)\s*[:=]\s*([^\s,;]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        static AgentForUnityContextCollector()
        {
            UnityEngine.Application.logMessageReceivedThreaded -= CaptureLog;
            UnityEngine.Application.logMessageReceivedThreaded += CaptureLog;
        }

        internal static AgentContextItem CaptureProject(string projectRoot, bool automatic)
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Where(package => package != null &&
                                  (package.name.StartsWith("com.unity.", StringComparison.Ordinal) ||
                                   package.name.StartsWith("com.zlr.", StringComparison.Ordinal)))
                .OrderBy(package => package.name, StringComparer.Ordinal)
                .Take(40)
                .Select(package => $"- {package.name} {package.version}");
            var content = new StringBuilder()
                .AppendLine("Unity project environment")
                .AppendLine($"Unity: {UnityEngine.Application.unityVersion}")
                .AppendLine($"Project: {NormalizePath(projectRoot)}")
                .AppendLine($"Build target: {EditorUserBuildSettings.activeBuildTarget}")
                .AppendLine("Packages:")
                .Append(string.Join("\n", packages))
                .ToString();
            return Create(AgentContextKind.Project, "Project", projectRoot, content, automatic);
        }

        internal static AgentContextItem CaptureScene(string projectRoot)
        {
            var scene = SceneManager.GetActiveScene();
            var path = string.IsNullOrEmpty(scene.path) ? "Unsaved" : ToProjectRelative(projectRoot, scene.path);
            var content = $"Active scene\nName: {scene.name}\nPath: {path}\nLoaded: {scene.isLoaded}\nDirty: {scene.isDirty}\nRoot objects: {scene.rootCount}";
            return Create(AgentContextKind.Scene, "Scene · " + scene.name, path, content, false);
        }

        internal static AgentContextItem CaptureSelection(string projectRoot)
        {
            return CaptureSelection(projectRoot, Selection.objects);
        }

        internal static AgentContextItem CaptureSelection(string projectRoot, Object[] selection)
        {
            if (selection == null || selection.Length == 0)
            {
                throw new InvalidOperationException("Nothing is selected in the Unity Editor.");
            }

            var content = new StringBuilder("Unity selection");
            foreach (var selected in selection.Take(30))
            {
                AppendSelectedObject(content, projectRoot, selected);
            }

            if (selection.Length > 30)
            {
                content.AppendLine().Append($"... {selection.Length - 30} additional objects omitted");
            }

            var selectedNames = string.Join(", ", selection.Take(2).Select(item => item == null ? "Missing" : item.name));
            var additionalCount = selection.Length - Math.Min(selection.Length, 2);
            var label = "Selection · " + selectedNames + (additionalCount > 0 ? $" +{additionalCount}" : string.Empty);
            return Create(AgentContextKind.Selection, label, "Unity Selection", content.ToString(), false);
        }

        internal static IReadOnlyList<AgentConsoleLogEntry> GetConsoleEntries()
        {
            if (TryGetUnityConsoleEntries(out var entries))
            {
                return entries;
            }

            lock (ConsoleLock)
            {
                return ConsoleEntries.Reverse().ToList();
            }
        }

        internal static AgentContextItem CaptureConsole(IReadOnlyList<AgentConsoleLogEntry> selectedEntries)
        {
            if (selectedEntries == null || selectedEntries.Count == 0)
            {
                throw new InvalidOperationException("Select at least one Console log to attach.");
            }

            var entries = selectedEntries
                .Where(entry => entry != null)
                .GroupBy(entry => entry.Id)
                .Select(group => group.First())
                .OrderBy(entry => entry.Id)
                .ToList();
            if (entries.Count == 0)
            {
                throw new InvalidOperationException("Select at least one Console log to attach.");
            }

            var content = new StringBuilder("Selected Unity Console messages");
            foreach (var entry in entries)
            {
                content.AppendLine()
                    .Append('[').Append(entry.Type).Append("] ").AppendLine(entry.Message);
                if (!string.IsNullOrWhiteSpace(entry.StackTrace))
                {
                    content.AppendLine(entry.StackTrace);
                }
            }

            var firstLine = Redact(entries[0].Message).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? entries[0].Type.ToString();
            if (firstLine.Length > 60)
                firstLine = firstLine.Substring(0, 60) + "…";
            var label = "Console · " + firstLine + (entries.Count > 1 ? $" +{entries.Count - 1}" : string.Empty);
            return Create(AgentContextKind.Console, label, "Unity Console", content.ToString(), false);
        }

        internal static AgentContextItem CaptureFile(string projectRoot, string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                throw new ArgumentException("A file path is required.", nameof(absolutePath));
            }

            var normalizedRoot = NormalizePath(projectRoot).TrimEnd('/') + "/";
            var normalizedPath = NormalizePath(absolutePath);
            var comparison = UnityEngine.Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!normalizedPath.StartsWith(normalizedRoot, comparison))
            {
                throw new InvalidOperationException("Only files inside the current Unity project can be attached.");
            }

            var file = new FileInfo(absolutePath);
            if (!file.Exists)
            {
                throw new FileNotFoundException("The selected file does not exist.", absolutePath);
            }

            if (file.Length > MaxContextCharacters)
            {
                throw new InvalidOperationException("The selected file exceeds the 128 KiB context limit.");
            }

            var bytes = File.ReadAllBytes(absolutePath);
            if (bytes.Take(Math.Min(bytes.Length, 8192)).Any(value => value == 0))
            {
                throw new InvalidOperationException("Binary files cannot be attached as text context.");
            }

            var relativePath = ToProjectRelative(projectRoot, normalizedPath);
            var text = Encoding.UTF8.GetString(bytes);
            var content = $"File: {relativePath}\n```\n{text}\n```";
            return Create(AgentContextKind.File, Path.GetFileName(relativePath), relativePath, content, false);
        }

        internal static AgentContextItem CaptureGitDiff(string projectRoot)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "diff --no-ext-diff -- .",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("Could not start Git.");
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(3000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                        // The process may have exited between the timeout and the kill request.
                    }

                    throw new TimeoutException("Git diff did not finish within three seconds.");
                }

                var output = outputTask.GetAwaiter().GetResult();
                var error = errorTask.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Git diff failed." : error.Trim());
                }

                if (string.IsNullOrWhiteSpace(output))
                {
                    throw new InvalidOperationException("The project has no unstaged Git diff.");
                }

                if (output.Length > MaxContextCharacters)
                {
                    throw new InvalidOperationException("The Git diff exceeds the 128 KiB context limit.");
                }

                return Create(AgentContextKind.GitDiff, "Git Diff", "git diff", "Current Git diff\n" + output, false);
            }
        }

        internal static AgentProjectChangesSnapshot GetProjectChanges(string projectRoot)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "status --porcelain=v1 -z --branch --untracked-files=all",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Keep the non-repository diagnostic stable across the Editor's locale.
            startInfo.EnvironmentVariables["LC_ALL"] = "C";
            foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE" })
                startInfo.EnvironmentVariables.Remove(name);

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("Could not start Git.");
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(3000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                        // The process may have exited between the timeout and the kill request.
                    }

                    throw new TimeoutException("Git status did not finish within three seconds.");
                }

                var output = outputTask.GetAwaiter().GetResult();
                var error = errorTask.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                {
                    if (error.IndexOf("not a git repository", StringComparison.OrdinalIgnoreCase) >= 0)
                        return new AgentProjectChangesSnapshot(string.Empty, Array.Empty<AgentProjectChange>(), AgentGitAvailability.NotRepository);
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Git status failed." : error.Trim());
                }

                return ParseProjectChanges(output);
            }
        }

        private static AgentProjectChangesSnapshot ParseProjectChanges(string output)
        {
            var changes = new List<AgentProjectChange>();
            var records = (output ?? string.Empty).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            var branch = string.Empty;
            var firstChangeRecordIndex = 0;
            if (records.Length > 0 && records[0].StartsWith("## ", StringComparison.Ordinal))
            {
                branch = ParseProjectBranch(records[0]);
                firstChangeRecordIndex = 1;
            }

            for (var index = firstChangeRecordIndex; index < records.Length; index++)
            {
                var record = records[index];
                if (record.Length < 3 || record[2] != ' ')
                {
                    continue;
                }

                var indexStatus = record[0];
                var workTreeStatus = record[1];
                if (indexStatus == '!' && workTreeStatus == '!')
                {
                    continue;
                }

                changes.Add(new AgentProjectChange(
                    record.Substring(3),
                    GetProjectChangeType(indexStatus, workTreeStatus)));
                if (indexStatus == 'R' || indexStatus == 'C')
                {
                    index++;
                }
            }

            return new AgentProjectChangesSnapshot(
                branch,
                changes.OrderBy(change => change.Path, StringComparer.Ordinal).ToList());
        }

        private static string ParseProjectBranch(string branchRecord)
        {
            var branchStatus = branchRecord.Substring(3).Trim();
            if (branchStatus.StartsWith("No commits yet on ", StringComparison.Ordinal))
            {
                return branchStatus.Substring("No commits yet on ".Length);
            }

            if (branchStatus.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                return "HEAD";
            }

            var upstreamSeparatorIndex = branchStatus.IndexOf("...", StringComparison.Ordinal);
            var statusSuffixIndex = branchStatus.IndexOf(' ');
            var branchEndIndex = upstreamSeparatorIndex >= 0
                ? upstreamSeparatorIndex
                : statusSuffixIndex >= 0 ? statusSuffixIndex : branchStatus.Length;
            return branchStatus.Substring(0, branchEndIndex);
        }

        private static AgentProjectChangeType GetProjectChangeType(char indexStatus, char workTreeStatus)
        {
            if (indexStatus == '?' && workTreeStatus == '?')
            {
                return AgentProjectChangeType.Untracked;
            }

            if (indexStatus == 'U' || workTreeStatus == 'U')
            {
                return AgentProjectChangeType.Conflicted;
            }

            if (indexStatus == 'R' || workTreeStatus == 'R')
            {
                return AgentProjectChangeType.Renamed;
            }

            if (indexStatus == 'C' || workTreeStatus == 'C')
            {
                return AgentProjectChangeType.Copied;
            }

            if (indexStatus == 'A' || workTreeStatus == 'A')
            {
                return AgentProjectChangeType.Added;
            }

            if (indexStatus == 'D' || workTreeStatus == 'D')
            {
                return AgentProjectChangeType.Deleted;
            }

            return AgentProjectChangeType.Modified;
        }

        internal static string Redact(string value)
        {
            return string.IsNullOrEmpty(value)
                ? string.Empty
                : SecretAssignment.Replace(value, match => match.Groups[1].Value + "=[REDACTED]");
        }

        internal static string ComputeHash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static AgentContextItem Create(
            AgentContextKind kind,
            string label,
            string source,
            string content,
            bool automatic)
        {
            var redacted = Redact(content);
            if (redacted.Length > MaxContextCharacters)
            {
                throw new InvalidOperationException($"{label} context exceeds the 128 KiB limit.");
            }

            return new AgentContextItem(
                ComputeHash(kind + "\n" + redacted),
                kind,
                label,
                source,
                redacted,
                DateTime.Now,
                automatic);
        }

        private static void AppendSelectedObject(StringBuilder output, string projectRoot, Object selected)
        {
            if (selected == null)
            {
                return;
            }

            output.AppendLine().Append("- ").Append(selected.name).Append(" (").Append(selected.GetType().FullName).Append(')');
#if UNITY_6000_3_OR_NEWER
            output.AppendLine().Append("  Entity ID: ").Append(selected.GetEntityId().ToString());
#else
            output.AppendLine().Append("  Instance ID: ").Append(selected.GetInstanceID());
#endif
            output.AppendLine().Append("  Global Object ID: ").Append(GlobalObjectId.GetGlobalObjectIdSlow(selected));
            var assetPath = AssetDatabase.GetAssetPath(selected);
            if (!string.IsNullOrEmpty(assetPath))
            {
                output.AppendLine().Append("  Asset: ").Append(ToProjectRelative(projectRoot, assetPath));
                output.AppendLine().Append("  GUID: ").Append(AssetDatabase.AssetPathToGUID(assetPath));
            }

            var gameObject = selected as GameObject;
            if (selected is Component component)
            {
                gameObject = component.gameObject;
            }

            if (gameObject == null)
            {
                return;
            }

            output.AppendLine().Append("  Hierarchy: ").Append(GetHierarchyPath(gameObject.transform));
            if (gameObject.scene.IsValid())
            {
                output.AppendLine().Append("  Scene: ").Append(string.IsNullOrEmpty(gameObject.scene.path)
                    ? gameObject.scene.name + " (unsaved)"
                    : gameObject.scene.path);
            }
            output.AppendLine().Append("  Active: ").Append(gameObject.activeSelf);
            output.AppendLine().Append("  Components: ").Append(string.Join(", ", gameObject.GetComponents<Component>()
                .Where(value => value != null)
                .Select(value => value.GetType().FullName)));
        }

        private static string GetHierarchyPath(Transform transform)
        {
            var segments = new Stack<string>();
            while (transform != null)
            {
                segments.Push(transform.name);
                transform = transform.parent;
            }

            return string.Join("/", segments);
        }

        private static string ToProjectRelative(string projectRoot, string path)
        {
            var normalizedRoot = NormalizePath(projectRoot).TrimEnd('/') + "/";
            var normalizedPath = NormalizePath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? normalizedPath.Substring(normalizedRoot.Length)
                : normalizedPath;
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path ?? string.Empty).Replace('\\', '/');
        }

        private static void CaptureLog(string condition, string stackTrace, LogType type)
        {
            lock (ConsoleLock)
            {
                ConsoleEntries.Enqueue(new AgentConsoleLogEntry(
                    ++_nextConsoleEntryId,
                    LimitConsoleText(Redact(condition), MaxConsoleMessageCharacters),
                    LimitConsoleText(Redact(stackTrace), MaxConsoleStackTraceCharacters),
                    type));
                while (ConsoleEntries.Count > MaxConsoleEntries)
                {
                    ConsoleEntries.Dequeue();
                }
            }
        }

        private static bool TryGetUnityConsoleEntries(out IReadOnlyList<AgentConsoleLogEntry> entries)
        {
            entries = null;
            try
            {
                var logEntriesType = FindUnityEditorType("UnityEditor.LogEntries");
                var logEntryType = FindUnityEditorType("UnityEditor.LogEntry");
                if (logEntriesType == null || logEntryType == null)
                {
                    return false;
                }

                const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var startGettingEntries = logEntriesType.GetMethod("StartGettingEntries", staticFlags);
                var endGettingEntries = logEntriesType.GetMethod("EndGettingEntries", staticFlags);
                var getEntry = logEntriesType.GetMethod("GetEntryInternal", staticFlags);
                if (startGettingEntries == null || endGettingEntries == null || getEntry == null ||
                    (logEntryType.GetField("message", instanceFlags) == null &&
                     logEntryType.GetField("condition", instanceFlags) == null))
                {
                    return false;
                }

                var entryCount = startGettingEntries.Invoke(null, null);
                try
                {
                    // This count uses the same filtered/collapsed rows as GetEntryInternal.
                    var count = Convert.ToInt32(entryCount);
                    var snapshot = new List<AgentConsoleLogEntry>(Math.Min(count, MaxConsoleEntries));
                    for (var index = Math.Max(0, count - MaxConsoleEntries); index < count; index++)
                    {
                        var arguments = new[] { (object)index, Activator.CreateInstance(logEntryType) };
                        if (getEntry.Invoke(null, arguments) is bool found && found)
                            snapshot.Add(ReadUnityConsoleEntry(arguments[1], index + 1L));
                    }

                    entries = snapshot.AsEnumerable().Reverse().ToList();
                    return true;
                }
                finally
                {
                    endGettingEntries.Invoke(null, null);
                }
            }
            catch (Exception)
            {
                entries = null;
                return false;
            }
        }

        internal static AgentConsoleLogEntry ReadUnityConsoleEntry(object entry, long id)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = entry.GetType();
            var message = (type.GetField("message", flags) ?? type.GetField("condition", flags))?.GetValue(entry) as string;
            var trace = type.GetField("stackTrace", flags)?.GetValue(entry) as string;
            var stackStart = type.GetField("callstackTextStartUTF16", flags)?.GetValue(entry);
            if (trace == null && message != null && stackStart is int start && start > 0 && start < message.Length)
            {
                trace = message.Substring(start);
                message = message.Substring(0, start).TrimEnd();
            }

            return new AgentConsoleLogEntry(id,
                LimitConsoleText(Redact(message), MaxConsoleMessageCharacters),
                LimitConsoleText(Redact(trace), MaxConsoleStackTraceCharacters),
                GetConsoleLogType(type.GetField("mode", flags)?.GetValue(entry)));
        }

        private static LogType GetConsoleLogType(object mode)
        {
            var modeValue = mode == null ? 0 : Convert.ToInt32(mode);
            const int warningFlags = (1 << 7) | (1 << 9) | (1 << 12);
            const int exceptionFlags = 1 << 17;
            const int assertionFlags = (1 << 1) | (1 << 21);
            const int errorFlags = (1 << 0) | (1 << 4) | (1 << 6) | (1 << 8) | (1 << 11);
            if ((modeValue & exceptionFlags) != 0)
            {
                return LogType.Exception;
            }

            if ((modeValue & assertionFlags) != 0)
            {
                return LogType.Assert;
            }

            if ((modeValue & errorFlags) != 0)
            {
                return LogType.Error;
            }

            return (modeValue & warningFlags) != 0 ? LogType.Warning : LogType.Log;
        }

        private static Type FindUnityEditorType(string typeName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(typeName, false))
                .FirstOrDefault(type => type != null);
        }

        private static string LimitConsoleText(string value, int maximumCharacters)
        {
            value = value ?? string.Empty;
            return value.Length <= maximumCharacters
                ? value
                : value.Substring(0, maximumCharacters) + "\n[captured text truncated]";
        }
    }
}
