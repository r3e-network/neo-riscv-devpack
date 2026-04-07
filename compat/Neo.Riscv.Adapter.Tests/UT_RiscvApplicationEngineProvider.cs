using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Persistence.Providers;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.SmartContract.RiscV;
using System;

namespace Neo.Riscv.Adapter.Tests;

[TestClass]
public class UT_RiscvApplicationEngineProvider
{
    private string? _previousLibraryPath;

    [TestInitialize]
    public void TestSetup()
    {
        _previousLibraryPath = Environment.GetEnvironmentVariable(NativeRiscvVmBridge.LibraryPathEnvironmentVariable);
        ApplicationEngine.Provider = null;
        RiscvApplicationEngineProviderResolver.ResetForTesting();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        ApplicationEngine.Provider = null;
        Environment.SetEnvironmentVariable(NativeRiscvVmBridge.LibraryPathEnvironmentVariable, _previousLibraryPath);
        RiscvApplicationEngineProviderResolver.ResetForTesting();
    }

    [TestMethod]
    public void TestConfiguredRiscvProviderBecomesDefault()
    {
        var libraryPath = Environment.GetEnvironmentVariable(NativeRiscvVmBridge.LibraryPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(libraryPath))
            Assert.Inconclusive($"{NativeRiscvVmBridge.LibraryPathEnvironmentVariable} is not set.");

        ApplicationEngine.Provider = RiscvApplicationEngineProviderResolver.ResolveRequiredProvider();
        using var system = new NeoSystem(AdapterTestProtocolSettings.Default, new MemoryStoreProvider());
        using var snapshot = system.GetSnapshotCache();
        using var appEngine = ApplicationEngine.Create(
            TriggerType.Application,
            null,
            snapshot,
            gas: 0,
            settings: AdapterTestProtocolSettings.Default);

        Assert.AreEqual(typeof(RiscvApplicationEngine).FullName, appEngine.GetType().FullName);
    }

    [TestMethod]
    public void TestConfiguredRiscvProviderBootstrapsNativeState()
    {
        var libraryPath = Environment.GetEnvironmentVariable(NativeRiscvVmBridge.LibraryPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(libraryPath))
            Assert.Inconclusive($"{NativeRiscvVmBridge.LibraryPathEnvironmentVariable} is not set.");

        ApplicationEngine.Provider = RiscvApplicationEngineProviderResolver.ResolveRequiredProvider();
        using var system = new NeoSystem(AdapterTestProtocolSettings.Default, new MemoryStoreProvider());
        using var snapshot = system.GetSnapshotCache();

        Assert.IsNotNull(NativeContract.ContractManagement.GetContract(snapshot, NativeContract.NEO.Hash));
        Assert.IsNotNull(NativeContract.ContractManagement.GetContract(snapshot, NativeContract.GAS.Hash));
        Assert.AreEqual(system.GenesisBlock.Hash, NativeContract.Ledger.CurrentHash(snapshot));
    }

    [TestMethod]
    public void TestExplicitRiscvProviderBootstrapsNativeState()
    {
        var libraryPath = Environment.GetEnvironmentVariable(NativeRiscvVmBridge.LibraryPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(libraryPath))
            Assert.Inconclusive($"{NativeRiscvVmBridge.LibraryPathEnvironmentVariable} is not set.");

        ApplicationEngine.Provider = new RiscvApplicationEngineProvider(new NativeRiscvVmBridge(libraryPath));
        using var system = new NeoSystem(AdapterTestProtocolSettings.Default, new MemoryStoreProvider());
        using var snapshot = system.GetSnapshotCache();

        Assert.IsNotNull(NativeContract.ContractManagement.GetContract(snapshot, NativeContract.NEO.Hash));
        Assert.IsNotNull(NativeContract.ContractManagement.GetContract(snapshot, NativeContract.GAS.Hash));
        Assert.AreEqual(system.GenesisBlock.Hash, NativeContract.Ledger.CurrentHash(snapshot));
    }
}
