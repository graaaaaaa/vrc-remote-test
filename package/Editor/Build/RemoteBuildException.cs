using System;

namespace VRCRemoteTest
{
    public sealed class RemoteBuildException : Exception
    {
        public string ErrorCodeValue { get; }

        public RemoteBuildException(string errorCode, string message, Exception inner = null)
            : base(message, inner)
        {
            ErrorCodeValue = errorCode;
        }
    }
}
