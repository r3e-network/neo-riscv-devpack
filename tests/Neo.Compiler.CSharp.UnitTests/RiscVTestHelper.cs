// Copyright (C) 2015-2026 The Neo Project.
//
// RiscVTestHelper.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Microsoft.CodeAnalysis;
using Neo.Compiler.Backend.RiscV;
using System;
using System.Collections.Concurrent;
using System.IO;

namespace Neo.Compiler.CSharp.UnitTests;

/// <summary>
/// Helper to compile and cache RISC-V contract binaries for testing.
/// </summary>
public static class RiscVTestHelper
{
    private const string RiscvVmRootEnvironmentVariable = "NEO_RISCV_VM_ROOT";
    private static readonly string OutputDir = Path.Combine(Path.GetTempPath(), "neo-riscv-test-contracts");
    private static readonly ConcurrentDictionary<string, string?> _cache = new();
    private static readonly ConcurrentDictionary<string, object> _buildLocks = new();
    private static bool _initialized;

    /// <summary>
    /// Ensures all test contracts are compiled for RISC-V.
    /// Call once in test assembly initialization.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        // If pre-built binaries exist, skip compilation entirely
        var prebuiltDirs = new[]
        {
            Environment.GetEnvironmentVariable("NEO_RISCV_CONTRACTS_DIR"),
            "/tmp/riscv-test-output/riscv",
            Path.Combine(OutputDir, "riscv"),
        };
        foreach (var dir in prebuiltDirs)
        {
            if (dir != null && Directory.Exists(dir) &&
                Directory.GetFiles(dir, "contract.polkavm", SearchOption.AllDirectories).Length > 50)
            {
                // Pre-built binaries available — skip slow Rust compilation
                return;
            }
        }

        Directory.CreateDirectory(OutputDir);

