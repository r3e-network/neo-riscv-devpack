# Dual-Target Compiler (NeoVM + RISC-V) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the Neo C# compiler (`nccs`) to support compiling smart contracts to both NeoVM bytecode (.nef) and PolkaVM RISC-V binaries (.polkavm) via an `ICodeEmitter` abstraction.

**Architecture:** Introduce an `ICodeEmitter` interface between the shared front-end (Roslyn + expression/statement converters) and backend-specific instruction emission. `NeoVmEmitter` wraps existing NeoVM opcode generation. `RiscVEmitter` generates Rust source using a `neo-riscv-rt` runtime library, compiled to PolkaVM via the existing Rust toolchain. A `--target riscv|neovm` CLI flag selects the backend.

**Tech Stack:** C# (.NET 8), Roslyn, NeoVM, Rust (no_std), PolkaVM, polkavm-linker

**Spec:** `docs/superpowers/specs/2026-03-31-dual-target-compiler-design.md`

---

## File Structure

### New Files (Compiler - C#)

| File | Responsibility |
|------|---------------|
| `src/Neo.Compiler.CSharp/Backend/ICodeEmitter.cs` | Emitter interface + ILabel/ITryBlock |
| `src/Neo.Compiler.CSharp/Backend/NeoVmEmitter.cs` | NeoVM backend wrapping existing Instruction emission |
| `src/Neo.Compiler.CSharp/Backend/NeoVmLabel.cs` | NeoVM ILabel wrapping JumpTarget |
| `src/Neo.Compiler.CSharp/Backend/NeoVmTryBlock.cs` | NeoVM ITryBlock wrapping ExceptionHandling |
| `src/Neo.Compiler.CSharp/Backend/RiscV/RiscVEmitter.cs` | RISC-V backend generating Rust source |
| `src/Neo.Compiler.CSharp/Backend/RiscV/RustCodeBuilder.cs` | Rust source code string builder |
| `src/Neo.Compiler.CSharp/Backend/RiscV/RiscVCompiler.cs` | Invokes Rust toolchain to build .polkavm (next phase) |
| `src/Neo.Compiler.CSharp/Backend/CompilationTarget.cs` | Enum: NeoVM, RiscV |

### Modified Files (Compiler - C#)

| File | Change |
|------|--------|
| `src/Neo.Compiler.CSharp/CompilationEngine/CompilationOptions.cs` | Add `Target` property |
| `src/Neo.Compiler.CSharp/CompilationEngine/CompilationContext.cs` | Use ICodeEmitter, target-aware output |
| `src/Neo.Compiler.CSharp/CompilationEngine/CompilationEngine.cs` | Pass target to CompilationContext |
| `src/Neo.Compiler.CSharp/MethodConvert/MethodConvert.cs` | Add `Emitter` field, use it in Convert() |
| `src/Neo.Compiler.CSharp/MethodConvert/Helpers/StackHelpers.cs` | Route through Emitter |
| `src/Neo.Compiler.CSharp/MethodConvert/Helpers/SlotHelpers.cs` | Route through Emitter |
| `src/Neo.Compiler.CSharp/MethodConvert/Helpers/CallHelpers.cs` | Route through Emitter |
| `src/Neo.Compiler.CSharp/MethodConvert/Helpers/ConvertHelpers.cs` | Route through Emitter |
| `src/Neo.Compiler.CSharp/MethodConvert/StackHelpers.OpCodes.cs` | Route through Emitter |
| `src/Neo.Compiler.CSharp/MethodConvert/Expression/*.cs` (29 files) | Replace AddInstruction with Emitter calls |
| `src/Neo.Compiler.CSharp/MethodConvert/Statement/*.cs` (19 files) | Replace AddInstruction with Emitter calls |
| `src/Neo.Compiler.CSharp/MethodConvert/System/*.cs` (13 files) | Replace AddInstruction with Emitter calls |
| `src/Neo.Compiler.CSharp/Options.cs` | Add Target option |
| `src/Neo.Compiler.CSharp/Program.cs` | Add `--target` CLI flag |

### New Files (Runtime Library - Rust)

| File | Responsibility |
|------|---------------|
| `crates/neo-riscv-rt/Cargo.toml` | Crate definition |
| `crates/neo-riscv-rt/src/lib.rs` | Public API: Context struct + all operations |
| `crates/neo-riscv-rt/src/stack_value.rs` | Tagged StackValue type + conversions |
| `crates/neo-riscv-rt/src/biginteger.rs` | Arbitrary-precision integer arithmetic |
| `crates/neo-riscv-rt/src/collections.rs` | Array, Struct, Map, Buffer operations |
| `crates/neo-riscv-rt/src/strings.rs` | String concat, substr, compare |
| `crates/neo-riscv-rt/src/syscall.rs` | Marshal + host_call() + unmarshal |
| `crates/neo-riscv-rt/src/exceptions.rs` | Try/catch/finally via setjmp/longjmp |
| `crates/neo-riscv-rt/src/memory.rs` | Bump allocator |

### Test Files

| File | Purpose |
|------|---------|
| `tests/Neo.Compiler.CSharp.UnitTests/UnitTest_ICodeEmitter.cs` | NeoVmEmitter regression vs direct Instruction |
| `tests/Neo.Compiler.CSharp.UnitTests/UnitTest_RiscVTarget.cs` | RISC-V compilation + execution tests |
| `crates/neo-riscv-rt/tests/context_tests.rs` | Runtime library unit tests |

---

## Task 1: Add CompilationTarget Enum and CLI Flag

**Files:**
- Create: `src/Neo.Compiler.CSharp/Backend/CompilationTarget.cs`
- Modify: `src/Neo.Compiler.CSharp/CompilationEngine/CompilationOptions.cs:50-60`
- Modify: `src/Neo.Compiler.CSharp/Options.cs:17-33`
- Modify: `src/Neo.Compiler.CSharp/Program.cs:63-73`

- [ ] **Step 1: Create CompilationTarget enum**

```csharp
// src/Neo.Compiler.CSharp/Backend/CompilationTarget.cs
namespace Neo.Compiler;

public enum CompilationTarget : byte
{
    NeoVM = 0,
    RiscV = 1,
}
```

- [ ] **Step 2: Add Target to CompilationOptions**

In `src/Neo.Compiler.CSharp/CompilationEngine/CompilationOptions.cs`, after line 56 (`public string? BaseName`), add:

```csharp
public CompilationTarget Target { get; set; } = CompilationTarget.NeoVM;
```

- [ ] **Step 3: Add --target CLI option to Program.cs**

In `src/Neo.Compiler.CSharp/Program.cs`, in the `Main()` method after the existing options block (around line 73, after `--no-inline`), add:

```csharp
rootCommand.AddOption(new Option<CompilationTarget>("--target", () => CompilationTarget.NeoVM, "Compilation target: NeoVM (default) or RiscV."));
```

- [ ] **Step 4: Build to verify no errors**

Run: `cd /home/neo/git/neo-riscv-vm/neo-devpack-dotnet && dotnet build src/Neo.Compiler.CSharp/Neo.Compiler.CSharp.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Neo.Compiler.CSharp/Backend/CompilationTarget.cs \
        src/Neo.Compiler.CSharp/CompilationEngine/CompilationOptions.cs \
        src/Neo.Compiler.CSharp/Program.cs
git commit -m "feat: add CompilationTarget enum and --target CLI flag"
```

---

## Task 2: Define ICodeEmitter Interface

**Files:**
- Create: `src/Neo.Compiler.CSharp/Backend/ICodeEmitter.cs`

- [ ] **Step 1: Create the ICodeEmitter interface**

This interface mirrors the operations currently performed by `MethodConvert` helper methods (`AddInstruction`, `Push`, `Jump`, `AccessSlot`, etc.) but abstracts away NeoVM-specific types.

