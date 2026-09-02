namespace VRCRemoteTest
{
    /// <summary>Terminal result of a single ExecuteRemoteBuildAsync run.</summary>
    public sealed class RemoteBuildOutcome
    {
        public bool Succeeded { get; }
        public string BuildId { get; }
        public BuildResult Result { get; }
        public string ErrorCode { get; }
        public string ErrorMessage { get; }

        private RemoteBuildOutcome(
            bool succeeded, string buildId, BuildResult result, string errorCode, string errorMessage)
        {
            Succeeded = succeeded;
            BuildId = buildId;
            Result = result;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        public static RemoteBuildOutcome Success(string buildId, BuildResult result) =>
            new RemoteBuildOutcome(true, buildId, result, null, null);

        public static RemoteBuildOutcome Failure(string buildId, string errorCode, string errorMessage) =>
            new RemoteBuildOutcome(false, buildId, null, errorCode, errorMessage);
    }
}
