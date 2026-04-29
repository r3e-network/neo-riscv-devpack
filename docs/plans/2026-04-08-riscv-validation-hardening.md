# RISC-V Validation Hardening Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Restore the currently broken RISC-V validation path in the devpack workspace so dual-backend tests can build contracts against the in-workspace `neo-riscv-vm`, `FromHash(checkExistence)` behaves predictably, and staged cross-repo validation can be rerun with evidence.

**Architecture:** Keep the runtime and adapter architecture unchanged. Fix the validation and compiler/test harness boundaries so existence checks use direct storage state, generated RISC-V crates resolve the correct `neo-riscv-vm` workspace, and the Cargo invocation matches the current toolchain. Then rerun the affected devpack/core/VM validation slices.

**Tech Stack:** C#/.NET 10, MSTest, Rust nightly + Cargo, PolkaVM/polkatool, neo-riscv-vm cross-repo adapter bundle

### Task 1: Fix `TestEngine.FromHash` Existence Semantics

**Files:**
- Modify: `src/Neo.SmartContract.Testing/TestEngine.cs`
- Test: `tests/Neo.SmartContract.Testing.UnitTests/TestEngineTests.cs`

**Step 1: Use the existing failing test as the red case**

Run:

```bash
dotnet test tests/Neo.SmartContract.Testing.UnitTests/Neo.SmartContract.Testing.UnitTests.csproj --filter "FullyQualifiedName~TestHashExists"
```

Expected: FAIL because `engine.FromHash<NEO>(..., true)` raises `TestException` instead of the expected missing-contract behavior.

**Step 2: Write the minimal implementation**

- Change `TestEngine.FromHash<T>(..., checkExistence: true)` to query contract existence directly from storage through `NativeContract.ContractManagement.GetContract(Storage.Snapshot, hash)`.
- Throw `KeyNotFoundException` when the contract is missing.
- Keep the existing mock-construction path unchanged after a successful lookup.

**Step 3: Verify the targeted test**

Run:

```bash
dotnet test tests/Neo.SmartContract.Testing.UnitTests/Neo.SmartContract.Testing.UnitTests.csproj --filter "FullyQualifiedName~TestHashExists"
```

Expected: PASS.

### Task 2: Keep RISC-V Test Crates Bound To The Current Workspace

**Files:**
- Modify: `tests/Neo.Compiler.CSharp.UnitTests/RiscVTestHelper.cs`
- Test: `tests/Neo.Compiler.CSharp.UnitTests/UnitTest_RiscVExecution.cs`

**Step 1: Use the current failing execution tests as the red case**

Run:

```bash
dotnet test tests/Neo.Compiler.CSharp.UnitTests/Neo.Compiler.CSharp.UnitTests.csproj --filter "FullyQualifiedName~UnitTest_RiscVExecution"
```

Expected: FAIL because generated crates build against the wrong `neo-riscv-vm` clone and/or fail during Cargo invocation.

**Step 2: Write the minimal implementation**

- Update `FindRiscvVmRoot()` to prefer:
  - an explicit environment override if present
  - the current workspace sibling `../neo-riscv-vm`
  - only then any legacy fallback paths
- Avoid resolving `/home/neo/git/neo-riscv-vm` ahead of the active workspace.

**Step 3: Verify the targeted execution tests still fail for the remaining root cause only**

Run:

```bash
dotnet test tests/Neo.Compiler.CSharp.UnitTests/Neo.Compiler.CSharp.UnitTests.csproj --filter "FullyQualifiedName~UnitTest_RiscVExecution"
```

Expected: if still failing, the failure should now point at Cargo/toolchain invocation or generated/runtime behavior rather than the wrong repo path.

### Task 3: Remove The Obsolete Cargo JSON Target Flag

**Files:**
- Modify: `src/Neo.Compiler.CSharp/Backend/RiscV/RiscVBuildHelper.cs`
- Modify: `tests/Neo.Compiler.CSharp.UnitTests/RiscVTestHelper.cs`
- Test: `tests/Neo.Compiler.CSharp.UnitTests/UnitTest_RiscVExecution.cs`

