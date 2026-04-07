# Phase 2: NeoVM-to-Rust Translator — End-to-End RISC-V Compilation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Compile a C# smart contract to Rust source code that runs natively on PolkaVM, by translating the NeoVM instruction stream post-compilation.

**Architecture:** After the standard NeoVM compilation pipeline runs, a `NeoVmToRustTranslator` post-processor walks `MethodConvert.Instructions` (which have rich `JumpTarget` references for label resolution) and generates Rust source using `neo-riscv-rt::Context`. For control flow, methods with jumps use an offset-based state machine (`loop { match pc { ... } }`). The existing compilation pipeline is unchanged — we only add a post-processing step.

**Tech Stack:** C# (.NET 8), NeoVM bytecodes, Rust, neo-riscv-rt

**Spec:** `docs/superpowers/specs/2026-03-31-dual-target-compiler-design.md`

**Depends on:** Phase 1 complete (ICodeEmitter, NeoVmEmitter, RiscVEmitter, neo-riscv-rt scaffold)

---

## File Structure

### New Files

| File | Responsibility |
|------|---------------|
| `src/Neo.Compiler.CSharp/Backend/RiscV/NeoVmToRustTranslator.cs` | Translates NeoVM instructions → Rust source |
| `src/Neo.Compiler.CSharp/Backend/RiscV/InstructionTranslator.cs` | Maps individual NeoVM opcodes → Rust Context API calls |
| `tests/Neo.Compiler.CSharp.UnitTests/UnitTest_NeoVmToRustTranslator.cs` | Translator tests |
| `crates/neo-riscv-rt/src/arithmetic.rs` | Complete BigInteger + numeric operations |
| `crates/neo-riscv-rt/src/comparison.rs` | All comparison and logical operations |
| `crates/neo-riscv-rt/src/collections.rs` | Array, Struct, Map, Buffer operations |
| `crates/neo-riscv-rt/src/strings.rs` | String/ByteString operations |
| `crates/neo-riscv-rt/src/conversion.rs` | Type conversion operations |

### Modified Files

| File | Change |
|------|--------|
| `src/Neo.Compiler.CSharp/CompilationEngine/CompilationContext.cs` | Call translator when target is RiscV |
| `crates/neo-riscv-rt/src/lib.rs` | Add all missing Context methods |
| `crates/neo-riscv-rt/Cargo.toml` | (if new deps needed) |

---

## Task 1: Create InstructionTranslator — Single Opcode → Rust Line

**Files:**
- Create: `src/Neo.Compiler.CSharp/Backend/RiscV/InstructionTranslator.cs`

This is a pure static mapping from NeoVM opcodes to Rust code strings. No control flow logic — just individual instruction translation.

- [ ] **Step 1: Create InstructionTranslator**

