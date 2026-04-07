// Copyright (C) 2015-2026 The Neo Project.
//
// IRiscvVmBridge.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.

using System.Collections.Generic;
using Neo.VM.Types;

namespace Neo.SmartContract.RiscV
{
    /// <summary>
    /// Interface for the RISC-V VM bridge that executes contracts on PolkaVM.
    /// Supports two execution paths:
    /// - NeoVM contracts (ContractType.NeoVM): Script is NeoVM bytecode, interpreted
    ///   by the NeoVM interpreter running as a PolkaVM guest binary.
    /// - RISC-V contracts (ContractType.RiscV): Script is a PolkaVM binary (PVM\0 magic),
    ///   executed directly by PolkaVM without an interpreter layer.
    /// Both paths share the same host callback for SYSCALL/CALLT interop.
    /// </summary>
    public interface IRiscvVmBridge
    {
        /// <summary>
        /// Executes a contract through the PolkaVM runtime.
        /// The request contains the contract script(s) and execution context.
        /// PolkaVM auto-detects whether the script is a NeoVM bytecode blob
        /// (processed by the interpreter guest) or a native RISC-V binary
        /// (executed directly).
        /// </summary>
        RiscvExecutionResult Execute(RiscvExecutionRequest request);

        /// <summary>
        /// High-level entry point for executing a deployed contract by its state.
        /// Constructs the appropriate <see cref="RiscvExecutionRequest"/> from the
        /// engine context and contract metadata, then delegates to <see cref="Execute"/>.
        /// </summary>
        /// <param name="engine">The application engine providing execution context.</param>
        /// <param name="contract">The deployed contract state containing the script and type.</param>
        /// <param name="method">The contract method to invoke.</param>
        /// <param name="flags">The call flags for this invocation.</param>
        /// <param name="args">The arguments to pass to the contract method.</param>
        /// <returns>The execution result including final stack and gas consumed.</returns>
        RiscvExecutionResult ExecuteContract(
            ApplicationEngine engine,
            ContractState contract,
            string method,
            CallFlags flags,
            IReadOnlyList<StackItem> args);
    }
}
