using System.Security.Cryptography;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// One VRChat client to launch. Every value comes from the Bridge itself: the
/// validated config (executable, profile), the deployed file the Bridge named
/// (<c>vrc-remote-{validated buildId}.vrcw</c> in VrchatWorldsDirectory) and a room id
/// the Bridge generated -- never from a manifest (spec section 49).
/// </summary>
/// <param name="WorldPath">Full path of the deployed .vrcw that --url opens.</param>
/// <param name="RoomId">Shared by every client of one launch batch (see <see cref="NewRoomId"/>).</param>
/// <param name="NoVr">True for desktop clients; in VR mode only the first client uses the HMD.</param>
/// <param name="WatchWorlds">
/// False in Relaunch mode: a --watch-worlds client on another account would reload into
/// its own instance the moment the next build is deployed (spike case 5).
/// </param>
/// <param name="Profile">Relaunch mode only: the client's VRChat profile (0-99), i.e. its account.</param>
public sealed record VrchatLaunchRequest(
    string ExecutablePath, string WorldPath, string RoomId, bool NoVr, bool WatchWorlds = true, int? Profile = null)
{
    private const int RoomIdLength = 10;

    /// <summary>
    /// Ten random digits, the same shape the SDK's Build &amp; Test uses. VRChat does not
    /// derive the instance id from it (spike case 1), so it only has to be well-formed.
    /// </summary>
    public static string NewRoomId() =>
        string.Create(RoomIdLength, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
            }
        });
}