```csharp
// src/Neo.Compiler.CSharp/Backend/RiscV/InstructionTranslator.cs
using System;
using System.Buffers.Binary;
using System.Numerics;
using Neo.VM;

namespace Neo.Compiler.Backend.RiscV;

/// <summary>
/// Translates a single NeoVM Instruction to a Rust Context API call string.
/// Returns null for control-flow instructions (handled by NeoVmToRustTranslator).
/// </summary>
internal static class InstructionTranslator
{
    /// <summary>
    /// Translate instruction to Rust code. Returns null for jump/try instructions.
    /// </summary>
    public static string? Translate(Instruction instruction)
    {
        var op = instruction.OpCode;
        var operand = instruction.Operand;

        return op switch
        {
            // --- Push constants ---
            OpCode.PUSHM1 => "ctx.push_int(-1);",
            OpCode.PUSH0 => "ctx.push_int(0);",
            OpCode.PUSH1 => "ctx.push_int(1);",
            OpCode.PUSH2 => "ctx.push_int(2);",
            OpCode.PUSH3 => "ctx.push_int(3);",
            OpCode.PUSH4 => "ctx.push_int(4);",
            OpCode.PUSH5 => "ctx.push_int(5);",
            OpCode.PUSH6 => "ctx.push_int(6);",
            OpCode.PUSH7 => "ctx.push_int(7);",
            OpCode.PUSH8 => "ctx.push_int(8);",
            OpCode.PUSH9 => "ctx.push_int(9);",
            OpCode.PUSH10 => "ctx.push_int(10);",
            OpCode.PUSH11 => "ctx.push_int(11);",
            OpCode.PUSH12 => "ctx.push_int(12);",
            OpCode.PUSH13 => "ctx.push_int(13);",
            OpCode.PUSH14 => "ctx.push_int(14);",
            OpCode.PUSH15 => "ctx.push_int(15);",
            OpCode.PUSH16 => "ctx.push_int(16);",
            OpCode.PUSHINT8 => $"ctx.push_int({(sbyte)operand![0]});",
            OpCode.PUSHINT16 => $"ctx.push_int({BinaryPrimitives.ReadInt16LittleEndian(operand)});",
            OpCode.PUSHINT32 => $"ctx.push_int({BinaryPrimitives.ReadInt32LittleEndian(operand)});",
            OpCode.PUSHINT64 => $"ctx.push_int({BinaryPrimitives.ReadInt64LittleEndian(operand)});",
            OpCode.PUSHINT128 or OpCode.PUSHINT256 => $"ctx.push_bigint(&[{FormatBytes(operand!)}]);",
            OpCode.PUSHT => "ctx.push_bool(true);",
            OpCode.PUSHF => "ctx.push_bool(false);",
            OpCode.PUSHNULL => "ctx.push_null();",
            OpCode.PUSHDATA1 => $"ctx.push_bytes(&[{FormatBytes(operand!.AsSpan(1, operand[0]))}]);",
            OpCode.PUSHDATA2 => $"ctx.push_bytes(&[{FormatBytes(operand!.AsSpan(2, BinaryPrimitives.ReadUInt16LittleEndian(operand)))}]);",
            OpCode.PUSHDATA4 => $"ctx.push_bytes(&[{FormatBytes(operand!.AsSpan(4, (int)BinaryPrimitives.ReadUInt32LittleEndian(operand)))}]);",
            OpCode.PUSHA => null, // handled by translator (offset-based)

            // --- Stack manipulation ---
            OpCode.NOP => null, // skip NOPs (or emit as comment)
            OpCode.DROP => "ctx.drop();",
            OpCode.NIP => "ctx.nip();",
            OpCode.XDROP => "ctx.xdrop();",
            OpCode.CLEAR => "ctx.clear();",
            OpCode.DUP => "ctx.dup();",
            OpCode.OVER => "ctx.over();",
            OpCode.PICK => "ctx.pick();",
            OpCode.TUCK => "ctx.tuck();",
            OpCode.SWAP => "ctx.swap();",
            OpCode.ROT => "ctx.rot();",
            OpCode.ROLL => "ctx.roll();",
            OpCode.REVERSE3 => "ctx.reverse3();",
            OpCode.REVERSE4 => "ctx.reverse4();",
            OpCode.REVERSEN => "ctx.reverse_n();",
            OpCode.DEPTH => "ctx.depth();",

            // --- Slots ---
            OpCode.INITSLOT => $"ctx.init_slot({operand![0]}, {operand[1]});",
            OpCode.LDSFLD0 => "ctx.load_static(0);",
            OpCode.LDSFLD1 => "ctx.load_static(1);",
            OpCode.LDSFLD2 => "ctx.load_static(2);",
            OpCode.LDSFLD3 => "ctx.load_static(3);",
            OpCode.LDSFLD4 => "ctx.load_static(4);",
            OpCode.LDSFLD5 => "ctx.load_static(5);",
            OpCode.LDSFLD6 => "ctx.load_static(6);",
            OpCode.LDSFLD => $"ctx.load_static({operand![0]});",
            OpCode.STSFLD0 => "ctx.store_static(0);",
            OpCode.STSFLD1 => "ctx.store_static(1);",
            OpCode.STSFLD2 => "ctx.store_static(2);",
            OpCode.STSFLD3 => "ctx.store_static(3);",
            OpCode.STSFLD4 => "ctx.store_static(4);",
            OpCode.STSFLD5 => "ctx.store_static(5);",
            OpCode.STSFLD6 => "ctx.store_static(6);",
            OpCode.STSFLD => $"ctx.store_static({operand![0]});",
            OpCode.LDLOC0 => "ctx.load_local(0);",
            OpCode.LDLOC1 => "ctx.load_local(1);",
            OpCode.LDLOC2 => "ctx.load_local(2);",
            OpCode.LDLOC3 => "ctx.load_local(3);",
            OpCode.LDLOC4 => "ctx.load_local(4);",
            OpCode.LDLOC5 => "ctx.load_local(5);",
            OpCode.LDLOC6 => "ctx.load_local(6);",
            OpCode.LDLOC => $"ctx.load_local({operand![0]});",
            OpCode.STLOC0 => "ctx.store_local(0);",
            OpCode.STLOC1 => "ctx.store_local(1);",
            OpCode.STLOC2 => "ctx.store_local(2);",
            OpCode.STLOC3 => "ctx.store_local(3);",
            OpCode.STLOC4 => "ctx.store_local(4);",
            OpCode.STLOC5 => "ctx.store_local(5);",
            OpCode.STLOC6 => "ctx.store_local(6);",
            OpCode.STLOC => $"ctx.store_local({operand![0]});",
            OpCode.LDARG0 => "ctx.load_arg(0);",
            OpCode.LDARG1 => "ctx.load_arg(1);",
            OpCode.LDARG2 => "ctx.load_arg(2);",
            OpCode.LDARG3 => "ctx.load_arg(3);",
            OpCode.LDARG4 => "ctx.load_arg(4);",
            OpCode.LDARG5 => "ctx.load_arg(5);",
            OpCode.LDARG6 => "ctx.load_arg(6);",
            OpCode.LDARG => $"ctx.load_arg({operand![0]});",
            OpCode.STARG0 => "ctx.store_arg(0);",
            OpCode.STARG1 => "ctx.store_arg(1);",
            OpCode.STARG2 => "ctx.store_arg(2);",
            OpCode.STARG3 => "ctx.store_arg(3);",
            OpCode.STARG4 => "ctx.store_arg(4);",
            OpCode.STARG5 => "ctx.store_arg(5);",
            OpCode.STARG6 => "ctx.store_arg(6);",
            OpCode.STARG => $"ctx.store_arg({operand![0]});",

            // --- Arithmetic ---
            OpCode.ADD => "ctx.add();",
            OpCode.SUB => "ctx.sub();",
            OpCode.MUL => "ctx.mul();",
            OpCode.DIV => "ctx.div();",
            OpCode.MOD => "ctx.modulo();",
            OpCode.NEGATE => "ctx.negate();",
            OpCode.ABS => "ctx.abs_val();",
            OpCode.SIGN => "ctx.sign();",
            OpCode.MAX => "ctx.max();",
            OpCode.MIN => "ctx.min();",
            OpCode.POW => "ctx.pow();",
            OpCode.SQRT => "ctx.sqrt();",
            OpCode.MODMUL => "ctx.modmul();",
            OpCode.MODPOW => "ctx.modpow();",
            OpCode.SHL => "ctx.shl();",
            OpCode.SHR => "ctx.shr();",

            // --- Bitwise ---
            OpCode.AND => "ctx.bitwise_and();",
            OpCode.OR => "ctx.bitwise_or();",
            OpCode.XOR => "ctx.bitwise_xor();",
            OpCode.INVERT => "ctx.bitwise_not();",

            // --- Comparison ---
            OpCode.EQUAL => "ctx.equal();",
            OpCode.NOTEQUAL => "ctx.not_equal();",
            OpCode.LT => "ctx.less_than();",
            OpCode.LE => "ctx.less_or_equal();",
            OpCode.GT => "ctx.greater_than();",
            OpCode.GE => "ctx.greater_or_equal();",
            OpCode.NUMEQUAL => "ctx.num_equal();",
            OpCode.NUMNOTEQUAL => "ctx.num_not_equal();",
            OpCode.BOOLAND => "ctx.bool_and();",
            OpCode.BOOLOR => "ctx.bool_or();",
            OpCode.NOT => "ctx.not();",
            OpCode.NZ => "ctx.nz();",

            // --- Type ---
            OpCode.ISNULL => "ctx.is_null();",
            OpCode.ISTYPE => $"ctx.is_type(0x{operand![0]:x2});",
            OpCode.CONVERT => $"ctx.convert(0x{operand![0]:x2});",

            // --- Collections ---
            OpCode.NEWARRAY0 => "ctx.new_array_0();",
            OpCode.NEWARRAY => "ctx.new_array();",
            OpCode.NEWARRAY_T => $"ctx.new_array_t(0x{operand![0]:x2});",
            OpCode.NEWSTRUCT0 => "ctx.new_struct_0();",
            OpCode.NEWSTRUCT => "ctx.new_struct();",
            OpCode.NEWMAP => "ctx.new_map();",
            OpCode.NEWBUFFER => "ctx.new_buffer();",
            OpCode.APPEND => "ctx.append();",
            OpCode.SETITEM => "ctx.set_item();",
            OpCode.PICKITEM => "ctx.pick_item();",
            OpCode.REMOVE => "ctx.remove();",
            OpCode.SIZE => "ctx.size();",
            OpCode.HASKEY => "ctx.has_key();",
            OpCode.KEYS => "ctx.keys();",
            OpCode.VALUES => "ctx.values();",
            OpCode.PACK => "ctx.pack();",
            OpCode.UNPACK => "ctx.unpack();",
            OpCode.REVERSEITEMS => "ctx.reverse_items();",
            OpCode.CLEARITEMS => "ctx.clear_items();",
            OpCode.POPITEM => "ctx.pop_item();",

            // --- String / Byte ---
            OpCode.CAT => "ctx.cat();",
            OpCode.SUBSTR => "ctx.substr();",
            OpCode.LEFT => "ctx.left();",
            OpCode.RIGHT => "ctx.right();",
            OpCode.MEMCPY => "ctx.memcpy();",

            // --- Syscall ---
            OpCode.SYSCALL => $"ctx.syscall(0x{BinaryPrimitives.ReadUInt32LittleEndian(operand):x8});",
            OpCode.CALLT => $"ctx.call_token({BinaryPrimitives.ReadUInt16LittleEndian(operand)});",

            // --- Exception ---
            OpCode.THROW => "ctx.throw_ex();",
            OpCode.ABORT => "ctx.abort();",
            OpCode.ABORTMSG => "ctx.abort_msg();",
            OpCode.ASSERT => "ctx.assert_top();",
            OpCode.ASSERTMSG => "ctx.assert_msg();",

            // --- Control flow (handled by translator, not here) ---
            OpCode.JMP or OpCode.JMP_L
            or OpCode.JMPIF or OpCode.JMPIF_L
            or OpCode.JMPIFNOT or OpCode.JMPIFNOT_L
            or OpCode.JMPEQ or OpCode.JMPEQ_L
            or OpCode.JMPNE or OpCode.JMPNE_L
            or OpCode.JMPGT or OpCode.JMPGT_L
            or OpCode.JMPGE or OpCode.JMPGE_L
            or OpCode.JMPLT or OpCode.JMPLT_L
            or OpCode.JMPLE or OpCode.JMPLE_L
            or OpCode.CALL or OpCode.CALL_L
            or OpCode.RET
            or OpCode.TRY or OpCode.TRY_L
            or OpCode.ENDTRY or OpCode.ENDTRY_L
            or OpCode.ENDFINALLY
            or OpCode.CALLA
                => null, // handled by NeoVmToRustTranslator

            // --- Fallback ---
            _ => $"// unsupported opcode: {op}",
        };
    }

    private static string FormatBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return "";
        var parts = new string[data.Length];
        for (int i = 0; i < data.Length; i++)
            parts[i] = $"0x{data[i]:x2}";
        return string.Join(", ", parts);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Neo.Compiler.CSharp/Neo.Compiler.CSharp.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Neo.Compiler.CSharp/Backend/RiscV/InstructionTranslator.cs
git commit -m "feat: add InstructionTranslator mapping NeoVM opcodes to Rust Context calls"
```