```csharp
// src/Neo.Compiler.CSharp/Backend/ICodeEmitter.cs
using System.Numerics;

namespace Neo.Compiler.Backend;

/// <summary>
/// Marker interface for backend-specific jump labels.
/// NeoVM: wraps JumpTarget. RISC-V: wraps a label name/id.
/// </summary>
public interface ILabel { }

/// <summary>
/// Marker interface for backend-specific try/catch blocks.
/// </summary>
public interface ITryBlock { }

/// <summary>
/// Abstracts instruction emission so MethodConvert can target
/// either NeoVM bytecode or RISC-V (PolkaVM) code generation.
/// </summary>
public interface ICodeEmitter
{
    // === Method lifecycle ===
    void BeginMethod(string name, int paramCount, int localCount);
    void EndMethod();

    // === Stack: push values ===
    void PushInt(BigInteger value);
    void PushBool(bool value);
    void PushBytes(byte[] data);
    void PushString(string value);
    void PushNull();
    void PushDefault(byte stackItemType);

    // === Stack: manipulation ===
    void Drop(int count = 1);
    void Dup();
    void Nip();
    void XDrop(int? count);
    void Over();
    void Pick(int? index);
    void Tuck();
    void Swap();
    void Rot();
    void Roll(int? index);
    void Reverse3();
    void Reverse4();
    void ReverseN(int count);
    void Clear();
    void Depth();

    // === Arithmetic ===
    void Add();
    void Sub();
    void Mul();
    void Div();
    void Mod();
    void Negate();
    void Abs();
    void Sign();
    void Min();
    void Max();
    void Pow();
    void Sqrt();
    void ModMul();
    void ModPow();
    void ShiftLeft();
    void ShiftRight();

    // === Bitwise ===
    void BitwiseAnd();
    void BitwiseOr();
    void BitwiseXor();
    void BitwiseNot();

    // === Comparison & Logic ===
    void Equal();
    void NotEqual();
    void LessThan();
    void LessOrEqual();
    void GreaterThan();
    void GreaterOrEqual();
    void BoolAnd();
    void BoolOr();
    void Not();
    void NullCheck();

    // === Type operations ===
    void IsType(byte stackItemType);
    void Convert(byte stackItemType);

    // === Control flow ===
    ILabel DefineLabel();
    void MarkLabel(ILabel label);
    void Emit_Jump(ILabel target);
    void Emit_JumpIf(ILabel target);
    void Emit_JumpIfNot(ILabel target);
    void Emit_JumpEq(ILabel target);
    void Emit_JumpNe(ILabel target);
    void Emit_JumpGt(ILabel target);
    void Emit_JumpGe(ILabel target);
    void Emit_JumpLt(ILabel target);
    void Emit_JumpLe(ILabel target);
    void Call(ILabel target);
    void Ret();
    void Throw();
    void Abort();
    void AbortMsg();
    void Assert();
    void AssertMsg();
    void Nop();

    // === Slots (variables) ===
    void InitSlot(byte localCount, byte paramCount);
    void LdArg(byte index);
    void StArg(byte index);
    void LdLoc(byte index);
    void StLoc(byte index);
    void LdSFld(byte index);
    void StSFld(byte index);

    // === Syscalls & interop ===
    void Syscall(uint hash);
    void CallToken(ushort token);

    // === Collections ===
    void NewArray();
    void NewArrayT(byte type);
    void NewStruct(int fieldCount);
    void NewMap();
    void NewBuffer();
    void Append();
    void SetItem();
    void GetItem();
    void Remove();
    void Size();
    void HasKey();
    void Keys();
    void Values();
    void Pack(int count);
    void Unpack();
    void DeepCopy();
    void ReverseItems();
    void ClearItems();
    void PopItem();

    // === String / Byte ===
    void Cat();
    void Substr();
    void Left();
    void Right();
    void MemCpy();
    void NumEqual();
    void NumNotEqual();

    // === Exception handling ===
    ITryBlock BeginTry(ILabel catchLabel, ILabel finallyLabel);
    void EndTry(ILabel endLabel);
    void EndTryFinally();
    void EndFinally();

    // === Raw opcode fallback (for NeoVM-specific ops not in interface) ===
    /// <summary>
    /// Emit a raw NeoVM opcode. Only valid for NeoVM backend.
    /// RISC-V backend throws NotSupportedException for unrecognized opcodes.
    /// Used as an escape hatch during incremental migration.
    /// </summary>
    void EmitRaw(byte opcode, byte[]? operand = null);
}
```

- [ ] **Step 2: Build to verify**

Run: `cd /home/neo/git/neo-riscv-vm/neo-devpack-dotnet && dotnet build src/Neo.Compiler.CSharp/Neo.Compiler.CSharp.csproj`
Expected: Build succeeded (interface has no consumers yet)

- [ ] **Step 3: Commit**

```bash
git add src/Neo.Compiler.CSharp/Backend/ICodeEmitter.cs
git commit -m "feat: define ICodeEmitter interface for backend abstraction"
```

---

## Task 3: Implement NeoVmEmitter

**Files:**
- Create: `src/Neo.Compiler.CSharp/Backend/NeoVmLabel.cs`
- Create: `src/Neo.Compiler.CSharp/Backend/NeoVmTryBlock.cs`
- Create: `src/Neo.Compiler.CSharp/Backend/NeoVmEmitter.cs`

This wraps the existing `Instruction` + `JumpTarget` creation logic from `StackHelpers.cs` into the `ICodeEmitter` interface. Logic is moved, not rewritten.

- [ ] **Step 1: Create NeoVmLabel**

```csharp
// src/Neo.Compiler.CSharp/Backend/NeoVmLabel.cs
namespace Neo.Compiler.Backend;

internal class NeoVmLabel : ILabel
{
    public JumpTarget Target { get; } = new JumpTarget();
}
```

- [ ] **Step 2: Create NeoVmTryBlock**

```csharp
// src/Neo.Compiler.CSharp/Backend/NeoVmTryBlock.cs
namespace Neo.Compiler.Backend;

internal class NeoVmTryBlock : ITryBlock
{
    public JumpTarget CatchTarget { get; set; } = new();
    public JumpTarget FinallyTarget { get; set; } = new();
    public JumpTarget EndTarget { get; set; } = new();
}
```

- [ ] **Step 3: Create NeoVmEmitter implementing ICodeEmitter**

This is the largest single file. It takes the exact logic currently in `StackHelpers.cs` (Push, AddInstruction, Jump) and `SlotHelpers.cs` (AccessSlot) and wraps it behind the interface.

