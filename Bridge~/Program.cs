using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentForUnity.Bridge;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        BridgeOptions options;
        try
        {
            options = BridgeOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Agent Bridge: {exception.Message}");
            BridgeOptions.PrintUsage();
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler? cancelHandler = null;
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            await using var server = await BridgeServer.StartAsync(options, shutdown.Token);
            Console.Error.WriteLine(
                $"Agent Bridge listening on 127.0.0.1:{server.Port} (pid {Environment.ProcessId}).");
            await server.RunAsync(shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Agent Bridge failed: {exception}");
            return 1;
        }
        finally
        {
            if (cancelHandler != null)
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
    }
}

internal sealed class BridgeOptions
{
    private BridgeOptions(string projectPath, string codexPath, string statePath, string? modelCatalogPath, int port)
    {
        ProjectPath = projectPath;
        CodexPath = codexPath;
        StatePath = statePath;
        ModelCatalogPath = modelCatalogPath;
        Port = port;
    }

    internal string ProjectPath { get; }
    internal string CodexPath { get; }
    internal string StatePath { get; }
    internal string? ModelCatalogPath { get; }
    internal int Port { get; }

    internal static BridgeOptions Parse(IReadOnlyList<string> args)
    {
        string? projectPath = null;
        string? codexPath = null;
        string? statePath = null;
        string? modelCatalogPath = null;
        var port = 0;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--project-path":
                    projectPath = RequireValue(args, ref index, argument);
                    break;
                case "--codex-path":
                    codexPath = RequireValue(args, ref index, argument);
                    break;
                case "--state-path":
                    statePath = RequireValue(args, ref index, argument);
                    break;
                case "--model-catalog-path":
                    modelCatalogPath = RequireValue(args, ref index, argument);
                    break;
                case "--port":
                    var portText = RequireValue(args, ref index, argument);
                    if (!int.TryParse(portText, out port) || port is < 0 or > 65535)
                    {
                        throw new ArgumentException("--port must be an integer between 0 and 65535.");
                    }

                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{argument}'.");
            }
        }

        projectPath = Path.GetFullPath(projectPath ?? Directory.GetCurrentDirectory());
        codexPath ??= FindOnPath("codex");
        if (string.IsNullOrWhiteSpace(codexPath))
        {
            throw new ArgumentException("Codex CLI was not found. Pass --codex-path with its absolute path.");
        }

        codexPath = Path.GetFullPath(codexPath);
        statePath = Path.GetFullPath(
            statePath ?? Path.Combine(projectPath, "Library", "AgentForUnity", "bridge.json"));
        return new BridgeOptions(projectPath, codexPath, statePath, modelCatalogPath, port);
    }

    internal static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: AgentForUnity.Bridge --project-path <path> --codex-path <path> " +
            "[--state-path <path>] [--model-catalog-path <path>] [--port <0-65535>]");
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }

    private static string? FindOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

internal sealed class BridgeServer : IAsyncDisposable
{
    private const int ProtocolVersion = 1;
    private const int MaximumLineLength = 16 * 1024 * 1024;
    private const int MaximumPendingLines = 512;

    private readonly BridgeOptions _options;
    private readonly TcpListener _listener;
    private readonly CodexProcess _codex;
    private readonly string _token;
    private readonly SemaphoreSlim _broadcastGate = new(1, 1);
    private readonly object _pendingGate = new();
    private readonly Queue<string> _pendingLines = new();
    private readonly Dictionary<string, string> _pendingServerRequests = new(StringComparer.Ordinal);
    private CancellationTokenSource? _requestedShutdown;
    private ClientSession? _client;
    private bool _disposed;