---

## Task 2: Create NeoVmToRustTranslator — Method-Level Translation with Control Flow

**Files:**
- Create: `src/Neo.Compiler.CSharp/Backend/RiscV/NeoVmToRustTranslator.cs`

This translates compiled `MethodConvert` objects into Rust source. For methods with jumps, it generates an offset-based state machine that handles all control flow (if/else, loops, switch, try/catch).

- [ ] **Step 1: Create NeoVmToRustTranslator**

```csharp
// src/Neo.Compiler.CSharp/Backend/RiscV/NeoVmToRustTranslator.cs
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Neo.VM;

namespace Neo.Compiler.Backend.RiscV;

/// <summary>
/// Translates compiled NeoVM methods into Rust source code using neo-riscv-rt::Context.
/// Methods with jumps use an offset-based state machine for control flow.
/// </summary>
internal class NeoVmToRustTranslator
{
    public string Translate(string contractName, IReadOnlyList<(string Name, IReadOnlyList<Instruction> Instructions)> methods)
    {
        var builder = new RustCodeBuilder();

        foreach (var (name, instructions) in methods)
        {
            TranslateMethod(builder, name, instructions);
        }

        return builder.Build(contractName);
    }

    private void TranslateMethod(RustCodeBuilder builder, string name, IReadOnlyList<Instruction> instructions)
    {
        if (instructions.Count == 0) return;

        builder.BeginMethod(name);

        bool hasJumps = instructions.Any(i =>
            i.Target != null || IsJumpOpCode(i.OpCode) || i.OpCode == OpCode.RET);

        if (!hasJumps || instructions.Count <= 2)
        {
            // Simple method: no jumps, emit linearly
            TranslateLinear(builder, instructions);
        }
        else
        {
            // Complex method: use offset-based state machine
            TranslateWithStateMachine(builder, instructions);
        }

        builder.EndMethod();
    }

    private void TranslateLinear(RustCodeBuilder builder, IReadOnlyList<Instruction> instructions)
    {
        foreach (var instr in instructions)
        {
            if (instr.OpCode == OpCode.RET) { builder.Line("// ret"); continue; }
            if (instr.OpCode == OpCode.NOP) continue;
            string? line = InstructionTranslator.Translate(instr);
            if (line != null) builder.Line(line);
        }
    }

    private void TranslateWithStateMachine(RustCodeBuilder builder, IReadOnlyList<Instruction> instructions)
    {
        // Compute offsets for all instructions
        var offsetMap = new Dictionary<int, Instruction>();
        foreach (var instr in instructions)
            offsetMap[instr.Offset] = instr;

        // Collect jump target offsets for labeling
        var jumpTargets = new HashSet<int>();
        foreach (var instr in instructions)
        {
            if (instr.Target?.Instruction != null)
                jumpTargets.Add(instr.Target.Instruction.Offset);
            if (instr.Target2?.Instruction != null)
                jumpTargets.Add(instr.Target2.Instruction.Offset);
        }

        builder.Line("let mut _pc: i32 = 0;");
        builder.Line("loop {");
        builder.Line("    match _pc {");

        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            int nextOffset = (i + 1 < instructions.Count) ? instructions[i + 1].Offset : -1;

            builder.Line($"        {instr.Offset} => {{");

            // Handle control flow opcodes specially
            string? cfLine = TranslateControlFlow(instr, nextOffset);
            if (cfLine != null)
            {
                builder.Line($"            {cfLine}");
            }
            else
            {
                // Regular instruction
                string? line = InstructionTranslator.Translate(instr);
                if (line != null)
                    builder.Line($"            {line}");
                // Fall through to next instruction
                if (nextOffset >= 0)
                    builder.Line($"            _pc = {nextOffset};");
                else
                    builder.Line("            return;");
            }

            builder.Line("        }");
        }

        builder.Line("        _ => { ctx.fault(\"invalid pc\"); return; }");
        builder.Line("    }");
        builder.Line("}");
    }

    private string? TranslateControlFlow(Instruction instr, int nextOffset)
    {
        int targetOffset = instr.Target?.Instruction?.Offset ?? -1;
        int target2Offset = instr.Target2?.Instruction?.Offset ?? -1;

        return instr.OpCode switch
        {
            OpCode.NOP => (nextOffset >= 0) ? $"_pc = {nextOffset};" : "return;",
            OpCode.RET => "return;",
            OpCode.JMP or OpCode.JMP_L => $"_pc = {targetOffset};",
            OpCode.JMPIF or OpCode.JMPIF_L =>
                $"if ctx.pop_bool() {{ _pc = {targetOffset}; }} else {{ _pc = {nextOffset}; }}",
            OpCode.JMPIFNOT or OpCode.JMPIFNOT_L =>
                $"if !ctx.pop_bool() {{ _pc = {targetOffset}; }} else {{ _pc = {nextOffset}; }}",
            OpCode.JMPEQ or OpCode.JMPEQ_L =>
                $"if ctx.pop_cmp_eq() {{ _pc = {targetOffset}; }} else {{ _pc = {nextOffset}; }}",
            OpCode.JMPNE or OpCode.JMPNE_L =>
                $"if ctx.pop_cmp_ne() {{ _pc = {targetOffset}; }} else {{ _pc = {nextOffset}; }}",
            OpCode.JMPGT or OpCode.JMPGT_L =>
                $"if ctx.pop_cmp_gt() {{ _pc = {targetOffset}; }} else {{ _pc = {nextOffset}; }}",
            OpCode.JMPGE or OpCode.JMPGE_L =>
                $"if ctx.pop_cmp_ge() {{ _pc = {targetOffset}; }} else {{ _pc = {nextOffset}; }}",
            OpCode.JMPLT or OpCode.JMPLT_L =>
                $"if ctx.pop_cmp_lt() {{ _pc = {targetOffset}; }} else {{ _pc = {nextOffset}; }}",
            OpCode.JMPLE or OpCode.JMPLE_L =>
                $"if ctx.pop_cmp_le() {{ _pc = {targetOffset}; }} else {{ _pc = {nextOffset}; }}",
            OpCode.CALL or OpCode.CALL_L =>
                // Intra-method call is handled by the NeoVM optimizer as inlining.
                // If it reaches here, treat as jump (the call stack is managed by the NeoVM-level convention).
                $"_pc = {targetOffset}; // call",
            OpCode.CALLA => "ctx.calla(); return; // dynamic call",
            OpCode.TRY or OpCode.TRY_L =>
                $"ctx.try_enter({targetOffset}, {target2Offset}); _pc = {nextOffset};",
            OpCode.ENDTRY or OpCode.ENDTRY_L =>
                $"ctx.end_try(); _pc = {targetOffset};",
            OpCode.ENDFINALLY =>
                "ctx.end_finally(); return; // resumed by try handler",
            _ => null, // not a control flow instruction
        };
    }

    private static bool IsJumpOpCode(OpCode op)
    {
        return op >= OpCode.JMP && op <= OpCode.JMPLE_L
            || op == OpCode.CALL || op == OpCode.CALL_L
            || op == OpCode.RET
            || op == OpCode.TRY || op == OpCode.TRY_L
            || op == OpCode.ENDTRY || op == OpCode.ENDTRY_L
            || op == OpCode.ENDFINALLY
            || op == OpCode.CALLA;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Neo.Compiler.CSharp/Neo.Compiler.CSharp.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Neo.Compiler.CSharp/Backend/RiscV/NeoVmToRustTranslator.cs
git commit -m "feat: add NeoVmToRustTranslator with offset-based state machine for control flow"
```

