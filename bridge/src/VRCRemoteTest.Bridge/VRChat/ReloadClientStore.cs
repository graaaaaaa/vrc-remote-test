using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// StateDirectory\reload-clients.json: the VRChat clients the Bridge launched in Reload
/// mode, in the same format as the Relaunch record (<see cref="ManagedClientStore"/>) but a
/// separate file. Reload mode itself never reads it: it reuses whatever --watch-worlds
/// client runs, launched by the Bridge or not. Only closing clients through the LAN API
/// (<see cref="OwnedClientCloser"/>), including before a change of VrchatClientMode, reads it,
/// so a client is closed only on the same four-part match as in Relaunch mode. The Log Viewer
/// and the Relaunch flow never see this file. Profile is always 0 here: Reload clients have no
/// profile. Clients launched before v0.8.0 have no record and are never closed by the Bridge.
/// </summary>
public sealed class ReloadClientStore
{
    public const string FileName = "reload-clients.json";

    public ReloadClientStore(IOptions<BridgeOptions> options)
        : this(options.Value.StateDirectory)
    {
    }

    internal ReloadClientStore(string stateDirectory)
    {
        Store = new ManagedClientStore(stateDirectory, FileName);
    }

    public IManagedClientStore Store { get; }
}
