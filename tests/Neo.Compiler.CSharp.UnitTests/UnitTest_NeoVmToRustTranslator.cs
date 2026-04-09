// Copyright (C) 2015-2026 The Neo Project.
//
// UnitTest_NeoVmToRustTranslator.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Compiler.Backend.RiscV;
using Neo.VM;
using System.Collections.Generic;

namespace Neo.Compiler.CSharp.UnitTests;

[TestClass]
public class UnitTest_NeoVmToRustTranslator
{
    [TestMethod]
    public void TestSimpleArithmetic()
    {
        var instructions = CreateInstructions(
            (OpCode.PUSH2, null),
            (OpCode.PUSH3, null),
            (OpCode.ADD, null)
        );
        var translator = new NeoVmToRustTranslator();
        var rust = translator.Translate("Test", new List<(string Name, IReadOnlyList<Instruction> Instructions)>
        {
            ("testMethod", instructions)
        });
        Assert.IsTrue(rust.Contains("ctx.push_int(2);"), $"Expected push_int(2) in:\n{rust}");
        Assert.IsTrue(rust.Contains("ctx.push_int(3);"), $"Expected push_int(3) in:\n{rust}");
        Assert.IsTrue(rust.Contains("ctx.add();"), $"Expected add() in:\n{rust}");
    }

    [TestMethod]
    public void TestSyscall()
    {
        var hash = System.BitConverter.GetBytes((uint)0x31e85d92);
        var instructions = CreateInstructions(
            (OpCode.SYSCALL, hash)
        );
        var translator = new NeoVmToRustTranslator();
        var rust = translator.Translate("Test", new List<(string Name, IReadOnlyList<Instruction> Instructions)>
        {
            ("getStorage", instructions)
        });
        Assert.IsTrue(rust.Contains("bridge_syscall(ctx, 0x31e85d92);"), $"Expected bridge_syscall in:\n{rust}");
    }

    [TestMethod]
    public void TestSlotAccess()
    {
        var instructions = CreateInstructions(
            (OpCode.INITSLOT, new byte[] { 1, 2 }),
            (OpCode.LDARG0, null),
            (OpCode.STLOC0, null),
            (OpCode.LDLOC0, null)
        );
        var translator = new NeoVmToRustTranslator();
        var rust = translator.Translate("Test", new List<(string Name, IReadOnlyList<Instruction> Instructions)>
        {
            ("test", instructions)
        });
        Assert.IsTrue(rust.Contains("ctx.init_slot(1, 2);"), $"Expected init_slot in:\n{rust}");
        Assert.IsTrue(rust.Contains("ctx.load_arg(0);"), $"Expected load_arg in:\n{rust}");
        Assert.IsTrue(rust.Contains("ctx.store_local(0);"), $"Expected store_local in:\n{rust}");
        Assert.IsTrue(rust.Contains("ctx.load_local(0);"), $"Expected load_local in:\n{rust}");
    }

    [TestMethod]
    public void TestDispatchGeneration()
    {
        // Use instructions with no control flow (no RET) so EmitLinear is used
        var instructions = CreateInstructions(
            (OpCode.PUSH0, null)
        );
        var translator = new NeoVmToRustTranslator();
        var rust = translator.Translate("MyContract", new List<(string Name, IReadOnlyList<Instruction> Instructions)>
        {
            ("transfer", instructions),
            ("balanceOf", instructions)
        });
        Assert.IsTrue(rust.Contains("\"transfer\" => method_transfer(ctx)"), $"Expected transfer dispatch in:\n{rust}");
        Assert.IsTrue(rust.Contains("\"balanceOf\" => method_balanceof(ctx)"), $"Expected balanceOf dispatch in:\n{rust}");
        Assert.IsTrue(rust.Contains("_ => ctx.fault(\"Unknown method\")"), $"Expected fault dispatch in:\n{rust}");
    }

    [TestMethod]
    public void TestMethodSignature()
    {
        var instructions = CreateInstructions(
            (OpCode.PUSH1, null)
        );
        var translator = new NeoVmToRustTranslator();
        var rust = translator.Translate("TestContract", new List<(string Name, IReadOnlyList<Instruction> Instructions)>
        {
            ("myFunc", instructions)
        });
        Assert.IsTrue(rust.Contains("fn method_myfunc(ctx: &mut Context)"), $"Expected method signature in:\n{rust}");
        Assert.IsTrue(rust.Contains("fn dispatch(ctx: &mut Context, method: &str)"), $"Expected dispatch signature in:\n{rust}");
    }

    [TestMethod]
    public void TestImports()
    {
        var instructions = CreateInstructions(
            (OpCode.PUSH0, null)
        );
        var translator = new NeoVmToRustTranslator();
        var rust = translator.Translate("Test", new List<(string Name, IReadOnlyList<Instruction> Instructions)>
        {
            ("test", instructions)
        });
        Assert.IsTrue(rust.Contains("use neo_riscv_rt::Context;"), $"Expected Context import in:\n{rust}");
        Assert.IsTrue(rust.Contains("use neo_riscv_rt::stack_value::StackValue;"), $"Expected StackValue import in:\n{rust}");
    }

    [TestMethod]
    public void TestContractNameInHeader()
    {
        var instructions = CreateInstructions(
            (OpCode.PUSH0, null)
        );
        var translator = new NeoVmToRustTranslator();
        var rust = translator.Translate("MySpecialContract", new List<(string Name, IReadOnlyList<Instruction> Instructions)>
        {
            ("test", instructions)
        });
        Assert.IsTrue(rust.Contains("// Generated PolkaVM contract for: MySpecialContract"), $"Expected contract name in header:\n{rust}");
    }

    [TestMethod]
    public void TestStateMachineForControlFlow()
    {
        // RET is a control-flow opcode, so methods containing it get a state machine
        var instructions = CreateInstructions(
            (OpCode.PUSH1, null),
            (OpCode.RET, null)
        );
        var translator = new NeoVmToRustTranslator();
        var rust = translator.Translate("Test", new List<(string Name, IReadOnlyList<Instruction> Instructions)>
        {
            ("test", instructions)
        });
        // State machine uses match _pc
        Assert.IsTrue(rust.Contains("let mut _pc: i32 = 0;"), $"Expected _pc declaration in:\n{rust}");
        Assert.IsTrue(rust.Contains("match _pc"), $"Expected match _pc in:\n{rust}");
        Assert.IsTrue(rust.Contains("return;"), $"Expected return for RET in:\n{rust}");
    }

    /// <summary>
    /// Creates a list of Neo.Compiler.Instruction objects from opcode/operand pairs.
    /// These are the compiler's internal Instruction type (not Neo.VM.Instruction).
    /// </summary>
    private static IReadOnlyList<Instruction> CreateInstructions(params (OpCode op, byte[]? operand)[] ops)
    {
        var list = new List<Instruction>();
        int offset = 0;
        foreach (var (op, operand) in ops)
        {
            var instr = new Instruction
            {
                OpCode = op,
                Operand = operand,
                Offset = offset
            };
            list.Add(instr);
            offset += 1 + (operand?.Length ?? 0);
        }
        return list;
    }
}