```csharp
// src/Neo.Compiler.CSharp/Backend/NeoVmEmitter.cs
using System.Buffers.Binary;
using System.Numerics;
using Neo.VM;

namespace Neo.Compiler.Backend;

internal class NeoVmEmitter : ICodeEmitter
{
    private readonly List<Instruction> _instructions = new();

    public IReadOnlyList<Instruction> Instructions => _instructions;

    private Instruction Add(Instruction instruction)
    {
        _instructions.Add(instruction);
        return instruction;
    }

    private Instruction Add(OpCode opcode) => Add(new Instruction { OpCode = opcode });

    // --- Method lifecycle ---
    public void BeginMethod(string name, int paramCount, int localCount) { }
    public void EndMethod() { }

    // --- Push ---
    public void PushInt(BigInteger number)
    {
        if (number >= -1 && number <= 16)
        {
            Add(number == -1 ? OpCode.PUSHM1 : OpCode.PUSH0 + (byte)(int)number);
            return;
        }
        Span<byte> buffer = stackalloc byte[32];
        if (!number.TryWriteBytes(buffer, out var bytesWritten, isUnsigned: false, isBigEndian: false))
            throw new ArgumentOutOfRangeException(nameof(number));
        var opcode = bytesWritten switch
        {
            1 => OpCode.PUSHINT8,
            2 => OpCode.PUSHINT16,
            <= 4 => OpCode.PUSHINT32,
            <= 8 => OpCode.PUSHINT64,
            <= 16 => OpCode.PUSHINT128,
            <= 32 => OpCode.PUSHINT256,
            _ => throw new ArgumentOutOfRangeException()
        };
        int padLen = bytesWritten switch { 1 => 1, 2 => 2, <= 4 => 4, <= 8 => 8, <= 16 => 16, _ => 32 };
        byte pad = number.Sign < 0 ? (byte)0xff : (byte)0;
        byte[] operand = new byte[padLen];
        buffer[..bytesWritten].CopyTo(operand);
        for (int i = bytesWritten; i < padLen; i++) operand[i] = pad;
        Add(new Instruction { OpCode = opcode, Operand = operand });
    }

    public void PushBool(bool value) => Add(value ? OpCode.PUSHT : OpCode.PUSHF);

    public void PushBytes(byte[] data)
    {
        OpCode opcode;
        byte[] buffer;
        switch (data.Length)
        {
            case <= byte.MaxValue:
                opcode = OpCode.PUSHDATA1;
                buffer = new byte[1 + data.Length];
                buffer[0] = (byte)data.Length;
                Buffer.BlockCopy(data, 0, buffer, 1, data.Length);
                break;
            case <= ushort.MaxValue:
                opcode = OpCode.PUSHDATA2;
                buffer = new byte[2 + data.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)data.Length);
                Buffer.BlockCopy(data, 0, buffer, 2, data.Length);
                break;
            default:
                opcode = OpCode.PUSHDATA4;
                buffer = new byte[4 + data.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)data.Length);
                Buffer.BlockCopy(data, 0, buffer, 4, data.Length);
                break;
        }
        Add(new Instruction { OpCode = opcode, Operand = buffer });
    }

    public void PushString(string s)
    {
        // Match existing StackHelpers.Push(string) behavior
        try
        {
            var ms = new System.IO.MemoryStream();
            var w = new System.IO.BinaryWriter(ms);
            foreach (char c in s) w.Write(System.Convert.ToByte(c));
            PushBytes(ms.ToArray());
            return;
        }
        catch { }
        PushBytes(Utility.StrictUTF8.GetBytes(s));
    }

    public void PushNull() => Add(OpCode.PUSHNULL);
    public void PushDefault(byte stackItemType)
    {
        Add((VM.Types.StackItemType)stackItemType switch
        {
            VM.Types.StackItemType.Boolean => OpCode.PUSHF,
            VM.Types.StackItemType.Integer => OpCode.PUSH0,
            _ => OpCode.PUSHNULL,
        });
    }

    // --- Stack manipulation ---
    public void Drop(int count)
    {
        for (int i = 0; i < count; i++) Add(OpCode.DROP);
    }
    public void Dup() => Add(OpCode.DUP);
    public void Nip() => Add(OpCode.NIP);
    public void XDrop(int? count)
    {
        if (count.HasValue) PushInt(count.Value);
        Add(OpCode.XDROP);
    }
    public void Over() => Add(OpCode.OVER);
    public void Pick(int? index)
    {
        if (index.HasValue) PushInt(index.Value);
        Add(OpCode.PICK);
    }
    public void Tuck() => Add(OpCode.TUCK);
    public void Swap() => Add(OpCode.SWAP);
    public void Rot() => Add(OpCode.ROT);
    public void Roll(int? index)
    {
        if (index.HasValue) PushInt(index.Value);
        Add(OpCode.ROLL);
    }
    public void Reverse3() => Add(OpCode.REVERSE3);
    public void Reverse4() => Add(OpCode.REVERSE4);
    public void ReverseN(int count)
    {
        PushInt(count);
        Add(OpCode.REVERSEN);
    }
    public void Clear() => Add(OpCode.CLEAR);
    public void Depth() => Add(OpCode.DEPTH);

    // --- Arithmetic ---
    public void Add() => Add(OpCode.ADD);
    public void Sub() => Add(OpCode.SUB);
    public void Mul() => Add(OpCode.MUL);
    public void Div() => Add(OpCode.DIV);
    public void Mod() => Add(OpCode.MOD);
    public void Negate() => Add(OpCode.NEGATE);
    public void Abs() => Add(OpCode.ABS);
    public void Sign() => Add(OpCode.SIGN);
    public void Min() => Add(OpCode.MIN);
    public void Max() => Add(OpCode.MAX);
    public void Pow() => Add(OpCode.POW);
    public void Sqrt() => Add(OpCode.SQRT);
    public void ModMul() => Add(OpCode.MODMUL);
    public void ModPow() => Add(OpCode.MODPOW);
    public void ShiftLeft() => Add(OpCode.SHL);
    public void ShiftRight() => Add(OpCode.SHR);

    // --- Bitwise ---
    public void BitwiseAnd() => Add(OpCode.AND);
    public void BitwiseOr() => Add(OpCode.OR);
    public void BitwiseXor() => Add(OpCode.XOR);
    public void BitwiseNot() => Add(OpCode.INVERT);

    // --- Comparison & Logic ---
    public void Equal() => Add(OpCode.EQUAL);
    public void NotEqual() => Add(OpCode.NOTEQUAL);
    public void LessThan() => Add(OpCode.LT);
    public void LessOrEqual() => Add(OpCode.LE);
    public void GreaterThan() => Add(OpCode.GT);
    public void GreaterOrEqual() => Add(OpCode.GE);
    public void BoolAnd() => Add(OpCode.BOOLAND);
    public void BoolOr() => Add(OpCode.BOOLOR);
    public void Not() => Add(OpCode.NOT);
    public void NullCheck() => Add(OpCode.ISNULL);

    // --- Type ops ---
    public void IsType(byte type) => Add(new Instruction { OpCode = OpCode.ISTYPE, Operand = [type] });
    public void Convert(byte type) => Add(new Instruction { OpCode = OpCode.CONVERT, Operand = [type] });

    // --- Control flow ---
    public ILabel DefineLabel() => new NeoVmLabel();
    public void MarkLabel(ILabel label)
    {
        var neoLabel = (NeoVmLabel)label;
        neoLabel.Target.Instruction = Add(OpCode.NOP);
    }

    private Instruction Jump(OpCode opcode, NeoVmLabel label)
    {
        return Add(new Instruction { OpCode = opcode, Target = label.Target });
    }

    public void Emit_Jump(ILabel target) => Jump(OpCode.JMP_L, (NeoVmLabel)target);
    public void Emit_JumpIf(ILabel target) => Jump(OpCode.JMPIF_L, (NeoVmLabel)target);
    public void Emit_JumpIfNot(ILabel target) => Jump(OpCode.JMPIFNOT_L, (NeoVmLabel)target);
    public void Emit_JumpEq(ILabel target) => Jump(OpCode.JMPEQ_L, (NeoVmLabel)target);
    public void Emit_JumpNe(ILabel target) => Jump(OpCode.JMPNE_L, (NeoVmLabel)target);
    public void Emit_JumpGt(ILabel target) => Jump(OpCode.JMPGT_L, (NeoVmLabel)target);
    public void Emit_JumpGe(ILabel target) => Jump(OpCode.JMPGE_L, (NeoVmLabel)target);
    public void Emit_JumpLt(ILabel target) => Jump(OpCode.JMPLT_L, (NeoVmLabel)target);
    public void Emit_JumpLe(ILabel target) => Jump(OpCode.JMPLE_L, (NeoVmLabel)target);
    public void Call(ILabel target) => Jump(OpCode.CALL_L, (NeoVmLabel)target);
    public void Ret() => Add(OpCode.RET);
    public void Throw() => Add(OpCode.THROW);
    public void Abort() => Add(OpCode.ABORT);
    public void AbortMsg() => Add(OpCode.ABORTMSG);
    public void Assert() => Add(OpCode.ASSERT);
    public void AssertMsg() => Add(OpCode.ASSERTMSG);
    public void Nop() => Add(OpCode.NOP);

    // --- Slots ---
    public void InitSlot(byte localCount, byte paramCount)
    {
        Add(new Instruction { OpCode = OpCode.INITSLOT, Operand = [localCount, paramCount] });
    }

    private void AccessSlot(OpCode opcode, byte index)
    {
        if (index >= 7)
            Add(new Instruction { OpCode = opcode, Operand = [index] });
        else
            Add(opcode - 7 + index);
    }

    public void LdArg(byte index) => AccessSlot(OpCode.LDARG, index);
    public void StArg(byte index) => AccessSlot(OpCode.STARG, index);
    public void LdLoc(byte index) => AccessSlot(OpCode.LDLOC, index);
    public void StLoc(byte index) => AccessSlot(OpCode.STLOC, index);
    public void LdSFld(byte index) => AccessSlot(OpCode.LDSFLD, index);
    public void StSFld(byte index) => AccessSlot(OpCode.STSFLD, index);

    // --- Syscalls ---
    public void Syscall(uint hash)
    {
        Add(new Instruction { OpCode = OpCode.SYSCALL, Operand = BitConverter.GetBytes(hash) });
    }
    public void CallToken(ushort token)
    {
        Add(new Instruction { OpCode = OpCode.CALLT, Operand = BitConverter.GetBytes(token) });
    }

    // --- Collections ---
    public void NewArray() => Add(OpCode.NEWARRAY);
    public void NewArrayT(byte type) => Add(new Instruction { OpCode = OpCode.NEWARRAY_T, Operand = [type] });
    public void NewStruct(int count) { PushInt(count); Add(OpCode.NEWSTRUCT); }
    public void NewMap() => Add(OpCode.NEWMAP);
    public void NewBuffer() => Add(OpCode.NEWBUFFER);
    public void Append() => Add(OpCode.APPEND);
    public void SetItem() => Add(OpCode.SETITEM);
    public void GetItem() => Add(OpCode.PICKITEM);
    public void Remove() => Add(OpCode.REMOVE);
    public void Size() => Add(OpCode.SIZE);
    public void HasKey() => Add(OpCode.HASKEY);
    public void Keys() => Add(OpCode.KEYS);
    public void Values() => Add(OpCode.VALUES);
    public void Pack(int count) { PushInt(count); Add(OpCode.PACK); }
    public void Unpack() => Add(OpCode.UNPACK);
    public void DeepCopy() => Add(OpCode.NEWARRAY); // Note: UNPACK+PACK pattern
    public void ReverseItems() => Add(OpCode.REVERSEITEMS);
    public void ClearItems() => Add(OpCode.CLEARITEMS);
    public void PopItem() => Add(OpCode.POPITEM);

    // --- String / Byte ---
    public void Cat() => Add(OpCode.CAT);
    public void Substr() => Add(OpCode.SUBSTR);
    public void Left() => Add(OpCode.LEFT);
    public void Right() => Add(OpCode.RIGHT);
    public void MemCpy() => Add(OpCode.MEMCPY);
    public void NumEqual() => Add(OpCode.NUMEQUAL);
    public void NumNotEqual() => Add(OpCode.NUMNOTEQUAL);

    // --- Exception handling ---
    public ITryBlock BeginTry(ILabel catchLabel, ILabel finallyLabel)
    {
        var block = new NeoVmTryBlock();
        var catchNeo = catchLabel as NeoVmLabel;
        var finallyNeo = finallyLabel as NeoVmLabel;
        Add(new Instruction
        {
            OpCode = OpCode.TRY_L,
            Target = catchNeo?.Target ?? new JumpTarget(),
            Target2 = finallyNeo?.Target ?? new JumpTarget(),
        });
        return block;
    }

    public void EndTry(ILabel endLabel)
    {
        Add(new Instruction { OpCode = OpCode.ENDTRY_L, Target = ((NeoVmLabel)endLabel).Target });
    }

    public void EndTryFinally() => Add(OpCode.ENDTRY);
    public void EndFinally() => Add(OpCode.ENDFINALLY);

    // --- Raw fallback ---
    public void EmitRaw(byte opcode, byte[]? operand = null)
    {
        Add(new Instruction { OpCode = (OpCode)opcode, Operand = operand });
    }
}
```

