// Copyright (C) 2015-2026 The Neo Project.
//
// RiscVDirectRunner.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Neo.SmartContract.Testing;

/// <summary>
/// Minimal P/Invoke wrapper for directly executing RISC-V contracts via
/// the native neo_riscv_host library. Avoids the full adapter dependency
/// which requires a separate Neo.csproj that conflicts with the NuGet
/// Neo package used by the test framework.
///
/// Struct layouts and function signatures match the Rust FFI in
/// crates/neo-riscv-host/src/ffi.rs exactly.
/// </summary>
public static class RiscVExecutionBridge
{
    // ---------------------------------------------------------------
    //  Native struct layouts (must match repr(C) structs in Rust)
    // ---------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeStackItem
    {
        public uint Kind;           // 0=Integer, 1=ByteString, 2=Null, 3=Boolean, 4=Array, 5=BigInteger, 6=Iterator, 7=Struct, 8=Map, 9=Interop
        public long IntegerValue;
        public IntPtr BytesPtr;
        public nuint BytesLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeExecutionResult
    {
        public long FeeConsumedPico;
        public uint State;           // 0=HALT, 1=FAULT
        public IntPtr StackPtr;      // *mut NativeStackItem
        public nuint StackLen;
        public IntPtr ErrorPtr;      // *mut u8 (UTF-8)
        public nuint ErrorLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIntegerExecutionResult
    {
        public long FeeConsumedPico;
        public uint State;
        public long Value;
        public IntPtr ErrorPtr;
        public nuint ErrorLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeHostResult
    {
        public IntPtr StackPtr;      // *mut NativeStackItem
        public nuint StackLen;
        public IntPtr ErrorPtr;      // *mut u8 (UTF-8)
        public nuint ErrorLen;
    }

    // ---------------------------------------------------------------
    //  Callback delegate types
    // ---------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool HostCallbackDelegate(
        IntPtr userData,
        uint api,
        nuint instructionPointer,
        byte trigger,
        uint networkMagic,
        byte addressVersion,
        ulong persistingTimestamp,
        long gasLeft,
        IntPtr inputStackPtr,    // *const NativeStackItem
        nuint inputStackLen,
        IntPtr output);          // *mut NativeHostResult

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HostFreeCallbackDelegate(
        IntPtr userData,
        IntPtr result);          // *mut NativeHostResult

    // ---------------------------------------------------------------
    //  Native function delegate types
    // ---------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool ExecuteNativeContractDelegate(
        IntPtr binaryPtr,
        nuint binaryLen,
        IntPtr methodPtr,
        nuint methodLen,
        IntPtr initialStackPtr,  // *const NativeStackItem
        nuint initialStackLen,
        byte trigger,
        uint network,
        byte addressVersion,
        ulong timestamp,
        long gasLeft,
        long execFeeFactorPico,
        IntPtr userData,
        IntPtr hostCallback,
        IntPtr hostFree,
        IntPtr output);          // *mut NativeExecutionResult

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool ExecuteNativeContractBuiltinDelegate(
        IntPtr binaryPtr,
        nuint binaryLen,
        IntPtr methodPtr,
        nuint methodLen,
        IntPtr initialStackPtr,
        nuint initialStackLen,
        byte trigger,
        uint network,
        byte addressVersion,
        ulong timestamp,
        long gasLeft,
        long execFeeFactorPico,
        IntPtr output);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool ExecuteNativeContractBuiltinByIdDelegate(
        IntPtr binaryPtr,
        nuint binaryLen,
        uint methodId,
        IntPtr initialStackPtr,
        nuint initialStackLen,
        byte trigger,
        uint network,
        byte addressVersion,
        ulong timestamp,
        long gasLeft,
        long execFeeFactorPico,
        IntPtr output);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool ExecuteNativeContractBuiltinI64ByIdDelegate(
        IntPtr binaryPtr,
        nuint binaryLen,
        uint methodId,
        byte trigger,
        uint network,
        byte addressVersion,
        ulong timestamp,
        long gasLeft,
        long execFeeFactorPico,
        IntPtr output);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeExecutionResultDelegate(
        IntPtr result);          // *mut NativeExecutionResult

    // ---------------------------------------------------------------
    //  State
    // ---------------------------------------------------------------

    private static IntPtr s_libraryHandle;
    private static ExecuteNativeContractDelegate? s_executeNativeContract;
    private static ExecuteNativeContractBuiltinDelegate? s_executeNativeContractBuiltin;
    private static ExecuteNativeContractBuiltinByIdDelegate? s_executeNativeContractBuiltinById;
    private static ExecuteNativeContractBuiltinI64ByIdDelegate? s_executeNativeContractBuiltinI64ById;
    private static FreeExecutionResultDelegate? s_freeExecutionResult;

    // Prevent GC of delegates whose function pointers are passed to native code.
    private static HostCallbackDelegate? s_hostCallbackDelegate;
    private static HostFreeCallbackDelegate? s_hostFreeDelegate;
    private static IntPtr s_hostCallbackPtr;
    private static IntPtr s_hostFreePtr;

    private static bool s_initialized;

    // ---------------------------------------------------------------
    //  Public API
    // ---------------------------------------------------------------

    /// <summary>
    /// True when the native library was loaded and all symbols resolved.
    /// </summary>
    public static bool IsAvailable => s_initialized;

    /// <summary>
    /// Load the native RISC-V host library and resolve symbols.
    /// Safe to call multiple times -- subsequent calls are no-ops.
    /// </summary>
    public static void Initialize(string? libraryPath = null)
    {
        if (s_initialized) return;

        libraryPath ??= FindNativeLibrary();
        if (libraryPath == null || !File.Exists(libraryPath))
            throw new FileNotFoundException(
                "libneo_riscv_host.so not found. Build the Rust host library first.",
                libraryPath ?? "libneo_riscv_host.so");

        s_libraryHandle = NativeLibrary.Load(libraryPath);

        s_executeNativeContract = Marshal.GetDelegateForFunctionPointer<ExecuteNativeContractDelegate>(
            NativeLibrary.GetExport(s_libraryHandle, "neo_riscv_execute_native_contract"));
        if (NativeLibrary.TryGetExport(s_libraryHandle, "neo_riscv_execute_native_contract_builtin", out var builtinExport))
        {
            s_executeNativeContractBuiltin =
                Marshal.GetDelegateForFunctionPointer<ExecuteNativeContractBuiltinDelegate>(builtinExport);
        }
        if (NativeLibrary.TryGetExport(s_libraryHandle, "neo_riscv_execute_native_contract_builtin_by_id", out var builtinByIdExport))
        {
            s_executeNativeContractBuiltinById =
                Marshal.GetDelegateForFunctionPointer<ExecuteNativeContractBuiltinByIdDelegate>(builtinByIdExport);
        }
        if (NativeLibrary.TryGetExport(s_libraryHandle, "neo_riscv_execute_native_contract_builtin_i64_by_id", out var builtinI64ByIdExport))
        {
            s_executeNativeContractBuiltinI64ById =
                Marshal.GetDelegateForFunctionPointer<ExecuteNativeContractBuiltinI64ByIdDelegate>(builtinI64ByIdExport);
        }
        s_freeExecutionResult = Marshal.GetDelegateForFunctionPointer<FreeExecutionResultDelegate>(
            NativeLibrary.GetExport(s_libraryHandle, "neo_riscv_free_execution_result"));

        // Pin callback delegates for the lifetime of the process.
        s_hostCallbackDelegate = DummyHostCallback;
        s_hostFreeDelegate = DummyHostFree;
        s_hostCallbackPtr = Marshal.GetFunctionPointerForDelegate(s_hostCallbackDelegate);
        s_hostFreePtr = Marshal.GetFunctionPointerForDelegate(s_hostFreeDelegate);

        s_initialized = true;
    }

    /// <summary>
    /// Result of a RISC-V contract execution.
    /// </summary>
    public sealed class ExecutionResult
    {
        /// <summary>0 = HALT, 1 = FAULT</summary>
        public uint State { get; init; }
        public long FeeConsumedPico { get; init; }
        public string? Error { get; init; }
        /// <summary>Decoded result stack items (kind, raw bytes or integer).</summary>
        public ResultStackItem[] Stack { get; init; } = Array.Empty<ResultStackItem>();

        public bool IsHalt => State == 0;
        public bool IsFault => State != 0;
    }

    /// <summary>
    /// A decoded stack item from the native result.
    /// </summary>
    public sealed class ResultStackItem
    {
        public uint Kind { get; init; }
        public long IntegerValue { get; init; }
        public byte[]? Bytes { get; init; }
        public ResultStackItem[]? Children { get; init; }
    }

    /// <summary>
    /// Execute a native RISC-V contract method with no input arguments.
    /// </summary>
    public static ExecutionResult Execute(byte[] binary, string method)
    {
        var result = ExecuteBuiltin(binary, method);
        if (ShouldRetryWithLowerCamelAlias(method, result, out var fallbackMethod))
            return ExecuteBuiltin(binary, fallbackMethod!);
        return result;
    }

    /// <summary>
    /// Execute a native RISC-V contract method with a pre-serialized initial stack.
    /// </summary>
    public static ExecutionResult Execute(
        byte[] binary,
        string method,
        ReadOnlySpan<byte> serializedInitialStack,
        int initialStackItemCount)
    {
        if (!s_initialized)
            throw new InvalidOperationException("RiscVDirectRunner.Initialize() has not been called.");
        if (initialStackItemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(initialStackItemCount));

        var binaryHandle = GCHandle.Alloc(binary, GCHandleType.Pinned);
        var methodBytes = Encoding.UTF8.GetBytes(method);
        var methodHandle = GCHandle.Alloc(methodBytes, GCHandleType.Pinned);
        var initialStackPtr = IntPtr.Zero;

        // Allocate the output struct on unmanaged heap so we can pass a stable pointer.
        var resultSize = Marshal.SizeOf<NativeExecutionResult>();
        var resultPtr = Marshal.AllocHGlobal(resultSize);
        // Zero-init so the Rust side sees null pointers if it bails out early.
        {
            var zero = new byte[resultSize];
            Marshal.Copy(zero, 0, resultPtr, resultSize);
        }

        try
        {
            var ok = s_executeNativeContract!(
                binaryHandle.AddrOfPinnedObject(),
                (nuint)binary.Length,
                methodHandle.AddrOfPinnedObject(),
                (nuint)methodBytes.Length,
                initialStackPtr = MarshalSerializedInitialStack(serializedInitialStack, initialStackItemCount),
                (nuint)initialStackItemCount,
                0x40,                  // TriggerType.Application
                860833102u,            // Neo N3 MainNet magic
                53,                    // address version
                0,                     // timestamp (0 = none)
                10_000_000_000_000L,   // 10k GAS in pico
                30_000L,               // exec fee factor pico
                IntPtr.Zero,           // userData (unused in dummy callback)
                s_hostCallbackPtr,
                s_hostFreePtr,
                resultPtr);

            var nativeResult = Marshal.PtrToStructure<NativeExecutionResult>(resultPtr);

            if (!ok)
            {
                return new ExecutionResult
                {
                    State = 1,
                    FeeConsumedPico = nativeResult.FeeConsumedPico,
                    Error = "neo_riscv_execute_native_contract returned false.",
                    Stack = Array.Empty<ResultStackItem>(),
                };
            }

            string? error = null;
            if (nativeResult.ErrorPtr != IntPtr.Zero && nativeResult.ErrorLen > 0)
            {
                error = Marshal.PtrToStringUTF8(nativeResult.ErrorPtr, checked((int)nativeResult.ErrorLen));
            }

            var stack = ReadResultStack(nativeResult.StackPtr, nativeResult.StackLen);

            var result = new ExecutionResult
            {
                State = nativeResult.State,
                FeeConsumedPico = nativeResult.FeeConsumedPico,
                Error = error,
                Stack = stack,
            };

            if (ShouldRetryWithLowerCamelAlias(method, result, out var fallbackMethod))
                return Execute(binary, fallbackMethod!, serializedInitialStack, initialStackItemCount);

            return result;
        }
        finally
        {
            // Free the native result's heap allocations via the Rust free function.
            var nativeResult = Marshal.PtrToStructure<NativeExecutionResult>(resultPtr);
            if (nativeResult.StackPtr != IntPtr.Zero || nativeResult.ErrorPtr != IntPtr.Zero)
            {
                s_freeExecutionResult!(resultPtr);
            }
            Marshal.FreeHGlobal(resultPtr);
            if (initialStackPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(initialStackPtr);

            methodHandle.Free();
            binaryHandle.Free();
        }
    }

    public static ExecutionResult ExecuteBuiltin(byte[] binary, string method)
    {
        if (!s_initialized)
            throw new InvalidOperationException("RiscVDirectRunner.Initialize() has not been called.");
        if (s_executeNativeContractBuiltin == null)
            return Execute(binary, method, ReadOnlySpan<byte>.Empty, 0);

        var binaryHandle = GCHandle.Alloc(binary, GCHandleType.Pinned);
        var methodBytes = Encoding.UTF8.GetBytes(method);
        var methodHandle = GCHandle.Alloc(methodBytes, GCHandleType.Pinned);
        var resultSize = Marshal.SizeOf<NativeExecutionResult>();
        var resultPtr = Marshal.AllocHGlobal(resultSize);
        {
            var zero = new byte[resultSize];
            Marshal.Copy(zero, 0, resultPtr, resultSize);
        }

        try
        {
            var ok = s_executeNativeContractBuiltin!(
                binaryHandle.AddrOfPinnedObject(),
                (nuint)binary.Length,
                methodHandle.AddrOfPinnedObject(),
                (nuint)methodBytes.Length,
                IntPtr.Zero,
                0,
                0x40,
                860833102u,
                53,
                0,
                10_000_000_000_000L,
                30_000L,
                resultPtr);

            var nativeResult = Marshal.PtrToStructure<NativeExecutionResult>(resultPtr);
            if (!ok)
            {
                return new ExecutionResult
                {
                    State = 1,
                    FeeConsumedPico = nativeResult.FeeConsumedPico,
                    Error = "neo_riscv_execute_native_contract_builtin returned false.",
                    Stack = Array.Empty<ResultStackItem>(),
                };
            }

            string? error = null;
            if (nativeResult.ErrorPtr != IntPtr.Zero && nativeResult.ErrorLen > 0)
                error = Marshal.PtrToStringUTF8(nativeResult.ErrorPtr, checked((int)nativeResult.ErrorLen));

            var stack = ReadResultStack(nativeResult.StackPtr, nativeResult.StackLen);
            var result = new ExecutionResult
            {
                State = nativeResult.State,
                FeeConsumedPico = nativeResult.FeeConsumedPico,
                Error = error,
                Stack = stack,
            };

            if (ShouldRetryWithLowerCamelAlias(method, result, out var fallbackMethod))
                return ExecuteBuiltin(binary, fallbackMethod!);

            return result;
        }
        finally
        {
            var nativeResult = Marshal.PtrToStructure<NativeExecutionResult>(resultPtr);
            if (nativeResult.StackPtr != IntPtr.Zero || nativeResult.ErrorPtr != IntPtr.Zero)
                s_freeExecutionResult!(resultPtr);
            Marshal.FreeHGlobal(resultPtr);
            methodHandle.Free();
            binaryHandle.Free();
        }
    }

    public static long ExecuteBuiltinInteger(byte[] binary, string method)
    {
        if (!s_initialized)
            throw new InvalidOperationException("RiscVDirectRunner.Initialize() has not been called.");
        if (s_executeNativeContractBuiltinI64ById != null)
        {
            var binaryHandle = GCHandle.Alloc(binary, GCHandleType.Pinned);
            var methodId = ComputeMethodId(method);
            var resultSize = Marshal.SizeOf<NativeIntegerExecutionResult>();
            var resultPtr = Marshal.AllocHGlobal(resultSize);
            {
                var zero = new byte[resultSize];
                Marshal.Copy(zero, 0, resultPtr, resultSize);
            }

            try
            {
                var ok = s_executeNativeContractBuiltinI64ById!(
                    binaryHandle.AddrOfPinnedObject(),
                    (nuint)binary.Length,
                    methodId,
                    0x40,
                    860833102u,
                    53,
                    0,
                    10_000_000_000_000L,
                    30_000L,
                    resultPtr);

                var nativeResult = Marshal.PtrToStructure<NativeIntegerExecutionResult>(resultPtr);
                if (!ok)
                    throw new InvalidOperationException("neo_riscv_execute_native_contract_builtin_i64_by_id returned false.");
                if (nativeResult.State == 0)
                    return nativeResult.Value;
            }
            finally
            {
                var nativeResult = Marshal.PtrToStructure<NativeIntegerExecutionResult>(resultPtr);
                FreeNativeIntegerError(nativeResult);
                Marshal.FreeHGlobal(resultPtr);
                binaryHandle.Free();
            }
        }

        if (s_executeNativeContractBuiltinById == null)
        {
            var result = ExecuteBuiltin(binary, method);
            if (result.IsFault)
                throw new InvalidOperationException(result.Error ?? "Native RISC-V direct execution fault.");
            if (result.Stack.Length != 1 || result.Stack[0].Kind != 0)
                throw new InvalidOperationException($"Expected a single integer result for {method}.");
            return result.Stack[0].IntegerValue;
        }

        var binaryHandleFallback = GCHandle.Alloc(binary, GCHandleType.Pinned);
        var methodIdFallback = ComputeMethodId(method);
        var fallbackSize = Marshal.SizeOf<NativeExecutionResult>();
        var fallbackPtr = Marshal.AllocHGlobal(fallbackSize);
        {
            var zero = new byte[fallbackSize];
            Marshal.Copy(zero, 0, fallbackPtr, fallbackSize);
        }

        try
        {
            var ok = s_executeNativeContractBuiltinById!(
                binaryHandleFallback.AddrOfPinnedObject(),
                (nuint)binary.Length,
                methodIdFallback,
                IntPtr.Zero,
                0,
                0x40,
                860833102u,
                53,
                0,
                10_000_000_000_000L,
                30_000L,
                fallbackPtr);

            var nativeResult = Marshal.PtrToStructure<NativeExecutionResult>(fallbackPtr);
            if (!ok)
                throw new InvalidOperationException("neo_riscv_execute_native_contract_builtin_by_id returned false.");
            if (nativeResult.State != 0)
            {
                var error = nativeResult.ErrorPtr != IntPtr.Zero && nativeResult.ErrorLen > 0
                    ? Marshal.PtrToStringUTF8(nativeResult.ErrorPtr, checked((int)nativeResult.ErrorLen))
                    : "Native RISC-V direct execution fault.";
                throw new InvalidOperationException(error);
            }

            if (nativeResult.StackLen != 1 || nativeResult.StackPtr == IntPtr.Zero)
                throw new InvalidOperationException($"Expected a single stack item for {method}.");

            var item = Marshal.PtrToStructure<NativeStackItem>(nativeResult.StackPtr);
            if (item.Kind != 0)
                throw new InvalidOperationException($"Expected integer result kind for {method}, got {item.Kind}.");

            return item.IntegerValue;
        }
        finally
        {
            var nativeResult = Marshal.PtrToStructure<NativeExecutionResult>(fallbackPtr);
            if (nativeResult.StackPtr != IntPtr.Zero || nativeResult.ErrorPtr != IntPtr.Zero)
                s_freeExecutionResult!(fallbackPtr);
            Marshal.FreeHGlobal(fallbackPtr);
            binaryHandleFallback.Free();
        }
    }

    private static bool ShouldRetryWithLowerCamelAlias(string method, ExecutionResult result, out string? fallbackMethod)
    {
        fallbackMethod = GetLowerCamelAlias(method);
        return fallbackMethod != null
            && result.IsFault
            && string.Equals(result.Error, "Unknown method", StringComparison.Ordinal);
    }

    private static string? GetLowerCamelAlias(string method)
    {
        if (string.IsNullOrEmpty(method))
            return null;

        var first = method[0];
        if (!char.IsUpper(first))
            return null;

        var lowered = char.ToLowerInvariant(first);
        if (lowered == first)
            return null;

        return lowered + method.Substring(1);
    }

    public sealed class BuiltinIntegerInvoker : IDisposable
    {
        private readonly GCHandle _binaryHandle;
        private readonly int _binaryLength;
        private readonly uint _methodId;
        private readonly string _method;
        private readonly IntPtr _resultPtr;
        private readonly int _resultSize;
        private bool _preferI64Path = true;

        public BuiltinIntegerInvoker(byte[] binary, string method)
        {
            if (!s_initialized)
                throw new InvalidOperationException("RiscVDirectRunner.Initialize() has not been called.");
            if (s_executeNativeContractBuiltinById == null)
                throw new InvalidOperationException("Builtin-by-id native contract entry is not available.");

            _binaryHandle = GCHandle.Alloc(binary, GCHandleType.Pinned);
            _binaryLength = binary.Length;
            _methodId = ComputeMethodId(method);
            _method = method;
            _resultSize = Marshal.SizeOf<NativeExecutionResult>();
            _resultPtr = Marshal.AllocHGlobal(_resultSize);
            ZeroResultBuffer();
        }

        public long Invoke()
        {
            if (_preferI64Path && s_executeNativeContractBuiltinI64ById != null)
            {
                var intResultSize = Marshal.SizeOf<NativeIntegerExecutionResult>();
                var intResultPtr = Marshal.AllocHGlobal(intResultSize);
                try
                {
                    var zero = new byte[intResultSize];
                    Marshal.Copy(zero, 0, intResultPtr, intResultSize);

                    var intOk = s_executeNativeContractBuiltinI64ById!(
                        _binaryHandle.AddrOfPinnedObject(),
                        (nuint)_binaryLength,
                        _methodId,
                        0x40,
                        860833102u,
                        53,
                        0,
                        10_000_000_000_000L,
                        30_000L,
                        intResultPtr);

                    var nativeIntResult = Marshal.PtrToStructure<NativeIntegerExecutionResult>(intResultPtr);
                    if (!intOk)
                        throw new InvalidOperationException("neo_riscv_execute_native_contract_builtin_i64_by_id returned false.");
                    if (nativeIntResult.State == 0)
                        return nativeIntResult.Value;

                    _preferI64Path = false;
                }
                finally
                {
                    var nativeIntResult = Marshal.PtrToStructure<NativeIntegerExecutionResult>(intResultPtr);
                    FreeNativeIntegerError(nativeIntResult);
                    Marshal.FreeHGlobal(intResultPtr);
                }
            }

            ZeroResultBuffer();

            var ok = s_executeNativeContractBuiltinById!(
                _binaryHandle.AddrOfPinnedObject(),
                (nuint)_binaryLength,
                _methodId,
                IntPtr.Zero,
                0,
                0x40,
                860833102u,
                53,
                0,
                10_000_000_000_000L,
                30_000L,
                _resultPtr);

            var nativeResult = Marshal.PtrToStructure<NativeExecutionResult>(_resultPtr);
            try
            {
                if (!ok)
                    throw new InvalidOperationException("neo_riscv_execute_native_contract_builtin_by_id returned false.");
                if (nativeResult.State != 0)
                {
                    var error = nativeResult.ErrorPtr != IntPtr.Zero && nativeResult.ErrorLen > 0
                        ? Marshal.PtrToStringUTF8(nativeResult.ErrorPtr, checked((int)nativeResult.ErrorLen))
                        : "Native RISC-V direct execution fault.";
                    throw new InvalidOperationException(error);
                }

                if (nativeResult.StackLen != 1 || nativeResult.StackPtr == IntPtr.Zero)
                    throw new InvalidOperationException("Expected a single stack item.");

                var item = Marshal.PtrToStructure<NativeStackItem>(nativeResult.StackPtr);
                if (item.Kind != 0)
                    throw new InvalidOperationException($"Expected integer result kind, got {item.Kind}.");

                return item.IntegerValue;
            }
            finally
            {
                if (nativeResult.StackPtr != IntPtr.Zero || nativeResult.ErrorPtr != IntPtr.Zero)
                    s_freeExecutionResult!(_resultPtr);
            }
        }

        private void ZeroResultBuffer()
        {
            var zero = new byte[_resultSize];
            Marshal.Copy(zero, 0, _resultPtr, _resultSize);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_resultPtr);
            if (_binaryHandle.IsAllocated)
                _binaryHandle.Free();
        }
    }

    private static uint ComputeMethodId(string method)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in method)
            {
                hash ^= ch;
                hash *= 16777619;
            }
            return hash;
        }
    }

    private static void FreeNativeIntegerError(NativeIntegerExecutionResult result)
    {
        if (result.ErrorPtr == IntPtr.Zero)
            return;

        var freeResultSize = Marshal.SizeOf<NativeExecutionResult>();
        var freeResultPtr = Marshal.AllocHGlobal(freeResultSize);
        try
        {
            Marshal.StructureToPtr(new NativeExecutionResult
            {
                ErrorPtr = result.ErrorPtr,
                ErrorLen = result.ErrorLen,
            }, freeResultPtr, false);
            s_freeExecutionResult!(freeResultPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(freeResultPtr);
        }
    }

    // ---------------------------------------------------------------
    //  Dummy host callbacks
    // ---------------------------------------------------------------

    /// <summary>
    /// Dummy host callback that returns an empty stack for any syscall.
    /// This is sufficient for contracts that do not use syscalls (pure
    /// computation contracts). Contracts that need storage, notifications,
    /// etc. will fault with "Unsupported syscall".
    /// </summary>
    private static bool DummyHostCallback(
        IntPtr userData,
        uint api,
        nuint instructionPointer,
        byte trigger,
        uint networkMagic,
        byte addressVersion,
        ulong persistingTimestamp,
        long gasLeft,
        IntPtr inputStackPtr,
        nuint inputStackLen,
        IntPtr output)
    {
        // Write a NativeHostResult with empty stack to the output pointer.
        if (output != IntPtr.Zero)
        {
            var result = new NativeHostResult
            {
                StackPtr = IntPtr.Zero,
                StackLen = 0,
                ErrorPtr = IntPtr.Zero,
                ErrorLen = 0,
            };
            Marshal.StructureToPtr(result, output, false);
        }
        return true;
    }

    private static void DummyHostFree(IntPtr userData, IntPtr result)
    {
        // The dummy callback allocates nothing, so nothing to free.
    }

    // ---------------------------------------------------------------
    //  TestHostCallback: in-memory syscall handler for testing
    // ---------------------------------------------------------------

    /// <summary>
    /// API ID constants matching neo-riscv-devpack/src/api_ids.rs (FNV-1a hashes).
    /// </summary>
    private static class ApiIds
    {
        // System.Storage
        public const uint StorageGetContext = 0xCE67F69B;
        public const uint StorageGetReadOnlyContext = 0xE26BB4F6;
        public const uint StorageAsReadOnly = 0xE9BF4C76;
        public const uint StorageGet = 0x31E85D92;
        public const uint StoragePut = 0x84183FE6;
        public const uint StorageDelete = 0xEDC5582F;
        public const uint StorageFind = 0x9AB830DF;

        // System.Runtime
        public const uint RuntimeCheckWitness = 0x8CEC27F8;
        public const uint RuntimeNotify = 0x616F0195;
        public const uint RuntimeLog = 0x9647E7CF;
        public const uint RuntimeGetTrigger = 0xA0387DE9;
        public const uint RuntimeGetNetwork = 0xE0A0FBC5;
        public const uint RuntimeGetAddressVersion = 0xDC92494C;
        public const uint RuntimeGetTime = 0x0388C3B7;
        public const uint RuntimeGasLeft = 0xCED88814;
        public const uint RuntimePlatform = 0xF6FC79B2;
        public const uint RuntimeGetExecutingScriptHash = 0x74A8FEDB;
        public const uint RuntimeGetCallingScriptHash = 0x3C6E5339;
        public const uint RuntimeGetEntryScriptHash = 0x38E2B4F9;

        // System.Iterator
        public const uint IteratorNext = 0x9CED089C;
        public const uint IteratorValue = 0x1DBF54F3;

        // System.Storage.Local
        public const uint StorageLocalGet = 0xE85E8DD5;
        public const uint StorageLocalPut = 0x0AE30C39;
        public const uint StorageLocalDelete = 0x94F55475;
        public const uint StorageLocalFind = 0xF3527607;

        // CALLT marker
        public const uint CalltMarker = 0x43540000;
    }

    /// <summary>
    /// A test-friendly host callback that handles common syscalls with
    /// in-memory state. Enables testing contracts that use storage,
    /// logging, and notifications without a full Neo blockchain.
    /// </summary>
    public sealed class TestHostCallback
    {
        /// <summary>In-memory storage (key -> value).</summary>
        public Dictionary<byte[], byte[]> Storage { get; } = new(ByteArrayEqualityComparer.Instance);

        /// <summary>Captured log messages.</summary>
        public List<string> Logs { get; } = new();

        /// <summary>Debug: tracks which API IDs were called via callback.</summary>
        public List<uint> CalledApis { get; } = new();

        /// <summary>Captured notifications (script hash, event name, args).</summary>
        public List<NotificationRecord> Notifications { get; } = new();

        /// <summary>Delegate to handle Contract.Call. Return null for default (empty).</summary>
        public Func<byte[], string, ResultStackItem[], ResultStackItem[]?>? OnContractCall { get; set; }

        private ulong _nextHandle = 1;
        private readonly Dictionary<ulong, TestStorageContext> _storageContexts = new();
        private readonly Dictionary<ulong, TestIteratorState> _iterators = new();

        /// <summary>
        /// Execute a RISC-V contract with this host callback providing syscall support.
        /// </summary>
        public ExecutionResult Execute(byte[] binary, string method)
        {
            return ExecuteWithHost(binary, method, this);
        }

        /// <summary>
        /// Execute a RISC-V contract with initial stack arguments and this host callback.
        /// </summary>
        public ExecutionResult Execute(byte[] binary, string method, ResultStackItem[] initialArgs)
        {
            return ExecuteWithHost(binary, method, initialArgs, this);
        }

        internal bool TryGetStorageValue(NativeStackItem keyItem, out byte[] value)
        {
            foreach (var entry in Storage)
            {
                if (InputBytesEqual(keyItem, entry.Key))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = Array.Empty<byte>();
            return false;
        }

        internal void PutStorageValue(byte[] key, byte[] value)
        {
            Storage[key] = value;
        }

        internal void RemoveStorageValue(NativeStackItem keyItem)
        {
            byte[]? match = null;
            foreach (var entry in Storage)
            {
                if (InputBytesEqual(keyItem, entry.Key))
                {
                    match = entry.Key;
                    break;
                }
            }

            if (match != null)
                Storage.Remove(match);
        }

        internal ulong CreateStorageContext(bool isReadOnly)
        {
            var handle = _nextHandle++;
            _storageContexts[handle] = new TestStorageContext(isReadOnly);
            return handle;
        }

        internal bool TryGetStorageContext(NativeStackItem item, out TestStorageContext context)
        {
            if (item.Kind == 9 && item.IntegerValue > 0 &&
                _storageContexts.TryGetValue((ulong)item.IntegerValue, out var found))
            {
                context = found;
                return true;
            }

            context = default;
            return false;
        }

        internal ulong CreateIterator(byte[] prefix, int options)
        {
            var removePrefix = (options & 0x02) != 0;
            var keysOnly = (options & 0x01) != 0;
            var valuesOnly = (options & 0x04) != 0;
            var items = new List<ResultStackItem>();

            foreach (var entry in Storage)
            {
                if (!StartsWith(entry.Key, prefix))
                    continue;

                var key = removePrefix ? entry.Key[prefix.Length..] : entry.Key;
                var keyItem = new ResultStackItem { Kind = 1, Bytes = key };
                var valueItem = new ResultStackItem { Kind = 1, Bytes = entry.Value };
                items.Add(valuesOnly
                    ? valueItem
                    : keysOnly
                        ? keyItem
                        : new ResultStackItem { Kind = 4, Children = [keyItem, valueItem] });
            }

            var handle = _nextHandle++;
            _iterators[handle] = new TestIteratorState(items);
            return handle;
        }

        internal bool IteratorNext(NativeStackItem item)
        {
            return item.Kind == 6 && item.IntegerValue > 0 &&
                _iterators.TryGetValue((ulong)item.IntegerValue, out var iterator) &&
                iterator.Next();
        }

        internal ResultStackItem IteratorValue(NativeStackItem item)
        {
            if (item.Kind != 6 || item.IntegerValue <= 0 ||
                !_iterators.TryGetValue((ulong)item.IntegerValue, out var iterator))
                throw new InvalidOperationException("Iterator handle is not recognized.");

            return iterator.Value ?? new ResultStackItem { Kind = 2 };
        }

        private static bool StartsWith(byte[] value, byte[] prefix)
        {
            if (prefix.Length > value.Length)
                return false;
            for (var i = 0; i < prefix.Length; i++)
            {
                if (value[i] != prefix[i])
                    return false;
            }
            return true;
        }
    }

    public readonly record struct TestStorageContext(bool IsReadOnly);

    private sealed class TestIteratorState
    {
        private readonly List<ResultStackItem> _items;
        private int _index = -1;

        public TestIteratorState(List<ResultStackItem> items)
        {
            _items = items;
        }

        public ResultStackItem? Value =>
            _index >= 0 && _index < _items.Count ? _items[_index] : null;

        public bool Next()
        {
            if (_index + 1 >= _items.Count)
                return false;

            _index++;
            return true;
        }
    }

    /// <summary>
    /// A recorded notification event.
    /// </summary>
    public sealed class NotificationRecord
    {
        public required byte[] ScriptHash { get; init; }
        public required string EventName { get; init; }
        public required ResultStackItem[] Args { get; init; }
    }

    /// <summary>
    /// Execute a RISC-V contract with an optional TestHostCallback.
    /// Falls back to DummyHostCallback if host is null.
    /// </summary>
    public static ExecutionResult ExecuteWithHost(byte[] binary, string method, TestHostCallback? host)
    {
        if (!s_initialized)
            throw new InvalidOperationException("RiscVExecutionBridge.Initialize() has not been called.");

        HostCallbackDelegate callback;
        HostFreeCallbackDelegate freeCallback;
        GCHandle hostHandle;

        if (host != null)
        {
            hostHandle = GCHandle.Alloc(host);
            callback = TestHostCallbackImpl;
            freeCallback = TestHostFreeImpl;
        }
        else
        {
            hostHandle = default;
            callback = DummyHostCallback;
            freeCallback = DummyHostFree;
        }

        var callbackPtr = Marshal.GetFunctionPointerForDelegate(callback);
        var freePtr = Marshal.GetFunctionPointerForDelegate(freeCallback);

        var binaryHandle = GCHandle.Alloc(binary, GCHandleType.Pinned);
        var methodBytes = Encoding.UTF8.GetBytes(method);
        var methodHandle = GCHandle.Alloc(methodBytes, GCHandleType.Pinned);

        var resultSize = Marshal.SizeOf<NativeExecutionResult>();
        var resultPtr = Marshal.AllocHGlobal(resultSize);
        {
            var zero = new byte[resultSize];
            Marshal.Copy(zero, 0, resultPtr, resultSize);
        }

        try
        {
            var userData = host != null ? GCHandle.ToIntPtr(hostHandle) : IntPtr.Zero;

            var ok = s_executeNativeContract!(
                binaryHandle.AddrOfPinnedObject(),
                (nuint)binary.Length,
                methodHandle.AddrOfPinnedObject(),
                (nuint)methodBytes.Length,
                IntPtr.Zero,
                0,
                0x40,
                860833102u,
                53,
                0,
                10_000_000_000_000L,
                30_000L,
                userData,
                callbackPtr,
                freePtr,
                resultPtr);

            var nativeResult = Marshal.PtrToStructure<NativeExecutionResult>(resultPtr);
            if (!ok)
            {
                return new ExecutionResult
                {
                    State = 1,
                    FeeConsumedPico = nativeResult.FeeConsumedPico,
                    Error = "neo_riscv_execute_native_contract returned false.",
                    Stack = Array.Empty<ResultStackItem>(),
                };
            }

            string? error = null;
            if (nativeResult.ErrorPtr != IntPtr.Zero && nativeResult.ErrorLen > 0)
            {
                error = Marshal.PtrToStringUTF8(nativeResult.ErrorPtr, checked((int)nativeResult.ErrorLen));
            }

            var stack = ReadResultStack(nativeResult.StackPtr, nativeResult.StackLen);

            var result = new ExecutionResult
            {
                State = nativeResult.State,
                FeeConsumedPico = nativeResult.FeeConsumedPico,
                Error = error,
                Stack = stack,
            };

            if (ShouldRetryWithLowerCamelAlias(method, result, out var fallbackMethod))
                return ExecuteWithHost(binary, fallbackMethod!, host);

            return result;
        }
        finally
        {
            var nativeResult = Marshal.PtrToStructure<NativeExecutionResult>(resultPtr);
            if (nativeResult.StackPtr != IntPtr.Zero || nativeResult.ErrorPtr != IntPtr.Zero)
            {
                s_freeExecutionResult!(resultPtr);
            }
            Marshal.FreeHGlobal(resultPtr);

            methodHandle.Free();
            binaryHandle.Free();
            if (hostHandle.IsAllocated)
                hostHandle.Free();
        }
    }

    /// <summary>
    /// Execute a RISC-V contract with initial stack arguments and an optional TestHostCallback.
    /// </summary>
    public static ExecutionResult ExecuteWithHost(
        byte[] binary,
        string method,
        ResultStackItem[] initialArgs,
        TestHostCallback? host)
    {
        if (!s_initialized)
            throw new InvalidOperationException("RiscVDirectRunner.Initialize() has not been called.");

        HostCallbackDelegate callback;
        HostFreeCallbackDelegate freeCallback;
        GCHandle hostHandle;

        if (host != null)
        {
            hostHandle = GCHandle.Alloc(host);
            callback = TestHostCallbackImpl;
            freeCallback = TestHostFreeImpl;
        }
        else
        {
            hostHandle = default;
            callback = DummyHostCallback;
            freeCallback = DummyHostFree;
        }

        var callbackPtr = Marshal.GetFunctionPointerForDelegate(callback);
        var freePtr = Marshal.GetFunctionPointerForDelegate(freeCallback);

        // Build the initial stack as a pinned NativeStackItem array
        var itemSize = Marshal.SizeOf<NativeStackItem>();
        var stackItems = new NativeStackItem[initialArgs.Length];
        var allocatedByteArrays = new List<IntPtr>();

        for (var i = 0; i < initialArgs.Length; i++)
        {
            var arg = initialArgs[i];
            var item = new NativeStackItem
            {
                Kind = arg.Kind,
                IntegerValue = arg.IntegerValue,
            };

            if (arg.Bytes != null && arg.Bytes.Length > 0)
            {
                var bytesPtr = Marshal.AllocHGlobal(arg.Bytes.Length);
                Marshal.Copy(arg.Bytes, 0, bytesPtr, arg.Bytes.Length);
                item.BytesPtr = bytesPtr;
                item.BytesLen = (nuint)arg.Bytes.Length;
                allocatedByteArrays.Add(bytesPtr);
            }

            stackItems[i] = item;
        }

        // Marshal the array to unmanaged memory
        var stackPtr = Marshal.AllocHGlobal(itemSize * stackItems.Length);
        for (var i = 0; i < stackItems.Length; i++)
        {
            Marshal.StructureToPtr(stackItems[i], IntPtr.Add(stackPtr, i * itemSize), false);
        }

        var binaryHandle = GCHandle.Alloc(binary, GCHandleType.Pinned);
        var methodBytes = Encoding.UTF8.GetBytes(method);
        var methodHandle = GCHandle.Alloc(methodBytes, GCHandleType.Pinned);

        var resultSize = Marshal.SizeOf<NativeExecutionResult>();
        var resultPtr = Marshal.AllocHGlobal(resultSize);
        {
            var zero = new byte[resultSize];
            Marshal.Copy(zero, 0, resultPtr, resultSize);
        }

        try
        {
            var userData = host != null ? GCHandle.ToIntPtr(hostHandle) : IntPtr.Zero;

            var ok = s_executeNativeContract!(
                binaryHandle.AddrOfPinnedObject(),
                (nuint)binary.Length,
                methodHandle.AddrOfPinnedObject(),
                (nuint)methodBytes.Length,
                stackPtr,
                (nuint)initialArgs.Length,
                0x40,
                860833102u,
                53,
                0,
                10_000_000_000_000L,
                30_000L,
                userData,
                callbackPtr,
                freePtr,
                resultPtr);

            var nativeResult = Marshal.PtrToStructure<NativeExecutionResult>(resultPtr);
            if (!ok)
            {
                return new ExecutionResult
                {
                    State = 1,
                    FeeConsumedPico = nativeResult.FeeConsumedPico,
                    Error = "neo_riscv_execute_native_contract returned false.",
                    Stack = Array.Empty<ResultStackItem>(),
                };
            }

            string? error = null;
            if (nativeResult.ErrorPtr != IntPtr.Zero && nativeResult.ErrorLen > 0)
            {
                error = Marshal.PtrToStringUTF8(nativeResult.ErrorPtr, checked((int)nativeResult.ErrorLen));
            }

            var stack = ReadResultStack(nativeResult.StackPtr, nativeResult.StackLen);

            var result = new ExecutionResult
            {
                State = nativeResult.State,
                FeeConsumedPico = nativeResult.FeeConsumedPico,
                Error = error,
                Stack = stack,
            };

            if (ShouldRetryWithLowerCamelAlias(method, result, out var fallbackMethod))
                return ExecuteWithHost(binary, fallbackMethod!, initialArgs, host);

            return result;
        }
        finally
        {
            var nativeResult = Marshal.PtrToStructure<NativeExecutionResult>(resultPtr);
            if (nativeResult.StackPtr != IntPtr.Zero || nativeResult.ErrorPtr != IntPtr.Zero)
            {
                s_freeExecutionResult!(resultPtr);
            }
            Marshal.FreeHGlobal(resultPtr);
            Marshal.FreeHGlobal(stackPtr);

            foreach (var ptr in allocatedByteArrays)
                Marshal.FreeHGlobal(ptr);

            methodHandle.Free();
            binaryHandle.Free();
            if (hostHandle.IsAllocated)
                hostHandle.Free();
        }
    }

    /// <summary>
    /// Reads a NativeStackItem array from unmanaged memory into a managed array.
    /// </summary>
    private static NativeStackItem[] ReadInputStack(IntPtr ptr, nuint len)
    {
        if (ptr == IntPtr.Zero || len == 0)
            return Array.Empty<NativeStackItem>();

        var count = (int)len;
        var items = new NativeStackItem[count];
        var itemSize = Marshal.SizeOf<NativeStackItem>();

        for (var i = 0; i < count; i++)
        {
            items[i] = Marshal.PtrToStructure<NativeStackItem>(IntPtr.Add(ptr, i * itemSize));
        }

        return items;
    }

    /// <summary>
    /// Reads bytes from a NativeStackItem's bytes_ptr/bytes_len.
    /// </summary>
    private static byte[] ReadItemBytes(NativeStackItem item)
    {
        if (item.BytesPtr == IntPtr.Zero || item.BytesLen == 0)
            return Array.Empty<byte>();
        var bytes = new byte[(int)item.BytesLen];
        Marshal.Copy(item.BytesPtr, bytes, 0, bytes.Length);
        return bytes;
    }

    private static NativeStackItem ReadInputItem(IntPtr ptr, nuint len, int index)
    {
        if (ptr == IntPtr.Zero || index < 0 || index >= (int)len)
            return default;
        var itemSize = Marshal.SizeOf<NativeStackItem>();
        return Marshal.PtrToStructure<NativeStackItem>(IntPtr.Add(ptr, index * itemSize));
    }

    private static IntPtr MarshalSerializedInitialStack(ReadOnlySpan<byte> serializedInitialStack, int initialStackItemCount)
    {
        if (initialStackItemCount == 0)
            return IntPtr.Zero;

        var expectedLength = checked(Marshal.SizeOf<NativeStackItem>() * initialStackItemCount);
        if (serializedInitialStack.Length < expectedLength)
            throw new ArgumentException("Serialized initial stack is shorter than the declared item count.", nameof(serializedInitialStack));

        var stackPtr = Marshal.AllocHGlobal(expectedLength);
        Marshal.Copy(serializedInitialStack[..expectedLength].ToArray(), 0, stackPtr, expectedLength);
        return stackPtr;
    }

    private static bool TryReadInputBytes(IntPtr ptr, nuint len, int index, out byte[] bytes)
    {
        var item = ReadInputItem(ptr, len, index);
        if (item.Kind != 1)
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        if (item.BytesLen == 0)
        {
            bytes = Array.Empty<byte>();
            return true;
        }

        if (item.BytesPtr == IntPtr.Zero)
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        bytes = new byte[(int)item.BytesLen];
        Marshal.Copy(item.BytesPtr, bytes, 0, bytes.Length);
        return true;
    }

    private static bool IsByteStringItem(NativeStackItem item)
    {
        return item.Kind == 1 && (item.BytesLen == 0 || item.BytesPtr != IntPtr.Zero);
    }

    private static bool TryReadInputInteger(IntPtr ptr, nuint len, int index, out long value)
    {
        var item = ReadInputItem(ptr, len, index);
        if (item.Kind == 0 || item.Kind == 3)
        {
            value = item.IntegerValue;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadStorageContext(
        TestHostCallback host,
        IntPtr ptr,
        nuint len,
        int index,
        out TestStorageContext context)
    {
        return host.TryGetStorageContext(ReadInputItem(ptr, len, index), out context);
    }

    private static bool InputBytesEqual(NativeStackItem item, byte[] managed)
    {
        if (item.BytesPtr == IntPtr.Zero)
            return managed.Length == 0;
        if ((int)item.BytesLen != managed.Length)
            return false;
        for (var i = 0; i < managed.Length; i++)
        {
            if (Marshal.ReadByte(item.BytesPtr, i) != managed[i])
                return false;
        }
        return true;
    }

    /// <summary>
    /// Allocates a NativeStackItem on unmanaged heap and writes the result.
    /// Caller must free via TestHostFreeImpl.
    /// </summary>
    private static IntPtr AllocateResultItem(uint kind, long integerValue, byte[]? bytes)
    {
        var itemSize = Marshal.SizeOf<NativeStackItem>();
        var ptr = Marshal.AllocHGlobal(itemSize);
        var item = new NativeStackItem { Kind = kind, IntegerValue = integerValue };

        if (bytes != null && bytes.Length > 0)
        {
            var bytesHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            var bytesPtr = bytesHandle.AddrOfPinnedObject();
            s_pinnedByteHandles[bytesPtr] = bytesHandle;
            item.BytesPtr = bytesPtr;
            item.BytesLen = (nuint)bytes.Length;
        }

        Marshal.StructureToPtr(item, ptr, false);
        return ptr;
    }

    /// <summary>
    /// Writes a single NativeStackItem result to the output NativeHostResult.
    /// </summary>
    private static void WriteOutputSingle(IntPtr output, uint kind, long integerValue, byte[]? bytes)
    {
        if (output == IntPtr.Zero) return;
        if ((bytes == null || bytes.Length == 0) && TryWriteCachedSingle(output, kind, integerValue))
            return;
        var itemSize = Marshal.SizeOf<NativeStackItem>();
        var arrayPtr = Marshal.AllocHGlobal(itemSize);
        var item = new NativeStackItem { Kind = kind, IntegerValue = integerValue };

        if (bytes != null && bytes.Length > 0)
        {
            var bytesHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            var bytesPtr = bytesHandle.AddrOfPinnedObject();
            s_pinnedByteHandles[bytesPtr] = bytesHandle;
            item.BytesPtr = bytesPtr;
            item.BytesLen = (nuint)bytes.Length;
        }

        Marshal.StructureToPtr(item, arrayPtr, false);

        var result = new NativeHostResult
        {
            StackPtr = arrayPtr,
            StackLen = 1,
            ErrorPtr = IntPtr.Zero,
            ErrorLen = 0,
        };
        Marshal.StructureToPtr(result, output, false);
    }

    private static bool TryWriteCachedSingle(IntPtr output, uint kind, long integerValue)
    {
        var stackPtr = kind switch
        {
            0 when integerValue == 0 => s_cachedIntZeroStackPtr,
            2 when integerValue == 0 => s_cachedNullStackPtr,
            3 when integerValue == 1 => s_cachedBoolTrueStackPtr,
            3 when integerValue == 0 => s_cachedBoolFalseStackPtr,
            _ => IntPtr.Zero
        };

        if (stackPtr == IntPtr.Zero)
            return false;

        var result = new NativeHostResult
        {
            StackPtr = stackPtr,
            StackLen = 1,
            ErrorPtr = IntPtr.Zero,
            ErrorLen = 0,
        };
        Marshal.StructureToPtr(result, output, false);
        return true;
    }

    private static IntPtr CreateCachedSingleStackItem(uint kind, long integerValue)
    {
        var itemSize = Marshal.SizeOf<NativeStackItem>();
        var stackPtr = Marshal.AllocHGlobal(itemSize);
        var item = new NativeStackItem
        {
            Kind = kind,
            IntegerValue = integerValue,
            BytesPtr = IntPtr.Zero,
            BytesLen = 0,
        };
        Marshal.StructureToPtr(item, stackPtr, false);
        return stackPtr;
    }

    /// <summary>
    /// Writes an empty result to the output NativeHostResult.
    /// </summary>
    private static void WriteOutputEmpty(IntPtr output)
    {
        if (output == IntPtr.Zero) return;
        var result = new NativeHostResult
        {
            StackPtr = IntPtr.Zero,
            StackLen = 0,
            ErrorPtr = IntPtr.Zero,
            ErrorLen = 0,
        };
        Marshal.StructureToPtr(result, output, false);
    }

    /// <summary>
    /// Writes an error message to the output NativeHostResult.
    /// </summary>
    private static void WriteOutputError(IntPtr output, string message)
    {
        if (output == IntPtr.Zero) return;
        var errorBytes = Encoding.UTF8.GetBytes(message);
        var errorPtr = Marshal.AllocHGlobal(errorBytes.Length);
        Marshal.Copy(errorBytes, 0, errorPtr, errorBytes.Length);

        var result = new NativeHostResult
        {
            StackPtr = IntPtr.Zero,
            StackLen = 0,
            ErrorPtr = errorPtr,
            ErrorLen = (nuint)errorBytes.Length,
        };
        Marshal.StructureToPtr(result, output, false);
    }

    /// <summary>
    /// Tracks allocated NativeStackItem arrays for cleanup.
    /// </summary>
    private static readonly List<IntPtr> s_allocatedArrays = new();
    private static readonly object s_allocLock = new();
    private static readonly ConcurrentDictionary<IntPtr, GCHandle> s_pinnedByteHandles = new();
    private static readonly IntPtr s_cachedIntZeroStackPtr = CreateCachedSingleStackItem(0, 0);
    private static readonly IntPtr s_cachedNullStackPtr = CreateCachedSingleStackItem(2, 0);
    private static readonly IntPtr s_cachedBoolTrueStackPtr = CreateCachedSingleStackItem(3, 1);
    private static readonly IntPtr s_cachedBoolFalseStackPtr = CreateCachedSingleStackItem(3, 0);

    /// <summary>
    /// Native callback implementation that dispatches to TestHostCallback.
    /// </summary>
    private static bool TestHostCallbackImpl(
        IntPtr userData,
        uint api,
        nuint instructionPointer,
        byte trigger,
        uint networkMagic,
        byte addressVersion,
        ulong persistingTimestamp,
        long gasLeft,
        IntPtr inputStackPtr,
        nuint inputStackLen,
        IntPtr output)
    {
        var host = (TestHostCallback)GCHandle.FromIntPtr(userData).Target!;
        host.CalledApis.Add(api);

        switch (api)
        {
            // --- Storage syscalls ---
            case ApiIds.StorageGetContext:
            case ApiIds.StorageGetReadOnlyContext:
                // Return a dummy context (integer 0)
                WriteOutputSingle(output, 0, 0, null);
                return true;

            case ApiIds.StorageAsReadOnly:
                // Return the same context (no-op)
                WriteOutputSingle(output, 0, 0, null);
                return true;

            case ApiIds.StorageGet:
                // input: [ByteString(key)] -> returns ByteString(value) or null
                {
                    var keyItem = ReadInputItem(inputStackPtr, inputStackLen, 0);
                    if (IsByteStringItem(keyItem) &&
                        host.TryGetStorageValue(keyItem, out var storedValue))
                    {
                        WriteOutputSingle(output, 1, 0, storedValue);
                    }
                    else
                    {
                        // Key not found — return null
                        WriteOutputSingle(output, 2, 0, null);
                    }
                }
                return true;

            case ApiIds.StoragePut:
                // input: [context, ByteString(key), ByteString(value)]
                if (TryReadInputBytes(inputStackPtr, inputStackLen, 1, out var key) &&
                    TryReadInputBytes(inputStackPtr, inputStackLen, 2, out var value))
                {
                    host.PutStorageValue(key, value);
                }
                else if (TryReadInputBytes(inputStackPtr, inputStackLen, 0, out key) &&
                         TryReadInputBytes(inputStackPtr, inputStackLen, 1, out value))
                {
                    // Some contracts pass [key, value] without context
                    host.PutStorageValue(key, value);
                }
                WriteOutputEmpty(output);
                return true;

            case ApiIds.StorageDelete:
                // input: [context, ByteString(key)]
                {
                    var keyItem = ReadInputItem(inputStackPtr, inputStackLen, 1);
                    if (IsByteStringItem(keyItem))
                        host.RemoveStorageValue(keyItem);
                    else
                    {
                        keyItem = ReadInputItem(inputStackPtr, inputStackLen, 0);
                        if (IsByteStringItem(keyItem))
                            host.RemoveStorageValue(keyItem);
                    }
                }
                WriteOutputEmpty(output);
                return true;

            case ApiIds.StorageFind:
                // Return an empty iterator handle
                WriteOutputSingle(output, 6, 0, null);
                return true;

            // --- Storage.Local syscalls ---
            case ApiIds.StorageLocalGet:
                // input: [id, ByteString(key)]
                {
                    var keyItem = ReadInputItem(inputStackPtr, inputStackLen, 1);
                    if (IsByteStringItem(keyItem) &&
                        host.TryGetStorageValue(keyItem, out var val))
                        WriteOutputSingle(output, 1, 0, val);
                    else
                        WriteOutputEmpty(output);
                }
                return true;

            case ApiIds.StorageLocalPut:
                // input: [id, ByteString(key), ByteString(value)]
                if (TryReadInputBytes(inputStackPtr, inputStackLen, 1, out key) &&
                    TryReadInputBytes(inputStackPtr, inputStackLen, 2, out value))
                {
                    host.PutStorageValue(key, value);
                }
                WriteOutputEmpty(output);
                return true;

            case ApiIds.StorageLocalDelete:
                // input: [id, ByteString(key)]
                {
                    var keyItem = ReadInputItem(inputStackPtr, inputStackLen, 1);
                    if (IsByteStringItem(keyItem))
                        host.RemoveStorageValue(keyItem);
                }
                WriteOutputEmpty(output);
                return true;

            case ApiIds.StorageLocalFind:
                // Return an empty iterator handle
                WriteOutputSingle(output, 6, 0, null);
                return true;

            // --- Runtime syscalls ---
            case ApiIds.RuntimeCheckWitness:
                // Always return true in tests
                WriteOutputSingle(output, 3, 1, null);
                return true;

            case ApiIds.RuntimeLog:
            {
                var input = ReadInputStack(inputStackPtr, inputStackLen);
                if (input.Length >= 1)
                {
                    var msgBytes = ReadItemBytes(input[0]);
                    host.Logs.Add(Encoding.UTF8.GetString(msgBytes));
                }
                WriteOutputEmpty(output);
                return true;
            }

            case ApiIds.RuntimeNotify:
            {
                var input = ReadInputStack(inputStackPtr, inputStackLen);
                // input can be [scriptHash, eventName, Array(args)] or [eventName, Array(args)]
                if (input.Length >= 3)
                {
                    var hashBytes = ReadItemBytes(input[0]);
                    var eventName = Encoding.UTF8.GetString(ReadItemBytes(input[1]));
                    var args = new ResultStackItem[input.Length - 2];
                    for (var i = 2; i < input.Length; i++)
                    {
                        args[i - 2] = new ResultStackItem
                        {
                            Kind = input[i].Kind,
                            IntegerValue = input[i].IntegerValue,
                            Bytes = input[i].Kind == 1 || input[i].Kind == 5 ? ReadItemBytes(input[i]) : null,
                        };
                    }
                    host.Notifications.Add(new NotificationRecord
                    {
                        ScriptHash = hashBytes,
                        EventName = eventName,
                        Args = args,
                    });
                }
                else if (input.Length >= 2)
                {
                    var eventName = Encoding.UTF8.GetString(ReadItemBytes(input[0]));
                    var args = new ResultStackItem[input.Length - 1];
                    for (var i = 1; i < input.Length; i++)
                    {
                        args[i - 1] = new ResultStackItem
                        {
                            Kind = input[i].Kind,
                            IntegerValue = input[i].IntegerValue,
                            Bytes = input[i].Kind == 1 || input[i].Kind == 5 ? ReadItemBytes(input[i]) : null,
                        };
                    }
                    host.Notifications.Add(new NotificationRecord
                    {
                        ScriptHash = Array.Empty<byte>(),
                        EventName = eventName,
                        Args = args,
                    });
                }
                WriteOutputEmpty(output);
                return true;
            }

            case ApiIds.RuntimeGetTrigger:
                // TriggerType.Application = 0x40
                WriteOutputSingle(output, 0, 0x40, null);
                return true;

            case ApiIds.RuntimeGetNetwork:
                WriteOutputSingle(output, 0, 860833102, null);
                return true;

            case ApiIds.RuntimeGetAddressVersion:
                WriteOutputSingle(output, 0, 53, null);
                return true;

            case ApiIds.RuntimeGetTime:
                WriteOutputSingle(output, 0, 0, null);
                return true;

            case ApiIds.RuntimeGasLeft:
                WriteOutputSingle(output, 0, 10_000_000_000_000L, null);
                return true;

            case ApiIds.RuntimePlatform:
                var platBytes = Encoding.UTF8.GetBytes("NEO");
                WriteOutputSingle(output, 1, 0, platBytes);
                return true;

            case ApiIds.RuntimeGetExecutingScriptHash:
            case ApiIds.RuntimeGetCallingScriptHash:
            case ApiIds.RuntimeGetEntryScriptHash:
                // Return 20 zero bytes as a script hash
                WriteOutputSingle(output, 1, 0, new byte[20]);
                return true;

            // --- Iterator syscalls ---
            case ApiIds.IteratorNext:
                // Return false (no more items)
                WriteOutputSingle(output, 3, 0, null);
                return true;

            case ApiIds.IteratorValue:
                WriteOutputSingle(output, 2, 0, null);
                return true;

            default:
                // Unknown syscall — return empty to avoid fault
                WriteOutputEmpty(output);
                return true;
        }
    }

    /// <summary>
    /// Free callback for memory allocated by TestHostCallbackImpl.
    /// </summary>
    private static void TestHostFreeImpl(IntPtr userData, IntPtr result)
    {
        if (result == IntPtr.Zero) return;

        var nativeResult = Marshal.PtrToStructure<NativeHostResult>(result);

        // Free the stack items and their byte arrays
        if (nativeResult.StackPtr != IntPtr.Zero && nativeResult.StackLen > 0)
        {
            var itemSize = Marshal.SizeOf<NativeStackItem>();
            for (var i = 0; i < (int)nativeResult.StackLen; i++)
            {
                var itemPtr = IntPtr.Add(nativeResult.StackPtr, i * itemSize);
                var item = Marshal.PtrToStructure<NativeStackItem>(itemPtr);
                if (item.BytesPtr != IntPtr.Zero)
                {
                    if (s_pinnedByteHandles.TryRemove(item.BytesPtr, out var bytesHandle))
                        bytesHandle.Free();
                    else
                        Marshal.FreeHGlobal(item.BytesPtr);
                }
            }
            if (nativeResult.StackPtr != s_cachedIntZeroStackPtr &&
                nativeResult.StackPtr != s_cachedNullStackPtr &&
                nativeResult.StackPtr != s_cachedBoolTrueStackPtr &&
                nativeResult.StackPtr != s_cachedBoolFalseStackPtr)
            {
                Marshal.FreeHGlobal(nativeResult.StackPtr);
            }
        }

        if (nativeResult.ErrorPtr != IntPtr.Zero)
            Marshal.FreeHGlobal(nativeResult.ErrorPtr);
    }

    /// <summary>
    /// Equality comparer for byte[] keys in the storage dictionary.
    /// </summary>
    private sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayEqualityComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (x == y) return true;
            if (x == null || y == null) return false;
            if (x.Length != y.Length) return false;
            for (var i = 0; i < x.Length; i++)
                if (x[i] != y[i]) return false;
            return true;
        }

        public int GetHashCode(byte[] obj)
        {
            var hash = 17;
            foreach (var b in obj)
                hash = hash * 31 + b;
            return hash;
        }
    }

    // ---------------------------------------------------------------
    //  Stack reading helpers
    // ---------------------------------------------------------------

    private static ResultStackItem[] ReadResultStack(IntPtr stackPtr, nuint stackLen)
    {
        if (stackPtr == IntPtr.Zero || stackLen == 0)
            return Array.Empty<ResultStackItem>();

        var count = (int)stackLen;
        var items = new ResultStackItem[count];
        var itemSize = Marshal.SizeOf<NativeStackItem>();

        for (var i = 0; i < count; i++)
        {
            var itemPtr = IntPtr.Add(stackPtr, i * itemSize);
            var native = Marshal.PtrToStructure<NativeStackItem>(itemPtr);
            items[i] = DecodeStackItem(native);
        }

        return items;
    }

    private static ResultStackItem DecodeStackItem(NativeStackItem native)
    {
        byte[]? bytes = null;
        ResultStackItem[]? children = null;

        switch (native.Kind)
        {
            case 4: // Array
            case 7: // Struct
            case 8: // Map
                // BytesPtr points to nested NativeStackItem array, BytesLen is the count.
                children = ReadResultStack(native.BytesPtr, native.BytesLen);
                break;

            case 1: // ByteString
            case 5: // BigInteger (stored as little-endian bytes)
                if (native.BytesPtr != IntPtr.Zero && native.BytesLen > 0)
                {
                    bytes = new byte[(int)native.BytesLen];
                    Marshal.Copy(native.BytesPtr, bytes, 0, bytes.Length);
                }
                else
                {
                    bytes = Array.Empty<byte>();
                }
                break;

            // 0=Integer, 2=Null, 3=Boolean, 6=Iterator, 9=Interop
            // These use IntegerValue only; no BytesPtr payload.
        }

        return new ResultStackItem
        {
            Kind = native.Kind,
            IntegerValue = native.IntegerValue,
            Bytes = bytes,
            Children = children,
        };
    }

    // ---------------------------------------------------------------
    //  Library path resolution
    // ---------------------------------------------------------------

    private static string? FindNativeLibrary()
    {
        var envPath = Environment.GetEnvironmentVariable("NEO_RISCV_HOST_LIB");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        foreach (var path in EnumerateNativeLibraryCandidates())
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
                return full;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateNativeLibraryCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "libneo_riscv_host.so");
        yield return Path.Combine(AppContext.BaseDirectory, "Plugins", "Neo.Riscv.Adapter", "libneo_riscv_host.so");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in EnumerateNativeLibrarySearchRoots())
        {
            foreach (var candidate in new[]
            {
                Path.Combine(root, "target", "release", "libneo_riscv_host.so"),
                Path.Combine(root, "target", "debug", "libneo_riscv_host.so"),
                Path.Combine(root, "dist", "Plugins", "Neo.Riscv.Adapter", "libneo_riscv_host.so"),
                Path.Combine(root, "neo-riscv-vm", "target", "release", "libneo_riscv_host.so"),
                Path.Combine(root, "neo-riscv-vm", "target", "debug", "libneo_riscv_host.so"),
                Path.Combine(root, "neo-riscv-vm", "dist", "Plugins", "Neo.Riscv.Adapter", "libneo_riscv_host.so"),
                Path.Combine(root, "neo-riscv-core", "tests", "Neo.UnitTests", "Plugins", "Neo.Riscv.Adapter", "libneo_riscv_host.so"),
            })
            {
                var full = Path.GetFullPath(candidate);
                if (seen.Add(full))
                    yield return full;
            }
        }
    }

    private static IEnumerable<string> EnumerateNativeLibrarySearchRoots()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = Path.GetFullPath(start);
            while (!string.IsNullOrEmpty(directory))
            {
                yield return directory;

                var parent = Path.GetDirectoryName(directory);
                if (string.IsNullOrEmpty(parent) || parent == directory)
                    break;

                directory = parent;
            }
        }
    }
}
