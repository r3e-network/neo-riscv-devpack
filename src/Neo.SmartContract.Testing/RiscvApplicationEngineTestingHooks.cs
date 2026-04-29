using Neo.Persistence;
using Neo.SmartContract.Manifest;
using Neo.SmartContract.RiscV;
using Neo.SmartContract.Testing.Coverage;
using Neo.SmartContract.Testing.Extensions;
using Neo.SmartContract.Testing.Storage;
using Neo.VM;
using Neo.VM.Types;
using System;
using System.Collections.Generic;

namespace Neo.SmartContract.Testing
{
    internal sealed class RiscvApplicationEngineTestingHooks(TestEngine testEngine) : IRiscvApplicationEngineTestingHooks
    {
        public UInt160? OverrideCallingScriptHash(UInt160? current, UInt160? expected)
        {
            return testEngine.OnGetCallingScriptHash?.Invoke(current, expected) ?? expected;
        }

        public UInt160? OverrideEntryScriptHash(UInt160? current, UInt160? expected)
        {
            return testEngine.OnGetEntryScriptHash?.Invoke(current, expected) ?? expected;
        }

        public bool TryInvokeCustomMock(
            ApplicationEngine engine,
            DataCache snapshot,
            UInt160 contractHash,
            string method,
            StackItem[] args,
            out StackItem result)
        {
            result = StackItem.Null;
            if (!testEngine.TryGetCustomMock(contractHash, method, args.Length, out var customMock))
                return false;

            var methodParameters = customMock.Method.GetParameters();
            var parameters = new object[args.Length];
            for (var index = 0; index < args.Length; index++)
                parameters[index] = args[index].ConvertTo(methodParameters[index].ParameterType, testEngine.StringInterpreter)!;

            object? returnValue;
            var backup = testEngine.Storage;
            try
            {
                testEngine.Storage = new EngineStorage(backup.Store, snapshot);
                returnValue = customMock.Method.Invoke(customMock.Contract, parameters);
            }
            finally
            {
                testEngine.Storage = backup;
            }

            result = customMock.Method.ReturnType != typeof(void)
                ? engine.Convert(returnValue) ?? StackItem.Null
                : StackItem.Null;
            return true;
        }

        public void RecordMethodCoverage(UInt160 contractHash, ContractState contractState, ContractMethodDescriptor descriptor)
        {
            if (!testEngine.EnableCoverageCapture)
                return;

            if (contractState.Type == ContractType.RiscV)
                return;

            if (!testEngine.Coverage.TryGetValue(contractHash, out var coveredContract))
            {
                coveredContract = new CoveredContract(testEngine.MethodDetection, contractHash, contractState);
                testEngine.Coverage[contractHash] = coveredContract;
            }

            var coveredMethod = coveredContract.GetCoverage(descriptor.Name, descriptor.Parameters.Length);
            if (coveredMethod is null)
                return;

            var script = new Script(contractState.Script, false);
            if (descriptor.Offset < script.Length)
                coveredContract.Hit(descriptor.Offset, script.GetInstruction(descriptor.Offset), 0, null);
        }
    }
}
