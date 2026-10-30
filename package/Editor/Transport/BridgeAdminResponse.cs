using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCRemoteTest
{
    /// <summary>
    /// The answer to one call of the Bridge's management API (v0.8.0 and later): the HTTP
    /// status and the JSON body as text, so callers can read both the success shapes and
    /// the error bodies ({error, problems[], ...}). Never thrown: a transport without that
    /// API answers <see cref="Supported"/> false, and a Bridge that could not be reached
    /// answers status 0 with <see cref="Message"/>.
    /// </summary>
    public sealed class BridgeAdminResponse
    {
        public BridgeAdminResponse(bool supported, int statusCode, string body, string message)
        {
            Supported = supported;
            StatusCode = statusCode;
            Body = body;
            Message = message;
            Error = BridgeMessages.ErrorOf(body);
        }

        /// <summary>False when this transport has no management API.</summary>
        public bool Supported { get; }

        /// <summary>The HTTP status, or 0 when there was no answer.</summary>
        public int StatusCode { get; }

        /// <summary>The response body (JSON for every route but /logs/bridge), or null.</summary>
        public string Body { get; }

        /// <summary>The body's "error" member (busy, etagMismatch, invalidConfig, ...), or null.</summary>
        public string Error { get; }

        /// <summary>Why there is no answer (not supported, not reachable), or null.</summary>
        public string Message { get; }

        public bool IsSuccess => Supported && StatusCode >= 200 && StatusCode < 300;

        /// <summary>The body as <typeparamref name="T"/>, or null when it is missing or not that shape.</summary>
        public T As<T>() where T : class => BridgeMessages.Parse<T>(Body);

        public static BridgeAdminResponse NotSupported(string transportName) =>
            new BridgeAdminResponse(false, 0, null,
                transportName + " has no Bridge management API; it needs the LAN Direct transport and Bridge v0.8.0 or later.");

        public static BridgeAdminResponse Unreachable(string message) =>
            new BridgeAdminResponse(true, 0, null, message);
    }

    /// <summary>
    /// Reading the Bridge's small JSON error bodies and phrasing them, kept out of the HTTP
    /// code so the tests can call it directly. Everything read here is untrusted.
    /// </summary>
    internal static class BridgeMessages
    {
        /// <summary>How much of an error body is read (a 413 body is well under this).</summary>
        internal const int MaxErrorBodyBytes = 4 * 1024;

        private const int MaxJsonDepth = 8;

        /// <summary>
        /// For a 413 answer to an artifact upload, the UPLOAD_FAILED message with the limit
        /// from its body ({error, limitBytes, declaredBytes}); null when the status is not
        /// 413 or the body does not say (an older Bridge sent none), so the caller keeps its
        /// own message.
        /// </summary>
        internal static string TooLargeUploadMessage(long statusCode, string body)
        {
            if (statusCode != 413)
            {
                return null;
            }

            var limit = DescribeTooLarge(body);
            return limit == null ? null : "Bridge rejected artifact upload (HTTP 413): " + limit + ".";
        }

        /// <summary>
        /// The UPLOAD_FAILED message for a commit the Bridge refused. A Bridge that is about to
        /// restart its host (v0.8.0) answers 409 {error: "busy", operation: "restart"}: the
        /// artifact was uploaded but not committed, so the same build can be deployed again
        /// once the Bridge is back. Otherwise the body, cut and stripped of controls.
        /// </summary>
        internal static string CommitRejectedMessage(long statusCode, string body)
        {
            var prefix = "Bridge rejected build commit (HTTP " + statusCode + ")";
            if (statusCode == 409 && ErrorOf(body) == "busy")
            {
                var operation = ParseObject(body)?["operation"];
                var what = operation != null && operation.Type == JTokenType.String ? (string)operation : null;
                return what == "restart"
                    ? prefix + ": the Bridge is restarting. Deploy again once it is back (Deploy Last Build)."
                    : prefix + ": the Bridge is busy" + (what == null ? string.Empty : " (" + Sanitize(what, 40) + ")")
                        + ". Deploy again when it is done (Deploy Last Build).";
            }

            return string.IsNullOrWhiteSpace(body) ? prefix + "." : prefix + ": " + Sanitize(body, 500);
        }

        /// <summary>"the Bridge accepts at most N bytes (MaxArtifactSizeBytes)", or null.</summary>
        internal static string DescribeTooLarge(string body)
        {
            var json = ParseObject(body);
            var limit = ByteCount(json?["limitBytes"]);
            if (limit == null)
            {
                return null;
            }

            var error = json["error"]?.Type == JTokenType.String ? (string)json["error"] : null;
            var text = "the Bridge accepts at most " + FormatBytes(limit.Value)
                + (error == "artifactTooLarge" ? " (MaxArtifactSizeBytes)" : string.Empty);
            var declared = ByteCount(json["declaredBytes"]);
            if (declared != null)
            {
                text += "; this upload was " + FormatBytes(declared.Value);
            }

            return text;
        }

        /// <summary>
        /// A JSON integer from 0 to Int64.MaxValue, or null for anything else (missing, not an
        /// integer, negative, or too large for Int64, which Newtonsoft keeps as a BigInteger).
        /// Never throws, unlike a (long) cast of the token.
        /// </summary>
        internal static long? ByteCount(JToken token)
        {
            if (!(token is JValue value) || value.Type != JTokenType.Integer)
            {
                return null;
            }

            long number;
            switch (value.Value)
            {
                case long l:
                    number = l;
                    break;
                case int i:
                    number = i;
                    break;
                case ulong u when u <= (ulong)long.MaxValue:
                    number = (long)u;
                    break;
                default:
                    return null;
            }

            return number >= 0 ? number : (long?)null;
        }

        /// <summary>The "error" member of a JSON object body, or null.</summary>
        internal static string ErrorOf(string body)
        {
            var error = ParseObject(body)?["error"];
            return error != null && error.Type == JTokenType.String ? Sanitize((string)error, 100) : null;
        }

        /// <summary>"52428800 bytes (50.0 MiB)".</summary>
        internal static string FormatBytes(long bytes) =>
            bytes.ToString(CultureInfo.InvariantCulture) + " bytes ("
            + (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB)";

        /// <summary>
        /// Untrusted text for one line of the UI or the Console: controls, format characters
        /// (bidirectional overrides and isolates, zero-width characters) and line or paragraph
        /// separators become spaces, and it is cut to <paramref name="maxLength"/>.
        /// </summary>
        internal static string Sanitize(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var builder = new StringBuilder(Math.Min(text.Length, maxLength));
            foreach (var c in text)
            {
                if (builder.Length >= maxLength)
                {
                    return builder.Append("...").ToString();
                }

                builder.Append(IsUnsafe(c) ? ' ' : c);
            }

            return builder.ToString();
        }

        private static bool IsUnsafe(char c)
        {
            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    return true;
                default:
                    return false;
            }
        }

        internal static T Parse<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<T>(json, new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.None,
                    MaxDepth = MaxJsonDepth,
                });
            }
            catch (Exception ex) when (ex is JsonException or OverflowException)
            {
                return null;
            }
        }

        private static JObject ParseObject(string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return null;
            }

            try
            {
                using (var reader = new JsonTextReader(new StringReader(body))
                       {
                           DateParseHandling = DateParseHandling.None,
                           MaxDepth = MaxJsonDepth,
                       })
                {
                    return JToken.ReadFrom(reader) as JObject;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Keeps at most a fixed number of the bytes appended to it and drops the rest, so an
    /// answer of any length costs at most its capacity in memory. Used for every body this
    /// package reads from the Bridge's error and management answers.
    /// </summary>
    internal sealed class BoundedBytes
    {
        private readonly byte[] _buffer;
        private int _count;

        public BoundedBytes(int capacity)
        {
            _buffer = new byte[capacity];
        }

        /// <summary>True once more bytes arrived than the capacity.</summary>
        public bool Overflowed { get; private set; }

        public int Count => _count;

        public void Append(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0)
            {
                return;
            }

            var take = Math.Min(count, _buffer.Length - _count);
            if (take > 0)
            {
                Buffer.BlockCopy(data, offset, _buffer, _count, take);
                _count += take;
            }

            if (take < count)
            {
                Overflowed = true;
            }
        }

        /// <summary>The bytes as UTF-8 text; null when none arrived or when more than the capacity did (a cut body is not read).</summary>
        public string Text => _count == 0 || Overflowed ? null : Encoding.UTF8.GetString(_buffer, 0, _count);
    }
}
