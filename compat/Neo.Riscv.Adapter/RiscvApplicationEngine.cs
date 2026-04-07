// Copyright (C) 2015-2026 The Neo Project.
//
// RiscvApplicationEngine.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.

using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.VM;
using Neo.VM.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Neo.SmartContract.RiscV
{
    public sealed class RiscvApplicationEngine : ApplicationEngine
    {
        private const string TraceEnvironmentVariable = "NEO_RISCV_TRACE_ENGINE";
        private static readonly FieldInfo StrictModeField = typeof(Script).GetField("_strictMode", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate Neo.VM.Script strict mode field.");

        private readonly IRiscvVmBridge _bridge;
        private bool _neoVMOnly;

        internal RiscvApplicationEngine(
            TriggerType trigger,
            IVerifiable? container,
            DataCache snapshotCache,
            Block? persistingBlock,
            ProtocolSettings settings,
            long gas,
            IRiscvVmBridge bridge,
            IDiagnostic? diagnostic = null,
            JumpTable? jumpTable = null)
            : base(trigger, container, snapshotCache, persistingBlock, settings, gas, diagnostic, jumpTable)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        /// <summary>
        /// Signal the engine to process only NeoVM contexts on the next Execute() call.
        /// Used when the bridge needs to execute user contract contexts loaded via CallFromNativeContractAsync.
        /// </summary>
        internal void FlagNeoVMMode() => _neoVMOnly = true;
        internal void ClearNeoVMMode() => _neoVMOnly = false;

        /// <summary>
        /// Execute standard NeoVM until the invocation stack returns to the specified depth.
        /// Used to process a newly loaded context (e.g., from CallContractInternal) without
        /// processing the outer RISC-V contexts.
        /// </summary>
        internal VMState ExecuteUntilStackDepth(int targetDepth)
        {
            // Save and restore State since the outer RISC-V execution may have set it to BREAK/NONE
            var savedState = State;
            State = VMState.NONE;
            while (State != VMState.HALT && State != VMState.FAULT && InvocationStack.Count > targetDepth)
            {
                ExecuteNext();
            }
            var result = State;
            // Restore state for the outer execution if this context completed normally
            if (result == VMState.HALT)
                State = savedState;
            return result;
        }

        public override VMState Execute()
        {
            // NeoVM-only mode: skip RISC-V contexts, process only NeoVM user contracts.
            // This is used when the bridge calls back into the engine to process
            // contexts loaded via CallFromNativeContractAsync.
            if (_neoVMOnly)
            {
                _neoVMOnly = false;
                return base.Execute();
            }

            var contexts = InvocationStack
                .Reverse()
                .ToArray();

            Trace($"execute start contexts={contexts.Length} trigger={Trigger} neoVMOnly={_neoVMOnly}");
            var result = new RiscvExecutionResult(VMState.HALT, System.Array.Empty<StackItem>(), null);
            IReadOnlyList<StackItem> initialStack = System.Array.Empty<StackItem>();

            for (var index = contexts.Length - 1; index >= 0; index--)
            {
                var context = contexts[index];
                Trace($"bridge dispatch index={index} ip={context.InstructionPointer} scriptLen={((ReadOnlyMemory<byte>)context.Script).Length}");
                var prefix = contexts.Take(index + 1).ToArray();
                var scripts = prefix
                    .Select(current => ((ReadOnlyMemory<byte>)current.Script).ToArray())
                    .ToArray();
                var scriptHashes = prefix
                    .Select(current => current.GetState<ExecutionContextState>().ScriptHash ?? ((ReadOnlyMemory<byte>)current.Script).Span.ToScriptHash())
                    .ToArray();
                var contractTypes = prefix
                    .Select(current => current.GetState<ExecutionContextState>().Contract?.Type ?? ContractType.NeoVM)
                    .ToArray();
                var executionFacadeHashes = contractTypes
                    .Zip(scriptHashes, (contractType, scriptHash) =>
                        RiscvCompatibilityContracts.ResolveExecutionFacadeHash(contractType, scriptHash))
                    .ToArray();

                result = _bridge.Execute(new RiscvExecutionRequest(
                    this,
                    Trigger,
                    ProtocolSettings.Network,
                    ProtocolSettings.AddressVersion,
                    PersistingBlock?.Timestamp ?? 0,
                    GasLeft,
                    context.GetState<ExecutionContextState>().CallFlags,
                    scripts,
                    scriptHashes,
                    contractTypes,
                    executionFacadeHashes,
                    initialStack,
                    context.InstructionPointer));

                if (result.State != VMState.HALT)
                    break;

                initialStack = result.ResultStack;
                if (Trigger != TriggerType.Verification && !IsStrictMode(context.Script))
                    break;
            }

            while (ResultStack.Count > 0)
            {
                ResultStack.Pop();
            }

            foreach (var item in result.ResultStack)
            {
                ResultStack.Push(item);
            }

            if (result.State == VMState.HALT)
            {
                CurrentContext?.GetState<ExecutionContextState>().SnapshotCache?.Commit();
                InvocationStack.Clear();
            }

            FaultException = result.FaultException;
            State = result.State;
            return State;
        }

        private static void Trace(string message)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(TraceEnvironmentVariable), "1", StringComparison.Ordinal))
                return;

            Console.Error.WriteLine($"[neo-riscv-engine] {message}");
        }

        private static bool IsStrictMode(Script script)
        {
            return (bool)StrictModeField.GetValue(script)!;
        }
    }
}