        // Compile all test contracts with --target riscv
        var projectPath = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "..", "..",
            "Neo.Compiler.CSharp.TestContracts",
            "Neo.Compiler.CSharp.TestContracts.csproj"));

        var options = new CompilationOptions
        {
            Target = CompilationTarget.RiscV,
            Nullable = NullableContextOptions.Annotations,
        };
        var engine = new CompilationEngine(options);
        var contexts = engine.CompileProject(projectPath);

        foreach (var ctx in contexts)
        {
            if (!ctx.Success || ctx.GeneratedRustSource == null) continue;

            var name = ctx.ContractName?.ToLowerInvariant().Replace(" ", "_");
            if (name == null) continue;

            var crateDir = Path.Combine(OutputDir, "riscv", name);
            var srcDir = Path.Combine(crateDir, "src");
            Directory.CreateDirectory(srcDir);

            // Write main.rs
            File.WriteAllText(Path.Combine(srcDir, "main.rs"), ctx.GeneratedRustSource);

            // Write Cargo.toml with absolute paths
            var cargoToml = ctx.GeneratedCargoToml ?? "";
            // Fix relative crate paths to absolute
            var riscvVmRoot = FindRiscvVmRoot();
            if (riscvVmRoot != null)
            {
                cargoToml = cargoToml.Replace(
                    "path = \"../../crates",
                    $"path = \"{riscvVmRoot}/crates");
            }
            File.WriteAllText(Path.Combine(crateDir, "Cargo.toml"), cargoToml);

            _cache[ctx.ContractName!] = crateDir;
        }
    }

    /// <summary>
    /// Get the .polkavm binary path for a contract.
    /// Checks pre-built directory first, then cache, builds if needed.
    /// Returns null if compilation fails.
    /// </summary>
    public static string? GetPolkaVmBinary(string contractName)
    {
        // 1. Check pre-built directories (from batch build or Initialize())
        var prebuiltDirs = new[]
        {
            Environment.GetEnvironmentVariable("NEO_RISCV_CONTRACTS_DIR"),
            "/tmp/riscv-test-output/riscv",
            Path.Combine(OutputDir, "riscv"),
        };
        var prebuiltName = contractName.ToLowerInvariant().Replace(" ", "_");
        foreach (var dir in prebuiltDirs)
        {
            if (dir == null) continue;
            var candidate = Path.Combine(dir, prebuiltName, "contract.polkavm");
            if (File.Exists(candidate))
                return candidate;
        }

        // 2. Check cache
        if (!_cache.TryGetValue(contractName, out var crateDir) || crateDir == null)
            return null;

        var polkavmPath = Path.Combine(crateDir, "contract.polkavm");
        if (File.Exists(polkavmPath))
            return polkavmPath;

        // 3. Build on-the-fly (slow)
        var buildLock = _buildLocks.GetOrAdd(crateDir, static _ => new object());
        lock (buildLock)
        {
            if (File.Exists(polkavmPath))
                return polkavmPath;

            if (!BuildCrate(crateDir))
                return null;
        }

        return File.Exists(polkavmPath) ? polkavmPath : null;
    }

    private static bool BuildCrate(string crateDir)
    {
        try
        {
            var contractName = Path.GetFileName(crateDir);

            // Get original target JSON from polkatool
            var origTargetJson = RiscVBuildHelper.RunCommand("polkatool", ["get-target-json-path", "-b", "32"])?.Trim();
            if (string.IsNullOrEmpty(origTargetJson))
            {
                Console.Error.WriteLine($"[RiscV] {contractName}: polkatool get-target-json-path failed.");
                return false;
            }

            // Fix target JSON: add "abi" field required by newer nightly rustc
            var targetJson = Path.Combine(Path.GetTempPath(), $"neo-riscv32-polkavm-{Guid.NewGuid():N}.json");
            try
            {
                RiscVBuildHelper.FixTargetJson(origTargetJson!, targetJson);

                // Newer nightly toolchains accept JSON target paths directly.
                var buildResult = RiscVBuildHelper.RunCommand("cargo",
                    ["+nightly", "build", "--manifest-path", Path.Combine(crateDir, "Cargo.toml"), "--release", "--target", targetJson, "-Zbuild-std=core,alloc"],
                    workingDir: crateDir);
                if (buildResult == null)
                {
                    Console.Error.WriteLine($"[RiscV] {contractName}: cargo build failed.");
                    return false;
                }

                // Link — the output dir uses the JSON file's stem name
                var target = Path.GetFileNameWithoutExtension(targetJson);
                var name = Path.GetFileName(crateDir);
                var elf = Path.Combine(crateDir, "target", target, "release", name);
                var polkavm = Path.Combine(crateDir, "contract.polkavm");
                RiscVBuildHelper.RunCommand("polkatool", ["link", "--strip", "-o", polkavm, elf], workingDir: crateDir);

                if (!File.Exists(polkavm))
                {
                    Console.Error.WriteLine($"[RiscV] {contractName}: polkatool link produced no output.");
                    return false;
                }

                return true;
            }
            finally
            {
                try
                {
                    File.Delete(targetJson);
                }
                catch
                {
                    // Best-effort cleanup of a generated temp target file.
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RiscV] BuildCrate exception: {ex.Message}");
            return false;
        }
    }

    private static string? FindRiscvVmRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(RiscvVmRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var resolvedRoot = Path.GetFullPath(configuredRoot);
            if (HasRiscvVmCrates(resolvedRoot))
                return resolvedRoot;
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var resolvedRoot = FindRiscvVmRootFrom(start);
            if (resolvedRoot is not null)
                return resolvedRoot;
        }

        return null;
    }

    private static string? FindRiscvVmRootFrom(string start)
    {
        var dir = Path.GetFullPath(start);
        while (!string.IsNullOrEmpty(dir))
        {
            if (HasRiscvVmCrates(dir))
                return dir;

            var nestedCandidate = Path.Combine(dir, "neo-riscv-vm");
            if (HasRiscvVmCrates(nestedCandidate))
                return nestedCandidate;

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent))
                break;

            var siblingCandidate = Path.Combine(parent, "neo-riscv-vm");
            if (HasRiscvVmCrates(siblingCandidate))
                return siblingCandidate;

            dir = parent;
        }

        return null;
    }

    private static bool HasRiscvVmCrates(string path)
    {
        return Directory.Exists(Path.Combine(path, "crates", "neo-riscv-rt")) &&
            Directory.Exists(Path.Combine(path, "crates", "neo-riscv-contract-harness"));
    }
}
