// Copyright (C) 2015-2026 The Neo Project.
//
// DebugAndTestBase.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Json;
using Neo.Optimizer;
using Neo.SmartContract;
using Neo.SmartContract.Manifest;
using Neo.SmartContract.RiscV;
using Neo.SmartContract.Testing;
using Neo.SmartContract.Testing.Coverage;
using Neo.SmartContract.Testing.TestingStandards;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Neo.Compiler.CSharp.UnitTests;

public class DebugAndTestBase<T> : TestBase<T>
    where T : SmartContract.Testing.SmartContract, IContractInfo
{
    // allowing specific derived class to enable/disable Gas test
    protected virtual bool TestGasConsume { set; get; } = true;

    /// <summary>
    /// Whether to use RISC-V backend for execution.
    /// Controlled by NEO_TEST_BACKEND environment variable.
    /// </summary>
    private static readonly ExecutionBackend s_backend;

    /// <summary>
    /// Shared RISC-V bridge instance (created once, reused across tests).
    /// </summary>
    private static readonly IRiscvVmBridge? s_riscvBridge;

    static DebugAndTestBase()
    {
        // Read backend from environment
        var backendEnv = Environment.GetEnvironmentVariable("NEO_TEST_BACKEND") ?? "neovm";
        s_backend = backendEnv.Equals("riscv", StringComparison.OrdinalIgnoreCase)
            ? ExecutionBackend.RiscV
            : ExecutionBackend.NeoVM;

        // Initialize the RISC-V bridge if requested
        if (s_backend == ExecutionBackend.RiscV)
        {
            var libPath = FindNativeLibrary();
            if (libPath != null)
            {
                s_riscvBridge = new NativeRiscvVmBridge(libPath);
                Console.Error.WriteLine($"[RISC-V] Bridge initialized from {libPath}");
            }
            else
            {
                Console.Error.WriteLine("[RISC-V] WARNING: libneo_riscv_host.so not found, falling back to NeoVM");
            }
        }

        var context = TestCleanup.TestInitialize(typeof(T));
        TestSingleContractBasicBlockStartEnd(context!);
    }

    private static string? FindNativeLibrary()
    {
        var envPath = Environment.GetEnvironmentVariable("NEO_RISCV_HOST_LIB");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        var candidates = new[]
        {
            // Next to test binary (copied by MSBuild Content item)
            Path.Combine(AppContext.BaseDirectory, "libneo_riscv_host.so"),
            // Repo root build output
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "target", "release", "libneo_riscv_host.so")),
            // Absolute fallback
            "/home/neo/git/neo-riscv-vm/target/release/libneo_riscv_host.so",
        };

        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
                return full;
        }

        return null;
    }

    public static void TestSingleContractBasicBlockStartEnd(CompilationContext result)
    {
        TestSingleContractBasicBlockStartEnd(result.CreateExecutable(), result.CreateManifest(), result.CreateDebugInformation());
    }

    public static void TestSingleContractBasicBlockStartEnd(NefFile nef, ContractManifest manifest, JObject? debugInfo)
    {
        // Make sure the contract is optimized with RemoveUncoveredInstructions
        // Basic block analysis does not consider jump targets that are not covered
        (nef, manifest, debugInfo) = Reachability.RemoveUncoveredInstructions(nef, manifest, debugInfo);
        var basicBlocks = new ContractInBasicBlocks(nef, manifest, debugInfo);

        List<VM.Instruction> instructions = basicBlocks.coverage.addressAndInstructions.Select(kv => kv.i).ToList();
        Dictionary<VM.Instruction, HashSet<VM.Instruction>> jumpTargets = basicBlocks.coverage.jumpTargetToSources;

        Dictionary<VM.Instruction, VM.Instruction> nextAddrTable = new();
        VM.Instruction? prev = null;
        foreach (VM.Instruction i in instructions)
        {
            if (prev != null)
                nextAddrTable[prev] = i;
            prev = i;
        }

        foreach (BasicBlock basicBlock in basicBlocks.sortedBasicBlocks)
        {
            // Basic block ends with allowed OpCodes only, or the next instruction is a jump target
            Assert.IsTrue(OpCodeTypes.allowedBasicBlockEnds.Contains(basicBlock.instructions.Last().OpCode) || jumpTargets.ContainsKey(nextAddrTable[basicBlock.instructions.Last()]));
            // Instructions except the first are not jump targets
            foreach (VM.Instruction i in basicBlock.instructions.Skip(1))
                Assert.IsFalse(jumpTargets.ContainsKey(i));
            // Other instructions in the basic block are not those in allowedBasicBlockEnds
            foreach (VM.Instruction i in basicBlock.instructions.Take(basicBlock.instructions.Count - 1))
                Assert.IsFalse(OpCodeTypes.allowedBasicBlockEnds.Contains(i.OpCode));
        }

        // Each jump target starts a new basic block
        foreach (VM.Instruction target in jumpTargets.Keys)
            Assert.IsTrue(basicBlocks.basicBlocksByStartInstruction.ContainsKey(target));

        // Each instruction is included in only 1 basic block
        HashSet<VM.Instruction> includedInstructions = new();
        foreach (BasicBlock basicBlock in basicBlocks.sortedBasicBlocks)
            foreach (VM.Instruction instruction in basicBlock.instructions)
            {
                Assert.IsFalse(includedInstructions.Contains(instruction));
                includedInstructions.Add(instruction);
            }
    }

    protected override TestEngine CreateTestEngine()
    {
        var engine = base.CreateTestEngine();

        // Wire up the RISC-V bridge before any contracts are deployed
        if (s_backend == ExecutionBackend.RiscV && s_riscvBridge != null)
        {
            engine.RiscVBridge = s_riscvBridge;
            engine.Backend = ExecutionBackend.RiscV;
        }

        return engine;
    }

    protected void AssertGasConsumed(long gasConsumed)
    {
        if (TestGasConsume && Engine.Backend == ExecutionBackend.NeoVM)
            Assert.AreEqual(gasConsumed, Engine.FeeConsumed.Value);
        // Skip gas assertion for RISC-V — gas model differs
    }
}
