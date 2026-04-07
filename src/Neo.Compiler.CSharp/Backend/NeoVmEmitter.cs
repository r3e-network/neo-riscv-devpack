// Copyright (C) 2015-2026 The Neo Project.
//
// NeoVmEmitter.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Neo.VM;
using OpCode = Neo.VM.OpCode;

namespace Neo.Compiler.Backend;

/// <summary>
/// ICodeEmitter implementation that produces NeoVM instructions.
/// Replicates the instruction-emission logic from MethodConvert helpers
/// (StackHelpers.cs, SlotHelpers.cs) so the same patterns are preserved
/// when emitting through the abstraction layer.
/// </summary>
internal class NeoVmEmitter : ICodeEmitter
{
    private readonly List<Instruction> _instructions = new();

    /// <summary>
    /// The list of instructions emitted so far.
    /// </summary>
    public IReadOnlyList<Instruction> Instructions => _instructions;

    #region Internal helpers

    private Instruction AddInstruction(Instruction instruction)
    {
        _instructions.Add(instruction);
        return instruction;
    }

    private Instruction AddInstruction(OpCode opcode)
    {
        return AddInstruction(new Instruction { OpCode = opcode });
    }

    private Instruction Jump(OpCode opcode, JumpTarget target)
    {
        return AddInstruction(new Instruction
        {
            OpCode = opcode,
            Target = target
        });
    }

    /// <summary>
    /// Replicates the AccessSlot pattern from SlotHelpers.cs line 201-206.
    /// If index &lt; 7, uses the compact opcode (opcode - 7 + index);
    /// otherwise uses the operand form.
    /// </summary>
    private Instruction AccessSlot(OpCode opcode, byte index)
    {
        return index >= 7
            ? AddInstruction(new Instruction { OpCode = opcode, Operand = new[] { index } })
            : AddInstruction(opcode - 7 + index);
    }

    private static NeoVmLabel AsNeoVmLabel(ILabel label)
    {
        return (NeoVmLabel)label;
    }

    private static ReadOnlySpan<byte> PadRight(Span<byte> buffer, int dataLength, int padLength, bool negative)
    {
        byte pad = negative ? (byte)0xff : (byte)0;
        for (int x = dataLength; x < padLength; x++)
            buffer[x] = pad;
        return buffer[..padLength];
    }

    #endregion

    #region Method lifecycle

    public void BeginMethod(string name, int paramCount, int localCount)
    {
        // NeoVM does not emit a method header — InitSlot is used separately.
    }

    public void EndMethod()
    {
        // NeoVM does not emit a method footer.
    }

    #endregion

    #region Stack: push values

    /// <summary>
    /// Replicates Push(BigInteger) from StackHelpers.cs.
    /// Selects PUSH0-PUSH16, PUSHM1, or PUSHINT8/16/32/64/128/256 based on value size.
    /// </summary>
    public void PushInt(BigInteger value)
    {
        if (value >= -1 && value <= 16)
        {
            AddInstruction(value == -1 ? OpCode.PUSHM1 : OpCode.PUSH0 + (byte)(int)value);
            return;
        }

        Span<byte> buffer = stackalloc byte[32];
        if (!value.TryWriteBytes(buffer, out var bytesWritten, isUnsigned: false, isBigEndian: false))
            throw new ArgumentOutOfRangeException(nameof(value));

        var instruction = bytesWritten switch
        {
            1 => new Instruction
            {
                OpCode = OpCode.PUSHINT8,
                Operand = PadRight(buffer, bytesWritten, 1, value.Sign < 0).ToArray()
            },
            2 => new Instruction
            {
                OpCode = OpCode.PUSHINT16,
                Operand = PadRight(buffer, bytesWritten, 2, value.Sign < 0).ToArray()
            },
            <= 4 => new Instruction
            {
                OpCode = OpCode.PUSHINT32,
                Operand = PadRight(buffer, bytesWritten, 4, value.Sign < 0).ToArray()
            },
            <= 8 => new Instruction
            {
                OpCode = OpCode.PUSHINT64,
                Operand = PadRight(buffer, bytesWritten, 8, value.Sign < 0).ToArray()
            },
            <= 16 => new Instruction
            {
                OpCode = OpCode.PUSHINT128,
                Operand = PadRight(buffer, bytesWritten, 16, value.Sign < 0).ToArray()
            },
            <= 32 => new Instruction
            {
                OpCode = OpCode.PUSHINT256,
                Operand = PadRight(buffer, bytesWritten, 32, value.Sign < 0).ToArray()
            },
            _ => throw new ArgumentOutOfRangeException($"Number too large: {bytesWritten}")
        };
        AddInstruction(instruction);
    }