**Step 1: Confirm the current red case**

Run:

```bash
dotnet test tests/Neo.Compiler.CSharp.UnitTests/Neo.Compiler.CSharp.UnitTests.csproj --filter "FullyQualifiedName~UnitTest_RiscVExecution"
```

Expected: FAIL with Cargo reporting `unknown '-Z' flag specified: json-target-spec`.

**Step 2: Write the minimal implementation**

- Remove `-Zjson-target-spec` from both build helper command paths.
- Keep the target JSON patching (`abi`) and `-Zbuild-std=core,alloc`.
- Preserve stderr capture so failures remain diagnosable.

**Step 3: Verify the targeted execution tests**

Run:

```bash
dotnet test tests/Neo.Compiler.CSharp.UnitTests/Neo.Compiler.CSharp.UnitTests.csproj --filter "FullyQualifiedName~UnitTest_RiscVExecution"
```

Expected: the tests either PASS or reveal the next concrete translator/runtime mismatch to address.

### Task 4: Run The Validation Slice With Proper Adapter Staging

**Files:**
- Modify only if new evidence requires it: `dotnet/Neo.Riscv.Adapter/*`, `tests/Neo.UnitTests/*`, or VM packaging scripts
- Test: `neo-riscv-vm/scripts/package-adapter-plugin.sh`
- Test: `neo-riscv-core/tests/Neo.UnitTests/Neo.UnitTests.csproj`

**Step 1: Stage the adapter bundle**

Run:

```bash
cd /home/neo/git/neo-riscv/neo-riscv-vm
./scripts/package-adapter-plugin.sh
```

Expected: adapter DLL and native host library appear under `dist/Plugins/Neo.Riscv.Adapter/`.

**Step 2: Reproduce the core-side provider tests with staging**

Run:

```bash
mkdir -p /home/neo/git/neo-riscv/neo-riscv-core/tests/Neo.UnitTests/bin/Debug/net10.0/Plugins
cp -a /home/neo/git/neo-riscv/neo-riscv-vm/dist/Plugins/. /home/neo/git/neo-riscv/neo-riscv-core/tests/Neo.UnitTests/bin/Debug/net10.0/Plugins/
NEO_RISCV_HOST_LIB=/home/neo/git/neo-riscv/neo-riscv-vm/target/release/libneo_riscv_host.so \
dotnet test /home/neo/git/neo-riscv/neo-riscv-core/tests/Neo.UnitTests/Neo.UnitTests.csproj --filter "FullyQualifiedName~UT_ApplicationEngineProvider|FullyQualifiedName~UT_ApplicationEngine"
```

Expected: provider/bridge tests run against the staged adapter rather than failing on missing assembly.

### Task 5: Re-run Focused Validation And Fuzz Coverage

**Files:**
- No new source files unless failures force additional fixes
- Test: `tests/Neo.SmartContract.Testing.UnitTests/Neo.SmartContract.Testing.UnitTests.csproj`
- Test: `tests/Neo.Compiler.CSharp.UnitTests/Neo.Compiler.CSharp.UnitTests.csproj`
- Test: `cargo test --workspace --all-targets`
- Test: `cargo test --manifest-path fuzz/Cargo.toml --lib`

**Step 1: Re-run the devpack slices**

Run:

```bash
dotnet test tests/Neo.SmartContract.Testing.UnitTests/Neo.SmartContract.Testing.UnitTests.csproj
dotnet test tests/Neo.Compiler.CSharp.UnitTests/Neo.Compiler.CSharp.UnitTests.csproj --filter "FullyQualifiedName~UnitTest_RiscVExecution"
```

Expected: all currently exposed devpack regressions are green.

**Step 2: Re-run VM validation and standalone fuzz**

Run:

```bash
cd /home/neo/git/neo-riscv/neo-riscv-vm
cargo test --workspace --all-targets
cargo test --manifest-path fuzz/Cargo.toml --lib
```

Expected: PASS.