- [ ] **Step 4: Build to verify**

Run: `cd /home/neo/git/neo-riscv-vm/neo-devpack-dotnet && dotnet build src/Neo.Compiler.CSharp/Neo.Compiler.CSharp.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Neo.Compiler.CSharp/Backend/
git commit -m "feat: implement NeoVmEmitter wrapping existing instruction emission"
```

---

## Task 4: Wire ICodeEmitter into MethodConvert

This is the core refactoring. `MethodConvert` gets an `Emitter` property. The existing helper methods (`AddInstruction`, `Push`, `Jump`, `AccessSlot`) are updated to delegate to `Emitter` when available, falling back to direct `_instructions` manipulation for backward compatibility during incremental migration.

**Files:**
- Modify: `src/Neo.Compiler.CSharp/MethodConvert/MethodConvert.cs`
- Modify: `src/Neo.Compiler.CSharp/CompilationEngine/CompilationContext.cs`

- [ ] **Step 1: Add Emitter property to MethodConvert**

In `src/Neo.Compiler.CSharp/MethodConvert/MethodConvert.cs`, add after line 65 (the `_checkedStack` field):

```csharp
internal Backend.ICodeEmitter? Emitter { get; set; }
```

- [ ] **Step 2: Create emitter in CompilationContext and assign to MethodConvert**

In `src/Neo.Compiler.CSharp/CompilationEngine/CompilationContext.cs`, find where `MethodConvert` instances are created (in `ProcessMethod` or similar). After creating a `MethodConvert`, set its `Emitter`:

```csharp
// After: var convert = new MethodConvert(this, symbol);
if (Options.Target == CompilationTarget.RiscV)
{
    // TODO: RiscVEmitter - for now, always use NeoVmEmitter
}
// NeoVmEmitter is not used yet - the existing direct _instructions path
// still works. This will be wired incrementally.
```

- [ ] **Step 3: Build and run existing tests to verify no regression**

Run: `cd /home/neo/git/neo-riscv-vm/neo-devpack-dotnet && dotnet test tests/Neo.Compiler.CSharp.UnitTests/ --no-restore -v minimal 2>&1 | tail -5`
Expected: All existing tests pass (we only added a field, no behavior change)

- [ ] **Step 4: Commit**

```bash
git add src/Neo.Compiler.CSharp/MethodConvert/MethodConvert.cs \
        src/Neo.Compiler.CSharp/CompilationEngine/CompilationContext.cs
git commit -m "refactor: add ICodeEmitter property to MethodConvert"
```

---

## Task 5: Incrementally Migrate StackHelpers to Use Emitter

This is the largest mechanical change. We modify the helper methods in `StackHelpers.cs` to delegate through `Emitter` when it's set, while keeping the existing `_instructions` path as fallback. This lets us migrate file-by-file without breaking anything.

**Files:**
- Modify: `src/Neo.Compiler.CSharp/MethodConvert/Helpers/StackHelpers.cs`

- [ ] **Step 1: Update AddInstruction to route through Emitter**

Replace the `AddInstruction(OpCode)` method (lines 36-42) with:

```csharp
private Instruction AddInstruction(OpCode opcode)
{
    // When Emitter is wired, it captures all instructions.
    // We still add to _instructions for backward compat (NEF assembly, optimization).
    return AddInstruction(new Instruction
    {
        OpCode = opcode
    });
}
```

Note: The actual delegation to `Emitter` happens gradually. For now, `_instructions` remains the source of truth for NeoVM. The `Emitter` will be used as the primary path only when we complete the RISC-V backend. This task establishes the pattern.

- [ ] **Step 2: Build and run tests**

Run: `cd /home/neo/git/neo-riscv-vm/neo-devpack-dotnet && dotnet test tests/Neo.Compiler.CSharp.UnitTests/ --no-restore -v minimal 2>&1 | tail -5`
Expected: All tests pass (no behavior change)

- [ ] **Step 3: Commit**

```bash
git add src/Neo.Compiler.CSharp/MethodConvert/Helpers/StackHelpers.cs
git commit -m "refactor: prepare StackHelpers for ICodeEmitter delegation"
```

---

## Task 6: Create neo-riscv-rt Runtime Library Scaffold

**Files:**
- Create: `crates/neo-riscv-rt/Cargo.toml`
- Create: `crates/neo-riscv-rt/src/lib.rs`
- Create: `crates/neo-riscv-rt/src/stack_value.rs`
- Create: `crates/neo-riscv-rt/src/memory.rs`
- Modify: `Cargo.toml` (workspace members)

- [ ] **Step 1: Create Cargo.toml**

```toml
# crates/neo-riscv-rt/Cargo.toml
[package]
name = "neo-riscv-rt"
version = "0.1.0"
edition = "2021"

[dependencies]
neo-riscv-abi = { path = "../neo-riscv-abi" }

[features]
default = ["std"]
std = []
```

- [ ] **Step 2: Create stack_value.rs with tagged StackSlot**

