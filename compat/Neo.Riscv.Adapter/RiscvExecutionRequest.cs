// Copyright (C) 2015-2026 The Neo Project.
//
// RiscvExecutionRequest.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.

using System;
using System.Collections.Generic;
using Neo.VM.Types;

namespace Neo.SmartContract.RiscV
{
    public sealed class RiscvExecutionRequest
    {
        public ApplicationEngine Engine { get; }

        public TriggerType Trigger { get; }

        public uint NetworkMagic { get; }

        public byte AddressVersion { get; }

        public ulong PersistingTimestamp { get; }

        public long GasLeft { get; }

        public CallFlags CurrentCallFlags { get; }

        public IReadOnlyList<byte[]> Scripts { get; }

        public IReadOnlyList<UInt160> ScriptHashes { get; }

        public IReadOnlyList<UInt160> ExecutionFacadeHashes { get; }

        public IReadOnlyList<ContractType> ContractTypes { get; }

        public IReadOnlyList<StackItem> InitialStack { get; }

        public int InitialInstructionPointer { get; }

        /// <summary>
        /// The method name for native RISC-V contract execution.
        /// Prepended as ByteString to the stack for method dispatch.
        /// </summary>
        public string? Method { get; }

        public RiscvExecutionRequest(ApplicationEngine engine, TriggerType trigger, uint networkMagic, byte addressVersion, ulong persistingTimestamp, long gasLeft, CallFlags currentCallFlags, IReadOnlyList<byte[]> scripts, IReadOnlyList<UInt160> scriptHashes, IReadOnlyList<ContractType>? contractTypes = null, IReadOnlyList<UInt160>? executionFacadeHashes = null, IReadOnlyList<StackItem>? initialStack = null, int initialInstructionPointer = 0, string? method = null)
        {
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            Trigger = trigger;
            NetworkMagic = networkMagic;
            AddressVersion = addressVersion;
            PersistingTimestamp = persistingTimestamp;
            GasLeft = gasLeft;
            CurrentCallFlags = currentCallFlags;
            Scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
            ScriptHashes = scriptHashes ?? throw new ArgumentNullException(nameof(scriptHashes));
            ContractTypes = contractTypes ?? System.Array.Empty<ContractType>();
            ExecutionFacadeHashes = executionFacadeHashes ?? scriptHashes;
            InitialStack = initialStack ?? System.Array.Empty<StackItem>();
            InitialInstructionPointer = initialInstructionPointer;
            Method = method;
        }
    }
}
