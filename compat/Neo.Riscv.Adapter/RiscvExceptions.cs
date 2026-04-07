using System;

namespace Neo.SmartContract.RiscV
{
    public class VmExecutionException : Exception
    {
        public VmExecutionException(string message) : base(message) { }
        public VmExecutionException(string message, Exception inner) : base(message, inner) { }
    }

    public class FfiException : Exception
    {
        public int ErrorCode { get; }

        public FfiException(int errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }
    }
}
