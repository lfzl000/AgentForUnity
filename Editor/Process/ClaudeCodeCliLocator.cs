using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentForUnity.Editor.Application;

namespace AgentForUnity.Editor.Claude
{
    internal static class ClaudeCodeCliLocator
    {
        internal static Task<AgentCliInfo> DetectAsync() => Task.Run(Detect);

        private static AgentCliInfo Detect()
        {
            var explicitPath = Environment.GetEnvironmentVariable("CLAUDE_EXECUTABLE");
            if (!string.IsNullOrWhiteSpace(explicitPath)) return ReadVersion(explicitPath);

            var candidates = new List<string>();
            var name = Environment.OSVersion.Platform == PlatformID.Win32NT ? "claude.exe" : "claude";
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory, name));
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(userProfile, ".local", "bin", name));
            candidates.Add("/opt/homebrew/bin/claude");
            candidates.Add("/usr/local/bin/claude");
            // Unity launched by Hub does not inherit a shell's nvm PATH.
            var nvmRoot = Path.Combine(userProfile, ".nvm", "versions", "node");
            if (Directory.Exists(nvmRoot))
            {
                foreach (var directory in Directory.GetDirectories(nvmRoot).OrderByDescending(NodeVersion))
                    candidates.Add(Path.Combine(directory, "bin", "claude"));
            }

            AgentCliInfo firstError = null;
            foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
            {
                if (!File.Exists(candidate)) continue;
                var info = ReadVersion(candidate);
                if (info.IsAvailable) return info;
                if (firstError == null) firstError = info;
            }

            return firstError ?? new AgentCliInfo(null, null,
                "Claude Code CLI was not found. Install Claude Code or set CLAUDE_EXECUTABLE to its absolute executable path, then reconnect.");
        }

        private static Version NodeVersion(string directory)
        {
            return Version.TryParse(Path.GetFileName(directory).TrimStart('v'), out var version)
                ? version : new Version(0, 0);
        }

        private static AgentCliInfo ReadVersion(string executable)
        {
            try
            {
                var result = RunCommandAsync(executable, "--version", null, 5000).GetAwaiter().GetResult();
                if (result.ExitCode != 0) return new AgentCliInfo(null, null, result.Error);
                using (var reader = new StringReader(result.Output))
                    return new AgentCliInfo(executable, reader.ReadLine(), null);
            }
            catch (Exception exception)
            {
                return new AgentCliInfo(null, null, "Could not start Claude Code: " + exception.Message);
            }
        }

        internal static ProcessStartInfo CreateStartInfo(string executable, string arguments, string projectRoot)
        {
            var info = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = projectRoot ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            // npm installations invoke /usr/bin/env node from the same nvm bin directory.
            info.EnvironmentVariables["PATH"] = Path.GetDirectoryName(Path.GetFullPath(executable)) +
                Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            return info;
        }

        internal static async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(
            string executable, string arguments, string projectRoot, int timeoutMs)
        {
            using (var process = new Process { StartInfo = CreateStartInfo(executable, arguments, projectRoot) })
            {
                if (!process.Start()) throw new IOException("Could not start Claude Code.");
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!await Task.Run(() => process.WaitForExit(timeoutMs)).ConfigureAwait(false))
                {
                    process.Kill();
                    throw new TimeoutException("Claude Code command timed out: " + arguments);
                }
                return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
            }
        }

        // ProcessStartInfo.Arguments is parsed by the runtime, never by a shell.
        internal static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value ?? string.Empty)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
