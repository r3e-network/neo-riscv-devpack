namespace Neo.SmartContract.RiscV
{
    internal static class RiscvCompatibilityContracts
    {
        // Internal-only facade identity for legacy NeoVM execution on the outer RISC-V VM.
        // This is not a user-deployable contract hash and must not leak into contract-visible
        // script-hash semantics. It exists only to make the hidden compatibility route explicit.
        internal static readonly UInt160 LegacyNeoVmFacadeHash =
            new(new byte[]
            {
                0x4e, 0x65, 0x6f, 0x4c, 0x65, 0x67, 0x61, 0x63, 0x79, 0x56,
                0x4d, 0x52, 0x69, 0x73, 0x63, 0x76, 0x00, 0x00, 0x00, 0x01
            });

        internal static UInt160 ResolveExecutionFacadeHash(ContractType contractType, UInt160 actualScriptHash)
        {
            return contractType == ContractType.NeoVM ? LegacyNeoVmFacadeHash : actualScriptHash;
        }
    }
}
