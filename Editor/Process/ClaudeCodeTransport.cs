using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentForUnity.Editor.Codex;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentForUnity.Editor.Claude
{
    // Translates Claude Code's stream-json output to the small subset of App Server
    // notifications consumed by AgentForUnityService.
    internal sealed class ClaudeCodeTransport : ICodexAppServerTransport
    {
        private sealed class Record { internal string Line; internal bool Exited; internal int ExitCode; }
        private readonly string _executable;
        private readonly string _projectRoot;
        private readonly ConcurrentQueue<Record> _stdout = new ConcurrentQueue<Record>();
        private readonly ConcurrentQueue<string> _stderr = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private Process _process;
        private string _sessionId;
        private bool _disposed;
        private long _requestId;
        private string _threadId;
        private string _turnId;

        internal ClaudeCodeTransport(string executable, string projectRoot)
        { _executable = executable; _projectRoot = projectRoot; }
        // Unlike the Codex App Server, Claude Code is launched per turn. The
        // transport itself remains connected while no turn process is running.
        public bool HasExited => _disposed;
        public int ExitCode { get { try { return _process?.HasExited == true ? _process.ExitCode : 0; } catch { return 0; } } }

        public bool TryReadStdout(out string line)
        {
            line = null;
            if (!_stdout.TryDequeue(out var record)) return false;
            if (record.Exited) return false;
            line = record.Line;
            return true;
        }
        public bool TryReadStderr(out string line) => _stderr.TryDequeue(out line);

        public async Task WriteLineAsync(string line)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClaudeCodeTransport));
            JObject request;
            try { request = JObject.Parse(line); }
            catch (Exception exception) { _stderr.Enqueue(exception.Message); return; }
            var method = request.Value<string>("method");
            var id = request["id"];
            var parameters = request["params"] as JObject ?? new JObject();
            switch (method)
            {
                case "initialize":
                    EnqueueResponse(id, new JObject { ["userAgent"] = "claude-code", ["provider"] = "claude-code" });
                    break;
                case "initialized":
                    break;
                case "account/read":
                    await AccountAsync(id).ConfigureAwait(false);
                    break;
                case "account/rateLimits/read":
                    EnqueueResponse(id, new JObject());
                    break;
                case "model/list":
                    EnqueueResponse(id, new JObject { ["data"] = new JArray() });
                    break;
                case "thread/list":
                    EnqueueResponse(id, new JObject { ["data"] = new JArray(), ["nextCursor"] = JValue.CreateNull() });
                    break;
                case "thread/start":
                    _threadId = Guid.NewGuid().ToString("N");
                    // Claude creates the session when the first print turn starts.
                    // Do not invent a UUID and pass it to --resume; that makes the
                    // CLI reject every first turn as an unknown session.
                    _sessionId = null;
                    EnqueueResponse(id, new JObject { ["thread"] = new JObject { ["id"] = _threadId, ["name"] = "Claude Code", ["preview"] = "", ["status"] = new JObject { ["type"] = "idle" } } });
                    break;
                case "thread/resume":
                    _threadId = parameters.Value<string>("threadId") ?? _threadId ?? Guid.NewGuid().ToString("N");
                    // A persisted Claude session is not currently restored by this
                    // adapter. Start a fresh CLI session for the resumed display row.
                    _sessionId = null;
                    EnqueueResponse(id, new JObject { ["thread"] = new JObject { ["id"] = _threadId, ["name"] = "Claude Code", ["preview"] = "", ["status"] = new JObject { ["type"] = "idle" } } });
                    break;
                case "turn/start":
                    await StartTurnAsync(id, parameters).ConfigureAwait(false);
                    break;
                case "turn/interrupt":
                    TryKill();
                    EnqueueResponse(id, new JObject());
                    break;
                default:
                    EnqueueResponse(id, new JObject());
                    break;
            }
        }

        private async Task AccountAsync(JToken id)
        {
            try
            {
                var result = await ClaudeCodeCliLocator.RunCommandAsync(_executable, "auth status --json", _projectRoot, 8000).ConfigureAwait(false);
                var account = result.ExitCode == 0 ? JObject.Parse(result.Output) : null;
                JToken accountToken = account == null
                    ? JValue.CreateNull()
                    : account["account"] ?? account;
                EnqueueResponse(id, new JObject { ["account"] = accountToken, ["requiresOpenaiAuth"] = false });
            }
            catch (Exception exception)
            {
                _stderr.Enqueue("Claude auth status failed: " + exception.Message);
                EnqueueResponse(id, new JObject { ["account"] = new JObject { ["type"] = "Claude Code" }, ["requiresOpenaiAuth"] = false });
            }
        }

        private async Task StartTurnAsync(JToken id, JObject parameters)
        {
            _turnId = Guid.NewGuid().ToString("N");
            EnqueueResponse(id, new JObject { ["turn"] = new JObject { ["id"] = _turnId, ["status"] = "inProgress" } });
            EnqueueNotification("turn/started", new JObject { ["threadId"] = _threadId, ["turn"] = new JObject { ["id"] = _turnId } });
            var input = parameters["input"] as JArray;
            var text = string.Empty;
            foreach (var token in input ?? new JArray())
            {
                var item = token as JObject;
                if (item?.Value<string>("type") == "text") text += item.Value<string>("text") + "\n";
            }
            var args = "-p --input-format stream-json --output-format stream-json --include-partial-messages --verbose --permission-mode dontAsk";
            var model = parameters.Value<string>("model");
            if (!string.IsNullOrEmpty(model) && model != "claude-code" && model != "auto")
                args += " --model " + ClaudeCodeCliLocator.Quote(model);
            var effort = parameters.Value<string>("effort");
            if (!string.IsNullOrEmpty(effort))
                args += " --effort " + ClaudeCodeCliLocator.Quote(effort);
            await RunTurnAsync(args, text).ConfigureAwait(false);
        }

        private async Task RunTurnAsync(string arguments, string text)
        {
            if (_process != null) throw new InvalidOperationException("Claude Code is already running a turn.");
            var info = ClaudeCodeCliLocator.CreateStartInfo(_executable, arguments, _projectRoot);
            _process = new Process { StartInfo = info };
            if (!_process.Start()) throw new IOException("Claude Code did not start.");
            var outputTask = ReadOutputAsync(_process.StandardOutput);
            var errorTask = ReadErrorAsync(_process.StandardError);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var input = new JObject
                {
                    ["type"] = "user",
                    ["message"] = new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text })
                    }
                };
                await _process.StandardInput.WriteLineAsync(input.ToString(Formatting.None)).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                _process.StandardInput.Close();
            }
            finally { _writeLock.Release(); }
            _ = Task.Run(async () =>
            {
                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
                await Task.Run(() => _process.WaitForExit()).ConfigureAwait(false);
                _stdout.Enqueue(new Record { Exited = true, ExitCode = _process.ExitCode });
                if (_process.ExitCode == 0)
                    EnqueueNotification("turn/completed", new JObject { ["threadId"] = _threadId, ["turn"] = new JObject { ["id"] = _turnId, ["status"] = "completed" } });
                else
                    EnqueueNotification("turn/completed", new JObject { ["threadId"] = _threadId, ["turn"] = new JObject { ["id"] = _turnId, ["status"] = "failed", ["error"] = new JObject { ["message"] = "Claude Code exited with code " + _process.ExitCode } } });
                _process.Dispose(); _process = null;
            });
        }

        private async Task ReadOutputAsync(StreamReader reader)
        {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) Translate(line);
        }
        private async Task ReadErrorAsync(StreamReader reader)
        { string line; while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) _stderr.Enqueue(line); }

        private void Translate(string line)
        {
            try
            {
                var value = JObject.Parse(line);
                var type = value.Value<string>("type");
                var streamEvent = value["event"] as JObject;
                var deltaObject = (type == "content_block_delta" ? value : streamEvent)?["delta"] as JObject;
                var delta = deltaObject?.Value<string>("text") ??
                            (type == "content_block_delta" ? value.Value<string>("delta") : null);
                if ((type == "content_block_delta" || streamEvent?.Value<string>("type") == "content_block_delta") && !string.IsNullOrEmpty(delta))
                {
                    EnqueueNotification("item/agentMessage/delta", new JObject { ["threadId"] = _threadId, ["turnId"] = _turnId, ["itemId"] = "claude-agent-message", ["delta"] = delta });
                    return;
                }
                if (type == "assistant" && value["message"]?["content"] is JArray content)
                {
                    var text = string.Join("", content.OfType<JObject>().Where(x => x.Value<string>("type") == "text").Select(x => x.Value<string>("text")));
                    EnqueueNotification("item/completed", new JObject { ["threadId"] = _threadId, ["turnId"] = _turnId, ["item"] = new JObject { ["type"] = "agentMessage", ["id"] = "claude-agent-message", ["text"] = text } });
                }
            }
            catch (Exception exception) { _stderr.Enqueue("Ignored Claude stream message: " + exception.Message); }
        }

        private void EnqueueResponse(JToken id, JObject result)
        { _stdout.Enqueue(new Record { Line = new JObject { ["id"] = id, ["result"] = result }.ToString(Formatting.None) }); }
        private void EnqueueNotification(string method, JObject parameters)
        { _stdout.Enqueue(new Record { Line = new JObject { ["method"] = method, ["params"] = parameters }.ToString(Formatting.None) }); }
        private void TryKill() { try { if (_process != null && !_process.HasExited) _process.Kill(); } catch { } }
        public void Dispose() { if (_disposed) return; _disposed = true; TryKill(); try { _process?.Dispose(); } catch { } _writeLock.Dispose(); }
    }
}
