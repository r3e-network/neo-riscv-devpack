# Neo Devpack-Dotnet Dual-Target Compiler Design

## Overview

Refactor the Neo C# smart contract compiler (`nccs`) to support two compilation targets:
- **NeoVM** (existing) — produces `.nef` bytecode for the NeoVM stack machine
- **RISC-V** (new) — produces `.polkavm` binaries for the PolkaVM RISC-V runtime

The same C# source code compiles to either target. Full feature parity: every language feature, syscall, native contract, and framework API supported by the NeoVM target is also supported by the RISC-V target.

## Architecture

### Compiler Pipeline

```
C# Source
    |
[Shared Front-End]
    Roslyn Parsing -> SyntaxTree
    Semantic Analysis -> SemanticModel
    Contract Discovery -> SmartContract classes
    |
[Shared Middle-End]
    MethodConvert walks SemanticModel
    Expression/Statement converters call ICodeEmitter methods
    |
    +---------------------------+---------------------------+
    |  NeoVM Backend            |  RISC-V Backend           |
    |  NeoVmEmitter             |  RiscVEmitter             |
    |  -> Instruction objects   |  -> RISC-V instructions   |
    |  -> BasicOptimizer        |  -> RiscV optimizer       |
    |  -> NEF binary            |  -> PolkaVM binary        |
    |  + manifest.json          |  + manifest.json          |
    |  + debug.json             |  + debug.json             |
    +---------------------------+---------------------------+
```

### Key Principle

Everything above `ICodeEmitter` is shared. The split happens at instruction emission. The manifest is identical for both targets (same ABI, same method signatures, same events). Only the binary format and compiler field change.

## ICodeEmitter Interface

The abstraction layer that both backends implement. Extracted from existing `MethodConvert` helper methods.

```csharp
public interface ICodeEmitter
{
    // Lifecycle
    void BeginMethod(MethodConvert method);
    void EndMethod();

    // Stack Operations
    void Push(long value);
    void Push(bool value);
    void Push(byte[] data);
    void Push(string value);
    void PushNull();
    void Drop();
    void Dup();
    void Over();
    void Pick(int depth);
    void Roll(int depth);
    void Swap();
    void Reverse(int count);
    void Clear();

    // Arithmetic
    void Add(); void Sub(); void Mul(); void Div(); void Mod();
    void Negate(); void Abs(); void Sign();
    void Min(); void Max(); void Pow(); void Sqrt();
    void ModMul(); void ModPow();
    void ShiftLeft(); void ShiftRight();
    void BitwiseAnd(); void BitwiseOr(); void BitwiseXor(); void BitwiseNot();

    // Comparison & Logic
    void Equal(); void NotEqual();
    void LessThan(); void LessOrEqual();
    void GreaterThan(); void GreaterOrEqual();
    void BoolAnd(); void BoolOr(); void BoolNot();
    void NullCheck();
    void IsType(byte type);
    void Convert(byte type);

    // Control Flow
    ILabel DefineLabel();
    void MarkLabel(ILabel label);
    void Jump(ILabel target);
    void JumpIf(ILabel target);
    void JumpIfNot(ILabel target);
    void Call(MethodConvert target);
    void Return();
    void Throw();
    void Abort();
    void AbortWithMessage();
    void Assert();
    void Nop();

    // Variables (Slots)
    void InitSlot(int localCount, int argCount);
    void LoadLocal(int index);   void StoreLocal(int index);
    void LoadArg(int index);     void StoreArg(int index);
    void LoadStatic(int index);  void StoreStatic(int index);

    // Syscalls & Interop
    void Syscall(uint hash);
    void CallToken(ushort token);

    // Collections
    void NewArray(); void NewArrayT(byte type);
    void NewStruct(int fieldCount);
    void NewMap(); void NewBuffer();
    void Append(); void SetItem(); void GetItem(); void Remove();
    void Size(); void HasKey(); void Keys(); void Values();
    void Pack(int count); void Unpack();
    void DeepCopy();

    // String / Byte Operations
    void Concat(); void Substr(); void Left(); void Right();
    void MemCpy();

    // Exception Handling
    ITryBlock BeginTry();
    void BeginCatch(ITryBlock block);
    void BeginFinally(ITryBlock block);
    void EndTry(ITryBlock block);
    void EndFinally();
}

public interface ILabel { }
public interface ITryBlock { }
```

### Refactoring Strategy

The existing `MethodConvert` helper methods already align with this interface:

