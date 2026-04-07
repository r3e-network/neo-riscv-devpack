// Copyright (C) 2015-2026 The Neo Project.
//
// UnitTest_ICodeEmitter.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Compiler.Backend;
using Neo.VM;
using System.Linq;

namespace Neo.Compiler.CSharp.UnitTests
{
    [TestClass]
    public class UnitTest_ICodeEmitter
    {
        [TestMethod]
        public void TestPushInt_SmallValues()
        {
            var emitter = new NeoVmEmitter();
            emitter.PushInt(0);
            emitter.PushInt(1);
            emitter.PushInt(16);
            emitter.PushInt(-1);
            var instructions = emitter.Instructions;
            Assert.AreEqual(4, instructions.Count);
            Assert.AreEqual(OpCode.PUSH0, instructions[0].OpCode);
            Assert.AreEqual(OpCode.PUSH1, instructions[1].OpCode);
            Assert.AreEqual(OpCode.PUSH16, instructions[2].OpCode);
            Assert.AreEqual(OpCode.PUSHM1, instructions[3].OpCode);
        }

        [TestMethod]
        public void TestPushInt_LargeValues()
        {
            var emitter = new NeoVmEmitter();
            emitter.PushInt(255);
            emitter.PushInt(65536);
            Assert.AreEqual(OpCode.PUSHINT16, emitter.Instructions[0].OpCode);
            Assert.AreEqual(OpCode.PUSHINT32, emitter.Instructions[1].OpCode);
        }

        [TestMethod]
        public void TestArithmetic()
        {
            var emitter = new NeoVmEmitter();
            emitter.PushInt(2);
            emitter.PushInt(3);
            emitter.Add();
            emitter.Ret();
            var opcodes = emitter.Instructions.Select(i => i.OpCode).ToList();
            CollectionAssert.AreEqual(
                new[] { OpCode.PUSH2, OpCode.PUSH3, OpCode.ADD, OpCode.RET }, opcodes);
        }

        [TestMethod]
        public void TestSlotAccess()
        {
            var emitter = new NeoVmEmitter();
            emitter.InitSlot(2, 1);
            emitter.LdArg(0);
            emitter.StLoc(0);
            emitter.LdLoc(0);
            Assert.AreEqual(OpCode.INITSLOT, emitter.Instructions[0].OpCode);
            Assert.AreEqual(OpCode.LDARG0, emitter.Instructions[1].OpCode);
            Assert.AreEqual(OpCode.STLOC0, emitter.Instructions[2].OpCode);
            Assert.AreEqual(OpCode.LDLOC0, emitter.Instructions[3].OpCode);
        }

        [TestMethod]
        public void TestSlotAccess_HighIndex()
        {
            var emitter = new NeoVmEmitter();
            emitter.LdLoc(10);
            Assert.AreEqual(OpCode.LDLOC, emitter.Instructions[0].OpCode);
            Assert.AreEqual((byte)10, emitter.Instructions[0].Operand![0]);
        }

        [TestMethod]
        public void TestJump()
        {
            var emitter = new NeoVmEmitter();
            var label = emitter.DefineLabel();
            emitter.PushBool(true);
            emitter.Emit_JumpIf(label);
            emitter.PushInt(1);
            emitter.MarkLabel(label);
            emitter.PushInt(2);
            emitter.Ret();
            Assert.AreEqual(OpCode.PUSHT, emitter.Instructions[0].OpCode);
            Assert.AreEqual(OpCode.JMPIF_L, emitter.Instructions[1].OpCode);
        }

        [TestMethod]
        public void TestSyscall()
        {
            var emitter = new NeoVmEmitter();
            emitter.Syscall(0x525b7d62);
            Assert.AreEqual(OpCode.SYSCALL, emitter.Instructions[0].OpCode);
            Assert.AreEqual(4, emitter.Instructions[0].Operand!.Length);
        }

        [TestMethod]
        public void TestPushBytes()
        {
            var emitter = new NeoVmEmitter();
            emitter.PushBytes(new byte[] { 0x01, 0x02, 0x03 });
            Assert.AreEqual(OpCode.PUSHDATA1, emitter.Instructions[0].OpCode);
            Assert.AreEqual(3, emitter.Instructions[0].Operand![0]);
        }

        [TestMethod]
        public void TestCollections()
        {
            var emitter = new NeoVmEmitter();
            emitter.NewMap();
            emitter.Dup();
            emitter.PushInt(1);
            emitter.PushInt(2);
            emitter.SetItem();
            var opcodes = emitter.Instructions.Select(i => i.OpCode).ToList();
            CollectionAssert.AreEqual(
                new[] { OpCode.NEWMAP, OpCode.DUP, OpCode.PUSH1, OpCode.PUSH2, OpCode.SETITEM }, opcodes);
        }
    }
}
