using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace VRCRemoteTest
{
    /// <summary>Authenticated, pinned HTTPS client for the Windows Bridge.</summary>
    public sealed class LanRemoteTransport : IRemoteTransport, IDisposable
    {
        public const int SlotCount = 4;
        private readonly string _baseUrl;
        private readonly string _fingerprint;
        private readonly string _token;
        private readonly HttpClient _http;
        private readonly SynchronizationContext _mainContext;
        private readonly bool _isBatchMode;
        private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();
        private readonly object _cacheGate = new object();
        private volatile bool _connected;
        private DateTime _nextProbeAtUtc;

        private sealed class CacheEntry
        {
            public DateTime LastStarted;
            public string Value;
            public bool Loading;
        }

        public LanRemoteTransport(string url, string fingerprint, string token)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || uri.AbsolutePath != "/" || uri.Port < 1024)
                throw new RemoteBuildException(ErrorCode.InvalidConfiguration, "Bridge URL must be a direct HTTPS LAN endpoint.");
            if (fingerprint == null || fingerprint.Length != 64 || !IsHex(fingerprint))
                throw new RemoteBuildException(ErrorCode.InvalidConfiguration, "Bridge certificate SHA-256 is missing or invalid.");
            if (token == null || token.Length != 64 || !IsHex(token))
                throw new RemoteBuildException(ErrorCode.InvalidConfiguration, "Bridge pairing token is missing or invalid.");

            _baseUrl = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            _fingerprint = fingerprint.ToLowerInvariant();
            _token = token;
            _mainContext = SynchronizationContext.Current;
            _isBatchMode = Application.isBatchMode;

            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                cert != null && Sha256(cert.RawData) == _fingerprint;
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        public bool IsAvailable
        {
            get
            {
                if (!_connected && DateTime.UtcNow >= _nextProbeAtUtc)
                {
                    _nextProbeAtUtc = DateTime.UtcNow.AddSeconds(5);
                    _ = ProbeQuietlyAsync();
                }
                return _connected;
            }
        }

        public async Task ProbeAsync(CancellationToken cancellationToken = default)
        {
            if (_connected) return;
            try
            {
                using (var response = await _http.GetAsync(Url("/capabilities"), cancellationToken).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        throw new RemoteBuildException(ErrorCode.ProtocolVersionMismatch,
                            "Bridge has no LAN Direct capabilities endpoint; update Bridge and Unity together.");
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        throw new RemoteBuildException(ErrorCode.InvalidConfiguration, "Bridge pairing token was rejected.");
                    response.EnsureSuccessStatusCode();
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    if ((int?)json["apiVersion"] != 1
                        || (int?)json["buildProtocolVersion"] != ProtocolConstants.CurrentProtocolVersion)
                        throw new RemoteBuildException(ErrorCode.ProtocolVersionMismatch,
                            "Bridge and Unity package versions are incompatible; update both together.");
                    _connected = true;
                }
            }
            catch (RemoteBuildException) { _connected = false; throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _connected = false;
                throw new RemoteBuildException(ErrorCode.BridgeUnavailable,
                    "Could not connect securely to the Windows Bridge: " + ex.Message);
            }
        }

        private async Task ProbeQuietlyAsync()
        {
            try { await ProbeAsync().ConfigureAwait(false); }
            catch (Exception) { }
        }

        public async Task UploadBuildAsync(BuildArtifact artifact, BuildManifest manifest,
            string buildId, CancellationToken cancellationToken = default)
        {
            await ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (_isBatchMode || _mainContext == null)
                await UploadWithHttpClientAsync(artifact, buildId, cancellationToken).ConfigureAwait(false);
            else
                await UploadWithUnityAsync(artifact, buildId, cancellationToken).ConfigureAwait(false);

            var json = JsonConvert.SerializeObject(manifest);
            using (var body = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = await _http.PostAsync(Url("/builds/" + buildId + "/commit"), body,
                       cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new RemoteBuildException(ErrorCode.UploadFailed,
                        "Bridge rejected build commit (HTTP " + (int)response.StatusCode + "): "
                        + await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }

        private async Task UploadWithHttpClientAsync(BuildArtifact artifact, string buildId,
            CancellationToken cancellationToken)
        {
            using (var input = File.OpenRead(artifact.FullPath))
            using (var content = new StreamContent(input))
            using (var response = await _http.PutAsync(Url("/builds/" + buildId + "/artifact"),
                       content, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new RemoteBuildException(ErrorCode.UploadFailed,
                        "Bridge rejected artifact upload (HTTP " + (int)response.StatusCode + ").");
            }
        }

        /// <summary>Interactive Unity streams the file without buffering it in managed memory.</summary>
        private Task UploadWithUnityAsync(BuildArtifact artifact, string buildId,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainContext.Post(_ =>
            {
                var request = new UnityWebRequest(Url("/builds/" + buildId + "/artifact"), "PUT")
                {
                    uploadHandler = new UploadHandlerFile(artifact.FullPath),
                    downloadHandler = new DownloadHandlerBuffer(),
                    certificateHandler = new PinnedCertificateHandler(_fingerprint),
                };
                request.SetRequestHeader("Authorization", "Bearer " + _token);
                var operation = request.SendWebRequest();
                CancellationTokenRegistration registration = default;
                EditorApplication.CallbackFunction tick = null;
                tick = () =>
                {
                    if (!operation.isDone) return;
                    EditorApplication.update -= tick;
                    registration.Dispose();
                    if (request.result == UnityWebRequest.Result.Success && request.responseCode == 200)
                        completion.TrySetResult(true);
                    else
                        completion.TrySetException(new RemoteBuildException(ErrorCode.UploadFailed,
                            "Bridge artifact upload failed (HTTP " + request.responseCode + "): " + request.error));
                    request.Dispose();
                };
                registration = cancellationToken.Register(() => _mainContext.Post(__ => request.Abort(), null));
                EditorApplication.update += tick;
            }, null);
            return completion.Task;
        }

        private sealed class PinnedCertificateHandler : CertificateHandler
        {
            private readonly string _expected;
            public PinnedCertificateHandler(string expected) { _expected = expected; }
            protected override bool ValidateCertificate(byte[] certificateData) =>
                Sha256(certificateData) == _expected;
        }

        public async Task<BuildResult> PollResultAsync(string buildId,
            CancellationToken cancellationToken = default)
        {
            using (var response = await _http.GetAsync(Url("/builds/" + buildId), cancellationToken)
                       .ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<BuildResult>(json, JsonSettings());
            }
        }

        public VrchatStatus PollVrchatStatus() => Parse<VrchatStatus>(GetCached("/status", 10));
        public string PollVrchatLog() => GetCached("/logs/latest", 5);
        public VrchatClients PollVrchatClients() => Parse<VrchatClients>(GetCached("/clients", 5));
        public string PollClientLog(int slot) => SlotText("/logs/clients/", slot);
        public string PollClientMarkers(int slot) => SlotText("/markers/clients/", slot);

        public async Task DownloadFullLogAsync(int slot, long clientGeneration, long observationEpoch,
            string destinationPath, CancellationToken cancellationToken = default)
        {
            if (slot < 0 || slot >= SlotCount || string.IsNullOrEmpty(destinationPath))
                throw new RemoteBuildException(ErrorCode.InvalidConfiguration, "Invalid log export destination or slot.");

            var request = JsonConvert.SerializeObject(new
            {
                slot, clientGeneration, observationEpoch,
            });
            JObject metadata;
            using (var body = new StringContent(request, Encoding.UTF8, "application/json"))
            using (var response = await _http.PostAsync(Url("/log-exports"), body, cancellationToken)
                       .ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new RemoteBuildException(ErrorCode.ResultInvalid,
                        "The selected VRChat client session could not be exported (HTTP "
                        + (int)response.StatusCode + ").");
                metadata = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }

            var exportId = (string)metadata["exportId"];
            var expectedSize = (long?)metadata["size"];
            var expectedHash = (string)metadata["sha256"];
            if (exportId == null || expectedSize == null || expectedHash == null)
                throw new RemoteBuildException(ErrorCode.ResultInvalid, "Bridge returned invalid log export metadata.");

            var partial = destinationPath + ".part";
            try
            {
                using (var response = await _http.GetAsync(Url("/log-exports/" + exportId),
                           HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                        await source.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                }

                if (new FileInfo(partial).Length != expectedSize
                    || !string.Equals(Sha256Calculator.ComputeHash(partial), expectedHash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new RemoteBuildException(ErrorCode.ResultInvalid, "Downloaded log snapshot failed integrity check.");
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                File.Move(partial, destinationPath);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }

        private string SlotText(string prefix, int slot) =>
            slot < 0 || slot >= SlotCount ? null : GetCached(prefix + slot, 5);

        private string GetCached(string path, int refreshSeconds)
        {
            lock (_cacheGate)
            {
                if (!_cache.TryGetValue(path, out var entry))
                {
                    entry = new CacheEntry();
                    _cache[path] = entry;
                }
                if (!entry.Loading && DateTime.UtcNow - entry.LastStarted >= TimeSpan.FromSeconds(refreshSeconds))
                {
                    entry.Loading = true;
                    entry.LastStarted = DateTime.UtcNow;
                    _ = RefreshAsync(path, entry);
                }
                return entry.Value;
            }
        }

        private async Task RefreshAsync(string path, CacheEntry entry)
        {
            try
            {
                using (var response = await _http.GetAsync(Url(path)).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        lock (_cacheGate) entry.Value = null;
                    }
                    else if (response.IsSuccessStatusCode)
                    {
                        var value = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        lock (_cacheGate) entry.Value = value;
                    }
                }
            }
            catch (Exception) { }
            finally { lock (_cacheGate) entry.Loading = false; }
        }

        private static T Parse<T>(string json) where T : class
        {
            if (json == null) return null;
            try { return JsonConvert.DeserializeObject<T>(json, JsonSettings()); }
            catch (JsonException) { return null; }
        }

        private static JsonSerializerSettings JsonSettings() => new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 8,
        };

        private string Url(string path) => _baseUrl + "/api/v1" + path;
        private static string Sha256(byte[] bytes) =>
            BitConverter.ToString(SHA256.Create().ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();

        private static bool IsHex(string text)
        {
            foreach (var c in text)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        public void Dispose() => _http.Dispose();
    }
}
