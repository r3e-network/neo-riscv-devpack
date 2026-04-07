// Copyright (C) 2015-2026 The Neo Project.
//
// RiscvExecutionResult.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.

using Neo.VM;
using Neo.VM.Types;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Neo.SmartContract.RiscV
{
    public sealed class RiscvExecutionResult
    {
        public VMState State { get; }

        public IReadOnlyList<StackItem> ResultStack { get; }

        public Exception? FaultException { get; }

        public RiscvExecutionResult(VMState state, IEnumerable<StackItem> resultStack, Exception? faultException)
        {
            State = state;
            ResultStack = resultStack?.ToArray() ?? throw new ArgumentNullException(nameof(resultStack));
            FaultException = faultException;
        }
    }
}