| Current Method | ICodeEmitter Method |
|---|---|
| `AddInstruction(OpCode.PUSH1)` | `emitter.Push(1)` |
| `AddInstruction(OpCode.ADD)` | `emitter.Add()` |
| `Jump(OpCode.JMP, target)` | `emitter.Jump(label)` |
| `AccessSlot(OpCode.LDLOC, i)` | `emitter.LoadLocal(i)` |
| `CallInteropService(hash)` | `emitter.Syscall(hash)` |
| `AddInstruction(OpCode.NEWARRAY)` | `emitter.NewArray()` |

The 25+ expression converter files and 12+ statement converter files change minimally: replace direct `Instruction` creation with `ICodeEmitter` method calls.

## NeoVM Backend (NeoVmEmitter)

Wraps existing behavior. `NeoVmEmitter : ICodeEmitter` creates `Instruction` objects exactly as the current code does. This is a mechanical refactoring with zero behavior change.

```csharp
class NeoVmEmitter : ICodeEmitter
{
    private readonly List<Instruction> _instructions = new();

    public void Push(long value)
    {
        // Existing logic from MethodConvert: select PUSH0..PUSH16
        // or PUSHINT8/16/32/64/128/256 based on value range
    }

    public void Add() => _instructions.Add(new Instruction(OpCode.ADD));
    public void Syscall(uint hash) => _instructions.Add(new Instruction(OpCode.SYSCALL, BitConverter.GetBytes(hash)));
    // ... etc
}
```

Output: NEF binary + manifest.json + debug.json (unchanged from current compiler).

## RISC-V Backend

### Runtime Support Library (neo-riscv-rt)

A Rust crate compiled to PolkaVM, linked into every generated contract. Handles complex operations so generated code stays small.

```
neo-riscv-rt/ (new crate in neo-riscv-vm repo)
  src/
    lib.rs            -- public API for generated code
    stack_value.rs    -- Tagged StackValue: type checks, conversions
    biginteger.rs     -- Arbitrary-precision arithmetic (add, sub, mul, div, mod, pow, etc.)
    collections.rs    -- Array, Struct, Map, Buffer operations
    strings.rs        -- Concat, substr, compare, UTF-8 encoding
    syscall_marshal.rs -- Encode/decode StackValue for host_call()
    memory.rs         -- Bump allocator (16 MB arena)
    exceptions.rs     -- Try/catch/finally via setjmp/longjmp pattern
    dispatch.rs       -- Method name dispatch helpers
```

The runtime exports functions prefixed with `rt_`:
- `rt_add`, `rt_sub`, `rt_mul`, etc. — type-dispatched arithmetic
- `rt_equal`, `rt_compare`, etc. — comparison with type coercion
- `rt_syscall` — marshal args, call `host_call()`, unmarshal result
- `rt_new_array`, `rt_append`, `rt_get_item`, etc. — collection ops
- `rt_concat`, `rt_substr`, etc. — string ops
- `rt_try_enter`, `rt_try_leave`, `rt_throw` — exception handling
- `rt_deep_copy` — recursive value cloning
- `rt_encode_result`, `rt_decode_stack` — entry/exit serialization

### Stack Value Representation

Every value uses a 16-byte tagged slot:

```
StackSlot (16 bytes):
  [0..3]   tag: u32
             0 = Integer      value: i64 inline
             1 = Boolean      value: 0 or 1
             2 = ByteString   value: ptr to {len:u32, data:u8[]}
             3 = BigInteger   value: ptr to {len:u32, data:u8[]} (little-endian)
             4 = Array        value: ptr to {id:u32, len:u32, items:StackSlot[]}
             5 = Struct       value: ptr to {id:u32, len:u32, items:StackSlot[]}
             6 = Map          value: ptr to {id:u32, len:u32, pairs:(StackSlot,StackSlot)[]}
             7 = Null         value: 0
             8 = Interop      value: handle u64
             9 = Iterator     value: handle u64
            10 = Buffer       value: ptr to {id:u32, len:u32, data:u8[]}
            11 = Pointer      value: i64
  [4..7]   reserved (alignment padding)
  [8..15]  value: i64 or u64 (interpretation depends on tag)
```

### Guest Memory Layout