---

## Task 3: Wire Translator into CompilationContext

**Files:**
- Modify: `src/Neo.Compiler.CSharp/CompilationEngine/CompilationContext.cs`

After the standard NeoVM compilation, when `Target == RiscV`, run the translator and store the result in `GeneratedRustSource`.

- [ ] **Step 1: Add translation step to Compile()**

In `CompilationContext.cs`, find the `Compile()` method (around line 136). After the optimization block (around line 163, after `instructions.RebuildOperands()`), add:

```csharp
// After existing optimization:
if (Options.Target == CompilationTarget.RiscV)
{
    var translator = new Backend.RiscV.NeoVmToRustTranslator();
    var methods = new List<(string Name, IReadOnlyList<Instruction> Instructions)>();
    foreach (var exported in _methodsExported)
    {
        if (_methodsConverted.TryGetValue(exported.Symbol, out var convert))
        {
            methods.Add((exported.Name, convert.Instructions));
        }
    }
    GeneratedRustSource = translator.Translate(ContractName, methods);
}
```

Note: You'll need to check that `_methodsConverted` has a `TryGetValue` that takes `IMethodSymbol`. If not, iterate `_methodsConverted` and match by symbol. Read the `MethodConvertCollection` class to understand its API.

- [ ] **Step 2: Build and run existing tests**

