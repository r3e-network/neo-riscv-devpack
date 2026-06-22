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
    /// <summary>
    /// Adapter that wires <see cref="RiscvApplicationEngine"/> test-hook
    /// callbacks back into the <see cref="TestEngine"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Role:</b> When the devpack test framework runs a contract through
    /// the RISC-V adapter, the adapter exposes several test-only hook callbacks
    /// (script-hash overrides, custom-mock dispatch, coverage recording) via
    /// <see cref="IRiscvApplicationEngineTestingHooks"/>. This class implements
    /// that interface and routes each callback to the owning
    /// <see cref="TestEngine"/> so that mocks, coverage, and script-hash
    /// fixtures work the same as they do for the pure-NeoVM test path.</para>
    /// <para><b>Note on coverage:</b> Coverage recording is intentionally
    /// skipped for <see cref="ContractType.RiscV"/> contracts (see
    /// <see cref="RecordMethodCoverage"/>) because the RISC-V execution path
    /// does not expose NeoVM instruction-level offsets suitable for
    /// line-coverage attribution.</para>
    /// </remarks>
    internal sealed class RiscvApplicationEngineTestingHooks(TestEngine testEngine) : IRiscvApplicationEngineTestingHooks
    {
        /// <summary>
        /// Override the <c>CallingScriptHash</c> returned to a contract under
        /// test, using the <see cref="TestEngine"/>'s fixture if one is set.
        /// </summary>
        /// <param name="current">The hash the engine would normally return.</param>
        /// <param name="expected">The expected/fixture hash.</param>
        /// <returns>The overridden hash, or <paramref name="expected"/> if no fixture is set.</returns>
        public UInt160? OverrideCallingScriptHash(UInt160? current, UInt160? expected)
        {
            return testEngine.OnGetCallingScriptHash?.Invoke(current, expected) ?? expected;
        }

        /// <summary>
        /// Override the <c>EntryScriptHash</c> returned to a contract under
        /// test, using the <see cref="TestEngine"/>'s fixture if one is set.
        /// </summary>
        /// <param name="current">The hash the engine would normally return.</param>
        /// <param name="expected">The expected/fixture hash.</param>
        /// <returns>The overridden hash, or <paramref name="expected"/> if no fixture is set.</returns>
        public UInt160? OverrideEntryScriptHash(UInt160? current, UInt160? expected)
        {
            return testEngine.OnGetEntryScriptHash?.Invoke(current, expected) ?? expected;
        }

        /// <summary>
        /// Attempt to satisfy a contract call with a registered custom mock.
        /// </summary>
        /// <param name="engine">The executing engine (for result conversion).</param>
        /// <param name="snapshot">The current storage snapshot for the call.</param>
        /// <param name="contractHash">The hash of the contract being called.</param>
        /// <param name="method">The method name being called.</param>
        /// <param name="args">The call arguments as stack items.</param>
        /// <param name="result">When this returns <see langword="true"/>, the
        /// mock's return value as a <see cref="StackItem"/>.</param>
        /// <returns><see langword="true"/> if a matching mock was found and
        /// invoked; <see langword="false"/> to let the call proceed normally.</returns>
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

        /// <summary>
        /// Record a method hit for coverage purposes.
        /// </summary>
        /// <param name="contractHash">The hash of the executed contract.</param>
        /// <param name="contractState">The contract's state (script + manifest).</param>
        /// <param name="descriptor">The method descriptor that was hit.</param>
        /// <remarks>
        /// Coverage recording is skipped for <see cref="ContractType.RiscV"/>
        /// contracts, since the RISC-V execution path does not expose NeoVM
        /// instruction-level offsets suitable for line-coverage attribution.
        /// </remarks>
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
