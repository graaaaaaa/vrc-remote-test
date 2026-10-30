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

        /// <summary>"the Bridge accepts at most N bytes (MaxArtifactSizeBytes)", or null.</summary>
        internal static string DescribeTooLarge(string body)
        {
            var json = ParseObject(body);
            var limit = json?["limitBytes"];
            if (limit == null || limit.Type != JTokenType.Integer)
            {
                return null;
            }

            var error = json["error"]?.Type == JTokenType.String ? (string)json["error"] : null;
            var text = "the Bridge accepts at most " + FormatBytes((long)limit)
                + (error == "artifactTooLarge" ? " (MaxArtifactSizeBytes)" : string.Empty);
            var declared = json["declaredBytes"];
            if (declared != null && declared.Type == JTokenType.Integer)
            {
                text += "; this upload was " + FormatBytes((long)declared);
            }

            return text;
        }

        /// <summary>The "error" member of a JSON object body, or null.</summary>
        internal static string ErrorOf(string body)
        {
            var error = ParseObject(body)?["error"];
            return error != null && error.Type == JTokenType.String ? Sanitize((string)error, 100) : null;
        }

        /// <summary>The first <see cref="MaxErrorBodyBytes"/> bytes as UTF-8 text, or null.</summary>
        internal static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            return Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxErrorBodyBytes));
        }

        /// <summary>"52428800 bytes (50.0 MiB)".</summary>
        internal static string FormatBytes(long bytes) =>
            bytes.ToString(CultureInfo.InvariantCulture) + " bytes ("
            + (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB)";

        /// <summary>Untrusted text for one line of the UI or the Console: controls become spaces, and it is cut to <paramref name="maxLength"/>.</summary>
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

                builder.Append(char.IsControl(c) ? ' ' : c);
            }

            return builder.ToString();
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
            catch (JsonException)
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
}