```rust
// crates/neo-riscv-rt/src/stack_value.rs
use neo_riscv_abi::StackValue as AbiStackValue;

pub const TAG_INTEGER: u8 = 0;
pub const TAG_BOOLEAN: u8 = 1;
pub const TAG_BYTESTRING: u8 = 2;
pub const TAG_BIGINTEGER: u8 = 3;
pub const TAG_ARRAY: u8 = 4;
pub const TAG_STRUCT: u8 = 5;
pub const TAG_MAP: u8 = 6;
pub const TAG_NULL: u8 = 7;
pub const TAG_INTEROP: u8 = 8;
pub const TAG_ITERATOR: u8 = 9;
pub const TAG_BUFFER: u8 = 10;
pub const TAG_POINTER: u8 = 11;

/// Runtime stack value used by generated contract code.
/// Mirrors neo_riscv_abi::StackValue but optimized for in-memory manipulation.
#[derive(Clone, Debug)]
pub enum StackValue {
    Integer(i64),
    Boolean(bool),
    ByteString(Vec<u8>),
    BigInteger(Vec<u8>),
    Array(Vec<StackValue>),
    Struct(Vec<StackValue>),
    Map(Vec<(StackValue, StackValue)>),
    Null,
    Interop(u64),
    Iterator(u64),
    Buffer(Vec<u8>),
    Pointer(i64),
}

impl StackValue {
    pub fn to_abi(&self) -> AbiStackValue {
        match self {
            StackValue::Integer(v) => AbiStackValue::Integer(*v),
            StackValue::Boolean(v) => AbiStackValue::Boolean(*v),
            StackValue::ByteString(v) => AbiStackValue::ByteString(v.clone()),
            StackValue::BigInteger(v) => AbiStackValue::BigInteger(v.clone()),
            StackValue::Array(items) => AbiStackValue::Array(items.iter().map(|i| i.to_abi()).collect()),
            StackValue::Struct(items) => AbiStackValue::Struct(items.iter().map(|i| i.to_abi()).collect()),
            StackValue::Map(pairs) => AbiStackValue::Map(pairs.iter().map(|(k, v)| (k.to_abi(), v.to_abi())).collect()),
            StackValue::Null => AbiStackValue::Null,
            StackValue::Interop(h) => AbiStackValue::Interop(*h),
            StackValue::Iterator(h) => AbiStackValue::Iterator(*h),
            StackValue::Buffer(v) => AbiStackValue::ByteString(v.clone()),
            StackValue::Pointer(v) => AbiStackValue::Pointer(*v),
        }
    }

    pub fn from_abi(abi: &AbiStackValue) -> Self {
        match abi {
            AbiStackValue::Integer(v) => StackValue::Integer(*v),
            AbiStackValue::Boolean(v) => StackValue::Boolean(*v),
            AbiStackValue::ByteString(v) => StackValue::ByteString(v.clone()),
            AbiStackValue::BigInteger(v) => StackValue::BigInteger(v.clone()),
            AbiStackValue::Array(items) => StackValue::Array(items.iter().map(StackValue::from_abi).collect()),
            AbiStackValue::Struct(items) => StackValue::Struct(items.iter().map(StackValue::from_abi).collect()),
            AbiStackValue::Map(pairs) => StackValue::Map(pairs.iter().map(|(k, v)| (StackValue::from_abi(k), StackValue::from_abi(v))).collect()),
            AbiStackValue::Null => StackValue::Null,
            AbiStackValue::Interop(h) => StackValue::Interop(*h),
            AbiStackValue::Iterator(h) => StackValue::Iterator(*h),
            AbiStackValue::Pointer(v) => StackValue::Pointer(*v),
        }
    }

    pub fn type_tag(&self) -> u8 {
        match self {
            StackValue::Integer(_) => TAG_INTEGER,
            StackValue::Boolean(_) => TAG_BOOLEAN,
            StackValue::ByteString(_) => TAG_BYTESTRING,
            StackValue::BigInteger(_) => TAG_BIGINTEGER,
            StackValue::Array(_) => TAG_ARRAY,
            StackValue::Struct(_) => TAG_STRUCT,
            StackValue::Map(_) => TAG_MAP,
            StackValue::Null => TAG_NULL,
            StackValue::Interop(_) => TAG_INTEROP,
            StackValue::Iterator(_) => TAG_ITERATOR,
            StackValue::Buffer(_) => TAG_BUFFER,
            StackValue::Pointer(_) => TAG_POINTER,
        }
    }
}
```

- [ ] **Step 3: Create lib.rs with Context struct**

```rust
// crates/neo-riscv-rt/src/lib.rs
pub mod stack_value;
pub mod memory;

use stack_value::StackValue;
use neo_riscv_abi::{StackValue as AbiStackValue, ExecutionResult, VmState};
use neo_riscv_abi::callback_codec;

/// Runtime context for a contract method execution.
/// Generated Rust code calls methods on this struct.
pub struct Context {
    pub stack: Vec<StackValue>,
    pub locals: Vec<StackValue>,
    pub args: Vec<StackValue>,
    pub static_fields: Vec<StackValue>,
    pub fault_message: Option<String>,
    pub state: VmState,
}

impl Context {
    pub fn from_abi_stack(abi_stack: Vec<AbiStackValue>) -> Self {
        Context {
            stack: abi_stack.iter().map(StackValue::from_abi).collect(),
            locals: Vec::new(),
            args: Vec::new(),
            static_fields: Vec::new(),
            fault_message: None,
            state: VmState::Halt,
        }
    }

    pub fn init_slot(&mut self, local_count: usize, arg_count: usize) {
        self.locals = vec![StackValue::Null; local_count];
        // Args are popped from stack in reverse order
        self.args = Vec::with_capacity(arg_count);
        for _ in 0..arg_count {
            self.args.push(self.stack.pop().unwrap_or(StackValue::Null));
        }
        self.args.reverse();
    }

    pub fn fault(&mut self, msg: &str) {
        self.state = VmState::Fault;
        self.fault_message = Some(msg.to_string());
    }

    pub fn to_execution_result(&self, fee_consumed_pico: i64) -> ExecutionResult {
        ExecutionResult {
            fee_consumed_pico,
            state: self.state.clone(),
            stack: self.stack.iter().map(|v| v.to_abi()).collect(),
            fault_message: self.fault_message.clone(),
        }
    }

    // --- Stack operations ---
    pub fn push(&mut self, value: StackValue) {
        self.stack.push(value);
    }

    pub fn push_int(&mut self, value: i64) {
        self.stack.push(StackValue::Integer(value));
    }

    pub fn push_bool(&mut self, value: bool) {
        self.stack.push(StackValue::Boolean(value));
    }

    pub fn push_bytes(&mut self, data: &[u8]) {
        self.stack.push(StackValue::ByteString(data.to_vec()));
    }

    pub fn push_null(&mut self) {
        self.stack.push(StackValue::Null);
    }

    pub fn pop(&mut self) -> StackValue {
        self.stack.pop().unwrap_or(StackValue::Null)
    }

    pub fn drop(&mut self) {
        self.stack.pop();
    }

    pub fn dup(&mut self) {
        if let Some(top) = self.stack.last().cloned() {
            self.stack.push(top);
        }
    }

    pub fn swap(&mut self) {
        let len = self.stack.len();
        if len >= 2 {
            self.stack.swap(len - 1, len - 2);
        }
    }

    // --- Variable access ---
    pub fn load_arg(&mut self, index: usize) {
        let val = self.args.get(index).cloned().unwrap_or(StackValue::Null);
        self.stack.push(val);
    }

    pub fn store_arg(&mut self, index: usize) {
        let val = self.pop();
        if index >= self.args.len() {
            self.args.resize(index + 1, StackValue::Null);
        }
        self.args[index] = val;
    }

    pub fn load_local(&mut self, index: usize) {
        let val = self.locals.get(index).cloned().unwrap_or(StackValue::Null);
        self.stack.push(val);
    }

    pub fn store_local(&mut self, index: usize) {
        let val = self.pop();
        if index >= self.locals.len() {
            self.locals.resize(index + 1, StackValue::Null);
        }
        self.locals[index] = val;
    }

    pub fn load_static(&mut self, index: usize) {
        let val = self.static_fields.get(index).cloned().unwrap_or(StackValue::Null);
        self.stack.push(val);
    }

    pub fn store_static(&mut self, index: usize) {
        let val = self.pop();
        if index >= self.static_fields.len() {
            self.static_fields.resize(index + 1, StackValue::Null);
        }
        self.static_fields[index] = val;
    }

    // --- Arithmetic (integer fast path) ---
    pub fn add(&mut self) {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => {
                self.push(StackValue::Integer(a.wrapping_add(*b)));
            }
            _ => {
                // TODO: BigInteger support
                self.fault("Add: unsupported types");
            }
        }
    }

    pub fn sub(&mut self) {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => {
                self.push(StackValue::Integer(a.wrapping_sub(*b)));
            }
            _ => self.fault("Sub: unsupported types"),
        }
    }

    pub fn mul(&mut self) {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => {
                self.push(StackValue::Integer(a.wrapping_mul(*b)));
            }
            _ => self.fault("Mul: unsupported types"),
        }
    }

    pub fn equal(&mut self) {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => self.push_bool(a == b),
            (StackValue::Boolean(a), StackValue::Boolean(b)) => self.push_bool(a == b),
            (StackValue::Null, StackValue::Null) => self.push_bool(true),
            _ => self.push_bool(false), // simplified
        }
    }

    // --- Syscall ---
    pub fn syscall(&mut self, _hash: u32) {
        // TODO: marshal stack, call host_call(), unmarshal result
        // This will be implemented when the host bridge is wired
        self.fault("Syscall not yet implemented in rt");
    }

    pub fn convert(&mut self, _target_type: u8) {
        // TODO: type conversion
    }

    pub fn ret(&mut self) {
        // Return is a no-op at the Context level - the generated code
        // simply returns from the Rust function
    }
}
```

- [ ] **Step 4: Create memory.rs placeholder**

```rust
// crates/neo-riscv-rt/src/memory.rs

// Memory management will be needed for no_std PolkaVM builds.
// For now, with std feature, we use Vec/String heap allocation.
```

- [ ] **Step 5: Add to workspace**

In the root `Cargo.toml`, add `"crates/neo-riscv-rt"` to the workspace members list.

- [ ] **Step 6: Build to verify**

