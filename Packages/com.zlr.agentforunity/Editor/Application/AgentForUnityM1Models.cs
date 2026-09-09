using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AgentForUnity.Editor.Application
{
    internal enum AgentActivityKind
    {
        Status,
        Plan,
        Reasoning,
        Command,
        FileChange,
        Tool
    }

    internal sealed class AgentActivityItem
    {
        internal AgentActivityItem(string id, AgentActivityKind kind, string title)
        {
            Id = id ?? Guid.NewGuid().ToString("N");
            Kind = kind;
            Title = title ?? string.Empty;
        }

        internal string Id { get; }
        internal AgentActivityKind Kind { get; }
        internal string Title { get; set; }
        internal string Body { get; set; } = string.Empty;
        internal string Status { get; set; } = "inProgress";
        internal bool IsStreaming { get; set; }
    }

    internal enum AgentContextKind
    {
        Project,
        Selection,
        Console,
        File,
        Scene,
        GitDiff,
        Screenshot
    }

    internal sealed class AgentContextItem
    {
        internal AgentContextItem(
            string id,
            AgentContextKind kind,
            string label,
            string source,
            string content,
            DateTime capturedAt,
            bool automatic = false)
        {
            Id = id ?? Guid.NewGuid().ToString("N");
            Kind = kind;
            Label = label ?? kind.ToString();
            Source = source ?? string.Empty;
            Content = content ?? string.Empty;
            CapturedAt = capturedAt;
            IsAutomatic = automatic;
        }

        internal string Id { get; }
        internal AgentContextKind Kind { get; }
        internal string Label { get; }
        internal string Source { get; }
        internal string Content { get; }
        internal DateTime CapturedAt { get; }
        internal bool IsAutomatic { get; }
        internal int CharacterCount => Content.Length;
        internal string Preview => Content.Length <= 600 ? Content : Content.Substring(0, 600) + "\n[preview truncated]";
    }

    internal sealed class AgentChatAttachment
    {
        internal AgentChatAttachment(AgentContextKind kind, string label, string source)
        {
            Kind = kind;
            Label = string.IsNullOrWhiteSpace(label) ? kind.ToString() : label;
            Source = source ?? string.Empty;
        }

        internal AgentContextKind Kind { get; }
        internal string Label { get; }
        internal string Source { get; }
    }

    internal enum AgentApprovalKind
    {
        Command,
        FileChange,
        UserInput
    }

    internal sealed class AgentUserQuestion
    {
        internal string Id { get; set; }
        internal string Header { get; set; }
        internal string Question { get; set; }
        internal bool IsSecret { get; set; }
        internal bool AllowsOther { get; set; }
        internal IReadOnlyList<string> Options { get; set; } = Array.Empty<string>();
    }

    internal sealed class AgentApprovalRequest
    {
        internal string Key { get; set; }
        internal JToken RequestId { get; set; }
        internal string Method { get; set; }
        internal string ThreadId { get; set; }
        internal string TurnId { get; set; }
        internal string ItemId { get; set; }
        internal AgentApprovalKind Kind { get; set; }
        internal string Title { get; set; }
        internal string Reason { get; set; }
        internal string Command { get; set; }
        internal string WorkingDirectory { get; set; }
        internal string Details { get; set; }
        internal IReadOnlyList<AgentUserQuestion> Questions { get; set; } = Array.Empty<AgentUserQuestion>();
        internal bool IsResolved { get; set; }
        internal bool IsResponding { get; set; }
        internal string Resolution { get; set; }
    }

    internal enum AgentCompilationState
    {
        Idle,
        Compiling,
        Passed,
        Failed
    }

    internal sealed class AgentCompilationResult
    {
        internal AgentCompilationState State { get; set; }
        internal string TurnId { get; set; }
        internal string Summary { get; set; } = "No compilation result";
        internal string Details { get; set; } = string.Empty;
        internal DateTime CompletedAt { get; set; }
        internal bool CanContinueFix => State == AgentCompilationState.Failed && !string.IsNullOrEmpty(Details);
    }
}
