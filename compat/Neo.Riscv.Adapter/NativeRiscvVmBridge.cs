// Copyright (C) 2015-2026 The Neo Project.
//
// NativeRiscvVmBridge.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.

using Neo.VM;
using Neo.VM.Types;
using Neo.Network.P2P.Payloads;
using Neo.SmartContract;
using Neo.SmartContract.Iterators;
using Neo.SmartContract.Native;
using Neo.Cryptography.ECC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Neo.SmartContract.RiscV
{
    public sealed class NativeRiscvVmBridge : IRiscvVmBridge, IDisposable
    {
        public const string LibraryPathEnvironmentVariable = "NEO_RISCV_HOST_LIB";
        private const string TraceEnvironmentVariable = "NEO_RISCV_TRACE_HOST";
        private static readonly byte[] StorageContextTokenMagic = [0x4E, 0x52, 0x53, 0x43];

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeExecutionResult
        {
            public long FeeConsumedPico;
            public uint State;
            public IntPtr StackPtr;
            public nuint StackLen;
            public IntPtr ErrorPtr;
            public nuint ErrorLen;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeStackItem
        {
            public uint Kind;
            public long IntegerValue;
            public IntPtr BytesPtr;
            public nuint BytesLen;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeHostResult
        {
            public IntPtr StackPtr;
            public nuint StackLen;
            public IntPtr ErrorPtr;
            public nuint ErrorLen;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool ExecuteNativeContractDelegate(
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
            IntPtr userData,
            IntPtr hostCallback,
            IntPtr hostFree,
            out NativeExecutionResult result);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool ExecuteScriptDelegate(
            IntPtr scriptPtr,
            nuint scriptLen,
            nuint initialInstructionPointer,
            byte trigger,
            uint networkMagic,
            byte addressVersion,
            ulong persistingTimestamp,
            long gasLeft,
            long execFeeFactorPico,
            IntPtr initialStackPtr,
            nuint initialStackLen,
            IntPtr userData,
            IntPtr hostCallback,
            IntPtr hostFree,
            out NativeExecutionResult result);

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
            IntPtr inputStackPtr,
            nuint inputStackLen,
            out NativeHostResult result);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void HostFreeCallbackDelegate(IntPtr userData, ref NativeHostResult result);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeExecutionResultDelegate(ref NativeExecutionResult result);

        private sealed class HostCallbackState
        {
            public required NativeRiscvVmBridge Bridge { get; init; }

            public required RiscvExecutionRequest Request { get; init; }

            public required ExecutionScope Scope { get; init; }
        }

        private sealed class ExecutionScope
        {
            public Dictionary<ulong, IIterator> Iterators { get; } = new();

            public Dictionary<ulong, object> InteropObjects { get; } = new();

            public ulong NextIteratorHandle { get; set; } = 1;

            public ulong NextInteropHandle { get; set; } = 1;
        }

        private IntPtr _libraryHandle;
        private readonly ExecuteScriptDelegate _executeScript;
        private readonly ExecuteNativeContractDelegate _executeNativeContract;
        private readonly FreeExecutionResultDelegate _freeExecutionResult;
        private readonly HostCallbackDelegate _hostCallback;
        private readonly HostFreeCallbackDelegate _hostFreeCallback;
        private readonly IntPtr _hostCallbackPtr;
        private readonly IntPtr _hostFreeCallbackPtr;

        private static bool TraceEnabled =>
            string.Equals(Environment.GetEnvironmentVariable(TraceEnvironmentVariable), "1", StringComparison.Ordinal);

        public NativeRiscvVmBridge(string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath))
                throw new ArgumentException("Library path is required.", nameof(libraryPath));

            _hostCallback = StaticHostCallback;
            _hostFreeCallback = StaticHostFreeCallback;
            _hostCallbackPtr = Marshal.GetFunctionPointerForDelegate(_hostCallback);
            _hostFreeCallbackPtr = Marshal.GetFunctionPointerForDelegate(_hostFreeCallback);

            _libraryHandle = NativeLibrary.Load(libraryPath);
            var executeExport = NativeLibrary.GetExport(_libraryHandle, "neo_riscv_execute_script_with_host");
            var nativeContractExport = NativeLibrary.GetExport(_libraryHandle, "neo_riscv_execute_native_contract");
            var freeExport = NativeLibrary.GetExport(_libraryHandle, "neo_riscv_free_execution_result");
            _executeScript = Marshal.GetDelegateForFunctionPointer<ExecuteScriptDelegate>(executeExport);
            _executeNativeContract = Marshal.GetDelegateForFunctionPointer<ExecuteNativeContractDelegate>(nativeContractExport);
            _freeExecutionResult = Marshal.GetDelegateForFunctionPointer<FreeExecutionResultDelegate>(freeExport);
        }

        private static void Trace(string message)
        {
            if (!TraceEnabled) return;
            Console.Error.WriteLine($"[neo-riscv] {message}");
        }

        public RiscvExecutionResult Execute(RiscvExecutionRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (request.Scripts.Count == 0) throw new InvalidOperationException("No script loaded for RISC-V execution.");
            var script = request.Scripts[^1];
            var contractType = request.ContractTypes.Count == request.Scripts.Count
                ? request.ContractTypes[^1]
                : ContractType.NeoVM;
            var executionKind = RiscvExecutionDispatcher.Resolve(contractType, script);
            var scope = new ExecutionScope();

            return executionKind switch
            {
                RiscvExecutionKind.LegacyNeoVmCompatibility =>
                    ExecuteScriptInternal(request, script, request.InitialStack.ToArray(), request.InitialInstructionPointer, scope),
                RiscvExecutionKind.NativeRiscvDirect =>
                    ExecuteNativeContractInternal(request, script, request.InitialStack.ToArray(), request.Method ?? throw new InvalidOperationException("Method is required for native RISC-V contract execution."), scope),
                _ => throw new InvalidOperationException($"Unsupported execution kind: {executionKind}."),
            };
        }

        public RiscvExecutionResult ExecuteContract(
            ApplicationEngine engine,
            ContractState contract,
            string method,
            CallFlags flags,
            IReadOnlyList<StackItem> args)
        {
            if (engine is null) throw new ArgumentNullException(nameof(engine));
            if (contract is null) throw new ArgumentNullException(nameof(contract));
            if (method is null) throw new ArgumentNullException(nameof(method));

            var script = contract.Script.ToArray();
            var contractHash = contract.Hash;
            var contractType = contract.Type;
            var executionFacadeHash = RiscvCompatibilityContracts.ResolveExecutionFacadeHash(contractType, contractHash);

            // Look up the method descriptor to find the offset
            var descriptor = contract.Manifest.Abi.GetMethod(method, args.Count)
                ?? throw new InvalidOperationException(
                    $"Method \"{method}\" with {args.Count} parameter(s) doesn't exist in the contract {contractHash}.");

            // Build initial stack: args in reverse order (top-of-stack = last arg)
            var initialStack = new StackItem[args.Count];
            for (var i = 0; i < args.Count; i++)
            {
                initialStack[i] = args[args.Count - 1 - i];
            }

            var request = new RiscvExecutionRequest(
                engine,
                engine.Trigger,
                engine.ProtocolSettings.Network,
                engine.ProtocolSettings.AddressVersion,
                engine.PersistingBlock?.Timestamp ?? 0,
                engine.GasLeft,
                flags,
                new[] { script },
                new[] { contractHash },
                new[] { contractType },
                new[] { executionFacadeHash },
                initialStack,
                descriptor.Offset,
                method);

            return Execute(request);
        }

        private static bool StaticHostCallback(
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
            out NativeHostResult result)
        {
            result = default;
            try
            {
                Trace($"callback raw-enter api=0x{api:x8} ip={instructionPointer} inputStackLen={inputStackLen}");
                var handle = GCHandle.FromIntPtr(userData);
                var state = (HostCallbackState)handle.Target!;
                StackItem[] inputStack;
                try
                {
                    inputStack = state.Bridge.ReadStack(inputStackPtr, inputStackLen, state.Request.Engine.ReferenceCounter, state.Scope, decodeStorageContextTokens: false);
                    Trace($"callback read-stack api=0x{api:x8} managedStackLen={inputStack.Length}");
                }
                catch (Exception ex)
                {
                    Trace($"callback read-stack fault api=0x{api:x8} type={ex.GetType().FullName} message={ex.Message}");
                    result = CreateNativeHostError(ex);
                    return true;
                }
                return state.Bridge.HandleHostCallback(state.Request, state.Scope, api, instructionPointer, gasLeft, inputStack, out result);
            }
            catch (Exception ex)
            {
                Trace($"callback outer fault api=0x{api:x8} type={ex.GetType().FullName} message={ex.Message}");
                result = CreateNativeHostError(ex);
                return true;
            }
        }

        /// Marker for CALLT tokens sent through the syscall channel.
        /// Upper 16 bits = 0x4354 ("CT"), lower 16 bits = token_id.
        /// Must match the Rust-side constant in neo-riscv-guest.
        private const uint CalltMarkerHi = 0x4354;

        private bool HandleHostCallback(RiscvExecutionRequest request, ExecutionScope scope, uint api, nuint instructionPointer, long gasLeft, StackItem[] inputStack, out NativeHostResult result)
        {
            try
            {
                // Handle CALLT tokens: upper 16 bits = 0x4354 means this is a method token.
                if ((api >> 16) == CalltMarkerHi)
                {
                    var calltToken = (ushort)(api & 0xFFFF);
                    var calltGasLeft = gasLeft - (request.GasLeft - request.Engine.GasLeft);
                    var calltResult = HandleCallT(request, scope, calltGasLeft, calltToken, inputStack);
                    result = CreateNativeHostResult(calltResult, scope);
                    return true;
                }

                var descriptor = ApplicationEngine.GetInteropDescriptor(api);
                Trace($"syscall enter name={descriptor.Name} api=0x{api:x8} ip={instructionPointer} gasLeft={gasLeft} stackLen={inputStack.Length}");
                if (!request.CurrentCallFlags.HasFlag(descriptor.RequiredCallFlags))
                    throw new InvalidOperationException($"Cannot call this SYSCALL with the flag {request.CurrentCallFlags}.");
                if (descriptor.FixedPrice != 0)
                    request.Engine.AddFee(descriptor.FixedPrice * request.Engine.ExecFeePicoFactor);
                var effectiveGasLeft = gasLeft - (request.GasLeft - request.Engine.GasLeft);

                var stack = api switch
                {
                    uint hash when hash == ApplicationEngine.System_Runtime_Platform =>
                        Append(inputStack, new ByteString(Encoding.UTF8.GetBytes("NEO"))),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetTrigger =>
                        Append(inputStack, new Integer((int)request.Trigger)),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetNetwork =>
                        Append(inputStack, new Integer(request.NetworkMagic)),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetAddressVersion =>
                        Append(inputStack, new Integer(request.AddressVersion)),
                    uint hash when hash == ApplicationEngine.System_Runtime_GasLeft =>
                        Append(inputStack, new Integer(effectiveGasLeft)),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetRandom =>
                        Append(inputStack, new Integer(request.Engine.GetRandom())),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetScriptContainer =>
                        Append(inputStack, CreateScriptContainerItem(request)),
                    uint hash when hash == ApplicationEngine.System_Storage_GetContext =>
                        Append(inputStack, CreateStorageContextItem(request.Engine.GetStorageContext())),
                    uint hash when hash == ApplicationEngine.System_Storage_GetReadOnlyContext =>
                        Append(inputStack, CreateStorageContextItem(request.Engine.GetReadOnlyContext())),
                    uint hash when hash == ApplicationEngine.System_Storage_AsReadOnly =>
                        HandleStorageAsReadOnly(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Storage_Local_Get =>
                        HandleStorageLocalGet(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Storage_Local_Find =>
                        HandleStorageLocalFind(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Storage_Local_Put =>
                        HandleStorageLocalPut(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Storage_Local_Delete =>
                        HandleStorageLocalDelete(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Storage_Find =>
                        HandleStorageFind(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Storage_Get =>
                        HandleStorageGet(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Storage_Put =>
                        HandleStoragePut(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Storage_Delete =>
                        HandleStorageDelete(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Crypto_CheckSig =>
                        HandleCryptoCheckSig(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Crypto_CheckMultisig =>
                        HandleCryptoCheckMultisig(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Contract_NativeOnPersist =>
                        HandleNativeOnPersist(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Contract_NativePostPersist =>
                        HandleNativePostPersist(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Contract_CallNative =>
                        HandleContractCallNative(request, instructionPointer, inputStack),
                    uint hash when hash == ApplicationEngine.System_Contract_Call =>
                        HandleContractCall(request, scope, effectiveGasLeft, inputStack),
                    uint hash when hash == ApplicationEngine.System_Contract_GetCallFlags =>
                        Append(inputStack, new Integer((int)request.CurrentCallFlags)),
                    uint hash when hash == ApplicationEngine.System_Contract_CreateStandardAccount =>
                        HandleCreateStandardAccount(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Contract_CreateMultisigAccount =>
                        HandleCreateMultisigAccount(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Iterator_Next =>
                        HandleIteratorNext(inputStack),
                    uint hash when hash == ApplicationEngine.System_Iterator_Value =>
                        HandleIteratorValue(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetInvocationCounter =>
                        HandleGetInvocationCounter(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Runtime_CurrentSigners =>
                        Append(inputStack, request.Engine.Convert(request.Engine.GetCurrentSigners()) ?? StackItem.Null),
                    uint hash when hash == ApplicationEngine.System_Runtime_BurnGas =>
                        HandleBurnGas(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Runtime_CheckWitness =>
                        HandleCheckWitness(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetCallingScriptHash && request.Scripts.Count > 1 =>
                        Append(inputStack, new ByteString(request.ScriptHashes[^2].GetSpan().ToArray())),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetCallingScriptHash =>
                        Append(inputStack, StackItem.Null),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetNotifications =>
                        HandleGetNotifications(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetTime && request.PersistingTimestamp != 0 =>
                        Append(inputStack, new Integer(request.PersistingTimestamp)),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetTime =>
                        throw new InvalidOperationException("GetTime requires a persisting block timestamp."),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetExecutingScriptHash =>
                        Append(inputStack, new ByteString(request.ScriptHashes[^1].GetSpan().ToArray())),
                    uint hash when hash == ApplicationEngine.System_Runtime_GetEntryScriptHash =>
                        Append(inputStack, new ByteString(request.ScriptHashes[0].GetSpan().ToArray())),
                    uint hash when hash == ApplicationEngine.System_Runtime_LoadScript =>
                        HandleRuntimeLoadScript(request, scope, effectiveGasLeft, inputStack),
                    uint hash when hash == ApplicationEngine.System_Runtime_Notify =>
                        HandleRuntimeNotify(request, inputStack),
                    uint hash when hash == ApplicationEngine.System_Runtime_Log =>
                        HandleRuntimeLog(request, inputStack),
                    _ => throw new InvalidOperationException($"Unsupported syscall 0x{api:x8}.")
                };

                Trace($"syscall exit name={descriptor.Name} api=0x{api:x8} resultStackLen={stack.Length}");
                result = CreateNativeHostResult(stack, scope);
                return true;
            }
            catch (Exception ex)
            {
                Trace($"syscall fault api=0x{api:x8} type={ex.GetType().FullName} message={ex.Message}");
                result = CreateNativeHostError(ex);
                return true;
            }
        }

        private static StackItem[] Append(StackItem[] inputStack, StackItem item)
        {
            var next = new StackItem[inputStack.Length + 1];
            System.Array.Copy(inputStack, next, inputStack.Length);
            next[^1] = item;
            return next;
        }

        private static StackItem[] HandleRuntimeLog(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length == 0)
                throw new InvalidOperationException("Runtime.Log requires one argument.");

            if (inputStack[^1] is not ByteString message)
                throw new InvalidOperationException("Runtime.Log requires a byte string argument.");

            request.Engine.RuntimeLog(message.GetSpan().ToArray());

            var next = new StackItem[inputStack.Length - 1];
            System.Array.Copy(inputStack, next, next.Length);
            return next;
        }

        private static StackItem[] HandleRuntimeNotify(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length < 2)
                throw new InvalidOperationException("Runtime.Notify requires event name and state.");

            if (inputStack[^1] is not ByteString eventName)
                throw new InvalidOperationException("Runtime.Notify requires a byte string event name.");
            if (inputStack[^2] is not Neo.VM.Types.Array state)
                throw new InvalidOperationException("Runtime.Notify requires an array state.");

            request.Engine.RuntimeNotify(eventName.GetSpan().ToArray(), state);

            var next = new StackItem[inputStack.Length - 2];
            if (next.Length > 0)
            {
                System.Array.Copy(inputStack, next, next.Length);
            }
            return next;
        }

        private StackItem[] HandleGetInvocationCounter(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            var count = request.ScriptHashes.Count(scriptHash => scriptHash.Equals(request.ScriptHashes[^1]));
            var contract = NativeContract.ContractManagement.GetContract(request.Engine.SnapshotCache, request.ScriptHashes[^1]);
            if (contract is not null)
            {
                var engineCount = request.Engine.ExecuteInNativeContractContext(
                    request.ScriptHashes[^1],
                    request.ScriptHashes.Count > 1 ? request.ScriptHashes[^2] : null,
                    contract,
                    request.CurrentCallFlags,
                    () => request.Engine.GetInvocationCounter());
                count = Math.Max(count, engineCount);
            }
            return Append(inputStack, new Integer(count));
        }

        private StackItem[] HandleRuntimeLoadScript(RiscvExecutionRequest request, ExecutionScope scope, long gasLeft, StackItem[] inputStack)
        {
            if (inputStack.Length < 3)
                throw new InvalidOperationException("Runtime.LoadScript requires script, call flags, and args.");

            if (inputStack[^1] is not ByteString scriptItem)
                throw new InvalidOperationException("Runtime.LoadScript requires a byte string script.");
            if (inputStack[^2] is not Integer callFlagsItem)
                throw new InvalidOperationException("Runtime.LoadScript requires integer call flags.");
            if (inputStack[^3] is not Neo.VM.Types.Array argsArray)
                throw new InvalidOperationException("Runtime.LoadScript requires an array of arguments.");

            var callFlags = (CallFlags)(byte)callFlagsItem.GetInteger();
            if ((callFlags & ~CallFlags.All) != 0)
                throw new InvalidOperationException($"Invalid call flags: {callFlags}");

            var nestedScript = scriptItem.GetSpan().ToArray();
            var nextScripts = request.Scripts.Concat(new[] { nestedScript }).ToArray();
            var nextHashes = request.ScriptHashes.Concat(new[] { nestedScript.ToScriptHash() }).ToArray();
            var nextContractTypes = request.ContractTypes.Concat(new[]
            {
                nestedScript.Length >= 4 && nestedScript[0] == 0x50 && nestedScript[1] == 0x56 && nestedScript[2] == 0x4D && nestedScript[3] == 0x00
                    ? ContractType.RiscV
                    : ContractType.NeoVM
            }).ToArray();
            var nextExecutionFacadeHashes = nextContractTypes
                .Zip(nextHashes, (contractType, scriptHash) =>
                    RiscvCompatibilityContracts.ResolveExecutionFacadeHash(contractType, scriptHash))
                .ToArray();
            var nestedRequest = new RiscvExecutionRequest(
                request.Engine,
                request.Trigger,
                request.NetworkMagic,
                request.AddressVersion,
                request.PersistingTimestamp,
                gasLeft,
                callFlags & request.CurrentCallFlags & CallFlags.ReadOnly,
                nextScripts,
                nextHashes,
                nextContractTypes,
                nextExecutionFacadeHashes);

            var nestedInitialStack = new StackItem[argsArray.Count];
            for (var index = 0; index < argsArray.Count; index++)
            {
                nestedInitialStack[index] = argsArray[argsArray.Count - index - 1];
            }

            var nestedResult = ExecuteScriptInternal(nestedRequest, nestedScript, nestedInitialStack, 0, scope);
            if (nestedResult.State != VMState.HALT)
                throw nestedResult.FaultException ?? new InvalidOperationException("Runtime.LoadScript failed.");

            var next = new StackItem[inputStack.Length - 3 + nestedResult.ResultStack.Count];
            if (inputStack.Length > 3)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 3);
            }
            for (var index = 0; index < nestedResult.ResultStack.Count; index++)
            {
                next[inputStack.Length - 3 + index] = nestedResult.ResultStack[index];
            }
            return next;
        }

        private static StackItem[] HandleGetNotifications(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length == 0)
                throw new InvalidOperationException("Runtime.GetNotifications requires one argument.");

            UInt160? hash = inputStack[^1] switch
            {
                Null => null,
                ByteString bytes when bytes.GetSpan().Length == UInt160.Length => new UInt160(bytes.GetSpan()),
                _ => throw new InvalidOperationException("Runtime.GetNotifications requires null or a UInt160 byte string.")
            };

            var notifications = request.Engine.GetNotifications(hash);
            var next = new StackItem[inputStack.Length];
            if (inputStack.Length > 1)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 1);
            }
            next[^1] = notifications;
            return next;
        }

        private static StackItem[] HandleCheckWitness(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length == 0)
                throw new InvalidOperationException("Runtime.CheckWitness requires one argument.");

            if (inputStack[^1] is not ByteString data)
                throw new InvalidOperationException("Runtime.CheckWitness requires a byte string argument.");

            var next = new StackItem[inputStack.Length];
            if (inputStack.Length > 1)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 1);
            }

            next[^1] = request.Engine.CheckWitness(data.GetSpan().ToArray()) ? StackItem.True : StackItem.False;
            return next;
        }

        private static StackItem CreateScriptContainerItem(RiscvExecutionRequest request)
        {
            if (request.Engine.ScriptContainer is null)
                return request.Engine.GetScriptContainer();

            return request.Engine.Convert(request.Engine.ScriptContainer);
        }

        private RiscvExecutionResult ExecuteScriptInternal(RiscvExecutionRequest request, byte[] script, StackItem[] initialStack, int initialInstructionPointer, ExecutionScope scope)
        {
            var scriptPtr = Marshal.AllocHGlobal(script.Length);
            NativeExecutionResult nativeResult = default;
            var callbackState = new HostCallbackState { Bridge = this, Request = request, Scope = scope };
            var callbackHandle = GCHandle.Alloc(callbackState);
            var initialState = CreateNativeHostResult(initialStack, scope);

            try
            {
                Marshal.Copy(script, 0, scriptPtr, script.Length);
                if (!_executeScript(
                        scriptPtr,
                        (nuint)script.Length,
                        (nuint)initialInstructionPointer,
                        (byte)request.Trigger,
                        request.NetworkMagic,
                        request.AddressVersion,
                        request.PersistingTimestamp,
                        request.GasLeft,
                        checked((long)request.Engine.ExecFeePicoFactor),
                        initialState.StackPtr,
                        initialState.StackLen,
                        GCHandle.ToIntPtr(callbackHandle),
                        _hostCallbackPtr,
                        _hostFreeCallbackPtr,
                        out nativeResult))
                    throw new InvalidOperationException("Native RISC-V ABI call failed.");

                var stack = ReadStack(nativeResult.StackPtr, nativeResult.StackLen, request.Engine.ReferenceCounter, scope, decodeStorageContextTokens: true);
                if (TraceEnabled)
                {
                    for (var i = 0; i < stack.Length; i++)
                        Trace($"execute result[{i}] type={stack[i].GetType().Name} value={DescribeStackItem(stack[i])}");
                }
                // Reconcile Rust-side opcode fees with C# engine.
                // During syscall callbacks, C# already called AddFee for syscall fixed prices,
                // reducing Engine.GasLeft. We must subtract what C# already charged to avoid
                // double-counting. AddFee works in "internal pico" units (datoshi * FeeFactor),
                // while GasLeft is in datoshi. Conversion: pico = datoshi * FeeFactor.
                var gasSpentByCallbacksDatoshi = request.GasLeft - request.Engine.GasLeft;
                if (gasSpentByCallbacksDatoshi < 0) gasSpentByCallbacksDatoshi = 0;
                var gasSpentByCallbacksPico = (BigInteger)gasSpentByCallbacksDatoshi * ApplicationEngine.FeeFactor;
                var adjustedFeePico = nativeResult.FeeConsumedPico - gasSpentByCallbacksPico;
                if (adjustedFeePico > 0)
                {
                    request.Engine.AddFee(adjustedFeePico);
                }
                var state = nativeResult.State == 0 ? VMState.HALT : VMState.FAULT;
                var faultMessage = nativeResult.ErrorPtr == IntPtr.Zero
                    ? "Native Neo RISC-V execution fault."
                    : Marshal.PtrToStringUTF8(nativeResult.ErrorPtr, checked((int)nativeResult.ErrorLen)) ?? "Native Neo RISC-V execution fault.";
                var fault = state == VMState.FAULT ? RehydrateNativeException(faultMessage) : null;
                if (state == VMState.FAULT)
                {
                    Trace($"execute fault state={state} message={faultMessage}");
                }

                return new RiscvExecutionResult(state, stack, fault);
            }
            finally
            {
                StaticHostFreeCallback(IntPtr.Zero, ref initialState);
                if (nativeResult.StackPtr != IntPtr.Zero)
                {
                    _freeExecutionResult(ref nativeResult);
                }
                if (callbackHandle.IsAllocated)
                {
                    callbackHandle.Free();
                }
                Marshal.FreeHGlobal(scriptPtr);
            }
        }

        private RiscvExecutionResult ExecuteNativeContractInternal(RiscvExecutionRequest request, byte[] binary, StackItem[] initialStack, string method, ExecutionScope scope)
        {
            var binaryPtr = Marshal.AllocHGlobal(binary.Length);
            var methodBytes = Encoding.UTF8.GetBytes(method);
            var methodPtr = Marshal.AllocHGlobal(methodBytes.Length);
            NativeExecutionResult nativeResult = default;
            var callbackState = new HostCallbackState { Bridge = this, Request = request, Scope = scope };
            var callbackHandle = GCHandle.Alloc(callbackState);
            var initialState = CreateNativeHostResult(initialStack, scope);

            try
            {
                Marshal.Copy(binary, 0, binaryPtr, binary.Length);
                Marshal.Copy(methodBytes, 0, methodPtr, methodBytes.Length);
                if (!_executeNativeContract(
                        binaryPtr,
                        (nuint)binary.Length,
                        methodPtr,
                        (nuint)methodBytes.Length,
                        initialState.StackPtr,
                        initialState.StackLen,
                        (byte)request.Trigger,
                        request.NetworkMagic,
                        request.AddressVersion,
                        request.PersistingTimestamp,
                        request.GasLeft,
                        checked((long)request.Engine.ExecFeePicoFactor),
                        GCHandle.ToIntPtr(callbackHandle),
                        _hostCallbackPtr,
                        _hostFreeCallbackPtr,
                        out nativeResult))
                    throw new InvalidOperationException("Native RISC-V contract execution failed.");

                var stack = ReadStack(nativeResult.StackPtr, nativeResult.StackLen, request.Engine.ReferenceCounter, scope, decodeStorageContextTokens: true);
                if (TraceEnabled)
                {
                    for (var i = 0; i < stack.Length; i++)
                        Trace($"native execute result[{i}] type={stack[i].GetType().Name} value={DescribeStackItem(stack[i])}");
                }
                // Same gas reconciliation as ExecuteScriptInternal — subtract what C# already
                // charged during callbacks to avoid double-counting.
                var nativeGasSpentByCallbacksDatoshi = request.GasLeft - request.Engine.GasLeft;
                if (nativeGasSpentByCallbacksDatoshi < 0) nativeGasSpentByCallbacksDatoshi = 0;
                var nativeGasSpentByCallbacksPico = (BigInteger)nativeGasSpentByCallbacksDatoshi * ApplicationEngine.FeeFactor;
                var nativeAdjustedFeePico = nativeResult.FeeConsumedPico - nativeGasSpentByCallbacksPico;
                if (nativeAdjustedFeePico > 0)
                {
                    request.Engine.AddFee(nativeAdjustedFeePico);
                }
                var state = nativeResult.State == 0 ? VMState.HALT : VMState.FAULT;
                var faultMessage = nativeResult.ErrorPtr == IntPtr.Zero
                    ? "Native RISC-V contract execution fault."
                    : Marshal.PtrToStringUTF8(nativeResult.ErrorPtr, checked((int)nativeResult.ErrorLen)) ?? "Native RISC-V contract execution fault.";
                var fault = state == VMState.FAULT ? RehydrateNativeException(faultMessage) : null;
                if (state == VMState.FAULT)
                {
                    Trace($"native contract fault state={state} message={faultMessage}");
                }

                return new RiscvExecutionResult(state, stack, fault);
            }
            finally
            {
                StaticHostFreeCallback(IntPtr.Zero, ref initialState);
                if (nativeResult.StackPtr != IntPtr.Zero)
                {
                    _freeExecutionResult(ref nativeResult);
                }
                if (callbackHandle.IsAllocated)
                {
                    callbackHandle.Free();
                }
                Marshal.FreeHGlobal(methodPtr);
                Marshal.FreeHGlobal(binaryPtr);
            }
        }

        private static StackItem CreateStorageContextItem(StorageContext context)
        {
            var payload = new byte[StorageContextTokenMagic.Length + sizeof(int) + 1];
            System.Array.Copy(StorageContextTokenMagic, payload, StorageContextTokenMagic.Length);
            System.BitConverter.GetBytes(context.Id).CopyTo(payload, StorageContextTokenMagic.Length);
            payload[^1] = context.IsReadOnly ? (byte)1 : (byte)0;
            return new ByteString(payload);
        }

        private static StackItem[] HandleBurnGas(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length == 0)
                throw new InvalidOperationException("Runtime.BurnGas requires one argument.");

            if (inputStack[^1] is not Integer amount)
                throw new InvalidOperationException("Runtime.BurnGas requires an integer argument.");

            request.Engine.BurnGas((long)amount.GetInteger());

            var next = new StackItem[inputStack.Length - 1];
            if (next.Length > 0)
            {
                System.Array.Copy(inputStack, next, next.Length);
            }
            return next;
        }

        private static StackItem[] HandleStorageGet(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length < 2)
                throw new InvalidOperationException("Storage.Get requires context and key.");

            var context = ParseStorageContext(inputStack[^2]);
            if (inputStack[^1] is not ByteString key)
                throw new InvalidOperationException("Storage.Get requires a byte string key.");

            var value = request.Engine.Get(context, key.GetSpan().ToArray());
            var next = new StackItem[inputStack.Length - 1];
            if (inputStack.Length > 2)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 2);
            }
            next[^1] = value.HasValue ? new ByteString(value.Value) : StackItem.Null;
            return next;
        }

        private static StackItem[] HandleStorageLocalGet(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length == 0)
                throw new InvalidOperationException("Storage.Local.Get requires a key.");

            if (inputStack[^1] is not ByteString key)
                throw new InvalidOperationException("Storage.Local.Get requires a byte string key.");

            var value = request.Engine.GetLocal(key.GetSpan().ToArray());
            var next = new StackItem[inputStack.Length];
            if (inputStack.Length > 1)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 1);
            }
            next[^1] = value.HasValue ? new ByteString(value.Value) : StackItem.Null;
            return next;
        }

        private static StackItem[] HandleStorageLocalFind(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length < 2)
                throw new InvalidOperationException("Storage.Local.Find requires prefix and options.");

            if (inputStack[^2] is not ByteString prefix)
                throw new InvalidOperationException("Storage.Local.Find requires a byte string prefix.");
            if (inputStack[^1] is not Integer options)
                throw new InvalidOperationException("Storage.Local.Find requires integer options.");

            var iterator = request.Engine.FindLocal(prefix.GetSpan().ToArray(), (FindOptions)(byte)options.GetInteger());
            var next = new StackItem[inputStack.Length - 1];
            if (inputStack.Length > 2)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 2);
            }
            next[^1] = StackItem.FromInterface(iterator);
            return next;
        }

        private static StackItem[] HandleStorageAsReadOnly(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length == 0)
                throw new InvalidOperationException("Storage.AsReadOnly requires one argument.");

            var context = ParseStorageContext(inputStack[^1]);
            var next = new StackItem[inputStack.Length];
            if (inputStack.Length > 1)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 1);
            }
            next[^1] = CreateStorageContextItem(ApplicationEngine.AsReadOnly(context));
            return next;
        }

        private static StackItem[] HandleStoragePut(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length < 3)
                throw new InvalidOperationException("Storage.Put requires context, key, and value.");

            var context = ParseStorageContext(inputStack[^3]);
            if (inputStack[^2] is not ByteString key)
                throw new InvalidOperationException("Storage.Put requires a byte string key.");
            if (inputStack[^1] is not ByteString value)
                throw new InvalidOperationException("Storage.Put requires a byte string value.");

            request.Engine.Put(context, key.GetSpan().ToArray(), value.GetSpan().ToArray());

            var next = new StackItem[inputStack.Length - 3];
            if (next.Length > 0)
            {
                System.Array.Copy(inputStack, next, next.Length);
            }
            return next;
        }

        private static StackItem[] HandleStorageLocalPut(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length < 2)
                throw new InvalidOperationException("Storage.Local.Put requires key and value.");

            if (inputStack[^2] is not ByteString key)
                throw new InvalidOperationException("Storage.Local.Put requires a byte string key.");
            if (inputStack[^1] is not ByteString value)
                throw new InvalidOperationException("Storage.Local.Put requires a byte string value.");

            request.Engine.PutLocal(key.GetSpan().ToArray(), value.GetSpan().ToArray());

            var next = new StackItem[inputStack.Length - 2];
            if (next.Length > 0)
            {
                System.Array.Copy(inputStack, next, next.Length);
            }
            return next;
        }

        private static StackItem[] HandleStorageDelete(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length < 2)
                throw new InvalidOperationException("Storage.Delete requires context and key.");

            var context = ParseStorageContext(inputStack[^2]);
            if (inputStack[^1] is not ByteString key)
                throw new InvalidOperationException("Storage.Delete requires a byte string key.");

            request.Engine.Delete(context, key.GetSpan().ToArray());

            var next = new StackItem[inputStack.Length - 2];
            if (next.Length > 0)
            {
                System.Array.Copy(inputStack, next, next.Length);
            }
            return next;
        }

        private static StackItem[] HandleStorageLocalDelete(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length == 0)
                throw new InvalidOperationException("Storage.Local.Delete requires a key.");

            if (inputStack[^1] is not ByteString key)
                throw new InvalidOperationException("Storage.Local.Delete requires a byte string key.");

            request.Engine.DeleteLocal(key.GetSpan().ToArray());

            var next = new StackItem[inputStack.Length - 1];
            if (next.Length > 0)
            {
                System.Array.Copy(inputStack, next, next.Length);
            }
            return next;
        }

        private static StackItem[] HandleStorageFind(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length < 3)
                throw new InvalidOperationException("Storage.Find requires context, prefix, and options.");

            var context = ParseStorageContext(inputStack[^3]);
            if (inputStack[^2] is not ByteString prefix)
                throw new InvalidOperationException("Storage.Find requires a byte string prefix.");
            if (inputStack[^1] is not Integer options)
                throw new InvalidOperationException("Storage.Find requires integer options.");

            var iterator = request.Engine.Find(context, prefix.GetSpan().ToArray(), (FindOptions)(byte)options.GetInteger());
            var next = new StackItem[inputStack.Length - 2];
            if (inputStack.Length > 3)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 3);
            }
            next[^1] = StackItem.FromInterface(iterator);
            return next;
        }

        private static StackItem[] HandleIteratorNext(StackItem[] inputStack)
        {
            if (inputStack.Length == 0)
                throw new InvalidOperationException("Iterator.Next requires an iterator.");

            if (inputStack[^1] is not InteropInterface interop || interop.GetInterface<object>() is not IIterator iterator)
                throw new InvalidOperationException("Iterator.Next requires an iterator handle.");

            var next = new StackItem[inputStack.Length];
            if (inputStack.Length > 1)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 1);
            }
            next[^1] = iterator.Next() ? StackItem.True : StackItem.False;
            return next;
        }

        private static StackItem[] HandleIteratorValue(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length == 0)
                throw new InvalidOperationException("Iterator.Value requires an iterator.");

            if (inputStack[^1] is not InteropInterface interop || interop.GetInterface<object>() is not IIterator iterator)
                throw new InvalidOperationException("Iterator.Value requires an iterator handle.");

            var next = new StackItem[inputStack.Length];
            if (inputStack.Length > 1)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 1);
            }
            next[^1] = iterator.Value(request.Engine.ReferenceCounter);
            return next;
        }

        private static StorageContext ParseStorageContext(StackItem item)
        {
            if (item is ByteString encoded && TryParseStorageContextToken(encoded.GetSpan(), out var encodedContext))
                return encodedContext;

            if (item is not Neo.VM.Types.Array array || array.Count != 2)
                throw new InvalidOperationException("Storage context must be a two-item array.");

            return new StorageContext
            {
                Id = (int)array[0].GetInteger(),
                IsReadOnly = array[1].GetBoolean(),
            };
        }

        private static bool TryParseStorageContextToken(ReadOnlySpan<byte> bytes, out StorageContext context)
        {
            context = default;
            if (bytes.Length != StorageContextTokenMagic.Length + sizeof(int) + 1)
                return false;
            if (!bytes[..StorageContextTokenMagic.Length].SequenceEqual(StorageContextTokenMagic))
                return false;

            context = new StorageContext
            {
                Id = System.BitConverter.ToInt32(bytes.Slice(StorageContextTokenMagic.Length, sizeof(int))),
                IsReadOnly = bytes[^1] != 0,
            };
            return true;
        }

        private static StackItem CreateStorageContextArray(StorageContext context)
        {
            return new Neo.VM.Types.Array(new StackItem[]
            {
                new Integer(context.Id),
                context.IsReadOnly ? StackItem.True : StackItem.False,
            });
        }

        private static StackItem[] HandleCryptoCheckSig(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length < 2)
                throw new InvalidOperationException("CheckSig requires pubkey and signature.");

            if (inputStack[^1] is not ByteString pubkey)
                throw new InvalidOperationException("CheckSig requires a byte string public key.");
            if (inputStack[^2] is not ByteString signature)
                throw new InvalidOperationException("CheckSig requires a byte string signature.");

            var next = new StackItem[inputStack.Length - 1];
            if (inputStack.Length > 2)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 2);
            }
            next[^1] = request.Engine.CheckSig(pubkey.GetSpan().ToArray(), signature.GetSpan().ToArray()) ? StackItem.True : StackItem.False;
            return next;
        }

        private static StackItem[] HandleCryptoCheckMultisig(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length < 2)
                throw new InvalidOperationException("CheckMultisig requires pubkeys and signatures.");

            byte[][] pubKeys;
            byte[][] signatures;
            var consumed = 0;

            if (inputStack[^1] is Neo.VM.Types.Array pubKeysArray && inputStack[^2] is Neo.VM.Types.Array signaturesArray)
            {
                pubKeys = new byte[pubKeysArray.Count][];
                for (var index = 0; index < pubKeysArray.Count; index++)
                {
                    if (pubKeysArray[index] is not ByteString pubKey)
                        throw new InvalidOperationException("CheckMultisig public keys must be byte strings.");
                    pubKeys[index] = pubKey.GetSpan().ToArray();
                }

                signatures = new byte[signaturesArray.Count][];
                for (var index = 0; index < signaturesArray.Count; index++)
                {
                    if (signaturesArray[index] is not ByteString signature)
                        throw new InvalidOperationException("CheckMultisig signatures must be byte strings.");
                    signatures[index] = signature.GetSpan().ToArray();
                }

                consumed = 2;
            }
            else
            {
                if (inputStack[^1] is not Integer pubKeyCountItem)
                    throw new InvalidOperationException("CheckMultisig requires a public key count.");

                var pubKeyCount = checked((int)pubKeyCountItem.GetInteger());
                if (pubKeyCount <= 0 || inputStack.Length < pubKeyCount + 2)
                    throw new InvalidOperationException("CheckMultisig public key count is invalid.");

                var signatureCountIndex = inputStack.Length - 2 - pubKeyCount;
                if (signatureCountIndex < 0 || inputStack[signatureCountIndex] is not Integer signatureCountItem)
                    throw new InvalidOperationException("CheckMultisig requires a signature count.");

                var signatureCount = checked((int)signatureCountItem.GetInteger());
                if (signatureCount <= 0 || signatureCountIndex < signatureCount)
                    throw new InvalidOperationException("CheckMultisig signature count is invalid.");

                pubKeys = new byte[pubKeyCount][];
                for (var index = 0; index < pubKeyCount; index++)
                {
                    if (inputStack[inputStack.Length - 2 - index] is not ByteString pubKey)
                        throw new InvalidOperationException("CheckMultisig public keys must be byte strings.");
                    pubKeys[pubKeyCount - index - 1] = pubKey.GetSpan().ToArray();
                }

                signatures = new byte[signatureCount][];
                for (var index = 0; index < signatureCount; index++)
                {
                    if (inputStack[signatureCountIndex - index - 1] is not ByteString signature)
                        throw new InvalidOperationException("CheckMultisig signatures must be byte strings.");
                    signatures[signatureCount - index - 1] = signature.GetSpan().ToArray();
                }

                consumed = pubKeyCount + signatureCount + 2;
            }

            var next = new StackItem[inputStack.Length - consumed + 1];
            if (inputStack.Length > consumed)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - consumed);
            }
            next[^1] = request.Engine.CheckMultisig(pubKeys, signatures) ? StackItem.True : StackItem.False;
            return next;
        }

        private static StackItem[] HandleNativeOnPersist(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (request.Trigger != TriggerType.OnPersist)
                throw new InvalidOperationException();

            foreach (var contract in NativeContract.Contracts)
            {
                if (!contract.IsActive(request.Engine.ProtocolSettings, request.Engine.PersistingBlock!.Index))
                    continue;

                CompleteContractTask(contract.OnPersistAsync(request.Engine), "NativeOnPersist");
            }
            return inputStack;
        }

        private static StackItem[] HandleNativePostPersist(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (request.Trigger != TriggerType.PostPersist)
                throw new InvalidOperationException();

            foreach (var contract in NativeContract.Contracts)
            {
                if (!contract.IsActive(request.Engine.ProtocolSettings, request.Engine.PersistingBlock!.Index))
                    continue;

                CompleteContractTask(contract.PostPersistAsync(request.Engine), "NativePostPersist");
            }
            return inputStack;
        }

        private static void CompleteContractTask(ContractTask task, string operation)
        {
            if (!task.GetAwaiter().IsCompleted)
                throw new InvalidOperationException($"{operation} returned an incomplete asynchronous native task.");

            task.GetResult();
        }

        private static StackItem[] HandleContractCallNative(RiscvExecutionRequest request, nuint instructionPointer, StackItem[] inputStack)
        {
            if (inputStack.Length == 0)
                throw new InvalidOperationException("Contract.CallNative requires a version.");

            if (inputStack[^1] is not Integer versionItem)
                throw new InvalidOperationException("Contract.CallNative requires an integer version.");

            var currentContract = NativeContract.GetContract(request.ScriptHashes[^1])
                ?? throw new InvalidOperationException("It is not allowed to use \"System.Contract.CallNative\" directly.");
            if (!currentContract.IsActive(request.Engine.ProtocolSettings, NativeContract.Ledger.CurrentIndex(request.Engine.SnapshotCache)))
                throw new InvalidOperationException($"The native contract {currentContract.Name} is not active.");

            var versionRaw = versionItem.GetInteger();
            if (versionRaw != 0)
                throw new InvalidOperationException($"The native contract of version {versionRaw} is not active.");

            var currentAllowedMethods = currentContract.GetContractMethods(request.Engine);
            if (!currentAllowedMethods.TryGetValue(checked((int)instructionPointer), out var method))
                throw new InvalidOperationException($"No native method is available at instruction pointer {instructionPointer} for contract {currentContract.Name}.");
            if (method.ActiveIn is not null && !request.Engine.IsHardforkEnabled(method.ActiveIn.Value))
                throw new InvalidOperationException($"Cannot call this method before hardfork {method.ActiveIn}.");
            if (method.DeprecatedIn is not null && request.Engine.IsHardforkEnabled(method.DeprecatedIn.Value))
                throw new InvalidOperationException($"Cannot call this method after hardfork {method.DeprecatedIn}.");
            if (!request.CurrentCallFlags.HasFlag(method.RequiredCallFlags))
                throw new InvalidOperationException($"Cannot call this method with the flag {request.CurrentCallFlags}.");

            if (!request.Engine.IsHardforkEnabled(Hardfork.HF_Faun) ||
                !NativeContract.Policy.IsWhitelistFeeContract(request.Engine.SnapshotCache, currentContract.Hash, method.Descriptor, out var fixedFee))
            {
                request.Engine.AddFee(
                    (method.CpuFee * request.Engine.ExecFeePicoFactor) +
                    (method.StorageFee * request.Engine.StoragePrice * ApplicationEngine.FeeFactor));
            }

            var parameterCount = method.Parameters.Length;
            if (inputStack.Length < parameterCount + 1)
                throw new InvalidOperationException($"Native contract method \"{method.Descriptor.Name}\" expects {parameterCount} argument(s).");

            var parameters = new List<object?>();
            if (method.NeedApplicationEngine) parameters.Add(request.Engine);
            if (method.NeedSnapshot) parameters.Add(request.Engine.SnapshotCache);
            for (var index = 0; index < parameterCount; index++)
            {
                parameters.Add(request.Engine.Convert(inputStack[inputStack.Length - 2 - index], method.Parameters[index]));
            }

            var currentContractState = NativeContract.ContractManagement.GetContract(request.Engine.SnapshotCache, currentContract.Hash)
                ?? currentContract.GetContractState(request.Engine.ProtocolSettings, request.Engine.PersistingBlock?.Index ?? NativeContract.Ledger.CurrentIndex(request.Engine.SnapshotCache));

            object? returnValue = request.Engine.ExecuteInNativeContractContext(
                currentContract.Hash,
                request.ScriptHashes.Count > 1 ? request.ScriptHashes[^2] : null,
                currentContractState,
                request.CurrentCallFlags,
                () => method.Handler.Invoke(currentContract, parameters.ToArray()));
            if (returnValue is ContractTask task)
            {
                if (!task.GetAwaiter().IsCompleted)
                {
                    // Execute pending user contract contexts so ContextUnloaded completes the task.
                    request.Engine.Execute();
                }
                returnValue = task.GetResult();
            }

            var pushed = method.Descriptor.ReturnType != ContractParameterType.Void
                ? request.Engine.Convert(returnValue)
                : StackItem.Null;
            var next = new StackItem[inputStack.Length - parameterCount - 1 + 1];
            if (inputStack.Length > parameterCount + 1)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - parameterCount - 1);
            }
            next[^1] = pushed;
            return next;
        }

        private static StackItem[] HandleCreateStandardAccount(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length == 0)
                throw new InvalidOperationException("CreateStandardAccount requires a public key.");

            if (inputStack[^1] is not ByteString pubKeyBytes)
                throw new InvalidOperationException("CreateStandardAccount requires a byte string public key.");

            var pubKey = ECPoint.DecodePoint(pubKeyBytes.GetSpan().ToArray(), ECCurve.Secp256r1);
            var next = new StackItem[inputStack.Length];
            if (inputStack.Length > 1)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 1);
            }
            next[^1] = new ByteString(request.Engine.CreateStandardAccount(pubKey).GetSpan().ToArray());
            return next;
        }

        private static StackItem[] HandleCreateMultisigAccount(RiscvExecutionRequest request, StackItem[] inputStack)
        {
            if (inputStack.Length < 2)
                throw new InvalidOperationException("CreateMultisigAccount requires m and public keys.");

            if (inputStack[^1] is not Integer mValue)
                throw new InvalidOperationException("CreateMultisigAccount requires integer m.");
            if (inputStack[^2] is not Integer countValue)
                throw new InvalidOperationException("CreateMultisigAccount requires integer public key count.");

            var count = (int)countValue.GetInteger();
            if (count < 0 || inputStack.Length < count + 2)
                throw new InvalidOperationException("CreateMultisigAccount public key count is invalid.");

            var pubKeys = new ECPoint[count];
            for (var index = 0; index < count; index++)
            {
                var item = inputStack[inputStack.Length - 3 - index];
                if (item is not ByteString pubKeyBytes)
                    throw new InvalidOperationException("CreateMultisigAccount requires byte string public keys.");
                pubKeys[count - index - 1] = ECPoint.DecodePoint(pubKeyBytes.GetSpan().ToArray(), ECCurve.Secp256r1);
            }

            var next = new StackItem[inputStack.Length - count - 1];
            if (next.Length > 0)
            {
                System.Array.Copy(inputStack, next, next.Length - 1);
            }
            next[^1] = new ByteString(request.Engine.CreateMultisigAccount((int)mValue.GetInteger(), pubKeys).GetSpan().ToArray());
            return next;
        }

        private StackItem[] HandleCallT(RiscvExecutionRequest request, ExecutionScope scope, long gasLeft, ushort token, StackItem[] inputStack)
        {
            // Resolve the method token from the current contract's NEF.
            var currentHash = request.ScriptHashes[^1];
            var contractState = NativeContract.ContractManagement.GetContract(request.Engine.SnapshotCache, currentHash)
                ?? throw new InvalidOperationException($"CALLT: current contract {currentHash} not found in storage.");
            if (token >= contractState.Nef.Tokens.Length)
                throw new InvalidOperationException($"CALLT: token {token} out of range (contract {currentHash} has {contractState.Nef.Tokens.Length} tokens).");

            var methodToken = contractState.Nef.Tokens[token];
            Trace($"callt enter token={token} target={methodToken.Hash} method={methodToken.Method} params={methodToken.ParametersCount} callFlags={methodToken.CallFlags}");

            // NOTE: CALLT opcode fee is already charged by the guest via host_on_instruction.
            // Do NOT call AddFee here — that would double-charge.

            // Pop the method's parameter count from the stack and pack into an Array.
            if (inputStack.Length < methodToken.ParametersCount)
                throw new InvalidOperationException($"CALLT: stack has {inputStack.Length} items but method {methodToken.Method} expects {methodToken.ParametersCount} parameters.");

            var argsArray = new Neo.VM.Types.Array();
            for (var i = 0; i < methodToken.ParametersCount; i++)
                argsArray.Add(inputStack[inputStack.Length - 1 - i]);

            // Build the equivalent System.Contract.Call stack: [remaining..., argsArray, callFlags, method, hash]
            var callStack = new StackItem[inputStack.Length - methodToken.ParametersCount + 4];
            if (inputStack.Length > methodToken.ParametersCount)
                System.Array.Copy(inputStack, callStack, inputStack.Length - methodToken.ParametersCount);

            callStack[^4] = argsArray;
            callStack[^3] = new Integer((int)methodToken.CallFlags);
            callStack[^2] = new ByteString(Encoding.UTF8.GetBytes(methodToken.Method));
            callStack[^1] = new ByteString(methodToken.Hash.GetSpan().ToArray());

            return HandleContractCall(request, scope, gasLeft, callStack);
        }

        private StackItem[] HandleContractCall(RiscvExecutionRequest request, ExecutionScope scope, long gasLeft, StackItem[] inputStack)
        {
            if (inputStack.Length < 4)
                throw new InvalidOperationException("Contract.Call requires hash, method, call flags, and args.");

            if (inputStack[^1] is not ByteString hashBytes || hashBytes.GetSpan().Length != UInt160.Length)
                throw new InvalidOperationException("Contract.Call requires a contract hash.");
            if (inputStack[^2] is not ByteString methodBytes)
                throw new InvalidOperationException("Contract.Call requires a method name.");
            if (inputStack[^3] is not Integer callFlagsItem)
                throw new InvalidOperationException("Contract.Call requires integer call flags.");
            if (inputStack[^4] is not Neo.VM.Types.Array argsArray)
                throw new InvalidOperationException("Contract.Call requires an argument array.");

            var contractHash = new UInt160(hashBytes.GetSpan());
            var method = methodBytes.GetString() ?? throw new InvalidOperationException("Method name must be valid UTF-8.");
            var callFlags = (CallFlags)(byte)callFlagsItem.GetInteger();
            if ((callFlags & ~CallFlags.All) != 0)
                throw new InvalidOperationException($"Invalid call flags: {callFlags}");
            Trace($"contract.call enter hash={contractHash} method={method} stackLen={inputStack.Length} args={argsArray.Count}");

            // Delegate ALL contract calls to the standard NeoVM path via CallContractInternal.
            // This uses the EXACT same code path as NeoVM (context creation, _initialize handling,
            // static field setup, snapshot clone, ContextUnloaded commit), which is critical for
            // state root compatibility. The RISC-V guest only executes the outermost transaction script.
            if (request.Engine is RiscvApplicationEngine riscvEngine)
            {
                var contractState = NativeContract.ContractManagement.GetContract(request.Engine.SnapshotCache, contractHash)
                    ?? NativeContract.GetContract(contractHash)?.GetContractState(request.Engine.ProtocolSettings, request.Engine.PersistingBlock?.Index ?? 0);
                if (contractState == null)
                    throw new InvalidOperationException($"Called Contract Does Not Exist: {contractHash}.{method}");
                var md = contractState.Manifest.Abi.GetMethod(method, argsArray.Count);
                var hasReturnValue = md is { ReturnType: not ContractParameterType.Void };
                var args = new StackItem[argsArray.Count];
                for (var i = 0; i < argsArray.Count; i++)
                    args[i] = argsArray[i];

                var stackDepth = riscvEngine.InvocationStack.Count;
                riscvEngine.CallContractInternal(contractHash, method, callFlags, hasReturnValue, args);
                var state = riscvEngine.ExecuteUntilStackDepth(stackDepth);

                if (state == VMState.FAULT)
                    throw riscvEngine.FaultException ?? new InvalidOperationException($"Contract call failed: {contractHash}.{method}");

                var resultItem = hasReturnValue && riscvEngine.CurrentContext?.EvaluationStack.Count > 0
                    ? riscvEngine.Pop()
                    : StackItem.Null;
                Trace($"contract.call exit hash={contractHash} method={method} result={DescribeStackItem(resultItem)}");
                var callResult = new StackItem[inputStack.Length - 4 + 1];
                if (inputStack.Length > 4)
                    System.Array.Copy(inputStack, callResult, inputStack.Length - 4);
                callResult[^1] = resultItem;
                return callResult;
            }

            // Fallback for non-RISC-V engines (shouldn't normally reach here)
            var contract = NativeContract.ContractManagement.GetContract(request.Engine.SnapshotCache, contractHash)
                ?? throw new InvalidOperationException($"Called Contract Does Not Exist: {contractHash}.{method}");
            request.Engine.IncrementInvocationCounter(contract.Hash);
            var descriptor = contract.Manifest.Abi.GetMethod(method, argsArray.Count)
                ?? throw new InvalidOperationException($"Method \"{method}\" with {argsArray.Count} parameter(s) doesn't exist in the contract {contractHash}.");
            if (NativeContract.Policy.IsBlocked(request.Engine.SnapshotCache, contract.Hash))
                throw new InvalidOperationException($"The contract {contract.Hash} has been blocked.");
            if (!descriptor.Safe)
            {
                var currentState = request.Engine.CurrentContext!.GetState<ExecutionContextState>();
                var executingContract = request.Engine.IsHardforkEnabled(Hardfork.HF_Domovoi)
                    ? currentState.Contract
                    : NativeContract.ContractManagement.GetContract(request.Engine.SnapshotCache, request.Engine.CurrentScriptHash!);
                if (executingContract?.CanCall(contract, descriptor.Name) == false)
                    throw new InvalidOperationException($"Cannot Call Method {descriptor.Name} Of Contract {contract.Hash} From Contract {request.Engine.CurrentScriptHash}");
            }
            var calleeScript = contract.Script.ToArray();
            var nestedScripts = request.Scripts.Concat(new[] { calleeScript }).ToArray();
            var nestedContractTypes = request.ContractTypes.Concat(new[] { contract.Type }).ToArray();
            var nestedScriptHashes = request.ScriptHashes.Concat(new[] { contract.Hash }).ToArray();
            var nestedExecutionFacadeHashes = request.ExecutionFacadeHashes
                .Concat(new[] { RiscvCompatibilityContracts.ResolveExecutionFacadeHash(contract.Type, contract.Hash) })
                .ToArray();
            var nestedRequest = new RiscvExecutionRequest(
                request.Engine,
                request.Trigger,
                request.NetworkMagic,
                request.AddressVersion,
                request.PersistingTimestamp,
                gasLeft,
                ((descriptor.Safe ? callFlags & ~(CallFlags.WriteStates | CallFlags.AllowNotify) : callFlags) & request.CurrentCallFlags),
                nestedScripts,
                nestedScriptHashes,
                nestedContractTypes,
                nestedExecutionFacadeHashes);
            var nestedInitialStack = new StackItem[argsArray.Count];
            for (var index = 0; index < argsArray.Count; index++)
            {
                nestedInitialStack[index] = argsArray[argsArray.Count - index - 1];
            }

            var nestedResult = request.Engine.ExecuteInNativeContractContext(
                contract.Hash,
                request.ScriptHashes[^1],
                contract,
                nestedRequest.CurrentCallFlags,
                () => contract.Type == ContractType.RiscV
                    ? ExecuteNativeContractInternal(nestedRequest, calleeScript, nestedInitialStack, method, scope)
                    : ExecuteScriptInternal(nestedRequest, calleeScript, nestedInitialStack, descriptor.Offset, scope));
            if (nestedResult.State != VMState.HALT)
                throw nestedResult.FaultException ?? new InvalidOperationException("Contract.Call failed.");
            Trace($"contract.call nested exit method={method} resultCount={nestedResult.ResultStack.Count}");

            var returnedCount = nestedResult.ResultStack.Count == 0 ? 1 : nestedResult.ResultStack.Count;
            var next = new StackItem[inputStack.Length - 4 + returnedCount];
            if (inputStack.Length > 4)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 4);
            }
            if (nestedResult.ResultStack.Count == 0)
            {
                next[^1] = StackItem.Null;
            }
            for (var index = 0; index < nestedResult.ResultStack.Count; index++)
            {
                next[inputStack.Length - 4 + index] = nestedResult.ResultStack[index];
            }
            return next;
        }

        private static string DescribeStackItem(StackItem item)
        {
            return item switch
            {
                ByteString bytes => $"bytes:{Convert.ToHexString(bytes.GetSpan())}",
                Integer integer => $"int:{integer.GetInteger()}",
                Neo.VM.Types.Boolean boolean => $"bool:{boolean.GetBoolean()}",
                Null => "null",
                Neo.VM.Types.Struct @struct => $"struct:{@struct.Count}",
                Neo.VM.Types.Array array => $"array:{array.Count}",
                Neo.VM.Types.Map map => $"map:{map.Count}",
                InteropInterface => "interop",
                _ => item.GetType().Name,
            };
        }

        private StackItem[] HandleNativeContractCall(
            RiscvExecutionRequest request,
            StackItem[] inputStack,
            ContractState contractState,
            NativeContract nativeContract,
            string methodName,
            CallFlags callFlags,
            Neo.VM.Types.Array argsArray)
        {
            var method = nativeContract
                .GetContractMethods(request.Engine)
                .Values
                .FirstOrDefault(candidate => candidate.Descriptor.Name == methodName && candidate.Parameters.Length == argsArray.Count)
                ?? throw new InvalidOperationException($"Method \"{methodName}\" with {argsArray.Count} parameter(s) doesn't exist in the native contract {nativeContract.Hash}.");

            if (method.ActiveIn is not null && !request.Engine.IsHardforkEnabled(method.ActiveIn.Value))
                throw new InvalidOperationException($"Cannot call this method before hardfork {method.ActiveIn}.");
            if (method.DeprecatedIn is not null && request.Engine.IsHardforkEnabled(method.DeprecatedIn.Value))
                throw new InvalidOperationException($"Cannot call this method after hardfork {method.DeprecatedIn}.");

            var effectiveFlags = callFlags;
            if (method.Descriptor.Safe)
                effectiveFlags &= ~(CallFlags.WriteStates | CallFlags.AllowNotify);
            effectiveFlags &= request.CurrentCallFlags;

            if (!effectiveFlags.HasFlag(method.RequiredCallFlags))
                throw new InvalidOperationException($"Cannot call this method with the flag {effectiveFlags}.");

            if (!request.Engine.IsHardforkEnabled(Hardfork.HF_Faun) ||
                !NativeContract.Policy.IsWhitelistFeeContract(request.Engine.SnapshotCache, nativeContract.Hash, method.Descriptor, out var fixedFee))
            {
                request.Engine.AddFee(
                    (method.CpuFee * request.Engine.ExecFeePicoFactor) +
                    (method.StorageFee * request.Engine.StoragePrice * ApplicationEngine.FeeFactor));
            }
            else
            {
                request.Engine.AddFee(fixedFee!.Value * ApplicationEngine.FeeFactor);
            }

            var parameters = new List<object?>();
            if (method.NeedApplicationEngine) parameters.Add(request.Engine);
            if (method.NeedSnapshot) parameters.Add(request.Engine.SnapshotCache);
            for (var index = 0; index < method.Parameters.Length; index++)
                parameters.Add(request.Engine.Convert(argsArray[index], method.Parameters[index]));

            object? returnValue = request.Engine.ExecuteInNativeContractContext(
                nativeContract.Hash,
                request.ScriptHashes[^1],
                contractState,
                effectiveFlags,
                () => method.Handler.Invoke(nativeContract, parameters.ToArray()));
            if (returnValue is ContractTask task)
            {
                if (!task.GetAwaiter().IsCompleted)
                {
                    if (request.Engine is RiscvApplicationEngine riscvEngine)
                    {
                        riscvEngine.FlagNeoVMMode();
                        riscvEngine.Execute();
                        riscvEngine.ClearNeoVMMode();
                    }
                }
                returnValue = task.GetResult();
            }

            var pushed = method.Descriptor.ReturnType != ContractParameterType.Void
                ? request.Engine.Convert(returnValue)
                : StackItem.Null;

            var next = new StackItem[inputStack.Length - 4 + 1];
            if (inputStack.Length > 4)
            {
                System.Array.Copy(inputStack, next, inputStack.Length - 4);
            }
            next[^1] = pushed;
            return next;
        }

        private static void StaticHostFreeCallback(IntPtr userData, ref NativeHostResult result)
        {
            if (result.StackPtr != IntPtr.Zero)
            {
                FreeNativeStackItems(result.StackPtr, (int)result.StackLen);
                result.StackPtr = IntPtr.Zero;
                result.StackLen = 0;
            }

            if (result.ErrorPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(result.ErrorPtr);
                result.ErrorPtr = IntPtr.Zero;
                result.ErrorLen = 0;
            }
        }

        private static void FreeNativeStackItems(IntPtr stackPtr, int stackLen)
        {
            if (stackPtr == IntPtr.Zero) return;
            var itemSize = Marshal.SizeOf<NativeStackItem>();
            for (var index = 0; index < stackLen; index++)
            {
                var itemPtr = IntPtr.Add(stackPtr, index * itemSize);
                var item = Marshal.PtrToStructure<NativeStackItem>(itemPtr);
                if (item.BytesPtr != IntPtr.Zero)
                {
                    // Kind 4 (Array), 7 (Struct), 8 (Map): BytesPtr points to nested NativeStackItem array
                    if (item.Kind == 4 || item.Kind == 7 || item.Kind == 8)
                    {
                        FreeNativeStackItems(item.BytesPtr, (int)item.BytesLen);
                    }
                    else
                    {
                        Marshal.FreeHGlobal(item.BytesPtr);
                    }
                }
            }
            Marshal.FreeHGlobal(stackPtr);
        }

        private NativeHostResult CreateNativeHostResult(StackItem[] stack, ExecutionScope scope)
        {
            if (stack.Length == 0)
            {
                return new NativeHostResult();
            }

            var itemSize = Marshal.SizeOf<NativeStackItem>();
            var stackPtr = Marshal.AllocHGlobal(itemSize * stack.Length);

            for (var index = 0; index < stack.Length; index++)
            {
                var nativeItem = stack[index] switch
                {
                    Integer integer when integer.Size > sizeof(long) => CreateNativeBigIntegerItem(integer),
                    Integer integer => new NativeStackItem
                    {
                        Kind = 0,
                        IntegerValue = (long)integer.GetInteger(),
                        BytesPtr = IntPtr.Zero,
                        BytesLen = 0,
                    },
                    ByteString byteString => CreateNativeByteStringItem(byteString),
                    Neo.VM.Types.Buffer buffer => CreateNativeBufferItem(buffer),
                    Neo.VM.Types.Struct @struct => CreateNativeStructItem(@struct, scope),
                    Neo.VM.Types.Array array => CreateNativeArrayItem(array, scope),
                    Neo.VM.Types.Map map => CreateNativeMapItem(map, scope),
                    Neo.VM.Types.Boolean boolean => new NativeStackItem
                    {
                        Kind = 3,
                        IntegerValue = boolean.GetBoolean() ? 1 : 0,
                        BytesPtr = IntPtr.Zero,
                        BytesLen = 0,
                    },
                    InteropInterface interop when interop.GetInterface<object>() is IIterator iterator => new NativeStackItem
                    {
                        Kind = 6,
                        IntegerValue = checked((long)RegisterIterator(scope, iterator)),
                        BytesPtr = IntPtr.Zero,
                        BytesLen = 0,
                    },
                    InteropInterface interop => new NativeStackItem
                    {
                        Kind = 9,
                        IntegerValue = checked((long)RegisterInterop(scope, interop.GetInterface<object>()!)),
                        BytesPtr = IntPtr.Zero,
                        BytesLen = 0,
                    },
                    Null => new NativeStackItem
                    {
                        Kind = 2,
                        IntegerValue = 0,
                        BytesPtr = IntPtr.Zero,
                        BytesLen = 0,
                    },
                    Neo.VM.Types.Pointer pointer => new NativeStackItem
                    {
                        Kind = 10,
                        IntegerValue = pointer.Position,
                        BytesPtr = IntPtr.Zero,
                        BytesLen = 0,
                    },
                    _ => throw new InvalidOperationException($"Unsupported host callback stack item type: {stack[index].GetType().Name}.")
                };

                Marshal.StructureToPtr(nativeItem, IntPtr.Add(stackPtr, index * itemSize), false);
            }

            return new NativeHostResult
            {
                StackPtr = stackPtr,
                StackLen = (nuint)stack.Length,
                ErrorPtr = IntPtr.Zero,
                ErrorLen = 0,
            };
        }

        private static NativeStackItem CreateNativeByteStringItem(ByteString byteString)
        {
            var bytes = byteString.GetSpan().ToArray();
            var bytesPtr = bytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(bytes.Length);
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, bytesPtr, bytes.Length);
            }

            return new NativeStackItem
            {
                Kind = 1,
                IntegerValue = 0,
                BytesPtr = bytesPtr,
                BytesLen = (nuint)bytes.Length,
            };
        }

        private static NativeStackItem CreateNativeBufferItem(Neo.VM.Types.Buffer buffer)
        {
            var bytes = buffer.GetSpan().ToArray();
            var bytesPtr = bytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(bytes.Length);
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, bytesPtr, bytes.Length);
            }

            return new NativeStackItem
            {
                Kind = 11,
                IntegerValue = 0,
                BytesPtr = bytesPtr,
                BytesLen = (nuint)bytes.Length,
            };
        }

        private static NativeStackItem CreateNativeBigIntegerItem(Integer integer)
        {
            var bytes = integer.GetInteger().ToByteArray();
            var bytesPtr = bytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(bytes.Length);
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, bytesPtr, bytes.Length);
            }

            return new NativeStackItem
            {
                Kind = 5,
                IntegerValue = 0,
                BytesPtr = bytesPtr,
                BytesLen = (nuint)bytes.Length,
            };
        }

        private NativeStackItem CreateNativeArrayItem(Neo.VM.Types.Array array, ExecutionScope scope)
        {
            var items = new StackItem[array.Count];
            for (var index = 0; index < array.Count; index++)
            {
                items[index] = array[index];
            }

            var nested = CreateNativeHostResult(items, scope);
            return new NativeStackItem
            {
                Kind = 4,
                IntegerValue = 0,
                BytesPtr = nested.StackPtr,
                BytesLen = nested.StackLen,
            };
        }

        private NativeStackItem CreateNativeStructItem(Neo.VM.Types.Struct @struct, ExecutionScope scope)
        {
            var items = new StackItem[@struct.Count];
            for (var index = 0; index < @struct.Count; index++)
            {
                items[index] = @struct[index];
            }

            var nested = CreateNativeHostResult(items, scope);
            return new NativeStackItem
            {
                Kind = 7,
                IntegerValue = 0,
                BytesPtr = nested.StackPtr,
                BytesLen = nested.StackLen,
            };
        }

        private NativeStackItem CreateNativeMapItem(Neo.VM.Types.Map map, ExecutionScope scope)
        {
            var items = new StackItem[map.Count * 2];
            var offset = 0;
            foreach (var entry in map)
            {
                items[offset++] = entry.Key;
                items[offset++] = entry.Value;
            }

            var nested = CreateNativeHostResult(items, scope);
            return new NativeStackItem
            {
                Kind = 8,
                IntegerValue = 0,
                BytesPtr = nested.StackPtr,
                BytesLen = nested.StackLen,
            };
        }

        private static NativeHostResult CreateNativeHostError(Exception exception)
        {
            var payload = string.Join("\n", new[]
            {
                exception.GetType().FullName ?? typeof(InvalidOperationException).FullName!,
                exception.Message,
                exception.InnerException?.GetType().FullName ?? string.Empty,
                exception.InnerException?.Message ?? string.Empty,
            });
            var bytes = Encoding.UTF8.GetBytes(payload);
            var errorPtr = bytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(bytes.Length);
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, errorPtr, bytes.Length);
            }

            return new NativeHostResult
            {
                StackPtr = IntPtr.Zero,
                StackLen = 0,
                ErrorPtr = errorPtr,
                ErrorLen = (nuint)bytes.Length,
            };
        }

        private static Exception RehydrateNativeException(string payload)
        {
            // Split on newline - inner messages may contain newlines so join parts[3..]
            var parts = payload.Split('\n');
            if (parts.Length < 4)
            {
                return new InvalidOperationException(payload);
            }

            var innerMessage = parts.Length > 4
                ? string.Join("\n", parts[3..])
                : parts[3];
            Exception? inner = string.IsNullOrEmpty(parts[2]) ? null : CreateException(parts[2], innerMessage);
            if (parts[0] == typeof(System.Reflection.TargetInvocationException).FullName && inner is not null)
                return new System.Reflection.TargetInvocationException(inner);
            return CreateException(parts[0], parts[1], inner);
        }

        private static Exception CreateException(string typeName, string message, Exception? inner = null)
        {
            return typeName switch
            {
                nameof(ArgumentException) or "System.ArgumentException" => new ArgumentException(message, inner),
                nameof(ArgumentOutOfRangeException) or "System.ArgumentOutOfRangeException" => new ArgumentOutOfRangeException(paramName: null, message: message),
                nameof(FormatException) or "System.FormatException" => new FormatException(message, inner),
                nameof(InvalidOperationException) or "System.InvalidOperationException" => new InvalidOperationException(message, inner),
                nameof(NullReferenceException) or "System.NullReferenceException" => new NullReferenceException(message),
                nameof(NotSupportedException) or "System.NotSupportedException" => new NotSupportedException(message, inner),
                nameof(NotImplementedException) or "System.NotImplementedException" => new NotImplementedException(message, inner),
                nameof(OverflowException) or "System.OverflowException" => new OverflowException(message, inner),
                nameof(IndexOutOfRangeException) or "System.IndexOutOfRangeException" => new IndexOutOfRangeException(message),
                nameof(KeyNotFoundException) or "System.Collections.Generic.KeyNotFoundException" => new KeyNotFoundException(message, inner),
                nameof(DivideByZeroException) or "System.DivideByZeroException" => new DivideByZeroException(message, inner),
                _ => new InvalidOperationException(message, inner),
            };
        }

        private static StackItem ReadByteString(NativeStackItem nativeItem, bool decodeStorageContextTokens)
        {
            if (nativeItem.BytesPtr == IntPtr.Zero)
                return ByteString.Empty;

            var bytes = new byte[checked((int)nativeItem.BytesLen)];
            Marshal.Copy(nativeItem.BytesPtr, bytes, 0, bytes.Length);
            return decodeStorageContextTokens && TryParseStorageContextToken(bytes, out var context)
                ? CreateStorageContextArray(context)
                : new ByteString(bytes);
        }

        private static StackItem ReadBuffer(NativeStackItem nativeItem)
        {
            var bytes = new byte[checked((int)nativeItem.BytesLen)];
            if (bytes.Length > 0)
            {
                Marshal.Copy(nativeItem.BytesPtr, bytes, 0, bytes.Length);
            }
            return new Neo.VM.Types.Buffer(bytes);
        }

        private static Integer ReadBigInteger(NativeStackItem nativeItem)
        {
            var bytes = new byte[checked((int)nativeItem.BytesLen)];
            if (bytes.Length > 0)
            {
                Marshal.Copy(nativeItem.BytesPtr, bytes, 0, bytes.Length);
            }
            return new Integer(new BigInteger(bytes));
        }

        private StackItem[] ReadStack(IntPtr stackPtr, nuint stackLen, IReferenceCounter? referenceCounter, ExecutionScope scope, bool decodeStorageContextTokens)
        {
            if (stackPtr == IntPtr.Zero || stackLen == 0)
                return System.Array.Empty<StackItem>();

            var stack = new StackItem[(int)stackLen];
            for (var index = 0; index < stack.Length; index++)
            {
                var itemPtr = IntPtr.Add(stackPtr, index * Marshal.SizeOf<NativeStackItem>());
                var nativeItem = Marshal.PtrToStructure<NativeStackItem>(itemPtr);
                if (TraceEnabled)
                {
                    Trace($"readstack item[{index}] kind={nativeItem.Kind} int={nativeItem.IntegerValue} bytesLen={nativeItem.BytesLen} bytesPtr=0x{nativeItem.BytesPtr.ToInt64():x}");
                }
                stack[index] = nativeItem.Kind switch
                {
                    0 => new Integer(nativeItem.IntegerValue),
                    5 => ReadBigInteger(nativeItem),
                    1 => ReadByteString(nativeItem, decodeStorageContextTokens),
                    11 => ReadBuffer(nativeItem),
                    3 => nativeItem.IntegerValue != 0 ? StackItem.True : StackItem.False,
                    4 => ReadArray(nativeItem, referenceCounter, scope),
                    7 => ReadStruct(nativeItem, referenceCounter, scope),
                    9 => ReadInteropHandle(scope, checked((ulong)nativeItem.IntegerValue)),
                    8 => ReadMap(nativeItem, referenceCounter, scope),
                    6 => ReadIteratorHandle(scope, checked((ulong)nativeItem.IntegerValue)),
                    2 => StackItem.Null,
                    10 => new Neo.VM.Types.Pointer(Script.Empty, (int)nativeItem.IntegerValue),
                    _ => throw new InvalidOperationException($"Unsupported native stack item kind: {nativeItem.Kind}.")
                };
            }
            return stack;
        }

        private static StackItem ReadIteratorHandle(ExecutionScope scope, ulong handle)
        {
            if (!scope.Iterators.TryGetValue(handle, out var iterator))
                throw new InvalidOperationException($"Unknown iterator handle: {handle}.");
            return StackItem.FromInterface(iterator);
        }

        private static StackItem ReadInteropHandle(ExecutionScope scope, ulong handle)
        {
            if (!scope.InteropObjects.TryGetValue(handle, out var value))
                throw new InvalidOperationException($"Unknown interop handle: {handle}.");
            return StackItem.FromInterface(value);
        }

        private static ulong RegisterIterator(ExecutionScope scope, IIterator iterator)
        {
            var handle = scope.NextIteratorHandle++;
            scope.Iterators[handle] = iterator;
            return handle;
        }

        private static ulong RegisterInterop(ExecutionScope scope, object value)
        {
            var handle = scope.NextInteropHandle++;
            scope.InteropObjects[handle] = value;
            return handle;
        }

        private Neo.VM.Types.Array ReadArray(NativeStackItem nativeItem, IReferenceCounter? referenceCounter, ExecutionScope scope)
        {
            var children = ReadStack(nativeItem.BytesPtr, (nuint)nativeItem.BytesLen, referenceCounter, scope, decodeStorageContextTokens: true);
            return new Neo.VM.Types.Array(referenceCounter, children);
        }

        private Neo.VM.Types.Struct ReadStruct(NativeStackItem nativeItem, IReferenceCounter? referenceCounter, ExecutionScope scope)
        {
            var children = ReadStack(nativeItem.BytesPtr, (nuint)nativeItem.BytesLen, referenceCounter, scope, decodeStorageContextTokens: true);
            return new Neo.VM.Types.Struct(referenceCounter, children);
        }

        private Neo.VM.Types.Map ReadMap(NativeStackItem nativeItem, IReferenceCounter? referenceCounter, ExecutionScope scope)
        {
            var children = ReadStack(nativeItem.BytesPtr, (nuint)nativeItem.BytesLen, referenceCounter, scope, decodeStorageContextTokens: true);
            if (children.Length % 2 != 0)
                throw new InvalidOperationException("Native map stack item contains an odd number of entries.");

            var map = new Neo.VM.Types.Map(referenceCounter);
            for (var index = 0; index < children.Length; index += 2)
            {
                if (children[index] is not PrimitiveType key)
                    throw new InvalidOperationException("Native map stack item contains a non-primitive key.");
                map[key] = children[index + 1];
            }
            return map;
        }

        public void Dispose()
        {
            if (_libraryHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(_libraryHandle);
                _libraryHandle = IntPtr.Zero;
            }
        }
    }
}