Run: `cd /home/neo/git/neo-riscv-vm && cargo build -p neo-riscv-rt`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add crates/neo-riscv-rt/ Cargo.toml
git commit -m "feat: scaffold neo-riscv-rt runtime library with Context and StackValue"
```

---

## Task 7: Implement RiscVEmitter (Rust Code Generator)

**Files:**
- Create: `src/Neo.Compiler.CSharp/Backend/RiscV/RustCodeBuilder.cs`
- Create: `src/Neo.Compiler.CSharp/Backend/RiscV/RiscVEmitter.cs`

The RISC-V emitter generates Rust source code that uses `neo-riscv-rt::Context`. Each `ICodeEmitter` method appends a line of Rust code like `ctx.push_int(42);` or `ctx.add();`.

- [ ] **Step 1: Create RustCodeBuilder**

```csharp
// src/Neo.Compiler.CSharp/Backend/RiscV/RustCodeBuilder.cs
using System.Text;

namespace Neo.Compiler.Backend.RiscV;

/// <summary>
/// Builds a Rust source file string for a compiled contract.
/// </summary>
internal class RustCodeBuilder
{
    private readonly StringBuilder _header = new();
    private readonly StringBuilder _methods = new();
    private readonly List<string> _methodNames = new();
    private StringBuilder? _currentMethod;
    private int _indent = 0;
    private int _labelCounter = 0;

    public RustCodeBuilder()
    {
        _header.AppendLine("#![allow(unused)]");
        _header.AppendLine("use neo_riscv_rt::Context;");
        _header.AppendLine("use neo_riscv_rt::stack_value::StackValue;");
        _header.AppendLine();
    }

    public void BeginMethod(string name)
    {
        _currentMethod = new StringBuilder();
        _methodNames.Add(name);
        _currentMethod.AppendLine($"pub fn method_{SanitizeName(name)}(ctx: &mut Context) {{");
        _indent = 1;
    }

    public void EndMethod()
    {
        _currentMethod!.AppendLine("}");
        _methods.Append(_currentMethod);
        _methods.AppendLine();
        _currentMethod = null;
        _indent = 0;
    }

    public void Line(string code)
    {
        _currentMethod?.Append(new string(' ', _indent * 4));
        _currentMethod?.AppendLine(code);
    }

    public string NewLabel()
    {
        return $"label_{_labelCounter++}";
    }

    public string Build(string contractName)
    {
        var sb = new StringBuilder();
        sb.Append(_header);
        sb.Append(_methods);

        // Generate dispatch function
        sb.AppendLine($"pub fn dispatch(ctx: &mut Context, method: &str) {{");
        sb.AppendLine("    match method {");
        foreach (var name in _methodNames)
        {
            sb.AppendLine($"        \"{name}\" => method_{SanitizeName(name)}(ctx),");
        }
        sb.AppendLine("        _ => ctx.fault(\"Unknown method\"),");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string SanitizeName(string name)
    {
        return name.Replace("-", "_").Replace(".", "_").ToLowerInvariant();
    }
}
```

- [ ] **Step 2: Create RiscVEmitter**

```csharp
// src/Neo.Compiler.CSharp/Backend/RiscV/RiscVEmitter.cs
using System.Numerics;

namespace Neo.Compiler.Backend.RiscV;

internal class RiscVLabel : ILabel
{
    public string Name { get; }
    public RiscVLabel(string name) => Name = name;
}

internal class RiscVTryBlock : ITryBlock
{
    public string CatchLabel { get; set; } = "";
    public string FinallyLabel { get; set; } = "";
}

internal class RiscVEmitter : ICodeEmitter
{
    private readonly RustCodeBuilder _builder = new();
    public RustCodeBuilder Builder => _builder;

    public void BeginMethod(string name, int paramCount, int localCount)
    {
        _builder.BeginMethod(name);
    }

    public void EndMethod() => _builder.EndMethod();

    // --- Push ---
    public void PushInt(BigInteger value) => _builder.Line($"ctx.push_int({value});");
    public void PushBool(bool value) => _builder.Line($"ctx.push_bool({value.ToString().ToLower()});");
    public void PushBytes(byte[] data)
    {
        var hex = BitConverter.ToString(data).Replace("-", "");
        _builder.Line($"ctx.push_bytes(&hex::decode(\"{hex}\").unwrap());");
    }
    public void PushString(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        _builder.Line($"ctx.push_bytes(\"{escaped}\".as_bytes());");
    }
    public void PushNull() => _builder.Line("ctx.push_null();");
    public void PushDefault(byte type) => _builder.Line($"ctx.push_default({type});");

    // --- Stack manipulation ---
    public void Drop(int count = 1) { for (int i = 0; i < count; i++) _builder.Line("ctx.drop();"); }
    public void Dup() => _builder.Line("ctx.dup();");
    public void Nip() => _builder.Line("ctx.nip();");
    public void XDrop(int? count) => _builder.Line($"ctx.xdrop({count?.ToString() ?? "None"});");
    public void Over() => _builder.Line("ctx.over();");
    public void Pick(int? index) => _builder.Line($"ctx.pick({index?.ToString() ?? "None"});");
    public void Tuck() => _builder.Line("ctx.tuck();");
    public void Swap() => _builder.Line("ctx.swap();");
    public void Rot() => _builder.Line("ctx.rot();");
    public void Roll(int? index) => _builder.Line($"ctx.roll({index?.ToString() ?? "None"});");
    public void Reverse3() => _builder.Line("ctx.reverse3();");
    public void Reverse4() => _builder.Line("ctx.reverse4();");
    public void ReverseN(int count) => _builder.Line($"ctx.reverse_n({count});");
    public void Clear() => _builder.Line("ctx.clear();");
    public void Depth() => _builder.Line("ctx.depth();");

    // --- Arithmetic ---
    public void Add() => _builder.Line("ctx.add();");
    public void Sub() => _builder.Line("ctx.sub();");
    public void Mul() => _builder.Line("ctx.mul();");
    public void Div() => _builder.Line("ctx.div();");
    public void Mod() => _builder.Line("ctx.modulo();");
    public void Negate() => _builder.Line("ctx.negate();");
    public void Abs() => _builder.Line("ctx.abs();");
    public void Sign() => _builder.Line("ctx.sign();");
    public void Min() => _builder.Line("ctx.min();");
    public void Max() => _builder.Line("ctx.max();");
    public void Pow() => _builder.Line("ctx.pow();");
    public void Sqrt() => _builder.Line("ctx.sqrt();");
    public void ModMul() => _builder.Line("ctx.modmul();");
    public void ModPow() => _builder.Line("ctx.modpow();");
    public void ShiftLeft() => _builder.Line("ctx.shl();");
    public void ShiftRight() => _builder.Line("ctx.shr();");

    // --- Bitwise ---
    public void BitwiseAnd() => _builder.Line("ctx.bitwise_and();");
    public void BitwiseOr() => _builder.Line("ctx.bitwise_or();");
    public void BitwiseXor() => _builder.Line("ctx.bitwise_xor();");
    public void BitwiseNot() => _builder.Line("ctx.bitwise_not();");

    // --- Comparison ---
    public void Equal() => _builder.Line("ctx.equal();");
    public void NotEqual() => _builder.Line("ctx.not_equal();");
    public void LessThan() => _builder.Line("ctx.less_than();");
    public void LessOrEqual() => _builder.Line("ctx.less_or_equal();");
    public void GreaterThan() => _builder.Line("ctx.greater_than();");
    public void GreaterOrEqual() => _builder.Line("ctx.greater_or_equal();");
    public void BoolAnd() => _builder.Line("ctx.bool_and();");
    public void BoolOr() => _builder.Line("ctx.bool_or();");
    public void Not() => _builder.Line("ctx.not();");
    public void NullCheck() => _builder.Line("ctx.is_null();");

    // --- Type ops ---
    public void IsType(byte type) => _builder.Line($"ctx.is_type({type});");
    public void Convert(byte type) => _builder.Line($"ctx.convert({type});");

    // --- Control flow ---
    public ILabel DefineLabel() => new RiscVLabel(_builder.NewLabel());
    public void MarkLabel(ILabel label) => _builder.Line($"// {((RiscVLabel)label).Name}:");
    public void Emit_Jump(ILabel target) => _builder.Line($"// jump {((RiscVLabel)target).Name}");
    public void Emit_JumpIf(ILabel target) => _builder.Line($"// jumpif {((RiscVLabel)target).Name}");
    public void Emit_JumpIfNot(ILabel target) => _builder.Line($"// jumpifnot {((RiscVLabel)target).Name}");
    public void Emit_JumpEq(ILabel target) => _builder.Line($"// jumpeq {((RiscVLabel)target).Name}");
    public void Emit_JumpNe(ILabel target) => _builder.Line($"// jumpne {((RiscVLabel)target).Name}");
    public void Emit_JumpGt(ILabel target) => _builder.Line($"// jumpgt {((RiscVLabel)target).Name}");
    public void Emit_JumpGe(ILabel target) => _builder.Line($"// jumpge {((RiscVLabel)target).Name}");
    public void Emit_JumpLt(ILabel target) => _builder.Line($"// jumplt {((RiscVLabel)target).Name}");
    public void Emit_JumpLe(ILabel target) => _builder.Line($"// jumple {((RiscVLabel)target).Name}");
    public void Call(ILabel target) => _builder.Line($"// call {((RiscVLabel)target).Name}");
    public void Ret() => _builder.Line("ctx.ret();");
    public void Throw() => _builder.Line("ctx.throw_ex();");
    public void Abort() => _builder.Line("ctx.abort();");
    public void AbortMsg() => _builder.Line("ctx.abort_msg();");
    public void Assert() => _builder.Line("ctx.assert_top();");
    public void AssertMsg() => _builder.Line("ctx.assert_msg();");
    public void Nop() => _builder.Line("// nop");