Run: `dotnet build src/Neo.Compiler.CSharp/Neo.Compiler.CSharp.csproj`
Then: `NEO_SKIP_TEST_COVERAGE=1 dotnet test tests/Neo.Compiler.CSharp.UnitTests/ --filter "FullyQualifiedName~UnitTest_ICodeEmitter" -v normal`
Expected: Build and existing tests pass (we only added code in a new branch)

- [ ] **Step 3: Commit**

```bash
git add src/Neo.Compiler.CSharp/CompilationEngine/CompilationContext.cs
git commit -m "feat: wire NeoVmToRustTranslator into compilation pipeline for --target riscv"
```

---

## Task 4: Complete neo-riscv-rt Context Methods

**Files:**
- Modify: `crates/neo-riscv-rt/src/lib.rs`
- Create: `crates/neo-riscv-rt/src/comparison.rs`
- Create: `crates/neo-riscv-rt/src/collections.rs`
- Create: `crates/neo-riscv-rt/src/strings.rs`
- Create: `crates/neo-riscv-rt/src/conversion.rs`

The generated Rust code calls many Context methods that don't exist yet. Add all methods that `InstructionTranslator` generates calls to. For Phase 2, methods that are complex (BigInteger arithmetic, collection operations) can have basic implementations with TODO comments for edge cases. The goal is that the generated code COMPILES.

