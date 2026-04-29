// Copyright (C) 2015-2026 The Neo Project.
//
// UnitTest_RiscVEndToEnd.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Compiler;
using Neo.Compiler.Backend.RiscV;
using Neo.Extensions;
using Neo.SmartContract;
using Neo.SmartContract.Manifest;
using Neo.SmartContract.Native;
using Neo.SmartContract.Testing;
using Neo.VM;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using ContractOnDeploymentArtifact = Neo.SmartContract.Testing.Contract_OnDeployment1;
using ContractTypesArtifact = Neo.SmartContract.Testing.Contract_Types;

namespace Neo.Compiler.CSharp.UnitTests;

[TestClass]
[TestCategory("RiscV")]
public class UnitTest_RiscVEndToEnd
{
    /// <summary>
    /// Path to test contracts project, resolved the same way as TestCleanup.cs.
    /// From bin/Debug/net10.0 we go ../../../../Neo.Compiler.CSharp.TestContracts/
    /// </summary>
    private static readonly string TestContractsPath = Path.GetFullPath(
        Path.Combine("..", "..", "..", "..", "Neo.Compiler.CSharp.TestContracts",
            "Neo.Compiler.CSharp.TestContracts.csproj"));

    [TestMethod]
    public void TestCompileContract_RiscV_GeneratesRust()
    {
        var ctx = CompileRiscVContext("Contract_Assignment");
        Assert.IsNotNull(ctx.GeneratedRustSource,
            "GeneratedRustSource should be populated for RiscV target");

        var rust = ctx.GeneratedRustSource!;

        // Verify Rust file structure
        Assert.IsTrue(rust.Contains("use neo_riscv_rt::Context;"),
            "Should import Context");
        Assert.IsTrue(rust.Contains("fn dispatch(ctx: &mut Context, method: &str)"),
            "Should have dispatch function");

        // Verify at least one method was translated
        Assert.IsTrue(rust.Contains("fn method_"),
            "Should have at least one compiled method");

        // Verify the dispatch table references the contract's exported methods
        Assert.IsTrue(rust.Contains("\"testAssignment\"") || rust.Contains("\"TestAssignment\""),
            $"Should contain a dispatch entry for testAssignment/TestAssignment. Dispatch section:\n{rust.Substring(rust.IndexOf("fn dispatch(ctx"))}");

    }

    [TestMethod]
    public void TestCompileDeployInvokeAndCrossCall_RiscVAndNeoVmContracts()
    {
        var ctx = CompileRiscVContext("Contract_Types");
        RiscVTestHelper.Initialize();
        var polkavmPath = RiscVTestHelper.GetPolkaVmBinary("Contract_Types");
        if (polkavmPath is null)
            Assert.Inconclusive("Contract_Types RISC-V binary could not be built in this environment.");

        var riscvNef = RiscVBuildHelper.CreateDeployableNef(
            ctx.CreateExecutable(),
            File.ReadAllBytes(polkavmPath));
        var riscvManifest = ctx.CreateManifest();

        var engine = new TestEngine(true, ExecutionBackend.RiscV)
        {
            Fee = 10000_00000000,
        };
        engine.SetTransactionSigners(UInt160.Parse("0xa400ff00ff00ff00ff00ff00ff00ff00ff00ff01"));

        var neoVmState = Deploy(engine, ContractTypesArtifact.Nef, ContractTypesArtifact.Manifest);
        Assert.AreEqual(ContractType.NeoVM, neoVmState.Type);

        var riscvState = Deploy(engine, riscvNef, riscvManifest);
        Assert.AreEqual(ContractType.RiscV, riscvState.Type);

        using var neoVmCallsRiscV = new ScriptBuilder();
        neoVmCallsRiscV.EmitDynamicCall(riscvState.Hash, "checkInt", CallFlags.All);
        Assert.AreEqual(new BigInteger(5), engine.Execute(neoVmCallsRiscV.ToArray()).GetInteger());

        using var riscvCallsNeoVm = new ScriptBuilder();
        riscvCallsNeoVm.Emit(OpCode.NEWARRAY0);
        riscvCallsNeoVm.EmitPush(CallFlags.All);
        riscvCallsNeoVm.EmitPush("checkInt");
        riscvCallsNeoVm.EmitPush(neoVmState.Hash);
        riscvCallsNeoVm.EmitPush(4);
        riscvCallsNeoVm.Emit(OpCode.PACK);
        riscvCallsNeoVm.EmitPush(CallFlags.All);
        riscvCallsNeoVm.EmitPush("call");
        riscvCallsNeoVm.EmitPush(riscvState.Hash);
        riscvCallsNeoVm.EmitSysCall(ApplicationEngine.System_Contract_Call);
        Assert.AreEqual(new BigInteger(5), engine.Execute(riscvCallsNeoVm.ToArray()).GetInteger());
    }

    [TestMethod]
    public void TestCompileDeployInvoke_RiscVRuntimeNotifyAndLog()
    {
        var engine = new TestEngine(true, ExecutionBackend.RiscV)
        {
            Fee = 10000_00000000,
        };
        engine.SetTransactionSigners(UInt160.Parse("0xa400ff00ff00ff00ff00ff00ff00ff00ff00ff01"));

        var typesContract = DeployRiscV<ContractTypesArtifact>(engine, "Contract_Types");
        var notifications = new List<string>();
        var handler = new ContractTypesArtifact.delDummyEvent(notifications.Add!);
        typesContract.OnDummyEvent += handler;
        typesContract.CheckEvent();
        typesContract.OnDummyEvent -= handler;

        Assert.AreEqual(1, notifications.Count);
        Assert.AreEqual("neo", notifications.Single());

        var logs = new List<string>();
        engine.OnRuntimeLog += (_, message) => logs.Add(message);
        DeployRiscV<ContractOnDeploymentArtifact>(engine, "Contract_OnDeployment1");

        CollectionAssert.AreEqual(new[] { "Deployed" }, logs);
    }

    [TestMethod]
    public void TestNativeGasTransferInvokesRiscVOnNEP17PaymentAndReturnsBoolean()
    {
        var engine = new TestEngine(true, ExecutionBackend.RiscV)
        {
            Fee = 10000_00000000,
        };
        engine.SetTransactionSigners(engine.ValidatorsAddress);

        var receiver = DeployRiscV(engine, "Contract_NEP17Receiver");

        var logs = new List<string>();
        engine.OnRuntimeLog += (_, message) => logs.Add(message);

        using var transfer = new ScriptBuilder();
        transfer.EmitDynamicCall(
            NativeContract.GAS.Hash,
            "transfer",
            engine.ValidatorsAddress,
            receiver.Hash,
            BigInteger.One,
            "payload");

        Assert.IsTrue(engine.Execute(transfer.ToArray()).GetBoolean());
        CollectionAssert.AreEqual(new[] { "onNEP17Payment" }, logs);
    }

    private static CompilationContext CompileRiscVContext(string contractName)
    {
        Assert.IsTrue(File.Exists(TestContractsPath),
            $"Test contracts project not found at: {TestContractsPath}");

        var options = new CompilationOptions
        {
            Target = CompilationTarget.RiscV,
            Nullable = NullableContextOptions.Annotations,
        };
        var engine = new CompilationEngine(options);
        var contexts = engine.CompileProject(TestContractsPath);

        var ctx = contexts.FirstOrDefault(c => c.ContractName == contractName);
        Assert.IsNotNull(ctx, $"{contractName} should be found among compiled contexts");

        Assert.IsTrue(ctx.Success,
            $"Compilation should succeed. Diagnostics: {string.Join("; ", ctx.Diagnostics.Select(d => d.ToString()))}");

        return ctx;
    }

    private static T DeployRiscV<T>(TestEngine engine, string contractName)
        where T : Neo.SmartContract.Testing.SmartContract
    {
        var (nef, manifest) = CreateRiscVArtifacts(contractName);
        return engine.Deploy<T>(nef, manifest);
    }

    private static ContractState DeployRiscV(TestEngine engine, string contractName)
    {
        var (nef, manifest) = CreateRiscVArtifacts(contractName);
        return Deploy(engine, nef, manifest);
    }

    private static (NefFile Nef, ContractManifest Manifest) CreateRiscVArtifacts(string contractName)
    {
        var ctx = CompileRiscVContext(contractName);
        RiscVTestHelper.Initialize();
        var polkavmPath = RiscVTestHelper.GetPolkaVmBinary(contractName);
        if (polkavmPath is null)
            Assert.Inconclusive($"{contractName} RISC-V binary could not be built in this environment.");

        var nef = RiscVBuildHelper.CreateDeployableNef(
            ctx.CreateExecutable(),
            File.ReadAllBytes(polkavmPath!));
        return (nef, ctx.CreateManifest());
    }

    private static ContractState Deploy(TestEngine engine, NefFile nef, ContractManifest manifest)
    {
        return engine.Native.ContractManagement.Deploy(
            nef.ToArray(),
            Encoding.UTF8.GetBytes(manifest.ToJson().ToString(false)),
            null);
    }
}
