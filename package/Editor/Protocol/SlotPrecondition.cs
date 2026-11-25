using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace VRCRemoteTest
{
    /// <summary>
    /// What a close or launch of one client expects to find on the Bridge (Bridge v0.12.0 and
    /// later, feature slotPreconditions.v1; docs/plans/r1b-contract.md §1.3): this Bridge process
    /// and host, its configuration, its operation revision, the current batch and the client
    /// generation. The Bridge does nothing (412) when any of it differs, so a request prepared from
    /// an earlier view cannot act on what changed since. Always valid once constructed.
    /// </summary>
    public sealed class SlotPrecondition
    {
        /// <summary>The capabilities feature that says the Bridge reads this body.</summary>
        public const string Feature = "slotPreconditions.v1";

        internal const int MaxEtagLength = 128;
        internal const int MaxBuildIdLength = 128;
        internal const int MaxOperationIdLength = 64;

        /// <summary>The seven keys of "expect", in the order they are written.</summary>
        internal static readonly string[] Keys =
        {
            "bridgeInstanceId", "hostGeneration", "activeConfigEtag", "savedConfigEtag",
            "operationRevision", "buildId", "clientGeneration",
        };

        /// <exception cref="ArgumentException">When a value is not one the Bridge accepts (see <see cref="Validate"/>).</exception>
        public SlotPrecondition(string bridgeInstanceId, long hostGeneration, string activeConfigEtag,
            string savedConfigEtag, long operationRevision, string buildId, long clientGeneration)
        {
            var problems = Validate(bridgeInstanceId, hostGeneration, activeConfigEtag, savedConfigEtag,
                operationRevision, buildId, clientGeneration);
            if (problems.Count > 0)
            {
                throw new ArgumentException("Not a valid slot precondition: " + string.Join(" ", problems));
            }

            BridgeInstanceId = bridgeInstanceId;
            HostGeneration = hostGeneration;
            ActiveConfigEtag = activeConfigEtag;
            SavedConfigEtag = savedConfigEtag;
            OperationRevision = operationRevision;
            BuildId = buildId;
            ClientGeneration = clientGeneration;
        }

        /// <summary>32 lowercase hex digits: the Bridge process.</summary>
        public string BridgeInstanceId { get; }

        public long HostGeneration { get; }

        public string ActiveConfigEtag { get; }

        public string SavedConfigEtag { get; }

        public long OperationRevision { get; }

        /// <summary>currentBatch.buildId; null when the Bridge has no launch plan.</summary>
        public string BuildId { get; }

        public long ClientGeneration { get; }

        /// <summary>
        /// Why these values cannot be sent; empty when they can. The same rules as the Bridge
        /// (§1.3): an etag is 1 to 128 characters or null, hostGeneration fits the Bridge's 32-bit
        /// counter.
        /// </summary>
        public static IReadOnlyList<string> Validate(string bridgeInstanceId, long hostGeneration, string activeConfigEtag,
            string savedConfigEtag, long operationRevision, string buildId, long clientGeneration)
        {
            var problems = new List<string>();
            if (!IsInstanceId(bridgeInstanceId))
            {
                problems.Add("bridgeInstanceId must be 32 lowercase hex digits.");
            }

            if (hostGeneration < 1 || hostGeneration > int.MaxValue)
            {
                problems.Add("hostGeneration must be an integer from 1.");
            }

            if (!IsEtag(activeConfigEtag))
            {
                problems.Add($"activeConfigEtag must be 1 to {MaxEtagLength} characters, or null.");
            }

            if (!IsEtag(savedConfigEtag))
            {
                problems.Add($"savedConfigEtag must be 1 to {MaxEtagLength} characters, or null.");
            }

            if (operationRevision < 0)
            {
                problems.Add("operationRevision must be an integer from 0.");
            }

            if (buildId != null && !IsToken(buildId, MaxBuildIdLength))
            {
                problems.Add($"buildId must be 1 to {MaxBuildIdLength} letters, digits, '_' or '-', or null.");
            }

            if (clientGeneration < 1)
            {
                problems.Add("clientGeneration must be an integer from 1.");
            }

            return problems;
        }

        /// <summary>An operation ID the Bridge accepts: 1 to 64 ASCII letters, digits, '_' or '-'.</summary>
        public static bool IsValidOperationId(string operationId) => IsToken(operationId, MaxOperationIdLength);

        /// <summary>
        /// The request body: {"expect":{the seven keys, in the order of <see cref="Keys"/>},"operationId":...},
        /// with no whitespace; "operationId" only when given. A null etag or buildId is written as null.
        /// </summary>
        /// <exception cref="ArgumentException">When <paramref name="operationId"/> is given and not valid.</exception>
        public string ToRequestJson(string operationId = null)
        {
            if (operationId != null && !IsValidOperationId(operationId))
            {
                throw new ArgumentException("An operation ID is 1 to 64 letters, digits, '_' or '-'.", nameof(operationId));
            }

            using (var text = new StringWriter(CultureInfo.InvariantCulture))
            {
                using (var writer = new JsonTextWriter(text) { Formatting = Formatting.None })
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("expect");
                    writer.WriteStartObject();
                    writer.WritePropertyName("bridgeInstanceId");
                    writer.WriteValue(BridgeInstanceId);
                    writer.WritePropertyName("hostGeneration");
                    writer.WriteValue(HostGeneration);
                    writer.WritePropertyName("activeConfigEtag");
                    writer.WriteValue(ActiveConfigEtag);
                    writer.WritePropertyName("savedConfigEtag");
                    writer.WriteValue(SavedConfigEtag);
                    writer.WritePropertyName("operationRevision");
                    writer.WriteValue(OperationRevision);
                    writer.WritePropertyName("buildId");
                    writer.WriteValue(BuildId);
                    writer.WritePropertyName("clientGeneration");
                    writer.WriteValue(ClientGeneration);
                    writer.WriteEndObject();
                    if (operationId != null)
                    {
                        writer.WritePropertyName("operationId");
                        writer.WriteValue(operationId);
                    }

                    writer.WriteEndObject();
                }

                return text.ToString();
            }
        }

        /// <summary>
        /// The precondition for <paramref name="clientGeneration"/> (the generation in the client
        /// row) under <paramref name="bridge"/> (a GET /bridge answer), or null with
        /// <paramref name="problem"/> set when that answer lacks a value the Bridge checks (an older
        /// Bridge) or has one it would refuse.
        /// </summary>
        public static SlotPrecondition From(BridgeInfo bridge, long clientGeneration, out string problem)
        {
            problem = null;
            if (bridge == null)
            {
                problem = "the Bridge's state (GET /bridge) could not be read";
                return null;
            }

            var missing = new List<string>();
            if (bridge.BridgeInstanceId == null)
            {
                missing.Add("bridgeInstanceId");
            }

            if (bridge.OperationRevision == null)
            {
                missing.Add("operationRevision");
            }

            if (missing.Count > 0)
            {
                problem = "the Bridge's state (GET /bridge) has no " + string.Join(" or ", missing);
                return null;
            }

            var problems = Validate(bridge.BridgeInstanceId, bridge.HostGeneration, bridge.ActiveConfigEtag,
                bridge.SavedConfigEtag, bridge.OperationRevision.Value, bridge.CurrentBatch?.BuildId, clientGeneration);
            if (problems.Count > 0)
            {
                // Built from untrusted text only through fixed sentences: the values are not echoed.
                problem = "the Bridge's state (GET /bridge) cannot be sent back as a check: " + string.Join(" ", problems);
                return null;
            }

            return new SlotPrecondition(bridge.BridgeInstanceId, bridge.HostGeneration, bridge.ActiveConfigEtag,
                bridge.SavedConfigEtag, bridge.OperationRevision.Value, bridge.CurrentBatch?.BuildId, clientGeneration);
        }

        private static bool IsInstanceId(string text)
        {
            if (text == null || text.Length != 32)
            {
                return false;
            }

            foreach (var c in text)
            {
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsEtag(string text) => text == null || (text.Length >= 1 && text.Length <= MaxEtagLength);

        private static bool IsToken(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length > maxLength)
            {
                return false;
            }

            foreach (var c in text)
            {
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && c != '_' && c != '-')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