- [ ] **Step 1: Read current lib.rs to see existing methods**

Check what's already implemented in `crates/neo-riscv-rt/src/lib.rs`.

- [ ] **Step 2: Add comparison module**

Create `crates/neo-riscv-rt/src/comparison.rs` with these methods on Context:

```rust
// crates/neo-riscv-rt/src/comparison.rs
use crate::Context;
use crate::stack_value::StackValue;

impl Context {
    pub fn not_equal(&mut self) {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => self.push_bool(a != b),
            (StackValue::Boolean(a), StackValue::Boolean(b)) => self.push_bool(a != b),
            (StackValue::Null, StackValue::Null) => self.push_bool(false),
            _ => self.push_bool(true),
        }
    }

    pub fn less_than(&mut self) {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => self.push_bool(a < b),
            _ => self.fault("LT: unsupported types"),
        }
    }

    pub fn less_or_equal(&mut self) {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => self.push_bool(a <= b),
            _ => self.fault("LE: unsupported types"),
        }
    }

    pub fn greater_than(&mut self) {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => self.push_bool(a > b),
            _ => self.fault("GT: unsupported types"),
        }
    }

    pub fn greater_or_equal(&mut self) {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => self.push_bool(a >= b),
            _ => self.fault("GE: unsupported types"),
        }
    }

    pub fn bool_and(&mut self) {
        let b = self.pop_bool();
        let a = self.pop_bool();
        self.push_bool(a && b);
    }

    pub fn bool_or(&mut self) {
        let b = self.pop_bool();
        let a = self.pop_bool();
        self.push_bool(a || b);
    }

    pub fn not(&mut self) {
        let val = self.pop_bool();
        self.push_bool(!val);
    }

    pub fn nz(&mut self) {
        let val = self.pop();
        match val {
            StackValue::Integer(v) => self.push_bool(v != 0),
            StackValue::Boolean(v) => self.push_bool(v),
            _ => self.push_bool(true),
        }
    }

    pub fn num_equal(&mut self) { self.equal(); }
    pub fn num_not_equal(&mut self) { self.not_equal(); }

    pub fn is_null(&mut self) {
        let val = self.pop();
        self.push_bool(matches!(val, StackValue::Null));
    }

    // Helpers for control flow
    pub fn pop_bool(&mut self) -> bool {
        match self.pop() {
            StackValue::Boolean(v) => v,
            StackValue::Integer(v) => v != 0,
            StackValue::Null => false,
            _ => false,
        }
    }

    pub fn pop_cmp_eq(&mut self) -> bool {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => a == b,
            _ => false,
        }
    }

    pub fn pop_cmp_ne(&mut self) -> bool { !self.pop_cmp_eq() }

    pub fn pop_cmp_gt(&mut self) -> bool {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => a > b,
            _ => false,
        }
    }

    pub fn pop_cmp_ge(&mut self) -> bool {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => a >= b,
            _ => false,
        }
    }

    pub fn pop_cmp_lt(&mut self) -> bool {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => a < b,
            _ => false,
        }
    }

    pub fn pop_cmp_le(&mut self) -> bool {
        let b = self.pop();
        let a = self.pop();
        match (&a, &b) {
            (StackValue::Integer(a), StackValue::Integer(b)) => a <= b,
            _ => false,
        }
    }
}
```