```
0x00000000 +-------------------------+
           | Code (.text)            |  Generated RISC-V + runtime lib
           +-------------------------+
           | Read-only data (.rodata)|  String literals, method name table
           +-------------------------+
           | Static fields (.data)   |  StackSlot[] for contract statics
           +-------------------------+
           | Heap (bump allocator)   |  BigIntegers, collections, byte arrays
           | v grows down            |
           |                         |
           | ^ grows up              |
           | Eval stack              |  StackSlot[] virtual evaluation stack
           +-------------------------+
           | Call frames (hw stack)  |  Return addr, saved regs, frame ptrs
0x01000000 +-------------------------+  (16 MB guest arena)
```

### Register Conventions

```
s0  -- evaluation stack pointer (next free StackSlot)
s1  -- locals/args base pointer (per-method frame)
s2  -- heap pointer (bump allocator head)
a0-a5 -- scratch / function call arguments
t0-t2 -- temporaries
ra  -- return address
sp  -- hardware stack pointer (call frames)
```

### RiscVEmitter Code Generation

```csharp
class RiscVEmitter : ICodeEmitter
{
    private readonly List<RiscVInstruction> _code = new();
    private readonly RuntimeLibrary _rt;  // pre-compiled runtime references

    public void Push(long value)
    {
        // Fast path: small integer, inline
        EmitLi(Reg.A0, TAG_INTEGER);
        EmitSw(Reg.A0, 0, Reg.S0);       // tag
        EmitLi(Reg.A0, value);
        EmitSd(Reg.A0, 8, Reg.S0);       // value
        EmitAddi(Reg.S0, Reg.S0, 16);    // advance stack ptr
    }

    public void Add()
    {
        // Check both tags == Integer for fast path
        EmitLw(Reg.T0, -32, Reg.S0);     // tag of second
        EmitLw(Reg.T1, -16, Reg.S0);     // tag of first
        EmitOr(Reg.T2, Reg.T0, Reg.T1);
        EmitBnez(Reg.T2, _slowPathLabel); // if either non-zero (non-integer), slow path

        // Integer fast path: inline add
        EmitLd(Reg.A0, -24, Reg.S0);     // second value
        EmitLd(Reg.A1, -8, Reg.S0);      // first value
        EmitAdd(Reg.A0, Reg.A0, Reg.A1);
        EmitAddi(Reg.S0, Reg.S0, -16);   // pop one slot
        EmitSd(Reg.A0, -8, Reg.S0);      // store result
        EmitJ(_continueLabel);

        // Slow path: call runtime
        MarkLabel(_slowPathLabel);
        EmitAddi(Reg.A0, Reg.S0, -32);   // ptr to operand pair
        EmitCall(_rt.Add);                // rt_add handles BigInteger, type errors
        EmitAddi(Reg.S0, Reg.S0, -16);
        MarkLabel(_continueLabel);
    }

    public void Syscall(uint hash)
    {
        // Determine arg count from known syscall table
        int argCount = AbiHelper.SyscallArgCount(hash);
        EmitMv(Reg.A0, Reg.S0);          // stack ptr
        EmitLi(Reg.A1, argCount);         // arg count
        EmitLi(Reg.A2, hash);             // api hash
        EmitCall(_rt.Syscall);            // rt_syscall marshals and calls host
        EmitMv(Reg.S0, Reg.A0);          // updated stack ptr
    }

    public void LoadLocal(int index)
    {
        int offset = index * 16;
        EmitLd(Reg.A0, offset, Reg.S1);      // tag + padding
        EmitSd(Reg.A0, 0, Reg.S0);
        EmitLd(Reg.A0, offset + 8, Reg.S1);  // value
        EmitSd(Reg.A0, 8, Reg.S0);
        EmitAddi(Reg.S0, Reg.S0, 16);
    }

    // ... all other ICodeEmitter methods follow same pattern
}
```

### Exception Handling

Uses setjmp/longjmp from the runtime library:

```
BeginTry():
    call rt_try_enter       // saves s0 (stack ptr), s1 (locals), catch/finally labels
                            // returns 0 on setup, 1 on exception (longjmp return)
    beqz a0, try_body       // normal path: enter try body
    j catch_label           // exception path: jump to catch

EndTry():
    call rt_try_leave       // pops try frame

Throw():
    call rt_throw           // longjmp to nearest catch handler

BeginFinally():
    // rt_try_enter stores finally label; rt_try_leave calls it before popping
```

Maximum 16 nested try frames (matching NeoVM limit, enforced by runtime).

### PolkaVM Binary Generation

Two-phase build process:

**Phase 1 (Initial): Intermediate compilation via Rust**
1. `RiscVEmitter` generates a Rust source file using `neo-riscv-rt` APIs
2. Compiled with `cargo build --target riscv32emu-unknown-none-polkavm`
3. `polkavm-linker` converts ELF to `.polkavm`
4. A helper tool (`neo-riscv-compile`) wraps steps 2-3, invoked by the C# compiler

**Phase 2 (Future): Direct binary generation**
1. Implement PolkaVM binary format writer in C#
2. `RiscVEmitter` generates RISC-V instructions directly
3. No external tool dependency

Phase 1 ships first for correctness; Phase 2 is a performance/packaging optimization.

### Generated Rust Code (Phase 1)

For a contract like:
```csharp
public class TokenContract : SmartContract
{
    public static BigInteger BalanceOf(UInt160 account)
    {
        return (BigInteger)Storage.Get(Storage.CurrentContext, (byte[])account);
    }
}
```

The emitter generates:
```rust
#![no_std]
#![no_main]
extern crate neo_riscv_rt;
use neo_riscv_rt::*;

#[polkavm_export]
pub extern "C" fn invoke(method_ptr: u32, method_len: u32, stack_ptr: u32, stack_len: u32) {
    let mut ctx = Context::from_raw(stack_ptr, stack_len);
    let method = ctx.read_str(method_ptr, method_len);
    match method {
        "balanceOf" => method_balance_of(&mut ctx),
        _ => ctx.fault("Unknown method"),
    }
    ctx.write_result();
}

fn method_balance_of(ctx: &mut Context) {
    // InitSlot(0 locals, 1 arg)
    ctx.init_slot(0, 1);
    // Storage.CurrentContext -> Syscall
    ctx.syscall(0x4a100170);  // System.Storage.GetContext
    // Load arg 0
    ctx.load_arg(0);
    // Convert to ByteString
    ctx.convert(TAG_BYTESTRING);
    // Storage.Get -> Syscall
    ctx.syscall(0x31e85d92);  // System.Storage.Get
    // Convert to Integer (BigInteger)
    ctx.convert(TAG_INTEGER);
    // Return
    ctx.ret();
}
```

## Contract Entry Points

### PolkaVM Exports

```
invoke(method_ptr: u32, method_len: u32, stack_ptr: u32, stack_len: u32)
    -- Main entry point. Dispatches to compiled methods.
get_result_ptr() -> u32
get_result_len() -> u32
    -- Access serialized ExecutionResult after invoke() returns.
```

Matches the existing `neo_riscv_execute_native_contract()` FFI convention. C#-compiled contracts are indistinguishable from Rust-compiled ones on the host side.

### Method Dispatch

Generated per-contract jump table:
```rust
match method {
    "transfer"   => method_transfer(ctx),
    "balanceOf"  => method_balance_of(ctx),
    "_deploy"    => method_deploy(ctx),
    "_initialize" => method_initialize(ctx),
    _ => ctx.fault("Unknown method"),
}
```

### Static Initialization

```rust
static INITIALIZED: AtomicBool = AtomicBool::new(false);

fn ensure_initialized(ctx: &mut Context) {
    if !INITIALIZED.swap(true, Ordering::Relaxed) {
        method_initialize(ctx);
    }
}
```

## Syscall Mapping

NeoVM and RISC-V use the same API hash scheme: `SHA256(name)[0..4]` as little-endian u32.

| Syscall | Hash | NeoVM Emission | RISC-V Emission |
|---|---|---|---|
| System.Contract.Call | 0x525b7d62 | `SYSCALL 0x525b7d62` | `ctx.syscall(0x525b7d62)` -> `host_call(...)` |
| System.Storage.Get | 0x31e85d92 | `SYSCALL 0x31e85d92` | `ctx.syscall(0x31e85d92)` -> `host_call(...)` |
| System.Runtime.Notify | 0x616f0195 | `SYSCALL 0x616f0195` | `ctx.syscall(0x616f0195)` -> `host_call(...)` |
| System.Runtime.CheckWitness | 0x8cec27f8 | `SYSCALL 0x8cec27f8` | `ctx.syscall(0x8cec27f8)` -> `host_call(...)` |

The `rt_syscall` runtime function:
1. Reads `syscall_arg_count(hash)` to know how many items to pop
2. Encodes them via `fast_codec::encode_stack()`
3. Calls `host_call(hash, ip, stack_ptr, stack_len, result_ptr, result_cap)`
4. Decodes result via `callback_codec::decode_stack_result()`
5. Pushes result items back onto the evaluation stack

