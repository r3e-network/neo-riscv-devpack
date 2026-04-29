// Copyright (C) 2015-2026 The Neo Project.
//
// TestingApplicationEngine.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract.Native;
using Neo.SmartContract.Testing.Extensions;
using Neo.SmartContract.Testing.Storage;
using Neo.VM;
using Neo.VM.Types;
using System;

namespace Neo.SmartContract.Testing
{
    /// <summary>
    /// NeoVM test engine that preserves devpack-specific hooks and custom mocks.
    /// </summary>
    internal sealed class TestingApplicationEngine : ApplicationEngine
    {
        private Instruction? _preInstruction;
        private ExecutionContext? _instructionContext;
        private int? _instructionPointer;
        private long _preExecuteInstructionFeeConsumed;
        private bool? _branchPath;

        public TestEngine Engine { get; }

        public override UInt160? CallingScriptHash
        {
            get
            {
                var expected = base.CallingScriptHash;
                return Engine.OnGetCallingScriptHash?.Invoke(CurrentScriptHash, expected) ?? expected;
            }
        }

        public override UInt160? EntryScriptHash
        {
            get
            {
                var expected = base.EntryScriptHash;
                return Engine.OnGetEntryScriptHash?.Invoke(CurrentScriptHash, expected) ?? expected;
            }
        }

        public TestingApplicationEngine(
            TestEngine engine,
            TriggerType trigger,
            IVerifiable? container,
            DataCache snapshot,
            Block? persistingBlock)
            : base(trigger, container, snapshot, persistingBlock, engine.ProtocolSettings, engine.Fee, null)
        {
            Engine = engine;
        }

        protected override void PreExecuteInstruction(Instruction instruction)
        {
            if (Engine.EnableCoverageCapture)
            {
                _preInstruction = instruction;
                _preExecuteInstructionFeeConsumed = FeeConsumed;
                _instructionContext = CurrentContext;
                _instructionPointer = _instructionContext?.InstructionPointer;
            }

            _branchPath = null;
            switch (instruction.OpCode)
            {
                case OpCode.JMPIF:
                case OpCode.JMPIF_L:
                case OpCode.JMPIFNOT:
                case OpCode.JMPIFNOT_L:
                    if (CurrentContext!.EvaluationStack.Count >= 1)
                        _branchPath = Peek(0).GetBoolean();
                    break;
                case OpCode.JMPEQ:
                case OpCode.JMPEQ_L:
                case OpCode.JMPNE:
                case OpCode.JMPNE_L:
                    if (CurrentContext!.EvaluationStack.Count >= 2)
                        _branchPath = Peek(0).GetInteger() == Peek(1).GetInteger();
                    break;
                case OpCode.JMPGT:
                case OpCode.JMPGT_L:
                    if (CurrentContext!.EvaluationStack.Count >= 2)
                        _branchPath = Peek(0).GetInteger() > Peek(1).GetInteger();
                    break;
                case OpCode.JMPGE:
                case OpCode.JMPGE_L:
                    if (CurrentContext!.EvaluationStack.Count >= 2)
                        _branchPath = Peek(0).GetInteger() >= Peek(1).GetInteger();
                    break;
                case OpCode.JMPLT:
                case OpCode.JMPLT_L:
                    if (CurrentContext!.EvaluationStack.Count >= 2)
                        _branchPath = Peek(0).GetInteger() < Peek(1).GetInteger();
                    break;
                case OpCode.JMPLE:
                case OpCode.JMPLE_L:
                    if (CurrentContext!.EvaluationStack.Count >= 2)
                        _branchPath = Peek(0).GetInteger() <= Peek(1).GetInteger();
                    break;
            }

            base.PreExecuteInstruction(instruction);
        }

        protected override void OnFault(Exception ex)
        {
            base.OnFault(ex);
            if (_preInstruction is not null)
                RecoverCoverage(_preInstruction);
        }

        protected override void PostExecuteInstruction(Instruction instruction)
        {
            base.PostExecuteInstruction(instruction);
            RecoverCoverage(instruction);
        }

        private void RecoverCoverage(Instruction instruction)
        {
            if (_instructionContext is null) return;

            var contractHash = _instructionContext.GetScriptHash();
            if (!Engine.Coverage.TryGetValue(contractHash, out var coveredContract))
            {
                var state = ReferenceEquals(EntryContext, _instructionContext)
                    ? null
                    : NativeContract.ContractManagement.GetContract(SnapshotCache, contractHash);
                coveredContract = new(Engine.MethodDetection, contractHash, state);
                Engine.Coverage[contractHash] = coveredContract;
            }

            if (_instructionPointer is null) return;

            coveredContract.Hit(_instructionPointer.Value, instruction, FeeConsumed - _preExecuteInstructionFeeConsumed, _branchPath);
            _branchPath = null;
            _preInstruction = null;
            _instructionContext = null;
            _instructionPointer = null;
        }

        protected override void OnSysCall(InteropDescriptor descriptor)
        {
            if (descriptor == System_Contract_Call &&
                Convert(Peek(0), descriptor.Parameters[0]) is UInt160 contractHash &&
                Convert(Peek(1), descriptor.Parameters[1]) is string method &&
                Convert(Peek(2), descriptor.Parameters[2]) is CallFlags callFlags &&
                Convert(Peek(3), descriptor.Parameters[3]) is VM.Types.Array args &&
                Engine.TryGetCustomMock(contractHash, method, args.Count, out var customMock))
            {
                Pop(); Pop(); Pop(); Pop();

                ValidateCallFlags(descriptor.RequiredCallFlags);
                AddFee(descriptor.FixedPrice * ExecFeePicoFactor);

                if (method.StartsWith('_')) throw new ArgumentException($"Invalid Method Name: {method}");
                if ((callFlags & ~CallFlags.All) != 0)
                    throw new ArgumentOutOfRangeException(nameof(callFlags));

                var methodParameters = customMock.Method.GetParameters();
                var parameters = new object[args.Count];
                for (int i = 0; i < args.Count; i++)
                    parameters[i] = args[i].ConvertTo(methodParameters[i].ParameterType, Engine.StringInterpreter)!;

                object? returnValue;
                var backup = Engine.Storage;
                try
                {
                    Engine.Storage = new EngineStorage(backup.Store, SnapshotCache);
                    returnValue = customMock.Method.Invoke(customMock.Contract, parameters);
                }
                finally
                {
                    Engine.Storage = backup;
                }

                Push(customMock.Method.ReturnType != typeof(void)
                    ? Convert(returnValue)
                    : StackItem.Null);
                return;
            }

            base.OnSysCall(descriptor);
        }
    }
}