- [ ] **Step 3: Add remaining arithmetic methods, collection stubs, string stubs, conversion stubs, and remaining stack operations to lib.rs or new module files**

Add all methods called by InstructionTranslator. For complex operations, provide stub implementations that handle the integer fast path and fault on unsupported types. Key methods needed:

Arithmetic: `div`, `modulo`, `negate`, `abs_val`, `sign`, `max`, `min`, `pow`, `sqrt`, `modmul`, `modpow`, `shl`, `shr`, `bitwise_and`, `bitwise_or`, `bitwise_xor`, `bitwise_not`

Stack: `nip`, `xdrop`, `over`, `pick`, `tuck`, `rot`, `roll`, `reverse3`, `reverse4`, `reverse_n`, `depth`, `clear`

Collections: `new_array_0`, `new_array`, `new_array_t`, `new_struct_0`, `new_struct`, `new_map`, `new_buffer`, `append`, `set_item`, `pick_item`, `remove`, `size`, `has_key`, `keys`, `values`, `pack`, `unpack`, `reverse_items`, `clear_items`, `pop_item`

Strings: `cat`, `substr`, `left`, `right`, `memcpy`

Types: `is_type`, `convert`, `is_null`

BigInteger: `push_bigint`

Exception: `throw_ex`, `abort`, `abort_msg`, `assert_top`, `assert_msg`, `try_enter`, `end_try`, `end_finally`

Syscall: `syscall`, `call_token`, `calla`

- [ ] **Step 4: Register modules in lib.rs**

```rust
pub mod comparison;
pub mod collections;
pub mod strings;
pub mod conversion;
```

- [ ] **Step 5: Build Rust**

