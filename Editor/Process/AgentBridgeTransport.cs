using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentForUnity.Editor.Codex
{
    /// <summary>
    /// Keeps the existing App Server client protocol while moving the App Server
    /// process out of the Unity Editor process. The bridge owns the Codex process;
    /// this transport owns only the Unity-side socket connection.
    /// </summary>
    internal sealed class AgentBridgeTransport : ICodexAppServerTransport
    {
        private const int StartupTimeoutMilliseconds = 60000;
        private const int ConnectTimeoutMilliseconds = 3000;

        private readonly ConcurrentQueue<string> _stdoutLines = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _stderrLines = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly object _startupLogLock = new object();
        private TcpClient _client;
        private NetworkStream _stream;
        private Process _bridgeProcess;
        private CancellationTokenSource _readCancellation;
        private int _disposed;
        private volatile bool _connectionClosed;
        private string _statePath;
        private string _startupLogPath;

        internal int ProcessId => _bridgeProcess != null ? _bridgeProcess.Id : 0;
        internal string StartupDiagnostic { get; private set; }
        internal bool ReusedExistingBridge { get; private set; }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _client == null || _connectionClosed || !_client.Connected ||
                           (_bridgeProcess != null && _bridgeProcess.HasExited);
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
                    return _bridgeProcess != null && _bridgeProcess.HasExited
                        ? _bridgeProcess.ExitCode
                        : 0;
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
            }
        }

        internal async Task StartAsync(
            string codexExecutablePath,
            string projectRoot,
            bool reuseExisting = true,
            string stateFileName = "bridge.json")
        {
            if (_bridgeProcess != null || _client != null)
            {
                throw new InvalidOperationException("The Agent Bridge has already been started.");
            }

            if (reuseExisting && await TryConnectToExistingAsync(projectRoot, stateFileName).ConfigureAwait(false))
            {
                ReusedExistingBridge = true;
                return;
            }

            var bridgeLaunch = AgentBridgeLocator.FindLaunch(projectRoot);
            if (bridgeLaunch == null)
            {
                throw new FileNotFoundException(
                    "Agent Bridge executable was not found. Reinstall the package, or set AGENT_FOR_UNITY_BRIDGE_PROJECT for source development.");
            }

            var bridgeDirectory = Path.GetDirectoryName(bridgeLaunch.Path);
            _statePath = Path.Combine(projectRoot, "Library", "AgentForUnity", stateFileName);
            _startupLogPath = Path.Combine(
                Path.GetDirectoryName(_statePath) ?? projectRoot,
                Path.GetFileNameWithoutExtension(stateFileName) + "-startup.log");
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath) ?? projectRoot);
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }

            var modelCatalogArgument = string.Empty;
            if (CodexAppServerProcess.TryCreateCompatibleModelCatalog(
                    projectRoot,
                    out var compatibleCatalogPath,
                    out var catalogDiagnostic))
            {
                modelCatalogArgument = " --model-catalog-path " + Quote(compatibleCatalogPath);
                StartupDiagnostic = catalogDiagnostic;
            }
            else if (!string.IsNullOrEmpty(catalogDiagnostic))
            {
                StartupDiagnostic = catalogDiagnostic;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = bridgeLaunch.FileName,
                Arguments = bridgeLaunch.ArgumentPrefix +
                            " --project-path " + Quote(projectRoot) +
                            " --codex-path " + Quote(codexExecutablePath) +
                            " --state-path " + Quote(_statePath) + modelCatalogArgument,
                WorkingDirectory = bridgeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            _bridgeProcess = new Process { StartInfo = startInfo };
            try
            {
                WriteStartupLog(
                    $"[{DateTimeOffset.Now:O}] Launching {startInfo.FileName} {startInfo.Arguments}");
                if (!_bridgeProcess.Start())
                {
                    throw new InvalidOperationException("Agent Bridge did not start.");
                }

                _ = ReadLinesAsync(_bridgeProcess.StandardOutput, _stderrLines, CancellationToken.None, WriteStartupLog);
                _ = ReadLinesAsync(_bridgeProcess.StandardError, _stderrLines, CancellationToken.None, WriteStartupLog);

                var descriptor = await WaitForDescriptorAsync(StartupTimeoutMilliseconds);
                if (descriptor == null)
                {
                    throw new TimeoutException("Timed out waiting for Agent Bridge startup.");
                }

                _client = await ConnectAsync(descriptor.Value<int>("port"), ConnectTimeoutMilliseconds);
                _stream = _client.GetStream();
                _readCancellation = new CancellationTokenSource();
                _connectionClosed = false;
                _ = ReadNetworkLinesAsync(_readCancellation.Token);
                await WriteLineAsync(JsonConvert.SerializeObject(new JObject
                {
                    ["type"] = "bridge.connect",
                    ["token"] = descriptor.Value<string>("token"),
                    ["protocolVersion"] = 1
                }));
            }
            catch (Exception exception)
            {
                WriteStartupLog($"[{DateTimeOffset.Now:O}] Bridge startup failed: {exception}");
                StopBridge();
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
            if (Volatile.Read(ref _disposed) != 0 || _stream == null || HasExited)
            {
                throw new InvalidOperationException("Agent Bridge is not connected.");
            }

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await _stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try { _readCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            try { _stream?.Close(); } catch (Exception) { }
            try { _client?.Close(); } catch (Exception) { }

            // The bridge deliberately remains alive across Unity Domain Reload.
            // It persists the Codex session and accepts a later Unity connection.
            var process = _bridgeProcess;
            _bridgeProcess = null;
            try { process?.Dispose(); } catch (Exception) { }
            _readCancellation?.Dispose();
            _writeLock.Dispose();
        }

        internal void StopBridge()
        {
            var process = _bridgeProcess;
            if (process == null)
            {
                return;
            }

            try
            {
                if (_stream != null && !_connectionClosed)
                {
                    WriteLineAsync("{\"type\":\"bridge.shutdown\"}").GetAwaiter().GetResult();
                }

                if (!process.HasExited)
                {
                    if (!process.WaitForExit(1500))
                    {
                        process.Kill();
                        process.WaitForExit(500);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private async Task<bool> TryConnectToExistingAsync(string projectRoot, string stateFileName)
        {
            var existingStatePath = Path.Combine(projectRoot, "Library", "AgentForUnity", stateFileName);
            if (!File.Exists(existingStatePath))
            {
                return false;
            }

            try
            {
                var descriptor = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(existingStatePath));
                var pid = descriptor?.Value<int?>("pid") ?? 0;
                var port = descriptor?.Value<int?>("port") ?? 0;
                var token = descriptor?.Value<string>("token");
                if (pid <= 0 || port <= 0 || string.IsNullOrEmpty(token) || !IsProcessRunning(pid))
                {
                    return false;
                }

                var client = await ConnectAsync(port, ConnectTimeoutMilliseconds).ConfigureAwait(false);
                _statePath = existingStatePath;
                _client = client;
                _stream = client.GetStream();
                _readCancellation = new CancellationTokenSource();
                _connectionClosed = false;
                _ = ReadNetworkLinesAsync(_readCancellation.Token);
                await WriteLineAsync(JsonConvert.SerializeObject(new JObject
                {
                    ["type"] = "bridge.connect",
                    ["token"] = token,
                    ["protocolVersion"] = 1
                })).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is SocketException || exception is TimeoutException || exception is JsonException)
            {
                try { _readCancellation?.Cancel(); } catch (ObjectDisposedException) { }
                try { _stream?.Close(); } catch (Exception) { }
                try { _client?.Close(); } catch (Exception) { }
                _stream = null;
                _client = null;
                StartupDiagnostic = "Existing Agent Bridge could not be reused: " + exception.Message;
                return false;
            }
        }

        private static bool IsProcessRunning(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private async Task<JObject> WaitForDescriptorAsync(int timeoutMilliseconds)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                if (_bridgeProcess == null || _bridgeProcess.HasExited)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    var details = DrainDiagnostics();
                    var exitCode = _bridgeProcess == null ? 0 : _bridgeProcess.ExitCode;
                    throw new InvalidOperationException(
                        string.IsNullOrEmpty(details)
                            ? $"Agent Bridge exited before it became ready (exit code {exitCode})."
                            : $"Agent Bridge exited before it became ready (exit code {exitCode}): {details}");
                }

                if (File.Exists(_statePath))
                {
                    try
                    {
                        var descriptor = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(_statePath));
                        if (descriptor?.Value<int?>("port") > 0 && !string.IsNullOrEmpty(descriptor.Value<string>("token")))
                        {
                            return descriptor;
                        }
                    }
                    catch (Exception exception)
                    {
                        StartupDiagnostic = "Waiting for Agent Bridge descriptor: " + exception.Message;
                    }
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            return null;
        }

        private static async Task<TcpClient> ConnectAsync(int port, int timeoutMilliseconds)
        {
            var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", port);
            if (await Task.WhenAny(connectTask, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false) != connectTask)
            {
                client.Close();
                throw new TimeoutException("Timed out connecting to Agent Bridge.");
            }

            await connectTask.ConfigureAwait(false);
            return client;
        }

        private async Task ReadNetworkLinesAsync(CancellationToken cancellationToken)
        {
            try
            {
                using (var reader = new StreamReader(_stream, new UTF8Encoding(false), false, 4096, true))
                {
                    string line;
                    while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (IsBridgeControlLine(line))
                        {
                            continue;
                        }

                        _stdoutLines.Enqueue(line);
                    }

                    _connectionClosed = true;
                }
            }
            catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException || exception is SocketException)
            {
                _connectionClosed = true;
                _stderrLines.Enqueue("Agent Bridge connection closed: " + exception.Message);
            }
        }

        private static async Task ReadLinesAsync(
            StreamReader reader,
            ConcurrentQueue<string> destination,
            CancellationToken cancellationToken,
            Action<string> onLine)
        {
            try
            {
                string line;
                while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    destination.Enqueue(line);
                    onLine?.Invoke(line);
                }
            }
            catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException)
            {
                var message = "Agent Bridge process output closed: " + exception.Message;
                destination.Enqueue(message);
                onLine?.Invoke(message);
            }
        }

        private static bool IsBridgeControlLine(string line)
        {
            try
            {
                var value = JsonConvert.DeserializeObject<JObject>(line);
                return value?.Value<string>("type") == "bridge.ready" ||
                       value?.Value<string>("type") == "bridge.event" ||
                       value?.Value<string>("type") == "bridge.error";
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private string DrainDiagnostics()
        {
            var lines = new System.Collections.Generic.List<string>();
            while (_stderrLines.TryDequeue(out var line))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line.Trim());
                }
            }

            return string.Join(" | ", lines);
        }

        private void WriteStartupLog(string line)
        {
            if (string.IsNullOrEmpty(_startupLogPath) || string.IsNullOrEmpty(line))
            {
                return;
            }

            try
            {
                lock (_startupLogLock)
                {
                    File.AppendAllText(_startupLogPath, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static class AgentBridgeLocator
    {
        private static readonly string[] DotnetFallbackPaths =
        {
            "/usr/local/share/dotnet/dotnet",
            "/opt/homebrew/bin/dotnet",
            "/usr/bin/dotnet"
        };

        internal sealed class LaunchInfo
        {
            internal LaunchInfo(string fileName, string path, string argumentPrefix)
            {
                FileName = fileName;
                Path = path;
                ArgumentPrefix = argumentPrefix;
            }

            internal string FileName { get; }
            internal string Path { get; }
            internal string ArgumentPrefix { get; }
        }

        internal static LaunchInfo FindLaunch(string projectRoot)
        {
            var packageRoots = FindPackageRoots(projectRoot).ToArray();
            var runtimeIdentifier = GetRuntimeIdentifier();
            foreach (var root in packageRoots)
            {
                var executableName = Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? "AgentForUnity.Bridge.exe"
                    : "AgentForUnity.Bridge";
                var executable = Path.Combine(root, "Bridge~", "runtimes", runtimeIdentifier, executableName);
                if (File.Exists(executable))
                {
                    return new LaunchInfo(executable, executable, string.Empty);
                }
            }

            var configured = Environment.GetEnvironmentVariable("AGENT_FOR_UNITY_BRIDGE_PROJECT");
            var projects = new[] { configured }
                .Concat(packageRoots.Select(root =>
                    Path.Combine(root, "Bridge~", "AgentForUnity.Bridge.csproj")))
                .Concat(new[]
                {
                    Path.Combine(projectRoot, "Bridge~", "AgentForUnity.Bridge.csproj"),
                    Path.Combine(projectRoot, "..", "AgentForUnity", "Bridge~", "AgentForUnity.Bridge.csproj"),
                    Path.Combine(projectRoot, "Packages", "com.zlr.agentforunity", "Bridge~", "AgentForUnity.Bridge.csproj")
                })
                .ToArray();
            var project = projects.FirstOrDefault(path => !string.IsNullOrEmpty(path) && File.Exists(path));
            if (string.IsNullOrEmpty(project))
            {
                return null;
            }

            var dotnet = ResolveDotnetExecutable();
            var bridgeHost = FindBridgeHost(project);
            if (!string.IsNullOrEmpty(bridgeHost))
            {
                return new LaunchInfo(bridgeHost, bridgeHost, string.Empty);
            }

            var bridgeAssembly = FindBridgeAssembly(project);
            if (!string.IsNullOrEmpty(bridgeAssembly))
            {
                return new LaunchInfo(dotnet, bridgeAssembly, Quote(bridgeAssembly));
            }

            return new LaunchInfo(dotnet, project, "run --project " + Quote(project) + " --");
        }

        internal static bool HasPackagedRuntime(string projectRoot)
        {
            var executableName = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? "AgentForUnity.Bridge.exe"
                : "AgentForUnity.Bridge";
            var runtimeIdentifier = GetRuntimeIdentifier();
            return FindPackageRoots(projectRoot).Any(root =>
                File.Exists(Path.Combine(root, "Bridge~", "runtimes", runtimeIdentifier, executableName)));
        }

        private static string FindBridgeHost(string projectPath)
        {
            var projectDirectory = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(projectDirectory))
            {
                return null;
            }

            var executableName = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? "AgentForUnity.Bridge.exe"
                : "AgentForUnity.Bridge";
            var candidates = new[]
            {
                Path.Combine(projectDirectory, "bin", "Debug", "net9.0", executableName),
                Path.Combine(projectDirectory, "bin", "Release", "net9.0", executableName)
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static string FindBridgeAssembly(string projectPath)
        {
            var projectDirectory = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(projectDirectory))
            {
                return null;
            }

            var candidates = new[]
            {
                Path.Combine(projectDirectory, "bin", "Debug", "net9.0", "AgentForUnity.Bridge.dll"),
                Path.Combine(projectDirectory, "bin", "Release", "net9.0", "AgentForUnity.Bridge.dll")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static string ResolveDotnetExecutable()
        {
            var configured = Environment.GetEnvironmentVariable("AGENT_FOR_UNITY_DOTNET");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (Path.IsPathRooted(configured) && File.Exists(configured))
                {
                    return configured;
                }

                var configuredFromPath = FindOnPath(configured);
                if (!string.IsNullOrEmpty(configuredFromPath))
                {
                    return configuredFromPath;
                }
            }

            var systemDotnet = DotnetFallbackPaths.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(systemDotnet))
            {
                return systemDotnet;
            }

            return FindOnPath("dotnet") ?? "dotnet";
        }

        private static string FindOnPath(string executableName)
        {
            if (Path.IsPathRooted(executableName))
            {
                return File.Exists(executableName) ? executableName : null;
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var candidateName = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? executableName + ".exe"
                : executableName;
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim(), candidateName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static System.Collections.Generic.IEnumerable<string> FindPackageRoots(string projectRoot)
        {
            // The manifest is authoritative for local file packages. PackageInfo can report
            // the virtual Packages/<name> path even when the package lives outside the project.
            var manifestPackageRoot = TryResolveManifestPackageRoot(projectRoot);
            if (!string.IsNullOrEmpty(manifestPackageRoot))
            {
                yield return manifestPackageRoot;
            }

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(AgentBridgeTransport).Assembly);
            if (packageInfo != null &&
                !string.IsNullOrEmpty(packageInfo.resolvedPath) &&
                Directory.Exists(packageInfo.resolvedPath) &&
                !string.Equals(
                    Path.GetFullPath(packageInfo.resolvedPath),
                    manifestPackageRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(packageInfo.resolvedPath);
            }

            var projectPackageRoot = Path.Combine(projectRoot, "Packages", "com.zlr.agentforunity");
            if (Directory.Exists(projectPackageRoot))
            {
                yield return projectPackageRoot;
            }

            if (Directory.Exists(projectRoot))
            {
                yield return projectRoot;
            }
            var packageCache = Path.Combine(projectRoot, "Library", "PackageCache");
            if (!Directory.Exists(packageCache)) yield break;
            foreach (var path in Directory.GetDirectories(packageCache, "com.zlr.agentforunity@*"))
            {
                if (Directory.Exists(path))
                {
                    yield return path;
                }
            }
        }

        private static string TryResolveManifestPackageRoot(string projectRoot)
        {
            var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                var dependencies = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(manifestPath))?
                    ["dependencies"] as JObject;
                var packageReference = dependencies?.Value<string>("com.zlr.agentforunity");
                return ResolveLocalPackageReference(projectRoot, packageReference);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ResolveLocalPackageReference(string projectRoot, string reference)
        {
            if (string.IsNullOrWhiteSpace(reference) ||
                !reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var pathText = reference.Substring("file:".Length);
            if (Uri.TryCreate(reference, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                pathText = uri.LocalPath;
            }

            var path = Path.IsPathRooted(pathText)
                ? pathText
                : Path.Combine(projectRoot, pathText);
            return Directory.Exists(path) ? Path.GetFullPath(path) : null;
        }

        private static string GetRuntimeIdentifier()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) return "win-x64";
            return Environment.Is64BitOperatingSystem &&
                   System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                   System.Runtime.InteropServices.Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
