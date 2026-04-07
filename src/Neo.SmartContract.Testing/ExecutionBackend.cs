// Copyright (C) 2015-2026 The Neo Project.
//
// ExecutionBackend.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

namespace Neo.SmartContract.Testing;

/// <summary>
/// Selects which VM backend the test engine uses for contract execution.
/// Set via the NEO_TEST_BACKEND environment variable:
///   neovm (default) — standard NeoVM ApplicationEngine
///   riscv — RISC-V PolkaVM via libneo_riscv_host.so
/// </summary>
public enum ExecutionBackend
{
    /// <summary>Standard NeoVM execution (default).</summary>
    NeoVM = 0,

    /// <summary>RISC-V execution via PolkaVM native library.</summary>
    RiscV = 1,
}
