using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.SmartContract.RiscV;
using System;
using System.IO;

namespace Neo.Riscv.Adapter.Tests;

[TestClass]
public class UT_RiscvApplicationEngineProviderResolver
{
    [TestMethod]
    public void ResolveLibraryPath_PrefersPluginFolder_WhenEnvVarUnset()
    {
        var previous = Environment.GetEnvironmentVariable(NativeRiscvVmBridge.LibraryPathEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeRiscvVmBridge.LibraryPathEnvironmentVariable, null);
            RiscvApplicationEngineProviderResolver.ResetForTesting();

            var fileName = GetPlatformFileName();
            var pluginRoot = Path.Combine(AppContext.BaseDirectory, "Plugins", "Neo.Riscv.Adapter");
            Directory.CreateDirectory(pluginRoot);
            var expected = Path.Combine(pluginRoot, fileName);

            File.WriteAllText(expected, string.Empty);
            try
            {
                var actual = RiscvApplicationEngineProviderResolver.ResolveLibraryPathForTesting();
                Assert.AreEqual(expected, actual);
            }
            finally
            {
                File.Delete(expected);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(NativeRiscvVmBridge.LibraryPathEnvironmentVariable, previous);
            RiscvApplicationEngineProviderResolver.ResetForTesting();
        }
    }

    private static string GetPlatformFileName()
    {
        if (OperatingSystem.IsWindows())
            return "neo_riscv_host.dll";
        if (OperatingSystem.IsMacOS())
            return "libneo_riscv_host.dylib";
        return "libneo_riscv_host.so";
    }
}
