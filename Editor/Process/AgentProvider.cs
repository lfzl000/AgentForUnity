using System;
using System.Threading.Tasks;
using AgentForUnity.Editor.Codex;
using AgentForUnity.Editor.Claude;

namespace AgentForUnity.Editor.Application
{
    internal sealed class AgentCliInfo
    {
        internal AgentCliInfo(string path, string version, string error)
        {
            Path = path;
            Version = version;
            Error = error;
        }

        internal string Path { get; }
        internal string Version { get; }
        internal string Error { get; }
        internal bool IsAvailable => !string.IsNullOrEmpty(Path) && string.IsNullOrEmpty(Error);
    }

    // The client currently consumes the existing App Server event envelope. A non-Codex
    // provider translates its native protocol into that envelope; it does not run Codex.
    internal interface IAgentProvider
    {
        string Id { get; }
        string DisplayName { get; }
        bool SupportsSteering { get; }
        bool SupportsCommitMessage { get; }
        Task<AgentCliInfo> DetectAsync();
        CodexAppServerClient StartClient(string executablePath, string projectRoot, out string diagnostic);
    }

    internal sealed class CodexAgentProvider : IAgentProvider
    {
        public string Id => "codex";
        public string DisplayName => "Codex";
        public bool SupportsSteering => true;
        public bool SupportsCommitMessage => true;

        public async Task<AgentCliInfo> DetectAsync()
        {
            var cli = await CodexCliLocator.DetectAsync();
            return new AgentCliInfo(cli.Path, cli.Version, cli.Error);
        }

        public CodexAppServerClient StartClient(string executablePath, string projectRoot, out string diagnostic)
        {
            var process = new CodexAppServerProcess();
            process.Start(executablePath, projectRoot);
            diagnostic = process.StartupDiagnostic;
            return new CodexAppServerClient(process);
        }
    }

    internal sealed class ClaudeCodeAgentProvider : IAgentProvider
    {
        public string Id => "claude-code";
        public string DisplayName => "Claude Code";
        public bool SupportsSteering => false;
        public bool SupportsCommitMessage => false;

        public Task<AgentCliInfo> DetectAsync() => ClaudeCodeCliLocator.DetectAsync();

        public CodexAppServerClient StartClient(string executablePath, string projectRoot, out string diagnostic)
        {
            diagnostic = "Claude Code uses local CLI authentication and permissions. " +
                         "Permission prompts and live steering are not supported by this integration yet.";
            return new CodexAppServerClient(new ClaudeCodeTransport(executablePath, projectRoot));
        }
    }

    internal static class AgentProviderRegistry
    {
        internal static IAgentProvider Create(string id)
        {
            switch (id)
            {
                case null:
                case "":
                case "codex": return new CodexAgentProvider();
                case "claude-code": return new ClaudeCodeAgentProvider();
                default: throw new ArgumentException("Unknown agent provider: " + id, nameof(id));
            }
        }
    }
}