    public void PushBool(bool value)
    {
        AddInstruction(value ? OpCode.PUSHT : OpCode.PUSHF);
    }

    /// <summary>
    /// Replicates Push(byte[]) from StackHelpers.cs.
    /// Uses PUSHDATA1/2/4 based on data.Length.
    /// </summary>
    public void PushBytes(byte[] data)
    {
        OpCode opcode;
        byte[] buffer;
        switch (data.Length)
        {
            case <= byte.MaxValue:
                opcode = OpCode.PUSHDATA1;
                buffer = new byte[sizeof(byte) + data.Length];
                buffer[0] = (byte)data.Length;
                Buffer.BlockCopy(data, 0, buffer, sizeof(byte), data.Length);
                break;
            case <= ushort.MaxValue:
                opcode = OpCode.PUSHDATA2;
                buffer = new byte[sizeof(ushort) + data.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)data.Length);
                Buffer.BlockCopy(data, 0, buffer, sizeof(ushort), data.Length);
                break;
            default:
                opcode = OpCode.PUSHDATA4;
                buffer = new byte[sizeof(uint) + data.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)data.Length);
                Buffer.BlockCopy(data, 0, buffer, sizeof(uint), data.Length);
                break;
        }
        AddInstruction(new Instruction { OpCode = opcode, Operand = buffer });
    }

    /// <summary>
    /// Replicates Push(string) from StackHelpers.cs.
    /// Tries byte-by-byte conversion first, falls back to UTF8.
    /// </summary>
    public void PushString(string value)
    {
        try
        {
            // Handle byte-like strings where each char fits in a single byte.
            MemoryStream pushed = new();
            BinaryWriter writer = new(pushed);
            foreach (char c in value)
                writer.Write(System.Convert.ToByte(c));
            PushBytes(pushed.ToArray());
            return;
        }
        catch { }
        PushBytes(Utility.StrictUTF8.GetBytes(value));
    }

    public void PushNull()
    {
        AddInstruction(OpCode.PUSHNULL);
    }

    public void PushDefault(byte stackItemType)
    {
        // Maps StackItemType to the appropriate push:
        //   Boolean → PUSHF, Integer → PUSH0, everything else → PUSHNULL
        // The StackItemType enum: Boolean=1, Integer=2 (from Neo.VM.Types)
        AddInstruction(stackItemType switch
        {
            1 => OpCode.PUSHF,   // StackItemType.Boolean
            2 => OpCode.PUSH0,   // StackItemType.Integer
            _ => OpCode.PUSHNULL,
        });
    }

    #endregion

    #region Stack manipulation

    public void Drop(int count = 1)
    {
        for (int i = 0; i < count; i++)
            AddInstruction(OpCode.DROP);
    }

    public void Dup() => AddInstruction(OpCode.DUP);
    public void Nip() => AddInstruction(OpCode.NIP);

    public void XDrop(int? count)
    {
        if (count.HasValue) PushInt(count.Value);
        AddInstruction(OpCode.XDROP);
    }

    public void Over() => AddInstruction(OpCode.OVER);

    public void Pick(int? index)
    {
        if (index.HasValue) PushInt(index.Value);
        AddInstruction(OpCode.PICK);
    }

    public void Tuck() => AddInstruction(OpCode.TUCK);
    public void Swap() => AddInstruction(OpCode.SWAP);
    public void Rot() => AddInstruction(OpCode.ROT);

    public void Roll(int? index)
    {
        if (index.HasValue) PushInt(index.Value);
        AddInstruction(OpCode.ROLL);
    }

    public void Reverse3() => AddInstruction(OpCode.REVERSE3);
    public void Reverse4() => AddInstruction(OpCode.REVERSE4);

    public void ReverseN(int count)
    {
        PushInt(count);
        AddInstruction(OpCode.REVERSEN);
    }

    public void Clear() => AddInstruction(OpCode.CLEAR);
    public void Depth() => AddInstruction(OpCode.DEPTH);

    #endregion

    #region Arithmetic

    public void Add() => AddInstruction(OpCode.ADD);
    public void Sub() => AddInstruction(OpCode.SUB);
    public void Mul() => AddInstruction(OpCode.MUL);
    public void Div() => AddInstruction(OpCode.DIV);
    public void Mod() => AddInstruction(OpCode.MOD);
    public void Negate() => AddInstruction(OpCode.NEGATE);
    public void Abs() => AddInstruction(OpCode.ABS);
    public void Sign() => AddInstruction(OpCode.SIGN);
    public void Min() => AddInstruction(OpCode.MIN);
    public void Max() => AddInstruction(OpCode.MAX);
    public void Pow() => AddInstruction(OpCode.POW);
    public void Sqrt() => AddInstruction(OpCode.SQRT);
    public void ModMul() => AddInstruction(OpCode.MODMUL);
    public void ModPow() => AddInstruction(OpCode.MODPOW);
    public void ShiftLeft() => AddInstruction(OpCode.SHL);
    public void ShiftRight() => AddInstruction(OpCode.SHR);

    #endregion

    #region Bitwise

    public void BitwiseAnd() => AddInstruction(OpCode.AND);
    public void BitwiseOr() => AddInstruction(OpCode.OR);
    public void BitwiseXor() => AddInstruction(OpCode.XOR);
    public void BitwiseNot() => AddInstruction(OpCode.INVERT);

    #endregion

    #region Comparison & Logic

    public void Equal() => AddInstruction(OpCode.EQUAL);
    public void NotEqual() => AddInstruction(OpCode.NOTEQUAL);
    public void LessThan() => AddInstruction(OpCode.LT);
    public void LessOrEqual() => AddInstruction(OpCode.LE);
    public void GreaterThan() => AddInstruction(OpCode.GT);
    public void GreaterOrEqual() => AddInstruction(OpCode.GE);
    public void BoolAnd() => AddInstruction(OpCode.BOOLAND);
    public void BoolOr() => AddInstruction(OpCode.BOOLOR);
    public void Not() => AddInstruction(OpCode.NOT);
    public void NullCheck() => AddInstruction(OpCode.ISNULL);

    #endregion

    #region Type operations

    public void IsType(byte stackItemType)
    {
        AddInstruction(new Instruction
        {
            OpCode = OpCode.ISTYPE,
            Operand = new[] { stackItemType }
        });
    }

    public void Convert(byte stackItemType)
    {
        AddInstruction(new Instruction
        {
            OpCode = OpCode.CONVERT,
            Operand = new[] { stackItemType }
        });
    }

    #endregion

    #region Control flow

    public ILabel DefineLabel()
    {
        return new NeoVmLabel();
    }

    public void MarkLabel(ILabel label)
    {
        var neoLabel = AsNeoVmLabel(label);
        // The next instruction added will become the target.
        // We set the JumpTarget.Instruction when the next instruction is emitted.
        // Use a NOP as the target instruction if we need an anchor point.
        neoLabel.Target.Instruction = AddInstruction(OpCode.NOP);
    }

    /// <summary>
    /// All jump methods use _L (long-form) opcodes. The optimizer compresses them later.
    /// </summary>
    public void Emit_Jump(ILabel target) => Jump(OpCode.JMP_L, AsNeoVmLabel(target).Target);
    public void Emit_JumpIf(ILabel target) => Jump(OpCode.JMPIF_L, AsNeoVmLabel(target).Target);
    public void Emit_JumpIfNot(ILabel target) => Jump(OpCode.JMPIFNOT_L, AsNeoVmLabel(target).Target);
    public void Emit_JumpEq(ILabel target) => Jump(OpCode.JMPEQ_L, AsNeoVmLabel(target).Target);
    public void Emit_JumpNe(ILabel target) => Jump(OpCode.JMPNE_L, AsNeoVmLabel(target).Target);
    public void Emit_JumpGt(ILabel target) => Jump(OpCode.JMPGT_L, AsNeoVmLabel(target).Target);
    public void Emit_JumpGe(ILabel target) => Jump(OpCode.JMPGE_L, AsNeoVmLabel(target).Target);
    public void Emit_JumpLt(ILabel target) => Jump(OpCode.JMPLT_L, AsNeoVmLabel(target).Target);
    public void Emit_JumpLe(ILabel target) => Jump(OpCode.JMPLE_L, AsNeoVmLabel(target).Target);

    public void Call(ILabel target)
    {
        Jump(OpCode.CALL_L, AsNeoVmLabel(target).Target);
    }

    public void Ret() => AddInstruction(OpCode.RET);
    public void Throw() => AddInstruction(OpCode.THROW);
    public void Abort() => AddInstruction(OpCode.ABORT);
    public void AbortMsg() => AddInstruction(OpCode.ABORTMSG);
    public void Assert() => AddInstruction(OpCode.ASSERT);
    public void AssertMsg() => AddInstruction(OpCode.ASSERTMSG);
    public void Nop() => AddInstruction(OpCode.NOP);

    #endregion

    #region Slots (variables)

    public void InitSlot(byte localCount, byte paramCount)
    {
        AddInstruction(new Instruction
        {
            OpCode = OpCode.INITSLOT,
            Operand = new[] { localCount, paramCount }
        });
    }

    public void LdArg(byte index) => AccessSlot(OpCode.LDARG, index);
    public void StArg(byte index) => AccessSlot(OpCode.STARG, index);
    public void LdLoc(byte index) => AccessSlot(OpCode.LDLOC, index);
    public void StLoc(byte index) => AccessSlot(OpCode.STLOC, index);
    public void LdSFld(byte index) => AccessSlot(OpCode.LDSFLD, index);
    public void StSFld(byte index) => AccessSlot(OpCode.STSFLD, index);

    #endregion

    #region Syscalls & interop

    public void Syscall(uint hash)
    {
        byte[] operand = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(operand, hash);
        AddInstruction(new Instruction
        {
            OpCode = OpCode.SYSCALL,
            Operand = operand
        });
    }

    public void CallToken(ushort token)
    {
        byte[] operand = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(operand, token);
        AddInstruction(new Instruction
        {
            OpCode = OpCode.CALLT,
            Operand = operand
        });
    }

    #endregion

    #region Collections

    public void NewArray() => AddInstruction(OpCode.NEWARRAY);

    public void NewArrayT(byte type)
    {
        AddInstruction(new Instruction
        {
            OpCode = OpCode.NEWARRAY_T,
            Operand = new[] { type }
        });
    }

    public void NewStruct(int fieldCount)
    {
        PushInt(fieldCount);
        AddInstruction(OpCode.NEWSTRUCT);
    }

    public void NewMap() => AddInstruction(OpCode.NEWMAP);
    public void NewBuffer() => AddInstruction(OpCode.NEWBUFFER);
    public void Append() => AddInstruction(OpCode.APPEND);
    public void SetItem() => AddInstruction(OpCode.SETITEM);
    public void GetItem() => AddInstruction(OpCode.PICKITEM);
    public void Remove() => AddInstruction(OpCode.REMOVE);
    public void Size() => AddInstruction(OpCode.SIZE);
    public void HasKey() => AddInstruction(OpCode.HASKEY);
    public void Keys() => AddInstruction(OpCode.KEYS);
    public void Values() => AddInstruction(OpCode.VALUES);

    public void Pack(int count)
    {
        PushInt(count);
        AddInstruction(OpCode.PACK);
    }

    public void Unpack() => AddInstruction(OpCode.UNPACK);
    public void DeepCopy() => throw new NotSupportedException("DeepCopy is not a single NeoVM opcode; use UNPACK+PACK pattern instead.");
    public void ReverseItems() => AddInstruction(OpCode.REVERSEITEMS);
    public void ClearItems() => AddInstruction(OpCode.CLEARITEMS);
    public void PopItem() => AddInstruction(OpCode.POPITEM);

    #endregion

    #region String / Byte

    public void Cat() => AddInstruction(OpCode.CAT);
    public void Substr() => AddInstruction(OpCode.SUBSTR);
    public void Left() => AddInstruction(OpCode.LEFT);
    public void Right() => AddInstruction(OpCode.RIGHT);
    public void MemCpy() => AddInstruction(OpCode.MEMCPY);
    public void NumEqual() => AddInstruction(OpCode.NUMEQUAL);
    public void NumNotEqual() => AddInstruction(OpCode.NUMNOTEQUAL);

    #endregion

    #region Exception handling

    /// <summary>
    /// Creates a TRY_L instruction with Target (catch) and Target2 (finally).
    /// </summary>
    public ITryBlock BeginTry(ILabel catchLabel, ILabel finallyLabel)
    {
        var catchTarget = AsNeoVmLabel(catchLabel).Target;
        var finallyTarget = AsNeoVmLabel(finallyLabel).Target;

        AddInstruction(new Instruction
        {
            OpCode = OpCode.TRY_L,
            Target = catchTarget,
            Target2 = finallyTarget
        });

        return new NeoVmTryBlock
        {
            CatchTarget = catchTarget,
            FinallyTarget = finallyTarget
        };
    }

    public void EndTry(ILabel endLabel)
    {
        Jump(OpCode.ENDTRY_L, AsNeoVmLabel(endLabel).Target);
    }

    public void EndTryFinally()
    {
        AddInstruction(OpCode.ENDFINALLY);
    }

    public void EndFinally()
    {
        AddInstruction(OpCode.ENDFINALLY);
    }

    #endregion

    #region Raw opcode fallback

    public void EmitRaw(byte opcode, byte[]? operand = null)
    {
        AddInstruction(new Instruction
        {
            OpCode = (OpCode)opcode,
            Operand = operand
        });
    }

    #endregion
}
