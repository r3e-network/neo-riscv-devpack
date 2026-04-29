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
    /// Shared RISC-V bridge instance (created once, reused across tests).
    /// </summary>
    private static readonly IRiscvVmBridge? s_riscvBridge;
    private static readonly ExecutionBackend s_backend;

    static DebugAndTestBase()
    {
        s_backend = ResolveBackendFromEnvironment();
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
                throw new InvalidOperationException(
                    "[RISC-V] native host library not found. Set NEO_RISCV_HOST_LIB or use NEO_TEST_BACKEND=neovm.");
            }
        }

        var context = TestCleanup.TestInitialize(typeof(T));
        TestSingleContractBasicBlockStartEnd(context!);
    }

    private static ExecutionBackend ResolveBackendFromEnvironment()
    {
        var backendEnv = Environment.GetEnvironmentVariable("NEO_TEST_BACKEND");
        if (string.IsNullOrWhiteSpace(backendEnv) ||
            backendEnv.Equals("neovm", StringComparison.OrdinalIgnoreCase))
            return ExecutionBackend.NeoVM;

        if (backendEnv.Equals("riscv", StringComparison.OrdinalIgnoreCase))
            return ExecutionBackend.RiscV;

        throw new InvalidOperationException(
            $"Unsupported test backend '{backendEnv}'. Use 'neovm' or 'riscv'.");
    }

    private static string? FindNativeLibrary()
    {
        var envPath = Environment.GetEnvironmentVariable("NEO_RISCV_HOST_LIB");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        foreach (var path in EnumerateNativeLibraryCandidates())
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
                return full;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateNativeLibraryCandidates()
    {
        var fileName = GetPlatformFileName();
        yield return Path.Combine(AppContext.BaseDirectory, fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "Plugins", "Neo.Riscv.Adapter", fileName);

        foreach (var root in EnumerateNativeLibrarySearchRoots().Distinct())
        {
            yield return Path.Combine(root, "target", "release", fileName);
            yield return Path.Combine(root, "target", "debug", fileName);
            yield return Path.Combine(root, "dist", "Plugins", "Neo.Riscv.Adapter", fileName);
            yield return Path.Combine(root, "neo-riscv-vm", "target", "release", fileName);
            yield return Path.Combine(root, "neo-riscv-vm", "target", "debug", fileName);
            yield return Path.Combine(root, "neo-riscv-vm", "dist", "Plugins", "Neo.Riscv.Adapter", fileName);
            yield return Path.Combine(root, "neo-riscv-core", "tests", "Neo.UnitTests", "Plugins", "Neo.Riscv.Adapter", fileName);
        }
    }

    private static string GetPlatformFileName()
    {
        if (OperatingSystem.IsWindows())
            return "neo_riscv_host.dll";
        if (OperatingSystem.IsMacOS())
            return "libneo_riscv_host.dylib";
        return "libneo_riscv_host.so";
    }

    private static IEnumerable<string> EnumerateNativeLibrarySearchRoots()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = Path.GetFullPath(start);
            while (!string.IsNullOrEmpty(directory))
            {
                yield return directory;

                var parent = Path.GetDirectoryName(directory);
                if (string.IsNullOrEmpty(parent) || parent == directory)
                    break;

                directory = parent;
            }
        }
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
        var engine = new TestEngine(true, s_backend, s_backend == ExecutionBackend.RiscV ? s_riscvBridge : null);
        engine.SetTransactionSigners(Alice);
        return engine;
    }

    protected void AssertGasConsumed(long gasConsumed)
    {
        if (TestGasConsume && Engine.Backend == ExecutionBackend.NeoVM)
            Assert.AreEqual(gasConsumed, Engine.FeeConsumed.Value);
        // Skip gas assertion for RISC-V — gas model differs
    }
}
