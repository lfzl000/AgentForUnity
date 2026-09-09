using AgentForUnity.Editor.Codex;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentForUnity.Editor.Tests
{
    internal sealed class CodexProtocolTests
    {
        [Test]
        public void SerializeRequest_OmitsJsonRpcHeaderAndPreservesParameters()
        {
            var json = CodexProtocol.SerializeRequest(42, "turn/start", new JObject
            {
                ["threadId"] = "thread-1",
                ["input"] = new JArray(new JObject { ["type"] = "text", ["text"] = "hello" })
            });

            var root = JObject.Parse(json);
            Assert.That(root["jsonrpc"], Is.Null);
            Assert.That(root.Value<long>("id"), Is.EqualTo(42));
            Assert.That(root.Value<string>("method"), Is.EqualTo("turn/start"));
            Assert.That(root["params"]?["input"]?[0]?["text"]?.Value<string>(), Is.EqualTo("hello"));
        }

        [Test]
        public void Parse_ClassifiesResponseNotificationAndServerRequest()
        {
            var response = CodexProtocol.Parse("{\"id\":1,\"result\":{\"ok\":true},\"future\":1}");
            var notification = CodexProtocol.Parse(
                "{\"method\":\"item/agentMessage/delta\",\"params\":{\"delta\":\"part\"}}");
            var serverRequest = CodexProtocol.Parse(
                "{\"id\":\"approval-1\",\"method\":\"item/fileChange/requestApproval\",\"params\":{}}");

            Assert.That(response.Kind, Is.EqualTo(CodexMessageKind.Response));
            Assert.That(response.NumericId, Is.EqualTo(1));
            Assert.That(response.Result.Value<bool>("ok"), Is.True);
            Assert.That(notification.Kind, Is.EqualTo(CodexMessageKind.Notification));
            Assert.That(notification.Method, Is.EqualTo("item/agentMessage/delta"));
            Assert.That(serverRequest.Kind, Is.EqualTo(CodexMessageKind.ServerRequest));
            Assert.That(serverRequest.Id.Value<string>(), Is.EqualTo("approval-1"));
        }

        [Test]
        public void Parse_InvalidJsonDoesNotExposeRawPayloadInError()
        {
            const string secret = "secret-prompt";
            var exception = Assert.Throws<System.FormatException>(() => CodexProtocol.Parse("{" + secret));

            Assert.That(exception.Message, Does.Not.Contain(secret));
            Assert.That(exception.Message, Is.EqualTo("The App Server emitted invalid JSON."));
        }

        [Test]
        public void SerializeErrorResponse_PreservesStringRequestId()
        {
            var json = CodexProtocol.SerializeErrorResponse(new JValue("request-7"), -32601, "unsupported");
            var root = JObject.Parse(json);

            Assert.That(root.Value<string>("id"), Is.EqualTo("request-7"));
            Assert.That(root["error"]?.Value<int>("code"), Is.EqualTo(-32601));
        }
    }
}
