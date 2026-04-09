// Copyright (C) 2015-2026 The Neo Project.
//
// RiscvAdapterPlugin.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.

using Neo.Plugins;

namespace Neo.SmartContract.RiscV
{
    /// <summary>
    /// Neo plugin that registers the RISC-V application engine provider.
    /// When loaded by the node, this plugin sets <see cref="ApplicationEngine.Provider"/>
    /// so that all subsequent engine creation uses the RISC-V backend.
    /// </summary>
    public sealed class RiscvAdapterPlugin : Plugin
    {
        public override string Name => "Neo.Riscv.Adapter";
        public override string Description => "RISC-V VM adapter for Neo smart contract execution via PolkaVM.";
        protected internal override UnhandledExceptionPolicy ExceptionPolicy => UnhandledExceptionPolicy.StopPlugin;

        public RiscvAdapterPlugin()
        {
            ApplicationEngine.Provider = RiscvApplicationEngineProviderResolver.ResolveRequiredProvider();
            Log($"RISC-V adapter initialized, provider registered.");
        }

        public override void Dispose()
        {
            ApplicationEngine.Provider = null;
            RiscvApplicationEngineProviderResolver.ResetForTesting();
            base.Dispose();
        }
    }
}
