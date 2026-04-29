// Copyright (C) 2015-2026 The Neo Project.
//
// RiscvAdapterStubs.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.

#if !NEO_RISCV_ADAPTER

using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract.Manifest;
using Neo.VM;
using Neo.VM.Types;
using System;

namespace Neo.SmartContract.RiscV
{
    public interface IRiscvVmBridge
    {
    }

    public interface IRiscvApplicationEngineTestingHooks
    {
        bool TryInvokeCustomMock(ApplicationEngine engine, DataCache snapshot, UInt160 contractHash, string method, StackItem[] args, out StackItem result);
        void RecordMethodCoverage(UInt160 contractHash, ContractState contract, ContractMethodDescriptor method);
    }

    public sealed class NativeRiscvVmBridge : IRiscvVmBridge
    {
        public const string LibraryPathEnvironmentVariable = "NEO_RISCV_HOST_LIB";

        public NativeRiscvVmBridge(string libraryPath)
        {
            throw new DllNotFoundException($"The RISC-V adapter project is not referenced by this build: {libraryPath}");
        }
    }

    public sealed class RiscvApplicationEngineProvider : IApplicationEngineProvider
    {
        public RiscvApplicationEngineProvider(IRiscvVmBridge bridge)
        {
        }

        public ApplicationEngine Create(
            TriggerType trigger,
            IVerifiable? container,
            DataCache snapshot,
            Block? persistingBlock,
            ProtocolSettings settings,
            long gas,
            IDiagnostic? diagnostic,
            JumpTable jumpTable)
        {
            throw new InvalidOperationException("RISC-V backend requested, but the Neo.Riscv.Adapter project is not available in this build.");
        }
    }

    public sealed class RiscvApplicationEngine : ApplicationEngine, IRiscvApplicationEngine
    {
        internal RiscvApplicationEngine(
            TriggerType trigger,
            IVerifiable? container,
            DataCache snapshotCache,
            Block? persistingBlock,
            ProtocolSettings settings,
            long gas,
            IDiagnostic? diagnostic = null,
            JumpTable? jumpTable = null)
            : base(trigger, container, snapshotCache, persistingBlock, settings, gas, diagnostic, jumpTable)
        {
        }

        public IRiscvApplicationEngineTestingHooks? TestingHooks { get; set; }
    }
}

#endif
