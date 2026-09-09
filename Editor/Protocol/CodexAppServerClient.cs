using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentForUnity.Editor.Codex
{
    internal sealed class CodexAppServerClient : IDisposable
    {
        private const int RequestTimeoutMilliseconds = 20000;
        private const int MaximumProtocolLineLength = 4 * 1024 * 1024;

        private sealed class PendingRequest
        {
            internal PendingRequest(string method)
            {
                Method = method;
                Completion = new TaskCompletionSource<JObject>();
            }

            internal string Method { get; }
            internal TaskCompletionSource<JObject> Completion { get; }
        }

        private readonly ICodexAppServerTransport _process;
        private readonly Dictionary<long, PendingRequest> _pending = new Dictionary<long, PendingRequest>();
        private long _nextRequestId;
        private bool _exitReported;
        private bool _disposed;

        internal CodexAppServerClient(ICodexAppServerTransport process)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
        }

        internal event Action<CodexMessage> NotificationReceived;
        internal event Action<CodexMessage> ServerRequestReceived;
        internal event Action<string> DiagnosticReceived;
        internal event Action<CodexAppServerClient, string> Disconnected;

        internal async Task<JObject> InitializeAsync()
        {
            var result = await SendRequestAsync(
                "initialize",
                new JObject
                {
                    ["clientInfo"] = new JObject
                    {
                        ["name"] = "agent_for_unity",
                        ["title"] = "Agent for Unity",
                        ["version"] = "0.2.0"
                    }
                });

            await SendNotificationAsync("initialized", new JObject());
            return result;
        }

        internal async Task<JObject> SendRequestAsync(string method, JObject parameters)
        {
            ThrowIfDisposed();
            var id = ++_nextRequestId;
            var pending = new PendingRequest(method);
            _pending.Add(id, pending);

            try
            {
                await _process.WriteLineAsync(CodexProtocol.SerializeRequest(id, method, parameters));
            }
            catch
            {
                _pending.Remove(id);
                throw;
            }

            var timeout = Task.Delay(RequestTimeoutMilliseconds);
            var completed = await Task.WhenAny(pending.Completion.Task, timeout);
            if (completed != pending.Completion.Task)
            {
                _pending.Remove(id);
                throw new TimeoutException($"Timed out waiting for {method}.");
            }

            return await pending.Completion.Task;
        }

        internal Task SendNotificationAsync(string method, JObject parameters)
        {
            ThrowIfDisposed();
            return _process.WriteLineAsync(CodexProtocol.SerializeNotification(method, parameters));
        }

        internal Task RespondToServerRequestAsync(JToken id, JObject result)
        {
            ThrowIfDisposed();
            return _process.WriteLineAsync(CodexProtocol.SerializeResponse(id, result));
        }

        internal Task RejectServerRequestAsync(CodexMessage message, int code, string reason)
        {
            if (message == null || message.Id == null)
            {
                throw new ArgumentException("A server request with an id is required.", nameof(message));
            }

            ThrowIfDisposed();
            return _process.WriteLineAsync(CodexProtocol.SerializeErrorResponse(message.Id, code, reason));
        }

        internal int Pump(int maximumMessages)
        {
            if (_disposed)
            {
                return 0;
            }

            var processed = 0;
            while (processed < maximumMessages && _process.TryReadStdout(out var line))
            {
                processed++;
                ProcessProtocolLine(line);
            }

            var stderrLimit = Math.Min(32, maximumMessages);
            for (var index = 0; index < stderrLimit && _process.TryReadStderr(out var line); index++)
            {
                DiagnosticReceived?.Invoke(Truncate(line, 4000));
            }

            if (!_exitReported && _process.HasExited)
            {
                _exitReported = true;
                var message = $"Codex App Server exited with code {_process.ExitCode}.";
                FailPending(new InvalidOperationException(message));
                Disconnected?.Invoke(this, message);
            }

            return processed;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            FailPending(new ObjectDisposedException(nameof(CodexAppServerClient)));
            _process.Dispose();
        }

        private void ProcessProtocolLine(string line)
        {
            if (line.Length > MaximumProtocolLineLength)
            {
                DiagnosticReceived?.Invoke("Ignored an App Server message larger than 4 MiB.");
                return;
            }

            CodexMessage message;
            try
            {
                message = CodexProtocol.Parse(line);
            }
            catch (Exception exception)
            {
                DiagnosticReceived?.Invoke(exception.Message);
                return;
            }

            switch (message.Kind)
            {
                case CodexMessageKind.Response:
                    CompleteRequest(message, false);
                    break;
                case CodexMessageKind.ErrorResponse:
                    CompleteRequest(message, true);
                    break;
                case CodexMessageKind.Notification:
                    NotificationReceived?.Invoke(message);
                    break;
                case CodexMessageKind.ServerRequest:
                    if (ServerRequestReceived != null)
                    {
                        ServerRequestReceived.Invoke(message);
                    }
                    else
                    {
                        DiagnosticReceived?.Invoke($"Rejected unsupported server request: {message.Method}");
                        _ = RejectUnsupportedServerRequestAsync(message);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void CompleteRequest(CodexMessage message, bool isError)
        {
            long id;
            try
            {
                id = message.NumericId;
            }
            catch (Exception parseException)
            {
                DiagnosticReceived?.Invoke(parseException.Message);
                return;
            }

            if (!_pending.TryGetValue(id, out var pending))
            {
                DiagnosticReceived?.Invoke($"Ignored a response for unknown request id {id}.");
                return;
            }

            _pending.Remove(id);
            if (!isError)
            {
                pending.Completion.TrySetResult(message.Result);
            }
            else
            {
                pending.Completion.TrySetException(CreateProtocolException(pending.Method, message));
            }
        }

        private static CodexProtocolException CreateProtocolException(string method, CodexMessage message)
        {
            var code = message.Error?.Value<int?>("code") ?? -1;
            var text = message.Error?.Value<string>("message") ?? "Unknown protocol error.";
            return new CodexProtocolException(method, code, text);
        }

        private async Task RejectUnsupportedServerRequestAsync(CodexMessage message)
        {
            try
            {
                await _process.WriteLineAsync(CodexProtocol.SerializeErrorResponse(
                    message.Id,
                    -32601,
                    $"{message.Method} is not supported by Agent for Unity."));
            }
            catch (Exception exception)
            {
                DiagnosticReceived?.Invoke($"Could not reject server request: {exception.Message}");
            }
        }

        private void FailPending(Exception exception)
        {
            var pendingRequests = new List<PendingRequest>(_pending.Values);
            _pending.Clear();
            foreach (var pending in pendingRequests)
            {
                pending.Completion.TrySetException(exception);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CodexAppServerClient));
            }
        }

        private static string Truncate(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximumLength)
            {
                return value;
            }

            return value.Substring(0, maximumLength) + " [truncated]";
        }
    }
}
