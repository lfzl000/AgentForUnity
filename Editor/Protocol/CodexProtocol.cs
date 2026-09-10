using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentForUnity.Editor.Codex
{
    internal enum CodexMessageKind
    {
        Response,
        ErrorResponse,
        Notification,
        ServerRequest
    }

    internal sealed class CodexMessage
    {
        internal CodexMessage(
            CodexMessageKind kind,
            JToken id,
            string method,
            JObject parameters,
            JObject result,
            JObject error)
        {
            Kind = kind;
            Id = id;
            Method = method;
            Params = parameters;
            Result = result;
            Error = error;
        }

        internal CodexMessageKind Kind { get; }
        internal JToken Id { get; }
        internal string Method { get; }
        internal JObject Params { get; }
        internal JObject Result { get; }
        internal JObject Error { get; }

        internal long NumericId
        {
            get
            {
                if (Id == null)
                {
                    throw new InvalidOperationException("The message does not contain an id.");
                }

                if (Id.Type == JTokenType.Integer)
                {
                    return Id.Value<long>();
                }

                if (long.TryParse(Id.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    return id;
                }

                throw new FormatException("The message id is not numeric.");
            }
        }
    }

    internal sealed class CodexProtocolException : Exception
    {
        internal CodexProtocolException(string method, int code, string message)
            : base($"{method} failed ({code}): {message}")
        {
            Method = method;
            Code = code;
        }

        internal string Method { get; }
        internal int Code { get; }
    }

    internal static class CodexProtocol
    {
        internal static string SerializeRequest(long id, string method, JToken parameters)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                throw new ArgumentException("A method is required.", nameof(method));
            }

            return new JObject
            {
                ["method"] = method,
                ["id"] = id,
                ["params"] = parameters ?? new JObject()
            }.ToString(Formatting.None);
        }

        internal static string SerializeNotification(string method, JObject parameters)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                throw new ArgumentException("A method is required.", nameof(method));
            }

            return new JObject
            {
                ["method"] = method,
                ["params"] = parameters ?? new JObject()
            }.ToString(Formatting.None);
        }

        internal static string SerializeErrorResponse(JToken id, int code, string message)
        {
            if (id == null)
            {
                throw new ArgumentNullException(nameof(id));
            }

            return new JObject
            {
                ["id"] = id.DeepClone(),
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message ?? string.Empty
                }
            }.ToString(Formatting.None);
        }

        internal static string SerializeResponse(JToken id, JObject result)
        {
            if (id == null)
            {
                throw new ArgumentNullException(nameof(id));
            }

            return new JObject
            {
                ["id"] = id.DeepClone(),
                ["result"] = result ?? new JObject()
            }.ToString(Formatting.None);
        }

        internal static CodexMessage Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new FormatException("The App Server emitted an empty JSONL record.");
            }

            JObject root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(json))
                {
                    // Cursors are opaque strings. Do not turn ISO timestamps into locale-formatted dates.
                    DateParseHandling = DateParseHandling.None
                })
                {
                    root = JObject.Load(reader);
                }
            }
            catch (JsonException exception)
            {
                throw new FormatException("The App Server emitted invalid JSON.", exception);
            }

            var method = root.Value<string>("method");
            var hasId = root.TryGetValue("id", out var id) && id.Type != JTokenType.Null;

            if (!string.IsNullOrEmpty(method))
            {
                var parameters = root["params"] as JObject ?? new JObject();
                return new CodexMessage(
                    hasId ? CodexMessageKind.ServerRequest : CodexMessageKind.Notification,
                    id,
                    method,
                    parameters,
                    null,
                    null);
            }

            if (!hasId)
            {
                throw new FormatException("A protocol message must contain either method or id.");
            }

            if (root["error"] is JObject error)
            {
                return new CodexMessage(CodexMessageKind.ErrorResponse, id, null, null, null, error);
            }

            var result = root["result"] as JObject;
            if (result == null)
            {
                throw new FormatException("A response must contain an object result or error.");
            }

            return new CodexMessage(CodexMessageKind.Response, id, null, null, result, null);
        }
    }
}
