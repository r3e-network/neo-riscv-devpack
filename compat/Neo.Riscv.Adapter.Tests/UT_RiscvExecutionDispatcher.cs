using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.SmartContract;
using Neo.SmartContract.RiscV;
using System;

namespace Neo.Riscv.Adapter.Tests;

[TestClass]
public class UT_RiscvExecutionDispatcher
{
    [TestMethod]
    public void RoutesLegacyNeoVmContractsToCompatibilityPath()
    {
        var kind = RiscvExecutionDispatcher.Resolve(
            ContractType.NeoVM,
            new byte[] { (byte)Neo.VM.OpCode.PUSH1, (byte)Neo.VM.OpCode.RET });

        Assert.AreEqual(RiscvExecutionKind.LegacyNeoVmCompatibility, kind);
    }

    [TestMethod]
    public void RoutesRiscvContractsToDirectPath()
    {
        var kind = RiscvExecutionDispatcher.Resolve(
            ContractType.RiscV,
            new byte[] { 0x50, 0x56, 0x4D, 0x00, 0x01 });

        Assert.AreEqual(RiscvExecutionKind.NativeRiscvDirect, kind);
    }

    [TestMethod]
    public void RejectsLegacyTypeWithPvmPayload()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            RiscvExecutionDispatcher.Resolve(
                ContractType.NeoVM,
                new byte[] { 0x50, 0x56, 0x4D, 0x00, 0x01 }));
    }

    [TestMethod]
    public void RejectsRiscvTypeWithLegacyPayload()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            RiscvExecutionDispatcher.Resolve(
                ContractType.RiscV,
                new byte[] { (byte)Neo.VM.OpCode.PUSH1, (byte)Neo.VM.OpCode.RET }));
    }

    [TestMethod]
    public void UsesLegacyFacadeHashForNeoVmContracts()
    {
        var actualHash = new UInt160(new byte[20]);
        Assert.AreEqual(
            RiscvCompatibilityContracts.LegacyNeoVmFacadeHash,
            RiscvCompatibilityContracts.ResolveExecutionFacadeHash(ContractType.NeoVM, actualHash));
    }

    [TestMethod]
    public void PreservesActualHashForNativeRiscvContracts()
    {
        var actualHash = new UInt160(new byte[]
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
            11, 12, 13, 14, 15, 16, 17, 18, 19, 20
        });
        Assert.AreEqual(
            RiscvCompatibilityContracts.ResolveExecutionFacadeHash(ContractType.RiscV, actualHash),
            actualHash);
    }
}
