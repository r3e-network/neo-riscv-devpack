// Copyright (C) 2015-2026 The Neo Project.
//
// RiscvApplicationEngineProvider.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.

using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.VM;
using System;

namespace Neo.SmartContract.RiscV
{
    public sealed class RiscvApplicationEngineProvider : IApplicationEngineProvider, IDisposable
    {
        private readonly IRiscvVmBridge _bridge;

        public RiscvApplicationEngineProvider(IRiscvVmBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        public ApplicationEngine Create(TriggerType trigger, IVerifiable? container, DataCache snapshot, Block? persistingBlock, ProtocolSettings settings, long gas, IDiagnostic? diagnostic, JumpTable jumpTable)
        {
            return new RiscvApplicationEngine(trigger, container, snapshot, persistingBlock, settings, gas, _bridge, diagnostic, jumpTable);
        }

        public void Dispose()
        {
            if (_bridge is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
