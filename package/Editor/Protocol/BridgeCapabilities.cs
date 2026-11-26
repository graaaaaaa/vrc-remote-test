using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace VRCRemoteTest
{
    /// <summary>
    /// GET /api/v1/capabilities, read leniently: a member missing or of another type is null, and
    /// "features" (Bridge v0.12.0 and later) is empty when it is missing, not an array, or not
    /// readable, the same as a Bridge that has none (docs/plans/r1b-contract.md §1.1).
    /// </summary>
    public sealed class BridgeCapabilities
    {
        private static readonly IReadOnlyList<string> NoFeatures = Array.Empty<string>();

        public long? ApiVersion { get; private set; }

        public long? BuildProtocolVersion { get; private set; }

        public long? MaxArtifactSizeBytes { get; private set; }

        public long? MaxClients { get; private set; }

        /// <summary>The string members of "features"; never null.</summary>
        public IReadOnlyList<string> Features { get; private set; } = NoFeatures;

        /// <summary>True only when "features" is an array holding the string slotPreconditions.v1.</summary>
        public bool SupportsSlotPreconditions => Has(SlotPrecondition.Feature);

        public bool Has(string feature)
        {
            foreach (var item in Features)
            {
                if (string.Equals(item, feature, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The capabilities in <paramref name="text"/>; null when it is not a JSON object.</summary>
        internal static BridgeCapabilities Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            try
            {
                return From(JToken.Parse(text) as JObject);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>Null only for a null object.</summary>
        public static BridgeCapabilities From(JObject json)
        {
            if (json == null)
            {
                return null;
            }

            var features = new List<string>();
            if (json["features"] is JArray array)
            {
                foreach (var item in array)
                {
                    if (item.Type == JTokenType.String)
                    {
                        features.Add((string)item);
                    }
                }
            }

            return new BridgeCapabilities
            {
                ApiVersion = BridgeMessages.ByteCount(json["apiVersion"]),
                BuildProtocolVersion = BridgeMessages.ByteCount(json["buildProtocolVersion"]),
                MaxArtifactSizeBytes = BridgeMessages.ByteCount(json["maxArtifactSizeBytes"]),
                MaxClients = BridgeMessages.ByteCount(json["maxClients"]),
                Features = features,
            };
        }
    }
}