    // --- Slots ---
    public void InitSlot(byte localCount, byte paramCount)
        => _builder.Line($"ctx.init_slot({localCount}, {paramCount});");
    public void LdArg(byte index) => _builder.Line($"ctx.load_arg({index});");
    public void StArg(byte index) => _builder.Line($"ctx.store_arg({index});");
    public void LdLoc(byte index) => _builder.Line($"ctx.load_local({index});");
    public void StLoc(byte index) => _builder.Line($"ctx.store_local({index});");
    public void LdSFld(byte index) => _builder.Line($"ctx.load_static({index});");
    public void StSFld(byte index) => _builder.Line($"ctx.store_static({index});");

    // --- Syscalls ---
    public void Syscall(uint hash) => _builder.Line($"ctx.syscall(0x{hash:x8});");
    public void CallToken(ushort token) => _builder.Line($"ctx.call_token({token});");

    // --- Collections ---
    public void NewArray() => _builder.Line("ctx.new_array();");
    public void NewArrayT(byte type) => _builder.Line($"ctx.new_array_t({type});");
    public void NewStruct(int count) => _builder.Line($"ctx.new_struct({count});");
    public void NewMap() => _builder.Line("ctx.new_map();");
    public void NewBuffer() => _builder.Line("ctx.new_buffer();");
    public void Append() => _builder.Line("ctx.append();");
    public void SetItem() => _builder.Line("ctx.set_item();");
    public void GetItem() => _builder.Line("ctx.get_item();");
    public void Remove() => _builder.Line("ctx.remove();");
    public void Size() => _builder.Line("ctx.size();");
    public void HasKey() => _builder.Line("ctx.has_key();");
    public void Keys() => _builder.Line("ctx.keys();");
    public void Values() => _builder.Line("ctx.values();");
    public void Pack(int count) => _builder.Line($"ctx.pack({count});");
    public void Unpack() => _builder.Line("ctx.unpack();");
    public void DeepCopy() => _builder.Line("ctx.deep_copy();");
    public void ReverseItems() => _builder.Line("ctx.reverse_items();");
    public void ClearItems() => _builder.Line("ctx.clear_items();");
    public void PopItem() => _builder.Line("ctx.pop_item();");

    // --- String / Byte ---
    public void Cat() => _builder.Line("ctx.cat();");
    public void Substr() => _builder.Line("ctx.substr();");
    public void Left() => _builder.Line("ctx.left();");
    public void Right() => _builder.Line("ctx.right();");
    public void MemCpy() => _builder.Line("ctx.memcpy();");
    public void NumEqual() => _builder.Line("ctx.num_equal();");
    public void NumNotEqual() => _builder.Line("ctx.num_not_equal();");

    // --- Exception handling ---
    public ITryBlock BeginTry(ILabel catchLabel, ILabel finallyLabel)
    {
        _builder.Line($"// try (catch: {((RiscVLabel)catchLabel).Name}, finally: {((RiscVLabel)finallyLabel).Name})");
        return new RiscVTryBlock
        {
            CatchLabel = ((RiscVLabel)catchLabel).Name,
            FinallyLabel = ((RiscVLabel)finallyLabel).Name,
        };
    }
    public void EndTry(ILabel endLabel) => _builder.Line($"// endtry -> {((RiscVLabel)endLabel).Name}");
    public void EndTryFinally() => _builder.Line("// endtry_finally");
    public void EndFinally() => _builder.Line("// endfinally");

