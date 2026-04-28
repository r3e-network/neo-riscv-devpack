// Copyright (C) 2015-2026 The Neo Project.
//
// UnitTest_RiscVTarget.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Compiler.Backend.RiscV;
using Neo.SmartContract;
using Neo.VM;
using System;

namespace Neo.Compiler.CSharp.UnitTests;

[TestClass]
[TestCategory("RiscV")]
public class UnitTest_RiscVTarget
{
    [TestMethod]
    public void TestRiscVEmitter_GeneratesRustSource()
    {
        var emitter = new RiscVEmitter();
        emitter.BeginMethod("balanceOf", 1, 0);
        emitter.InitSlot(0, 1);
        emitter.Syscall(0x4a100170); // System.Storage.GetContext
        emitter.LdArg(0);
        emitter.Convert(2); // ByteString
        emitter.Syscall(0x31e85d92); // System.Storage.Get
        emitter.Convert(0); // Integer
        emitter.Ret();
        emitter.EndMethod();

        var rustSource = emitter.Builder.Build("TestContract");

        Assert.IsTrue(rustSource.Contains("fn method_balanceof(ctx: &mut Context)"));
        Assert.IsTrue(rustSource.Contains("ctx.init_slot(0, 1);"));
        Assert.IsTrue(rustSource.Contains("bridge_syscall(ctx, 0x4a100170);"));
        Assert.IsTrue(rustSource.Contains("ctx.load_arg(0);"));
        Assert.IsTrue(rustSource.Contains("ctx.convert(0x02);"));
        Assert.IsTrue(rustSource.Contains("ctx.ret();"));
        Assert.IsTrue(rustSource.Contains("\"balanceOf\" => method_balanceof(ctx)"));
        Assert.IsTrue(rustSource.Contains("pub extern \"C\" fn execute_method(method_id: u32, stack_ptr: u32, stack_len: u32)"));
        Assert.IsTrue(rustSource.Contains("fn dispatch_by_id(ctx: &mut Context, method_id: u32)"));
    }

    [TestMethod]
    public void TestRiscVEmitter_RoutesStorageContextSyscallsThroughBridge()
    {
        var emitter = new RiscVEmitter();
        emitter.BeginMethod("storage", 0, 0);
        emitter.Syscall(ApplicationEngine.System_Storage_GetContext.Hash);
        emitter.Syscall(ApplicationEngine.System_Storage_GetReadOnlyContext.Hash);
        emitter.Syscall(ApplicationEngine.System_Storage_AsReadOnly.Hash);
        emitter.EndMethod();

        var rustSource = emitter.Builder.Build("StorageContract");

        Assert.IsTrue(rustSource.Contains($"bridge_syscall(ctx, 0x{ApplicationEngine.System_Storage_GetContext.Hash:x8});"));
        Assert.IsTrue(rustSource.Contains($"bridge_syscall(ctx, 0x{ApplicationEngine.System_Storage_GetReadOnlyContext.Hash:x8});"));
        Assert.IsTrue(rustSource.Contains($"bridge_syscall(ctx, 0x{ApplicationEngine.System_Storage_AsReadOnly.Hash:x8});"));
        Assert.IsFalse(rustSource.Contains("ctx.push_int(0);"));
        Assert.IsFalse(rustSource.Contains("Storage.AsReadOnly is a no-op"));
    }

    [TestMethod]
    public void TestInstructionTranslator_RoutesStorageContextSyscallsThroughBridge()
    {
        Assert.AreEqual(
            $"bridge_syscall(ctx, 0x{ApplicationEngine.System_Storage_GetContext.Hash:x8});",
            InstructionTranslator.Translate(new Instruction
            {
                OpCode = OpCode.SYSCALL,
                Operand = BitConverter.GetBytes(ApplicationEngine.System_Storage_GetContext.Hash),
            }));
        Assert.AreEqual(
            $"bridge_syscall(ctx, 0x{ApplicationEngine.System_Storage_GetReadOnlyContext.Hash:x8});",
            InstructionTranslator.Translate(new Instruction
            {
                OpCode = OpCode.SYSCALL,
                Operand = BitConverter.GetBytes(ApplicationEngine.System_Storage_GetReadOnlyContext.Hash),
            }));
        Assert.AreEqual(
            $"bridge_syscall(ctx, 0x{ApplicationEngine.System_Storage_AsReadOnly.Hash:x8});",
            InstructionTranslator.Translate(new Instruction
            {
                OpCode = OpCode.SYSCALL,
                Operand = BitConverter.GetBytes(ApplicationEngine.System_Storage_AsReadOnly.Hash),
            }));
    }

    [TestMethod]
    public void TestRiscVEmitter_MultipleMethodDispatch()
    {
        var emitter = new RiscVEmitter();

        emitter.BeginMethod("transfer", 3, 0);
        emitter.InitSlot(0, 3);
        emitter.Ret();
        emitter.EndMethod();

        emitter.BeginMethod("balanceOf", 1, 0);
        emitter.InitSlot(0, 1);
        emitter.Ret();
        emitter.EndMethod();

        var rustSource = emitter.Builder.Build("TokenContract");

        Assert.IsTrue(rustSource.Contains("\"transfer\" => method_transfer(ctx)"));
        Assert.IsTrue(rustSource.Contains("\"balanceOf\" => method_balanceof(ctx)"));
        Assert.IsTrue(rustSource.Contains("_ => ctx.fault(\"Unknown method\")"));
    }

    [TestMethod]
    public void CreateDeployableNefEmbedsPolkaVmBinaryAndRecomputesChecksum()
    {
        var source = new NefFile
        {
            Compiler = "test",
            Source = string.Empty,
            Tokens = [],
            Script = new byte[] { (byte)OpCode.RET },
        };
        source.CheckSum = NefFile.ComputeChecksum(source);
        var polkaVmBinary = new byte[] { 0x50, 0x56, 0x4d, 0x00, 0x01, 0x02, 0x03, 0x04 };

        var deployable = RiscVBuildHelper.CreateDeployableNef(source, polkaVmBinary);

        CollectionAssert.AreEqual(polkaVmBinary, deployable.Script.ToArray());
        Assert.AreEqual(NefFile.ComputeChecksum(deployable), deployable.CheckSum);
        Assert.AreNotEqual(source.CheckSum, deployable.CheckSum);
    }

    [TestMethod]
    public void RunCommandPreservesArgumentBoundaries()
    {
        var output = RiscVBuildHelper.RunCommand("printf", ["%s", "hello world"]);

        Assert.AreEqual("hello world", output);
    }
}
