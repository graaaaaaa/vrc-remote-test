namespace VRCRemoteTest.Bridge.Protocol;

public static class ProtocolConstants
{
    public const int CurrentProtocolVersion = 1;

    /// <summary>
    /// Suffix used for the manifest "commit" file. Its appearance in incoming/ is the
    /// sole trigger for the Bridge to begin processing a build (see spec section 17).
    /// </summary>
    public const string ManifestExtension = ".ready.json";
}
