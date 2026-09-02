namespace VRCRemoteTest.Bridge.VRChat;

public interface IVrchatLauncher
{
    /// <summary>Launches VRChat with --watch-worlds (and mode-appropriate flags). Returns false on failure.</summary>
    Task<bool> LaunchAsync(string executablePath, string mode);
}
