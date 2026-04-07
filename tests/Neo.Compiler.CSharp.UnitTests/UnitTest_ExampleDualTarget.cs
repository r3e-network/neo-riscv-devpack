// Copyright (C) 2015-2026 The Neo Project.
//
// UnitTest_ExampleDualTarget.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Neo.Compiler.CSharp.UnitTests;

/// <summary>
/// Verifies that all example contracts compile to both NeoVM and RISC-V
/// targets with matching contract names and valid generated Rust structure.
/// This is the integration-level dual-target test complementing the
/// per-example RiscV tests in each example's UnitTests project.
/// </summary>
[TestClass]
[TestCategory("RiscV")]
public class UnitTest_ExampleDualTarget
{
    private static readonly string ExamplesRoot = Path.GetFullPath(
        Path.Combine("..", "..", "..", "..", "..", "examples"));

    private static readonly (string CsProjRelative, string ExpectedName)[] ExampleContracts =
    {
        ("Example.SmartContract.HelloWorld/Example.SmartContract.HelloWorld.csproj", "SampleHelloWorld"),
        ("Example.SmartContract.DualTarget/Example.SmartContract.DualTarget.csproj", "DualTargetCounter"),
        ("Example.SmartContract.Storage/Example.SmartContract.Storage.csproj", "SampleStorage"),
        ("Example.SmartContract.NEP17/Example.SmartContract.NEP17.csproj", "SampleNep17Token"),
        ("Example.SmartContract.Event/Example.SmartContract.Event.csproj", "SampleEvent"),
        ("Example.SmartContract.ContractCall/Example.SmartContract.ContractCall.csproj", "SampleContractCall"),
        ("Example.SmartContract.Transfer/Example.SmartContract.Transfer.csproj", "SampleTransferContract"),
        ("Example.SmartContract.Modifier/Example.SmartContract.Modifier.csproj", "SampleModifier"),
        ("Example.SmartContract.Exception/Example.SmartContract.Exception.csproj", "SampleException"),
    };

    [TestMethod]
    public void AllExamples_CompileToBothTargets()
    {
        var failures = new List<string>();

        foreach (var (csproj, expectedName) in ExampleContracts)
        {
            var fullPath = Path.Combine(ExamplesRoot, csproj);
            if (!File.Exists(fullPath))
            {
                failures.Add($"{expectedName}: .csproj not found at {fullPath}");
                continue;
            }

            // NeoVM compilation
            var neovmOptions = new CompilationOptions
            {
                Nullable = NullableContextOptions.Annotations,
            };
            var neovmEngine = new CompilationEngine(neovmOptions);
            var neovmContexts = neovmEngine.CompileProject(fullPath);
            var neovmCtx = neovmContexts.FirstOrDefault();

            if (neovmCtx == null || !neovmCtx.Success)
            {
                failures.Add($"{expectedName}: NeoVM compilation failed");
                continue;
            }

            // RISC-V compilation
            var riscvOptions = new CompilationOptions
            {
                Target = CompilationTarget.RiscV,
                Nullable = NullableContextOptions.Annotations,
            };
            var riscvEngine = new CompilationEngine(riscvOptions);
            var riscvContexts = riscvEngine.CompileProject(fullPath);
            var riscvCtx = riscvContexts.FirstOrDefault();

            if (riscvCtx == null || !riscvCtx.Success)
            {
                failures.Add($"{expectedName}: RISC-V compilation failed");
                continue;
            }

            // Contract names must match
            if (neovmCtx.ContractName != riscvCtx.ContractName)
            {
                failures.Add($"{expectedName}: name mismatch NeoVM={neovmCtx.ContractName} RiscV={riscvCtx.ContractName}");
            }
        }

        Assert.IsTrue(failures.Count == 0,
            $"Dual-target compilation failures:\n{string.Join("\n", failures)}");
    }

