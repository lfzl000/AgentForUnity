using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentForUnity.Editor.Codex;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentForUnity.Editor.Tests
{
    internal sealed class CodexAppServerClientTests
    {
        private sealed class FakeTransport : ICodexAppServerTransport
        {
            private readonly ConcurrentQueue<string> _stdout = new ConcurrentQueue<string>();
            private readonly ConcurrentQueue<string> _stderr = new ConcurrentQueue<string>();

            internal List<string> Writes { get; } = new List<string>();
            public bool HasExited { get; set; }
            public int ExitCode { get; set; }
            public int DisposeCount { get; private set; }

            public bool TryReadStdout(out string line) => _stdout.TryDequeue(out line);
            public bool TryReadStderr(out string line) => _stderr.TryDequeue(out line);

            public Task WriteLineAsync(string line)
            {
                Writes.Add(line);
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                DisposeCount++;
            }

            internal void EnqueueStdout(string line) => _stdout.Enqueue(line);
        }

        [Test]
        public void RequestsHaveUniqueIdsAndResponsesMatchTheirRequest()
        {
            var transport = new FakeTransport();
            using (var client = new CodexAppServerClient(transport))
            {
                var accountTask = client.SendRequestAsync("account/read", new JObject());
                var modelTask = client.SendRequestAsync("model/list", new JObject());

                Assert.That(transport.Writes, Has.Count.EqualTo(2));
                var first = JObject.Parse(transport.Writes[0]);
                var second = JObject.Parse(transport.Writes[1]);
                Assert.That(first.Value<long>("id"), Is.Not.EqualTo(second.Value<long>("id")));

                transport.EnqueueStdout($"{{\"id\":{second.Value<long>("id")},\"result\":{{\"kind\":\"models\"}}}}");
                transport.EnqueueStdout($"{{\"id\":{first.Value<long>("id")},\"result\":{{\"kind\":\"account\"}}}}");
                client.Pump(8);

                Assert.That(accountTask.GetAwaiter().GetResult().Value<string>("kind"), Is.EqualTo("account"));
                Assert.That(modelTask.GetAwaiter().GetResult().Value<string>("kind"), Is.EqualTo("models"));
            }
        }

        [Test]
        public void ProtocolErrorIncludesOriginatingMethod()
        {
            var transport = new FakeTransport();
            using (var client = new CodexAppServerClient(transport))
            {
                var request = client.SendRequestAsync("thread/start", new JObject());
                var id = JObject.Parse(transport.Writes[0]).Value<long>("id");
                transport.EnqueueStdout($"{{\"id\":{id},\"error\":{{\"code\":99,\"message\":\"denied\"}}}}");
                client.Pump(4);

                try
                {
                    request.GetAwaiter().GetResult();
                    Assert.Fail("Expected a CodexProtocolException.");
                }
                catch (CodexProtocolException exception)
                {
                    Assert.That(exception.Method, Is.EqualTo("thread/start"));
                    Assert.That(exception.Code, Is.EqualTo(99));
                }
            }
        }

        [Test]
        public void UnknownNotificationIsForwardedWithoutDisconnecting()
        {
            var transport = new FakeTransport();
            using (var client = new CodexAppServerClient(transport))
            {
                CodexMessage received = null;
                client.NotificationReceived += message => received = message;
                transport.EnqueueStdout("{\"method\":\"future/event\",\"params\":{\"value\":7}}");

                client.Pump(4);

                Assert.That(received, Is.Not.Null);
                Assert.That(received.Method, Is.EqualTo("future/event"));
                Assert.That(received.Params.Value<int>("value"), Is.EqualTo(7));
            }
        }

        [Test]
        public void ProcessExitReportsOneDisconnectAndFailsPendingRequest()
        {
            var transport = new FakeTransport();
            using (var client = new CodexAppServerClient(transport))
            {
                var disconnectCount = 0;
                CodexAppServerClient disconnectedClient = null;
                client.Disconnected += (source, _) =>
                {
                    disconnectCount++;
                    disconnectedClient = source;
                };

                var request = client.SendRequestAsync("account/read", new JObject());
                transport.ExitCode = 17;
                transport.HasExited = true;

                client.Pump(4);
                client.Pump(4);

                Assert.That(disconnectCount, Is.EqualTo(1));
                Assert.That(disconnectedClient, Is.SameAs(client));
                try
                {
                    request.GetAwaiter().GetResult();
                    Assert.Fail("Expected the pending request to fail when the process exits.");
                }
                catch (InvalidOperationException exception)
                {
                    Assert.That(exception.Message, Does.Contain("code 17"));
                }
            }
        }

        [Test]
        public void ProcessExitCompletesAllRequestsBeforeReentrantDisposal()
        {
            var transport = new FakeTransport();
            var client = new CodexAppServerClient(transport);
            try
            {
                var first = client.SendRequestAsync("account/read", new JObject());
                var second = client.SendRequestAsync("model/list", new JObject());
                var disposeContinuation = first.ContinueWith(
                    _ => client.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                transport.ExitCode = 17;
                transport.HasExited = true;

                Assert.DoesNotThrow(() => client.Pump(4));
                Assert.That(() => first.GetAwaiter().GetResult(), Throws.TypeOf<InvalidOperationException>());
                Assert.That(() => second.GetAwaiter().GetResult(), Throws.TypeOf<InvalidOperationException>());
                Assert.That(disposeContinuation.Wait(1000), Is.True);
                Assert.That(transport.DisposeCount, Is.EqualTo(1));
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
