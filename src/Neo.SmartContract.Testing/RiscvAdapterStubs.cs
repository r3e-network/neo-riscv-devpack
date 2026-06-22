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
    /// <summary>
    /// Stub <see cref="IRiscvVmBridge"/> used when the native RISC-V adapter is
    /// NOT referenced (compile-time fallback so devpack builds without the
    /// native dependency).
    /// </summary>
    /// <remarks>
    /// The real <c>Neo.Riscv.Adapter</c> project (in
    /// <c>neo-riscv-vm/dotnet/Neo.Riscv.Adapter</c>) supersedes this entire
    /// file when the <c>NEO_RISCV_ADAPTER</c> conditional is defined. These
    /// stubs exist solely so that <c>Neo.SmartContract.Testing</c> can compile
    /// and run pure-NeoVM tests on machines that do not have the native
    /// <c>libneo_riscv_host</c> library. Every stub here throws on use.
    /// </remarks>
    public interface IRiscvVmBridge
    {
    }

    /// <summary>
    /// Stub test-hooks interface (see <see cref="IRiscvVmBridge"/> remarks) —
    /// the real implementation lives in <c>Neo.Riscv.Adapter</c>.
    /// </summary>
    public interface IRiscvApplicationEngineTestingHooks
    {
        /// <summary>
        /// Attempt to satisfy a contract call with a custom mock (stub —
        /// always throws in this fallback build).
        /// </summary>
        bool TryInvokeCustomMock(ApplicationEngine engine, DataCache snapshot, UInt160 contractHash, string method, StackItem[] args, out StackItem result);

        /// <summary>
        /// Record a method hit for coverage (stub — always throws in this
        /// fallback build).
        /// </summary>
        void RecordMethodCoverage(UInt160 contractHash, ContractState contract, ContractMethodDescriptor method);
    }

    /// <summary>
    /// Stub bridge whose constructor throws — install the real
    /// <c>Neo.Riscv.Adapter</c> to enable RISC-V execution.
    /// </summary>
    public sealed class NativeRiscvVmBridge : IRiscvVmBridge
    {
        /// <summary>
        /// Environment variable name for the host library path (mirrors the
        /// real adapter's constant for source compatibility).
        /// </summary>
        public const string LibraryPathEnvironmentVariable = "NEO_RISCV_HOST_LIB";

        /// <summary>
        /// Initializes a new instance — always throws in this stub build.
        /// </summary>
        /// <param name="libraryPath">The (unused) library path.</param>
        /// <exception cref="DllNotFoundException">
        /// Always thrown: the RISC-V adapter project is not referenced.
        /// </exception>
        public NativeRiscvVmBridge(string libraryPath)
        {
            throw new DllNotFoundException($"The RISC-V adapter project is not referenced by this build: {libraryPath}");
        }
    }

    /// <summary>
    /// Stub provider whose <see cref="Create"/> throws — install the real
    /// <c>Neo.Riscv.Adapter</c> to enable RISC-V execution.
    /// </summary>
    public sealed class RiscvApplicationEngineProvider : IApplicationEngineProvider
    {
        /// <summary>
        /// Initializes a new instance (no-op in this stub build).
        /// </summary>
        /// <param name="bridge">The (unused) bridge.</param>
        public RiscvApplicationEngineProvider(IRiscvVmBridge bridge)
        {
        }

        /// <summary>
        /// Create an engine — always throws in this stub build.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Always thrown: the RISC-V backend is unavailable.
        /// </exception>
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

    /// <summary>
    /// Stub engine that satisfies the type contract but does not execute —
    /// install the real <c>Neo.Riscv.Adapter</c> to enable RISC-V execution.
    /// </summary>
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

        /// <summary>
        /// Optional test-framework hooks (stub — inert in this fallback build).
        /// </summary>
        public IRiscvApplicationEngineTestingHooks? TestingHooks { get; set; }
    }
}

#endif
