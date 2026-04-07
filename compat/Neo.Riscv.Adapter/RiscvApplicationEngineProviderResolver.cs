// Copyright (C) 2015-2026 The Neo Project.
//
// RiscvApplicationEngineProviderResolver.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.

using System;
using System.IO;

namespace Neo.SmartContract.RiscV
{
    internal static class RiscvApplicationEngineProviderResolver
    {
        private static readonly object SyncRoot = new();
        private static string? _libraryPath;
        private static IApplicationEngineProvider? _provider;

        public static IApplicationEngineProvider ResolveRequiredProvider()
        {
            var libraryPath = ResolveLibraryPath()
                ?? throw new InvalidOperationException(
                    $"Neo requires the RISC-V host library. Set {NativeRiscvVmBridge.LibraryPathEnvironmentVariable}, " +
                    $"place the native library next to the application binaries, or ship it in Plugins/{typeof(RiscvAdapterPlugin).Assembly.GetName().Name}/.");

            lock (SyncRoot)
            {
                if (_provider is not null && string.Equals(_libraryPath, libraryPath, StringComparison.Ordinal))
                    return _provider;

                DisposeProvider(_provider);
                _provider = new RiscvApplicationEngineProvider(new NativeRiscvVmBridge(libraryPath));
                _libraryPath = libraryPath;
                return _provider;
            }
        }

        private static string? ResolveLibraryPath()
        {
            var configured = Environment.GetEnvironmentVariable(NativeRiscvVmBridge.LibraryPathEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            foreach (var candidate in GetDefaultCandidates())
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        // Kept internal to allow deterministic testing of path resolution without having to
        // actually load the native library.
        internal static string? ResolveLibraryPathForTesting() => ResolveLibraryPath();

        internal static string[] GetDefaultCandidatesForTesting() => GetDefaultCandidates();

        private static string[] GetDefaultCandidates()
        {
            var fileName = GetPlatformFileName();
            var assemblyName = typeof(RiscvAdapterPlugin).Assembly.GetName().Name ?? "Neo.Riscv.Adapter";
            return
            [
                // Preferred: ship the native library inside the adapter plugin folder.
                // This enables a "drop-in" Plugins bundle with no environment variables.
                Path.Combine(AppContext.BaseDirectory, "Plugins", assemblyName, fileName),
                Path.Combine(Environment.CurrentDirectory, "Plugins", assemblyName, fileName),
                Path.Combine(AppContext.BaseDirectory, fileName),
                Path.Combine(Environment.CurrentDirectory, fileName),
            ];
        }

        private static string GetPlatformFileName()
        {
            if (OperatingSystem.IsWindows())
                return "neo_riscv_host.dll";
            if (OperatingSystem.IsMacOS())
                return "libneo_riscv_host.dylib";
            return "libneo_riscv_host.so";
        }

        internal static void ResetForTesting()
        {
            lock (SyncRoot)
            {
                DisposeProvider(_provider);
                _provider = null;
                _libraryPath = null;
            }
        }

        private static void DisposeProvider(IApplicationEngineProvider? provider)
        {
            if (provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