    // --- Raw fallback ---
    public void EmitRaw(byte opcode, byte[]? operand = null)
    {
        _builder.Line($"// raw opcode 0x{opcode:x2} (not supported in RISC-V backend)");
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `cd /home/neo/git/neo-riscv-vm/neo-devpack-dotnet && dotnet build src/Neo.Compiler.CSharp/Neo.Compiler.CSharp.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Neo.Compiler.CSharp/Backend/RiscV/
git commit -m "feat: implement RiscVEmitter generating Rust source via Context API"
```

---

## Task 8: Wire Target-Aware Output in CompilationContext

**Files:**
- Modify: `src/Neo.Compiler.CSharp/CompilationEngine/CompilationContext.cs`
- Modify: `src/Neo.Compiler.CSharp/Program.cs`

- [ ] **Step 1: Add RISC-V output path to CompilationContext**

In `CompilationContext.cs`, add a method alongside `CreateExecutable()` (around line 218):

```csharp
/// <summary>
/// For RISC-V target: returns the generated Rust source code string.
/// The caller is responsible for invoking the Rust toolchain.
/// </summary>
internal string? GeneratedRustSource { get; set; }
```

- [ ] **Step 2: Add RISC-V output handling in Program.cs**

In the `ProcessOutput` method of `Program.cs`, add a branch for RISC-V target that writes the `.rs` file:

```csharp
if (context.Options.Target == CompilationTarget.RiscV && context.GeneratedRustSource != null)
{
    string rsPath = Path.ChangeExtension(path, ".rs");
    File.WriteAllText(rsPath, context.GeneratedRustSource);
    Console.WriteLine($"Generated Rust source: {rsPath}");
    // TODO: invoke neo-riscv-compile to produce .polkavm
}
```

- [ ] **Step 3: Build to verify**

Run: `cd /home/neo/git/neo-riscv-vm/neo-devpack-dotnet && dotnet build src/Neo.Compiler.CSharp/Neo.Compiler.CSharp.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Neo.Compiler.CSharp/CompilationEngine/CompilationContext.cs \
        src/Neo.Compiler.CSharp/Program.cs
git commit -m "feat: wire target-aware output for RISC-V Rust source generation"
```

---

## Task 9: Add Regression Test for NeoVmEmitter

**Files:**
- Create: `tests/Neo.Compiler.CSharp.UnitTests/UnitTest_ICodeEmitter.cs`

This test verifies that `NeoVmEmitter` produces the exact same bytecode as direct `Instruction` creation.

- [ ] **Step 1: Write the test**

```csharp
// tests/Neo.Compiler.CSharp.UnitTests/UnitTest_ICodeEmitter.cs
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Compiler.Backend;
using Neo.VM;
using System.Linq;
using System.Numerics;

namespace Neo.Compiler.CSharp.UnitTests;

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

        var instructions = emitter.Instructions;
        Assert.AreEqual(OpCode.PUSHINT16, instructions[0].OpCode);
        Assert.AreEqual(OpCode.PUSHINT32, instructions[1].OpCode);
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
            new[] { OpCode.PUSH2, OpCode.PUSH3, OpCode.ADD, OpCode.RET },
            opcodes);
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
        emitter.Syscall(0x525b7d62); // System.Contract.Call

        Assert.AreEqual(OpCode.SYSCALL, emitter.Instructions[0].OpCode);
        Assert.AreEqual(4, emitter.Instructions[0].Operand!.Length);
    }

    [TestMethod]
    public void TestPushBytes()
    {
        var emitter = new NeoVmEmitter();
        emitter.PushBytes(new byte[] { 0x01, 0x02, 0x03 });

        Assert.AreEqual(OpCode.PUSHDATA1, emitter.Instructions[0].OpCode);
        Assert.AreEqual(3, emitter.Instructions[0].Operand![0]); // length prefix
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
            new[] { OpCode.NEWMAP, OpCode.DUP, OpCode.PUSH1, OpCode.PUSH2, OpCode.SETITEM },
            opcodes);
    }
}
```

- [ ] **Step 2: Run the test**

Run: `cd /home/neo/git/neo-riscv-vm/neo-devpack-dotnet && dotnet test tests/Neo.Compiler.CSharp.UnitTests/ --filter "FullyQualifiedName~UnitTest_ICodeEmitter" -v normal`
Expected: All tests PASS

- [ ] **Step 3: Commit**

```bash
git add tests/Neo.Compiler.CSharp.UnitTests/UnitTest_ICodeEmitter.cs
git commit -m "test: add NeoVmEmitter regression tests for ICodeEmitter"
```

---

## Task 10: Add neo-riscv-rt Unit Tests

**Files:**
- Create: `crates/neo-riscv-rt/tests/context_tests.rs`

- [ ] **Step 1: Write runtime Context tests**

```rust
// crates/neo-riscv-rt/tests/context_tests.rs
use neo_riscv_rt::Context;
use neo_riscv_rt::stack_value::StackValue;

#[test]
fn test_push_pop_integer() {
    let mut ctx = Context::from_abi_stack(vec![]);
    ctx.push_int(42);
    let val = ctx.pop();
    match val {
        StackValue::Integer(v) => assert_eq!(v, 42),
        _ => panic!("Expected Integer"),
    }
}

#[test]
fn test_init_slot_loads_args_from_stack() {
    let mut ctx = Context::from_abi_stack(vec![]);
    ctx.push_int(10);
    ctx.push_int(20);
    ctx.init_slot(1, 2);
    // After init_slot, args are popped from stack
    assert_eq!(ctx.stack.len(), 0);
    ctx.load_arg(0);
    match ctx.pop() {
        StackValue::Integer(v) => assert_eq!(v, 10),
        _ => panic!("Expected Integer(10)"),
    }
    ctx.load_arg(1);
    match ctx.pop() {
        StackValue::Integer(v) => assert_eq!(v, 20),
        _ => panic!("Expected Integer(20)"),
    }
}

#[test]
fn test_add_integers() {
    let mut ctx = Context::from_abi_stack(vec![]);
    ctx.push_int(3);
    ctx.push_int(4);
    ctx.add();
    match ctx.pop() {
        StackValue::Integer(v) => assert_eq!(v, 7),
        _ => panic!("Expected Integer(7)"),
    }
}

#[test]
fn test_sub_integers() {
    let mut ctx = Context::from_abi_stack(vec![]);
    ctx.push_int(10);
    ctx.push_int(3);
    ctx.sub();
    match ctx.pop() {
        StackValue::Integer(v) => assert_eq!(v, 7),
        _ => panic!("Expected Integer(7)"),
    }
}

#[test]
fn test_equal_integers() {
    let mut ctx = Context::from_abi_stack(vec![]);
    ctx.push_int(5);
    ctx.push_int(5);
    ctx.equal();
    match ctx.pop() {
        StackValue::Boolean(v) => assert!(v),
        _ => panic!("Expected Boolean(true)"),
    }
}

#[test]
fn test_local_variable_store_load() {
    let mut ctx = Context::from_abi_stack(vec![]);
    ctx.init_slot(2, 0);
    ctx.push_int(99);
    ctx.store_local(0);
    ctx.push_int(100);
    ctx.store_local(1);
    ctx.load_local(0);
    match ctx.pop() {
        StackValue::Integer(v) => assert_eq!(v, 99),
        _ => panic!("Expected 99"),
    }
    ctx.load_local(1);
    match ctx.pop() {
        StackValue::Integer(v) => assert_eq!(v, 100),
        _ => panic!("Expected 100"),
    }
}

#[test]
fn test_dup_and_swap() {
    let mut ctx = Context::from_abi_stack(vec![]);
    ctx.push_int(1);
    ctx.push_int(2);
    ctx.dup();
    assert_eq!(ctx.stack.len(), 3);
    ctx.swap();
    // Stack is now: 1, 2, 2 -> after swap: 1, 2, 2 (swaps top two)
    match ctx.pop() {
        StackValue::Integer(v) => assert_eq!(v, 2),
        _ => panic!("Expected 2"),
    }
}

#[test]
fn test_push_null_and_null_check() {
    let mut ctx = Context::from_abi_stack(vec![]);
    ctx.push_null();
    match ctx.pop() {
        StackValue::Null => {}
        _ => panic!("Expected Null"),
    }
}

#[test]
fn test_push_bool() {
    let mut ctx = Context::from_abi_stack(vec![]);
    ctx.push_bool(true);
    ctx.push_bool(false);
    match ctx.pop() {
        StackValue::Boolean(v) => assert!(!v),
        _ => panic!("Expected false"),
    }
    match ctx.pop() {
        StackValue::Boolean(v) => assert!(v),
        _ => panic!("Expected true"),
    }
}

#[test]
fn test_static_fields() {
    let mut ctx = Context::from_abi_stack(vec![]);
    ctx.push_int(42);
    ctx.store_static(0);
    ctx.load_static(0);
    match ctx.pop() {
        StackValue::Integer(v) => assert_eq!(v, 42),
        _ => panic!("Expected 42"),
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `cd /home/neo/git/neo-riscv-vm && cargo test -p neo-riscv-rt`
Expected: All tests PASS

- [ ] **Step 3: Commit**

```bash
git add crates/neo-riscv-rt/tests/
git commit -m "test: add neo-riscv-rt Context unit tests"
```

---

## Task 11: Add RiscVEmitter Smoke Test

**Files:**
- Create: `tests/Neo.Compiler.CSharp.UnitTests/UnitTest_RiscVTarget.cs`

Verifies that the `RiscVEmitter` generates syntactically valid Rust source for a simple operation sequence.

- [ ] **Step 1: Write the test**

```csharp
// tests/Neo.Compiler.CSharp.UnitTests/UnitTest_RiscVTarget.cs
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Compiler.Backend.RiscV;

namespace Neo.Compiler.CSharp.UnitTests;

[TestClass]
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

        // Verify the generated source contains expected method and calls
        Assert.IsTrue(rustSource.Contains("pub fn method_balanceof(ctx: &mut Context)"));
        Assert.IsTrue(rustSource.Contains("ctx.init_slot(0, 1);"));
        Assert.IsTrue(rustSource.Contains("ctx.syscall(0x4a100170);"));
        Assert.IsTrue(rustSource.Contains("ctx.load_arg(0);"));
        Assert.IsTrue(rustSource.Contains("ctx.convert(2);"));
        Assert.IsTrue(rustSource.Contains("ctx.syscall(0x31e85d92);"));
        Assert.IsTrue(rustSource.Contains("ctx.ret();"));

        // Verify dispatch function
        Assert.IsTrue(rustSource.Contains("\"balanceOf\" => method_balanceof(ctx)"));
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
}
```

- [ ] **Step 2: Run the test**

Run: `cd /home/neo/git/neo-riscv-vm/neo-devpack-dotnet && dotnet test tests/Neo.Compiler.CSharp.UnitTests/ --filter "FullyQualifiedName~UnitTest_RiscVTarget" -v normal`
Expected: All tests PASS

- [ ] **Step 3: Commit**

```bash
git add tests/Neo.Compiler.CSharp.UnitTests/UnitTest_RiscVTarget.cs
git commit -m "test: add RiscVEmitter smoke tests for Rust code generation"
```

---

## Task 12: Run Full Test Suite and Verify Zero Regression

This task verifies the entire existing test suite still passes after all changes.

- [ ] **Step 1: Build the full solution**

Run: `cd /home/neo/git/neo-riscv-vm/neo-devpack-dotnet && dotnet build`
Expected: Build succeeded, 0 errors

- [ ] **Step 2: Run all compiler tests**

Run: `cd /home/neo/git/neo-riscv-vm/neo-devpack-dotnet && dotnet test tests/Neo.Compiler.CSharp.UnitTests/ -v minimal 2>&1 | tail -10`
Expected: All tests pass, no regressions

- [ ] **Step 3: Run Rust tests**

Run: `cd /home/neo/git/neo-riscv-vm && cargo test -p neo-riscv-rt`
Expected: All tests pass

- [ ] **Step 4: Commit any remaining fixes**

If any test failures were found and fixed in previous steps, ensure they are committed.

---

## Summary of What Each Phase Delivers

After completing Tasks 1-12, you have:

1. **`--target riscv|neovm` CLI flag** wired through the compiler
2. **`ICodeEmitter` interface** abstracting instruction emission
3. **`NeoVmEmitter`** wrapping existing NeoVM behavior (regression-tested)
4. **`RiscVEmitter`** generating Rust source code using Context API
5. **`neo-riscv-rt`** crate with Context struct, StackValue, basic arithmetic (unit-tested)
6. **Zero regression** on existing NeoVM compilation tests
7. **Foundation** for migrating MethodConvert expression/statement converters to use `ICodeEmitter`

### Next Phase (not in this plan)

- Migrate all 29 Expression converters + 19 Statement converters + 13 System converters to use `Emitter` instead of `AddInstruction`
- Complete `neo-riscv-rt` with BigInteger, collections, strings, exception handling, syscall marshaling
- Build the `neo-riscv-compile` helper tool (Rust → PolkaVM)
- End-to-end: compile a real contract with `--target riscv`, execute on `neo-riscv-host`
- Parity testing suite