CALLT (cross-contract token calls) uses the same mechanism with `hash = 0x4354_0000 | token_id`.

## Framework Compatibility

`Neo.SmartContract.Framework` remains unchanged. It defines C# wrapper classes and methods that the compiler recognizes and handles specially.

The compiler's SystemCall handler becomes target-aware:

```csharp
// In SystemCall.cs handler registration
void HandleStorageGet(MethodConvert mc, ...)
{
    // Same for both targets -- emitter abstracts the difference
    mc.Emitter.Syscall(InteropHash("System.Storage.Get"));
}
```

The `Emitter` property on `MethodConvert` dispatches to the appropriate backend. Framework methods, native contract wrappers (NEO, GAS, Ledger, etc.), and attribute handling all work identically for both targets.

## CLI Interface

### New Flag

```
nccs MyContract.csproj --target riscv
nccs MyContract.csproj --target neovm    (default, backward compatible)
```

### Implementation

```csharp
// In Program.cs command-line parsing
enum CompilationTarget { NeoVM, RiscV }

// In CompilationEngine
public CompilationEngine(Options options)
{
    _target = options.Target;  // new field
}

// In CompilationContext
ICodeEmitter CreateEmitter() => _engine.Target switch
{
    CompilationTarget.NeoVM => new NeoVmEmitter(),
    CompilationTarget.RiscV => new RiscVEmitter(_runtimeLib),
    _ => throw new NotSupportedException()
};
```

### Output Files

```
--target neovm:                    --target riscv:
  Contract.nef                       Contract.polkavm
  Contract.manifest.json             Contract.manifest.json
  Contract.debug.json                Contract.debug.json
  Contract.nef.txt (optional)        Contract.riscv.txt (optional disasm)
```

## Testing Strategy

### Unit Tests
- Test each `ICodeEmitter` method on both backends
- Verify `NeoVmEmitter` produces identical bytecode to current compiler (regression)
- Verify `RiscVEmitter` produces valid RISC-V instruction sequences

### Integration Tests
- Compile test contracts with `--target riscv`
- Execute on `neo-riscv-host` via `neo_riscv_execute_native_contract()`
- Verify correct results

### Parity Tests
- Compile the same contract for both targets
- Execute with identical inputs
- Assert identical outputs (stack results, events, storage mutations, gas consumed)
- Reuse existing `Neo.Compiler.CSharp.TestContracts` as the test corpus

### Test Contracts Priority
1. Arithmetic and control flow (if/else, loops, switch)
2. Storage operations (get, put, delete, find)
3. NEP-17 token contract (real-world baseline)
4. Native contract calls (NEO, GAS, Ledger)
5. Events and notifications
6. Exception handling (try/catch/finally)
7. Complex types (arrays, maps, structs)
8. Cross-contract calls (CALLT)
9. All remaining test contracts for full parity

## Implementation Phases

### Phase 1: Compiler Refactoring (no new backend yet)
- Introduce `ICodeEmitter` interface
- Implement `NeoVmEmitter` by extracting existing logic from `MethodConvert`
- Refactor `MethodConvert` and all expression/statement converters to use `ICodeEmitter`
- Verify all existing tests still pass (zero behavior change)

### Phase 2: Runtime Library (neo-riscv-rt)
- Create the Rust runtime crate
- Implement StackValue operations, BigInteger, collections, strings
- Implement syscall marshaling via `host_call()`
- Implement exception handling (setjmp/longjmp)
- Unit test each runtime function independently

### Phase 3: RISC-V Backend (RiscVEmitter)
- Implement `RiscVEmitter : ICodeEmitter`
- Generate Rust source code using `neo-riscv-rt` Context API
- Build helper tool (`neo-riscv-compile`) to compile generated Rust -> .polkavm
- Add `--target riscv` CLI flag

### Phase 4: Integration & Parity Testing
- Compile test contracts for RISC-V target
- Execute on neo-riscv-host
- Run parity tests against NeoVM output
- Fix discrepancies until all tests pass

### Phase 5: Optimization
- Inline fast paths for common operations (integer arithmetic)
- Reduce runtime library size (dead code elimination)
- Optimize generated code patterns
- Profile and benchmark vs NeoVM interpretation

### Phase 6: Direct Binary Generation (optional, future)
- Implement PolkaVM binary format writer in C#
- Generate RISC-V instructions directly, no Rust intermediary
- Ship fully self-contained compiler with no external toolchain dependency