    private BridgeServer(
        BridgeOptions options,
        TcpListener listener,
        CodexProcess codex,
        string token)
    {
        _options = options;
        _listener = listener;
        _codex = codex;
        _token = token;
    }

    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal static async Task<BridgeServer> StartAsync(
        BridgeOptions options,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.StatePath)!);
        var listener = new TcpListener(IPAddress.Loopback, options.Port);
        listener.Start();

        try
        {
            var codex = await CodexProcess.StartAsync(options, cancellationToken);
            var token = CreateToken();
            var server = new BridgeServer(options, listener, codex, token);
            await server.WriteStateFileAsync(cancellationToken);
            _ = server.ReadCodexOutputAsync(cancellationToken);
            return server;
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linkedShutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _requestedShutdown = linkedShutdown;
        using var registration = linkedShutdown.Token.Register(() =>
        {
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        });

        while (!linkedShutdown.IsCancellationRequested)
        {
            TcpClient tcpClient;
            try
            {
                tcpClient = await _listener.AcceptTcpClientAsync(linkedShutdown.Token);
            }
            catch (OperationCanceledException) when (linkedShutdown.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (linkedShutdown.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (linkedShutdown.IsCancellationRequested)
            {
                break;
            }

            _ = HandleClientAsync(tcpClient, linkedShutdown.Token);
        }

        _requestedShutdown = null;
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        await using var session = new ClientSession(tcpClient);
        try
        {
            if (!await AuthenticateAsync(session, cancellationToken))
            {
                return;
            }

            await AttachClientAsync(session, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await session.Reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                if (line.Length == 0 || line.Length > MaximumLineLength)
                {
                    await session.SendLineAsync(BridgeMessages.Error("JSONL line is empty or larger than 16 MiB."));
                    continue;
                }

                if (IsBridgeShutdown(line))
                {
                    _requestedShutdown?.Cancel();
                    break;
                }

                CompletePendingServerRequest(line);
                await _codex.WriteLineAsync(line, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"Agent Bridge client error: {exception.Message}");
        }
        finally
        {
            await DetachClientAsync(session);
        }
    }

    private async Task<bool> AuthenticateAsync(ClientSession session, CancellationToken cancellationToken)
    {
        var line = await session.Reader.ReadLineAsync(cancellationToken);
        if (line == null || line.Length > MaximumLineLength)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            var token = root.TryGetProperty("token", out var tokenElement)
                ? tokenElement.GetString()
                : null;
            if (!string.Equals(type, "bridge.connect", StringComparison.Ordinal) ||
                !FixedTimeEquals(token, _token))
            {
                await session.SendLineAsync(BridgeMessages.Error("Bridge authentication failed."));
                return false;
            }
        }
        catch (JsonException)
        {
            await session.SendLineAsync(BridgeMessages.Error("The first line must be a bridge.connect JSON object."));
            return false;
        }

        return true;
    }

    private async Task AttachClientAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await _broadcastGate.WaitAsync(cancellationToken);
        try
        {
            var previous = _client;
            _client = session;
            if (previous != null && !ReferenceEquals(previous, session))
            {
                await previous.DisposeAsync();
            }

            await session.SendLineAsync(BridgeMessages.Ready(Environment.ProcessId, Port, ProtocolVersion));
            string[] pending;
            lock (_pendingGate)
            {
                pending = _pendingLines.ToArray();
                _pendingLines.Clear();
            }

            foreach (var line in pending)
            {
                await session.SendLineAsync(line);
            }

            string[] pendingServerRequests;
            lock (_pendingGate)
            {
                pendingServerRequests = _pendingServerRequests.Values.ToArray();
            }

            foreach (var line in pendingServerRequests)
            {
                await session.SendLineAsync(line);
            }
        }
        catch
        {
            if (ReferenceEquals(_client, session))
            {
                _client = null;
            }

            throw;
        }
        finally
        {
            _broadcastGate.Release();
        }
    }

    private async Task DetachClientAsync(ClientSession session)
    {
        await _broadcastGate.WaitAsync();
        try
        {
            if (ReferenceEquals(_client, session))
            {
                _client = null;
            }
        }
        finally
        {
            _broadcastGate.Release();
        }
    }

    private async Task ReadCodexOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in _codex.ReadOutputAsync(cancellationToken))
            {
                if (line.Length > MaximumLineLength)
                {
                    Console.Error.WriteLine("Agent Bridge ignored an App Server line larger than 16 MiB.");
                    continue;
                }

                var isServerRequest = RememberPendingServerRequest(line);
                await BroadcastAsync(line, isServerRequest, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Agent Bridge App Server output ended: {exception.Message}");
        }
    }

    private async Task BroadcastAsync(string line, bool isServerRequest, CancellationToken cancellationToken)
    {
        await _broadcastGate.WaitAsync(cancellationToken);
        try
        {
            if (_client == null)
            {
                if (isServerRequest)
                {
                    return;
                }

                lock (_pendingGate)
                {
                    while (_pendingLines.Count >= MaximumPendingLines)
                    {
                        _pendingLines.Dequeue();
                    }

                    _pendingLines.Enqueue(line);
                }

                return;
            }

            try
            {
                await _client.SendLineAsync(line, cancellationToken);
            }
            catch (IOException)
            {
                _client = null;
                if (isServerRequest)
                {
                    return;
                }

                lock (_pendingGate)
                {
                    while (_pendingLines.Count >= MaximumPendingLines)
                    {
                        _pendingLines.Dequeue();
                    }

                    _pendingLines.Enqueue(line);
                }
            }
            catch (SocketException)
            {
                _client = null;
                if (isServerRequest)
                {
                    return;
                }

                lock (_pendingGate)
                {
                    while (_pendingLines.Count >= MaximumPendingLines)
                    {
                        _pendingLines.Dequeue();
                    }

                    _pendingLines.Enqueue(line);
                }
            }
        }
        finally
        {
            _broadcastGate.Release();
        }
    }

    private async Task WriteStateFileAsync(CancellationToken cancellationToken)
    {
        var state = new BridgeState
        {
            Pid = Environment.ProcessId,
            Port = Port,
            Token = _token,
            ProtocolVersion = ProtocolVersion,
            ProjectPath = _options.ProjectPath,
            StartedAtUtc = DateTimeOffset.UtcNow
        };
        var json = JsonSerializer.Serialize(state, BridgeJsonContext.Default.BridgeState);
        var temporaryPath = _options.StatePath + ".tmp-" + Environment.ProcessId;
        await File.WriteAllTextAsync(temporaryPath, json + "\n", new UTF8Encoding(false), cancellationToken);
        File.Move(temporaryPath, _options.StatePath, true);
        RestrictStateFilePermissions(_options.StatePath);
    }

    private bool RememberPendingServerRequest(string line)
    {
        if (!TryReadMessageId(line, requireMethod: true, out var id))
        {
            return false;
        }

        lock (_pendingGate)
        {
            _pendingServerRequests[id] = line;
        }

        return true;
    }

    private void CompletePendingServerRequest(string line)
    {
        if (!TryReadMessageId(line, requireMethod: false, out var id))
        {
            return;
        }

        lock (_pendingGate)
        {
            _pendingServerRequests.Remove(id);
        }
    }

    private static bool TryReadMessageId(string line, bool requireMethod, out string id)
    {
        id = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind == JsonValueKind.Null)
            {
                return false;
            }

            var hasMethod = root.TryGetProperty("method", out _);
            if (hasMethod != requireMethod)
            {
                return false;
            }

            if (!requireMethod &&
                !root.TryGetProperty("result", out _) &&
                !root.TryGetProperty("error", out _))
            {
                return false;
            }

            id = idElement.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsBridgeShutdown(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("type", out var type) &&
                   string.Equals(type.GetString(), "bridge.shutdown", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listener.Stop();
        ClientSession? client;
        await _broadcastGate.WaitAsync();
        try
        {
            client = _client;
            _client = null;
        }
        finally
        {
            _broadcastGate.Release();
        }

        if (client != null)
        {
            await client.DisposeAsync();
        }

        await _codex.DisposeAsync();
        try
        {
            if (File.Exists(_options.StatePath))
            {
                File.Delete(_options.StatePath);
            }
        }
        catch (IOException)
        {
        }

        _broadcastGate.Dispose();
    }

    private static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool FixedTimeEquals(string? left, string right)
    {
        if (left == null)
        {
            return false;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static void RestrictStateFilePermissions(string path)
    {
        // The state file contains the loopback authentication token. Unix hosts
        // should keep it private; Windows uses the user's normal project ACL.
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class ClientSession : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    internal ClientSession(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        Reader = new StreamReader(
            _client.GetStream(),
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024,
            leaveOpen: true);
        Writer = new StreamWriter(
            _client.GetStream(),
            new UTF8Encoding(false),
            bufferSize: 16 * 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    internal StreamReader Reader { get; }
    private StreamWriter Writer { get; }

    internal async Task SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await Writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            await Writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Reader.Dispose();
        Writer.Dispose();
        _client.Dispose();
        _writeGate.Dispose();
        await Task.CompletedTask;
    }
}

internal sealed class CodexProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private CodexProcess(Process process)
    {
        _process = process;
    }

    internal static async Task<CodexProcess> StartAsync(
        BridgeOptions options,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.CodexPath,
            WorkingDirectory = options.ProjectPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        startInfo.ArgumentList.Add("app-server");
        if (!string.IsNullOrWhiteSpace(options.ModelCatalogPath))
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("model_catalog_json='" + options.ModelCatalogPath.Replace("'", "''") + "'");
        }
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Codex App Server did not start.");
        }

        process.StandardInput.AutoFlush = true;
        _ = DrainDiagnosticsAsync(process.StandardError, cancellationToken);
        await Task.Yield();
        return new CodexProcess(process);
    }

    internal async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"Codex App Server exited with code {_process.ExitCode}.");
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal async IAsyncEnumerable<string> ReadOutputAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                yield break;
            }

            yield return line;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.FlushAsync();
                _process.StandardInput.Close();
                if (!await WaitForExitAsync(_process, TimeSpan.FromMilliseconds(1500)))
                {
                    _process.Kill(entireProcessTree: true);
                    await WaitForExitAsync(_process, TimeSpan.FromMilliseconds(500));
                }
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            _process.Dispose();
            _writeGate.Dispose();
        }
    }

    private static async Task DrainDiagnosticsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    Console.Error.WriteLine($"codex: {line}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

internal sealed class BridgeState
{
    [System.Text.Json.Serialization.JsonPropertyName("pid")]
    public int Pid { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("port")]
    public int Port { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("projectPath")]
    public string ProjectPath { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("startedAtUtc")]
    public DateTimeOffset StartedAtUtc { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(BridgeState))]
internal partial class BridgeJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

internal static class BridgeMessages
{
    internal static string Ready(int pid, int port, int protocolVersion)
    {
        return JsonSerializer.Serialize(new
        {
            type = "bridge.ready",
            pid,
            port,
            protocolVersion
        });
    }

    internal static string Error(string message)
    {
        return JsonSerializer.Serialize(new
        {
            type = "bridge.error",
            message
        });
    }
}
