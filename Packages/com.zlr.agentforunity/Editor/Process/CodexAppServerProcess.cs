using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentForUnity.Editor.Codex
{
    internal interface ICodexAppServerTransport : IDisposable
    {
        bool HasExited { get; }
        int ExitCode { get; }
        bool TryReadStdout(out string line);
        bool TryReadStderr(out string line);
        Task WriteLineAsync(string line);
    }

    internal sealed class CodexAppServerProcess : ICodexAppServerTransport
    {
        private const string DefaultArguments = "app-server --listen stdio://";
        private const string ModelCatalogSettingName = "model_catalog_json";

        private readonly ConcurrentQueue<string> _stdoutLines = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _stderrLines = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _stdinLock = new SemaphoreSlim(1, 1);
        private System.Diagnostics.Process _process;
        private int _disposed;

        internal int ProcessId => _process != null ? _process.Id : 0;
        internal string StartupDiagnostic { get; private set; }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process == null || _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public int ExitCode
        {
            get
            {
                try
                {
                    return _process != null && _process.HasExited ? _process.ExitCode : 0;
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
            }
        }

        internal void Start(string executablePath, string workingDirectory)
        {
            if (_process != null)
            {
                throw new InvalidOperationException("The App Server process has already been started.");
            }

            var arguments = DefaultArguments;
            if (TryCreateCompatibleModelCatalog(workingDirectory, out var compatibleCatalogPath, out var diagnostic))
            {
                arguments = $"app-server -c \"model_catalog_json='{compatibleCatalogPath}'\" --listen stdio://";
                StartupDiagnostic = diagnostic;
            }
            else if (!string.IsNullOrEmpty(diagnostic))
            {
                StartupDiagnostic = diagnostic;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // JSON-RPC requires the first byte on stdin to be `{`.  Encoding.UTF8 can
                // emit a BOM through StreamWriter, which app-server rejects as invalid JSON.
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            _process = new System.Diagnostics.Process { StartInfo = startInfo };
            try
            {
                if (!_process.Start())
                {
                    throw new InvalidOperationException("Codex App Server did not start.");
                }

                _process.StandardInput.AutoFlush = true;
                _ = ReadLinesAsync(_process.StandardOutput, _stdoutLines);
                _ = ReadLinesAsync(_process.StandardError, _stderrLines);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public bool TryReadStdout(out string line)
        {
            return _stdoutLines.TryDequeue(out line);
        }

        public bool TryReadStderr(out string line)
        {
            return _stderrLines.TryDequeue(out line);
        }

        public async Task WriteLineAsync(string line)
        {
            if (Volatile.Read(ref _disposed) != 0 || _process == null || HasExited)
            {
                throw new InvalidOperationException("Codex App Server is not running.");
            }

            await _stdinLock.WaitAsync();
            try
            {
                await _process.StandardInput.WriteLineAsync(line);
                await _process.StandardInput.FlushAsync();
            }
            finally
            {
                _stdinLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var process = _process;
            _process = null;
            if (process == null)
            {
                _stdinLock.Dispose();
                return;
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (Exception)
            {
                // The child may have already closed its input pipe.
            }

            try
            {
                if (!process.HasExited && !process.WaitForExit(1500))
                {
                    // This Process instance is the exact child started by this integration.
                    process.Kill();
                    process.WaitForExit(500);
                }
            }
            catch (Exception)
            {
                // Unity shutdown must continue even if the OS has already reaped the child.
            }
            finally
            {
                process.Dispose();
                _stdinLock.Dispose();
            }
        }

        private static async Task ReadLinesAsync(StreamReader reader, ConcurrentQueue<string> destination)
        {
            try
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    destination.Enqueue(line);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException exception)
            {
                destination.Enqueue($"I/O error: {exception.Message}");
            }
        }

        private static bool TryCreateCompatibleModelCatalog(
            string projectRoot,
            out string compatibleCatalogPath,
            out string diagnostic)
        {
            compatibleCatalogPath = null;
            diagnostic = null;

            try
            {
                var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (string.IsNullOrWhiteSpace(codexHome))
                {
                    codexHome = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".codex");
                }

                var configPath = Path.Combine(codexHome, "config.toml");
                if (!File.Exists(configPath) ||
                    !TryReadModelCatalogPath(configPath, out var configuredCatalogPath))
                {
                    return false;
                }

                if (!Path.IsPathRooted(configuredCatalogPath))
                {
                    configuredCatalogPath = Path.GetFullPath(Path.Combine(codexHome, configuredCatalogPath));
                }

                if (!File.Exists(configuredCatalogPath))
                {
                    return false;
                }

                var catalog = JObject.Parse(File.ReadAllText(configuredCatalogPath));
                if (!(catalog["models"] is JArray models))
                {
                    return false;
                }

                var changed = false;
                foreach (var model in models.OfType<JObject>())
                {
                    if (model.Value<string>("base_instructions") != null)
                    {
                        continue;
                    }

                    var instructions = model["model_messages"]?.Value<string>("instructions_template");
                    if (instructions == null)
                    {
                        diagnostic = "The configured Codex model catalog is missing base_instructions and cannot be adapted.";
                        return false;
                    }

                    model["base_instructions"] = instructions;
                    changed = true;
                }

                foreach (var model in models.OfType<JObject>())
                {
                    if (model["supports_reasoning_summaries"] == null)
                    {
                        model["supports_reasoning_summaries"] = true;
                        changed = true;
                    }

                    if (model["supports_parallel_tool_calls"] == null)
                    {
                        model["supports_parallel_tool_calls"] = true;
                        changed = true;
                    }
                }

                if (!changed)
                {
                    return false;
                }

                var compatibilityDirectory = Path.Combine(projectRoot, "Library", "AgentForUnity");
                Directory.CreateDirectory(compatibilityDirectory);
                compatibleCatalogPath = Path.Combine(compatibilityDirectory, "model_catalog_compat.json");
                if (compatibleCatalogPath.IndexOfAny(new[] { '\'', '\"' }) >= 0)
                {
                    compatibleCatalogPath = null;
                    diagnostic = "Could not adapt the Codex model catalog because the compatibility path contains a quote.";
                    return false;
                }

                File.WriteAllText(compatibleCatalogPath, catalog.ToString(Formatting.None), new UTF8Encoding(false));
                diagnostic = "Using an Agent for Unity compatibility copy of the configured Codex model catalog.";
                return true;
            }
            catch (Exception exception)
            {
                diagnostic = $"Could not prepare the Codex model catalog compatibility copy: {exception.Message}";
                return false;
            }
        }

        private static bool TryReadModelCatalogPath(string configPath, out string modelCatalogPath)
        {
            modelCatalogPath = null;
            foreach (var line in File.ReadLines(configPath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    return false;
                }

                if (!trimmed.StartsWith(ModelCatalogSettingName, StringComparison.Ordinal))
                {
                    continue;
                }

                var remainder = trimmed.Substring(ModelCatalogSettingName.Length).TrimStart();
                if (remainder.Length == 0 || remainder[0] != '=')
                {
                    continue;
                }

                return TryParseTomlString(remainder.Substring(1).TrimStart(), out modelCatalogPath);
            }

            return false;
        }

        private static bool TryParseTomlString(string value, out string parsed)
        {
            parsed = null;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            if (value[0] == '\'')
            {
                var end = value.IndexOf('\'', 1);
                if (end <= 1)
                {
                    return false;
                }

                parsed = value.Substring(1, end - 1);
                return !string.IsNullOrWhiteSpace(parsed);
            }

            if (value[0] != '\"')
            {
                return false;
            }

            var escaped = false;
            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                if (character == '\"' && !escaped)
                {
                    parsed = JsonConvert.DeserializeObject<string>(value.Substring(0, index + 1));
                    return !string.IsNullOrWhiteSpace(parsed);
                }

                escaped = character == '\\' && !escaped;
                if (character != '\\')
                {
                    escaped = false;
                }
            }

            return false;
        }
    }
}
