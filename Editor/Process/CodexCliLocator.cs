using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AgentForUnity.Editor.Codex
{
    internal sealed class CodexCliInfo
    {
        internal CodexCliInfo(string path, string version, string error)
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

    internal static class CodexCliLocator
    {
        private static readonly Version MinimumSupportedVersion = new Version(0, 144, 0);
        private static readonly Regex VersionPattern = new Regex(
            @"(?<!\d)(\d+)\.(\d+)\.(\d+)(?!\d)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly string[] MacFallbackPaths =
        {
            "/Applications/ChatGPT.app/Contents/Resources/codex",
            "/opt/homebrew/bin/codex",
            "/usr/local/bin/codex",
            "/usr/bin/codex"
        };

        internal static Task<CodexCliInfo> DetectAsync()
        {
            return Task.Run(Detect);
        }

        private static CodexCliInfo Detect()
        {
            var candidates = new List<string>();
            CodexCliInfo firstError = null;
            CodexCliInfo newestAvailable = null;
            Version newestVersion = null;
            AddCandidate(candidates, Environment.GetEnvironmentVariable("CODEX_EXECUTABLE"));

            var executableName = Environment.OSVersion.Platform == PlatformID.Win32NT ? "codex.exe" : "codex";
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(PathSeparator(), StringSplitOptions.RemoveEmptyEntries))
            {
                AddCandidate(candidates, System.IO.Path.Combine(directory.Trim(), executableName));
            }

            foreach (var fallback in MacFallbackPaths)
            {
                AddCandidate(candidates, fallback);
            }

            foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var versionResult = ReadVersion(candidate);
                if (versionResult.IsAvailable)
                {
                    if (TryParseVersion(versionResult.Version, out var version) &&
                        (newestVersion == null || version.CompareTo(newestVersion) > 0))
                    {
                        newestAvailable = versionResult;
                        newestVersion = version;
                    }

                    continue;
                }

                if (firstError == null)
                {
                    firstError = versionResult;
                }
            }

            return newestAvailable ?? firstError ?? new CodexCliInfo(
                null,
                null,
                "Codex CLI was not found. Install Codex, or set CODEX_EXECUTABLE to its absolute path, then reconnect.");
        }

        private static CodexCliInfo ReadVersion(string candidate)
        {
            try
            {
                using (var process = new System.Diagnostics.Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "--version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    if (!process.Start())
                    {
                        return new CodexCliInfo(null, null, $"Unable to start Codex CLI at {candidate}.");
                    }

                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000))
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                        return new CodexCliInfo(null, null, $"Timed out while checking Codex CLI at {candidate}.");
                    }

                    Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 1000);
                    var stdout = stdoutTask.Status == TaskStatus.RanToCompletion ? stdoutTask.Result : string.Empty;
                    var stderr = stderrTask.Status == TaskStatus.RanToCompletion ? stderrTask.Result : string.Empty;

                    if (process.ExitCode != 0)
                    {
                        return new CodexCliInfo(null, null, FirstNonEmptyLine(stderr, stdout));
                    }

                    var versionText = FirstNonEmptyLine(stdout, stderr);
                    if (!IsVersionSupported(versionText, out var compatibilityError))
                    {
                        return new CodexCliInfo(candidate, versionText, compatibilityError);
                    }

                    return new CodexCliInfo(candidate, versionText, null);
                }
            }
            catch (Exception exception)
            {
                return new CodexCliInfo(null, null, exception.Message);
            }
        }

        internal static bool IsVersionSupported(string versionText, out string error)
        {
            if (!TryParseVersion(versionText, out var version))
            {
                error = $"Could not parse the Codex CLI version from '{versionText ?? string.Empty}'.";
                return false;
            }

            if (version.CompareTo(MinimumSupportedVersion) < 0)
            {
                error = $"Codex CLI {version} is not supported by Agent for Unity 0.7.0. Install Codex CLI 0.144.0 or later.";
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryParseVersion(string versionText, out Version version)
        {
            version = null;
            var match = VersionPattern.Match(versionText ?? string.Empty);
            return match.Success && Version.TryParse(match.Value, out version);
        }

        private static char[] PathSeparator()
        {
            return new[] { System.IO.Path.PathSeparator };
        }

        private static void AddCandidate(ICollection<string> candidates, string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                candidates.Add(candidate.Trim());
            }
        }

        private static string FirstNonEmptyLine(params string[] values)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                using (var reader = new StringReader(value))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            return line.Trim();
                        }
                    }
                }
            }

            return "Unknown Codex CLI error.";
        }
    }
}