    [TestMethod]
    public void AllExamples_RiscV_GenerateValidRustStructure()
    {
        var failures = new List<string>();

        foreach (var (csproj, expectedName) in ExampleContracts)
        {
            var fullPath = Path.Combine(ExamplesRoot, csproj);
            if (!File.Exists(fullPath)) continue;

            var options = new CompilationOptions
            {
                Target = CompilationTarget.RiscV,
                Nullable = NullableContextOptions.Annotations,
            };
            var engine = new CompilationEngine(options);
            var contexts = engine.CompileProject(fullPath);
            var ctx = contexts.FirstOrDefault();

            if (ctx == null || !ctx.Success)
            {
                failures.Add($"{expectedName}: RISC-V compilation failed");
                continue;
            }

            var rust = ctx.GeneratedRustSource;
            if (rust == null)
            {
                failures.Add($"{expectedName}: no Rust source generated");
                continue;
            }

            if (!rust.Contains("fn dispatch("))
                failures.Add($"{expectedName}: missing dispatch function");

            if (!rust.Contains("neo_riscv_rt"))
                failures.Add($"{expectedName}: missing neo_riscv_rt import");

            if (rust.Contains("todo!()"))
                failures.Add($"{expectedName}: contains todo!() macro");

            if (rust.Contains("unimplemented!()"))
                failures.Add($"{expectedName}: contains unimplemented!() macro");

            // Balanced braces check
            int braces = 0;
            foreach (char ch in rust)
            {
                if (ch == '{') braces++;
                else if (ch == '}') braces--;
            }
            if (braces != 0)
                failures.Add($"{expectedName}: unbalanced braces (delta={braces})");

            // Cargo.toml validation
            var cargo = ctx.GeneratedCargoToml;
            if (cargo == null)
            {
                failures.Add($"{expectedName}: missing Cargo.toml");
                continue;
            }
            if (!cargo.Contains("[package]"))
                failures.Add($"{expectedName}: Cargo.toml missing [package]");
            if (!cargo.Contains("neo-riscv-rt"))
                failures.Add($"{expectedName}: Cargo.toml missing neo-riscv-rt");
            if (!cargo.Contains("[profile.release]"))
                failures.Add($"{expectedName}: Cargo.toml missing [profile.release]");
        }

        Assert.IsTrue(failures.Count == 0,
            $"RISC-V structure validation failures:\n{string.Join("\n", failures)}");
    }

    [TestMethod]
    public void AllExamples_HaveMatchingContractNames()
    {
        foreach (var (csproj, expectedName) in ExampleContracts)
        {
            var fullPath = Path.Combine(ExamplesRoot, csproj);
            if (!File.Exists(fullPath)) continue;

            var neovmOptions = new CompilationOptions
            {
                Nullable = NullableContextOptions.Annotations,
            };
            var neovmEngine = new CompilationEngine(neovmOptions);
            var neovmContexts = neovmEngine.CompileProject(fullPath);
            var neovmCtx = neovmContexts.FirstOrDefault();

            var riscvOptions = new CompilationOptions
            {
                Target = CompilationTarget.RiscV,
                Nullable = NullableContextOptions.Annotations,
            };
            var riscvEngine = new CompilationEngine(riscvOptions);
            var riscvContexts = riscvEngine.CompileProject(fullPath);
            var riscvCtx = riscvContexts.FirstOrDefault();

            Assert.IsNotNull(neovmCtx, $"{expectedName}: NeoVM context is null");
            Assert.IsNotNull(riscvCtx, $"{expectedName}: RISC-V context is null");
            Assert.IsTrue(neovmCtx!.Success, $"{expectedName}: NeoVM compilation failed");
            Assert.IsTrue(riscvCtx!.Success, $"{expectedName}: RISC-V compilation failed");
            Assert.AreEqual(neovmCtx.ContractName, riscvCtx.ContractName,
                $"{expectedName}: contract name mismatch between targets");
            Assert.AreEqual(expectedName, riscvCtx.ContractName,
                $"{expectedName}: unexpected contract name");
        }
    }
}
