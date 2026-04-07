// Copyright (C) 2015-2026 The Neo Project.
//
// RiscvBridgeScope.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.

using System;

namespace Neo.SmartContract.RiscV
{
    public sealed class RiscvBridgeScope : IDisposable
    {
        private readonly IApplicationEngineProvider? _previousProvider;

        private RiscvBridgeScope(IApplicationEngineProvider provider)
        {
            _previousProvider = ApplicationEngine.Provider;
            ApplicationEngine.Provider = provider;
        }

        public static RiscvBridgeScope UseProvider(IApplicationEngineProvider provider)
        {
            if (provider is null) throw new ArgumentNullException(nameof(provider));
            return new RiscvBridgeScope(provider);
        }

        public void Dispose()
        {
            ApplicationEngine.Provider = _previousProvider;
        }
    }
}
