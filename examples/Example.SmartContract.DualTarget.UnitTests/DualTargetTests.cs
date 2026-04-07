// Copyright (C) 2015-2026 The Neo Project.
//
// DualTargetTests.cs file belongs to the neo project and is free
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
using System.IO;
using System.Linq;

namespace Example.SmartContract.DualTarget.UnitTests;

/// <summary>
/// Demonstrates dual-target compilation: the same C# contract compiles
/// to both NeoVM (.nef) and RISC-V (Rust source) targets.
/// </summary>
[TestClass]
public class DualTargetTests
{
    private static readonly string ContractProjectPath = Path.GetFullPath(
        Path.Combine("..", "..", "..", "..", "Example.SmartContract.DualTarget",
            "Example.SmartContract.DualTarget.csproj"));

    [TestMethod]
    [TestCategory("RiscV")]
    public void CompilesToNeoVmTarget()
    {
        var options = new Neo.Compiler.CompilationOptions
        {
            Nullable = NullableContextOptions.Annotations,
        };
        var engine = new CompilationEngine(options);
        var contexts = engine.CompileProject(ContractProjectPath);

        Assert.AreEqual(1, contexts.Count);
        var ctx = contexts[0];
        Assert.IsTrue(ctx.Success, "Contract should compile successfully for NeoVM");
        Assert.AreEqual("DualTargetCounter", ctx.ContractName);
    }

    [TestMethod]
    [TestCategory("RiscV")]
    public void CompilesToRiscVTarget()
    {
        var options = new Neo.Compiler.CompilationOptions
        {
            Target = CompilationTarget.RiscV,
            Nullable = NullableContextOptions.Annotations,
        };
        var engine = new CompilationEngine(options);
        var contexts = engine.CompileProject(ContractProjectPath);

        Assert.AreEqual(1, contexts.Count);
        var ctx = contexts[0];
        Assert.IsTrue(ctx.Success, "Contract should compile successfully for RISC-V");
        Assert.AreEqual("DualTargetCounter", ctx.ContractName);
    }

    [TestMethod]
    [TestCategory("RiscV")]
    public void BothTargets_ProduceSameContractName()
    {
        var neovmOptions = new Neo.Compiler.CompilationOptions
        {
            Nullable = NullableContextOptions.Annotations,
        };
        var neovmEngine = new CompilationEngine(neovmOptions);
        var neovmContexts = neovmEngine.CompileProject(ContractProjectPath);

        var riscvOptions = new Neo.Compiler.CompilationOptions
        {
            Target = CompilationTarget.RiscV,
            Nullable = NullableContextOptions.Annotations,
        };
        var riscvEngine = new CompilationEngine(riscvOptions);
        var riscvContexts = riscvEngine.CompileProject(ContractProjectPath);

        Assert.AreEqual(neovmContexts[0].ContractName, riscvContexts[0].ContractName,
            "Both targets should produce the same contract name");
        Assert.IsTrue(neovmContexts[0].Success, "NeoVM compilation should succeed");
        Assert.IsTrue(riscvContexts[0].Success, "RISC-V compilation should succeed");
    }
}
