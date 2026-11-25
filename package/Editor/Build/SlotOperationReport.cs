using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace VRCRemoteTest
{
    /// <summary>
    /// What the Bridge's answer to closing or launching one client means, read the way
    /// scripts/vrcrt.sh close-client and launch-client read it (cmd_close_client,
    /// cmd_launch_client): an answer in a shape the Bridge does not send is "unknown", and no
    /// answer at all is "unknown" too, because the Bridge may have acted before the answer was
    /// lost. Nothing here retries. Everything from the Bridge is untrusted and sanitized.
    /// </summary>
    internal static class SlotOperationReport
    {
        private const int MaxPid = int.MaxValue;
        private const long MaxGeneration = (1L << 53) - 1;

        internal static VrchatClientSettingsOutcome ReadClose(int slot, BridgeAdminResponse response)
        {
            if (!response.Supported || response.StatusCode == 0)
            {
                return VrchatClientSettingsOutcome.Failure(
                    $"No answer from the Bridge ({BridgeMessages.Sanitize(response.Message, 200)}): whether slot {slot} "
                    + "was closed is unknown. Check the client list before trying again.");
            }

            var body = BridgeMessages.Parse<JObject>(response.Body);
            if ((response.StatusCode == 200 || response.StatusCode == 409) && body != null && body["closed"] != null)
            {
                var closed = Int(body["closed"], 0, 1);
                var notConfirmed = Int(body["notConfirmed"], 0, 1);
                var pid = Int(body["processId"], 1, MaxPid);
                var updated = body["recordUpdated"]?.Type == JTokenType.Boolean ? (bool?)body["recordUpdated"] : null;
                var problems = Strings(body["problems"]);
                if (Int(body["slot"], slot, slot) == null || pid == null || closed == null || notConfirmed == null
                    || closed + notConfirmed != 1 || updated == null || problems == null
                    || (response.StatusCode == 200) != (closed == 1 && updated == true))
                {
                    return NotAReport("close", slot);
                }

                return response.StatusCode == 200
                    ? VrchatClientSettingsOutcome.Success($"Closed slot {slot} (PID {pid}).", problems)
                    : VrchatClientSettingsOutcome.Failure(
                        closed == 1
                            ? $"Slot {slot} (PID {pid}) closed, but the Bridge could not update its record."
                            : $"Slot {slot} (PID {pid}) did not confirm closing and may still be running. Check the client list.",
                        problems);
            }

            if (response.StatusCode == 200)
            {
                return NotAReport("close", slot);
            }

            return Refused("close", slot, response, body);
        }

        internal static VrchatClientSettingsOutcome ReadLaunch(int slot, BridgeAdminResponse response)
        {
            if (!response.Supported || response.StatusCode == 0)
            {
                // Never retried: the Bridge may have started the client before the answer was lost.
                return VrchatClientSettingsOutcome.Failure(
                    $"No answer from the Bridge ({BridgeMessages.Sanitize(response.Message, 200)}): whether slot {slot} "
                    + "started is unknown. Check the client list before launching again.");
            }

            var body = BridgeMessages.Parse<JObject>(response.Body);
            if (response.StatusCode == 200)
            {
                var profile = Int(body?["profile"], 0, 99);
                var pid = Int(body?["processId"], 1, MaxPid);
                var generation = Long(body?["clientGeneration"], 1, MaxGeneration);
                if (body == null || Int(body["slot"], slot, slot) == null || profile == null || pid == null || generation == null)
                {
                    return NotAReport("launch", slot);
                }

                return VrchatClientSettingsOutcome.Success(
                    $"Launched slot {slot} (profile {profile}, PID {pid}, generation {generation}).", null);
            }

            if (response.StatusCode == 500 && body?["error"]?.Type == JTokenType.String
                && (string)body["error"] == "launchedNotRecorded")
            {
                var pid = body["processId"]?.Type == JTokenType.Null ? null : Int(body["processId"], 1, MaxPid);
                var problems = Strings(body["problems"]);
                if (body["launched"]?.Type != JTokenType.Boolean || !(bool)body["launched"]
                    || body["recorded"]?.Type != JTokenType.Boolean || (bool)body["recorded"]
                    || (body["processId"] != null && body["processId"].Type != JTokenType.Null && pid == null)
                    || problems == null)
                {
                    return NotAReport("launch", slot);
                }

                return VrchatClientSettingsOutcome.Failure(
                    $"VRChat started in slot {slot} (PID {(pid?.ToString() ?? "unknown")}), but the Bridge could not record it, "
                    + "so it will not close it. Close it on Windows.", problems);
            }

            return Refused("launch", slot, response, body);
        }

        /// <summary>400/409/500 with {error, problems} (or busy): the Bridge did nothing.</summary>
        private static VrchatClientSettingsOutcome Refused(string what, int slot, BridgeAdminResponse response, JObject body)
        {
            if (response.StatusCode == 404 && body == null)
            {
                return VrchatClientSettingsOutcome.Failure(
                    $"This Bridge cannot {what} one client; it is older than v0.10.0 (update it on Windows).");
            }

            var error = body?["error"]?.Type == JTokenType.String ? (string)body["error"] : null;
            var problems = Strings(body?["problems"]);
            if ((response.StatusCode == 400 || response.StatusCode == 409 || response.StatusCode == 500) && error != null
                && (problems != null || (error == "busy" && body["problems"] == null)))
            {
                if (error == "busy")
                {
                    return VrchatClientSettingsOutcome.Failure(RemoteBuildCoordinator.AdminFailure($"{what} slot {slot}", response));
                }

                return VrchatClientSettingsOutcome.Failure(
                    $"The Bridge did not {what} slot {slot} ({BridgeMessages.Sanitize(error, 40)}).", problems);
            }

            return VrchatClientSettingsOutcome.Failure(
                $"The Bridge's answer to {what} slot {slot} is not one it sends (HTTP {response.StatusCode}); "
                + "whether it acted is unknown. Check the client list.");
        }

        private static VrchatClientSettingsOutcome NotAReport(string what, int slot) =>
            VrchatClientSettingsOutcome.Failure(
                $"The Bridge's answer to {what} slot {slot} is not a {what} report; whether it acted is unknown. "
                + "Check the client list.");

        private static int? Int(JToken token, int min, int max)
        {
            var value = Long(token, min, max);
            return value == null ? (int?)null : (int)value.Value;
        }

        private static long? Long(JToken token, long min, long max)
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
                default:
                    return null;
            }

            return number >= min && number <= max ? number : (long?)null;
        }

        /// <summary>A JSON array of strings, or null when it is missing or holds anything else.</summary>
        private static List<string> Strings(JToken token)
        {
            if (!(token is JArray array))
            {
                return null;
            }

            var list = new List<string>();
            foreach (var item in array)
            {
                if (item.Type != JTokenType.String)
                {
                    return null;
                }

                list.Add((string)item);
            }

            return list;
        }
    }
}
