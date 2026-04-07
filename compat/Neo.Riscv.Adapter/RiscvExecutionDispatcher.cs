using System;

namespace Neo.SmartContract.RiscV
{
    internal enum RiscvExecutionKind : byte
    {
        LegacyNeoVmCompatibility = 0,
        NativeRiscvDirect = 1,
    }

    internal static class RiscvExecutionDispatcher
    {
        internal static RiscvExecutionKind Resolve(ContractType contractType, ReadOnlyMemory<byte> script)
        {
            var isPvmBinary = IsPvmBinary(script);

            return contractType switch
            {
                ContractType.NeoVM when !isPvmBinary => RiscvExecutionKind.LegacyNeoVmCompatibility,
                ContractType.RiscV when isPvmBinary => RiscvExecutionKind.NativeRiscvDirect,
                ContractType.NeoVM => throw new InvalidOperationException("Legacy NeoVM contract cannot use a PolkaVM binary payload."),
                ContractType.RiscV => throw new InvalidOperationException("RISC-V contract must use a PolkaVM binary payload."),
                _ => throw new InvalidOperationException($"Unsupported contract execution kind: {contractType}."),
            };
        }

        private static bool IsPvmBinary(ReadOnlyMemory<byte> script)
        {
            var span = script.Span;
            return span.Length >= 4
                && span[0] == 0x50
                && span[1] == 0x56
                && span[2] == 0x4D
                && span[3] == 0x00;
        }
    }
}