Run: `cargo build -p neo-riscv-rt`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add crates/neo-riscv-rt/
git commit -m "feat: complete neo-riscv-rt Context methods for all NeoVM operations"
```

---

## Task 5: Add Translator Unit Tests

**Files:**
- Create: `tests/Neo.Compiler.CSharp.UnitTests/UnitTest_NeoVmToRustTranslator.cs`

- [ ] **Step 1: Write translator tests**

Test that the translator produces correct Rust source for known instruction sequences.

```csharp
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
        // Create instructions: push 2, push 3, add, ret
        var instructions = CreateInstructions(
            (OpCode.PUSH2, null),
            (OpCode.PUSH3, null),
            (OpCode.ADD, null),
            (OpCode.RET, null)
        );

        var translator = new NeoVmToRustTranslator();
        var methods = new List<(string, IReadOnlyList<Instruction>)>
        {
            ("testMethod", instructions)
        };
        var rust = translator.Translate("TestContract", methods);

        Assert.IsTrue(rust.Contains("ctx.push_int(2);"));
        Assert.IsTrue(rust.Contains("ctx.push_int(3);"));
        Assert.IsTrue(rust.Contains("ctx.add();"));
        Assert.IsTrue(rust.Contains("\"testMethod\" => method_testmethod(ctx)"));
    }

    [TestMethod]
    public void TestSyscall()
    {
        var hash = System.BitConverter.GetBytes((uint)0x31e85d92);
        var instructions = CreateInstructions(
            (OpCode.SYSCALL, hash),
            (OpCode.RET, null)
        );

        var translator = new NeoVmToRustTranslator();
        var methods = new List<(string, IReadOnlyList<Instruction>)>
        {
            ("getStorage", instructions)
        };
        var rust = translator.Translate("Test", methods);

        Assert.IsTrue(rust.Contains("ctx.syscall(0x31e85d92);"));
    }

    [TestMethod]
    public void TestSlotAccess()
    {
        var instructions = CreateInstructions(
            (OpCode.INITSLOT, new byte[] { 1, 2 }),
            (OpCode.LDARG0, null),
            (OpCode.STLOC0, null),
            (OpCode.LDLOC0, null),
            (OpCode.RET, null)
        );

        var translator = new NeoVmToRustTranslator();
        var methods = new List<(string, IReadOnlyList<Instruction>)>
        {
            ("test", instructions)
        };
        var rust = translator.Translate("Test", methods);

        Assert.IsTrue(rust.Contains("ctx.init_slot(1, 2);"));
        Assert.IsTrue(rust.Contains("ctx.load_arg(0);"));
        Assert.IsTrue(rust.Contains("ctx.store_local(0);"));
        Assert.IsTrue(rust.Contains("ctx.load_local(0);"));
    }

    // Helper to create instruction lists with computed offsets
    private static IReadOnlyList<Instruction> CreateInstructions(params (OpCode op, byte[]? operand)[] ops)
    {
        var list = new List<Instruction>();
        int offset = 0;
        foreach (var (op, operand) in ops)
        {
            var instr = new Instruction { OpCode = op, Operand = operand, Offset = offset };
            list.Add(instr);
            offset += 1 + (operand?.Length ?? 0); // simplified offset calculation
        }
        return list;
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/Neo.Compiler.CSharp.UnitTests/ --filter "FullyQualifiedName~UnitTest_NeoVmToRustTranslator" -v normal`
Expected: All tests pass

- [ ] **Step 3: Commit**

```bash
git add tests/Neo.Compiler.CSharp.UnitTests/UnitTest_NeoVmToRustTranslator.cs
git commit -m "test: add NeoVmToRustTranslator unit tests"
```

---

## Task 6: End-to-End Integration Test

**Files:**
- Create: `tests/Neo.Compiler.CSharp.UnitTests/UnitTest_RiscVEndToEnd.cs`

Compile an actual C# test contract with `--target riscv` and verify the generated Rust source.

- [ ] **Step 1: Write end-to-end test**

This test uses the compiler infrastructure to compile a real test contract and verify Rust output is generated.

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Compiler;

namespace Neo.Compiler.CSharp.UnitTests;

[TestClass]
public class UnitTest_RiscVEndToEnd
{
    [TestMethod]
    public void TestCompileSimpleContract_RiscV()
    {
        // Use the compiler to compile a test contract with RiscV target
        var options = new CompilationOptions
        {
            Target = CompilationTarget.RiscV,
            Nullable = Microsoft.CodeAnalysis.NullableContextOptions.Annotations,
        };

        var engine = new CompilationEngine(options);

        // Compile the test contracts project
        var contexts = engine.CompileProject(
            System.IO.Path.GetFullPath("../Neo.Compiler.CSharp.TestContracts/Neo.Compiler.CSharp.TestContracts.csproj"));

        // Find a simple contract (Contract_Assignment is straightforward)
        var ctx = contexts.Find(c => c.ContractName == "Contract_Assignment");
        Assert.IsNotNull(ctx, "Contract_Assignment should compile");
        Assert.IsTrue(ctx.Success, "Compilation should succeed");
        Assert.IsNotNull(ctx.GeneratedRustSource, "Rust source should be generated");

        // Verify the Rust source contains expected patterns
        var rust = ctx.GeneratedRustSource;
        Assert.IsTrue(rust.Contains("pub fn method_"), "Should contain method declarations");
        Assert.IsTrue(rust.Contains("pub fn dispatch("), "Should contain dispatch function");
        Assert.IsTrue(rust.Contains("ctx: &mut Context"), "Should use Context parameter");
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/Neo.Compiler.CSharp.UnitTests/ --filter "FullyQualifiedName~UnitTest_RiscVEndToEnd" -v normal`
Expected: Test passes — a real contract compiled to Rust source

- [ ] **Step 3: Commit**

```bash
git add tests/Neo.Compiler.CSharp.UnitTests/UnitTest_RiscVEndToEnd.cs
git commit -m "test: add end-to-end RISC-V compilation test for real contract"
```

---

## Task 7: Run Full Test Suite

- [ ] **Step 1: Build full solution**

Run: `dotnet build`
Expected: 0 errors

- [ ] **Step 2: Run all C# tests**

Run: `NEO_SKIP_TEST_COVERAGE=1 dotnet test tests/Neo.Compiler.CSharp.UnitTests/ --no-restore -v minimal`
Expected: All tests pass, no regressions

- [ ] **Step 3: Run all Rust tests**

Run: `cd /home/neo/git/neo-riscv-vm && cargo test -p neo-riscv-rt`
Expected: All tests pass

---

## Summary

After completing Phase 2:
- `nccs Contract.csproj --target riscv` generates a `.rs` file with valid Rust source
- The generated code uses `neo-riscv-rt::Context` for all NeoVM operations
- Control flow (if/else, loops, switch, try/catch) works via offset-based state machine
- All opcodes are mapped (170+ NeoVM opcodes → Rust Context calls)
- Real contracts from the test suite compile to Rust source

### Next Phase (Phase 3)
- `neo-riscv-compile` tool: compile generated Rust → PolkaVM binary
- Execute compiled contracts on `neo-riscv-host`
- Parity testing: NeoVM vs RISC-V execution results
- Performance benchmarking
