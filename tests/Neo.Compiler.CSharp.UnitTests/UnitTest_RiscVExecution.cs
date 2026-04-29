// Copyright (C) 2015-2026 The Neo Project.
//
// UnitTest_RiscVExecution.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.SmartContract.Testing;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Neo.Compiler.CSharp.UnitTests;

/// <summary>
/// Tests that compile C# contracts to RISC-V (.polkavm) binaries and
/// execute them directly via the native neo_riscv_host library, using
/// <see cref="RiscVExecutionBridge"/> for P/Invoke.
///
/// These tests validate the full C#-to-RISC-V pipeline:
///   1. C# contract -> NeoVM bytecode (compiler frontend)
///   2. NeoVM bytecode -> Rust source (NeoVmToRustTranslator)
///   3. Rust source -> .polkavm binary (cargo + polkatool)
///   4. .polkavm binary -> native execution (neo_riscv_host)
/// </summary>
[TestClass]
[TestCategory("RiscV")]
public class UnitTest_RiscVExecution
{
    private static bool s_runtimeAvailable;
    private static bool s_contractsBuilt;
    private static int s_freedNativeIntegerErrors;

    private static RiscVExecutionBridge.ResultStackItem IntegerArg(long value) => new()
    {
        Kind = 0,
        IntegerValue = value,
    };

    private static RiscVExecutionBridge.ResultStackItem ByteStringArg(string value) => new()
    {
        Kind = 1,
        Bytes = System.Text.Encoding.UTF8.GetBytes(value),
    };

    private static RiscVExecutionBridge.ExecutionResult ExecuteWithArgs(
        byte[] binary,
        string method,
        params RiscVExecutionBridge.ResultStackItem[] args)
    {
        return RiscVExecutionBridge.ExecuteWithHost(binary, method, args, null);
    }

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        // Step 1: Load the native library.
        try
        {
            RiscVExecutionBridge.Initialize();
            s_runtimeAvailable = true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DllNotFoundException)
        {
            Console.Error.WriteLine($"[RiscV] Native library not available: {ex.Message}");
            return;
        }

        // Step 2: Compile all test contracts to RISC-V and build .polkavm binaries.
        try
        {
            RiscVTestHelper.Initialize();
            s_contractsBuilt = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RiscV] Contract compilation failed: {ex.Message}");
        }
    }

    private static byte[] LoadBinary(string contractName)
    {
        var path = RiscVTestHelper.GetPolkaVmBinary(contractName);
        Assert.IsNotNull(path, $"Failed to build .polkavm binary for {contractName}");
        return File.ReadAllBytes(path);
    }

    private byte[] LoadBinaryOrSkip(string contractName)
    {
        RequireRuntime();
        var path = RiscVTestHelper.GetPolkaVmBinary(contractName);
        if (path == null)
        {
            Assert.Inconclusive($"{contractName} .polkavm binary not available.");
            return Array.Empty<byte>(); // unreachable
        }
        return File.ReadAllBytes(path);
    }

    private void RequireRuntime()
    {
        if (!s_runtimeAvailable)
            Assert.Inconclusive("RISC-V native library (libneo_riscv_host.so) not available.");
        if (!s_contractsBuilt)
            Assert.Inconclusive("RISC-V contract compilation toolchain not available.");
    }

    // -----------------------------------------------------------
    //  Contract_Assignment: pure computation, no syscalls needed
    // -----------------------------------------------------------

    [TestMethod]
    public void Contract_Assignment_TestAssignment()
    {
        RequireRuntime();
        var binary = LoadBinary("Contract_Assignment");
        var result = RiscVExecutionBridge.Execute(binary, "testAssignment");
        Assert.IsTrue(result.IsHalt,
            $"Expected HALT but got state={result.State}. Error: {result.Error}");
    }

    [TestMethod]
    public void Contract_Assignment_TestCoalesceAssignment()
    {
        RequireRuntime();
        var binary = LoadBinary("Contract_Assignment");
        var result = RiscVExecutionBridge.Execute(binary, "testCoalesceAssignment");
        // Note: Nullable coalescing (??=) generates complex control flow with
        // ISNULL/JMPIFNOT/DUP opcodes. If this fails with "invalid pc", it
        // indicates a translation gap in NeoVmToRustTranslator for that pattern.
        Assert.IsTrue(result.IsHalt,
            $"Expected HALT but got state={result.State}. Error: {result.Error}");
    }

    // -----------------------------------------------------------
    //  Contract_Boolean: boolean operations
    // -----------------------------------------------------------

    [TestMethod]
    public void Contract_Boolean_TestBooleanOr()
    {
        RequireRuntime();
        var path = RiscVTestHelper.GetPolkaVmBinary("Contract_Boolean");
        if (path == null)
        {
            Assert.Inconclusive("Contract_Boolean .polkavm binary not available.");
            return;
        }

        var binary = File.ReadAllBytes(path);

        // testBooleanOr() returns true (true || false)
        var result = RiscVExecutionBridge.Execute(binary, "testBooleanOr");
        Assert.IsTrue(result.IsHalt,
            $"testBooleanOr: Expected HALT but got state={result.State}. Error: {result.Error}");
        // Should return a boolean true
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(3u, result.Stack[0].Kind, "Result should be a Boolean (kind=3).");
        Assert.AreEqual(1L, result.Stack[0].IntegerValue, "Result should be true (1).");
    }

    // -----------------------------------------------------------
    //  Contract_Types: various primitive return types
    // -----------------------------------------------------------

    [TestMethod]
    public void Contract_Types_CheckBoolTrue()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Types");
        var result = RiscVExecutionBridge.Execute(binary, "checkBoolTrue");
        Assert.IsTrue(result.IsHalt,
            $"checkBoolTrue: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(3u, result.Stack[0].Kind, "Result should be Boolean (kind=3).");
        Assert.AreEqual(1L, result.Stack[0].IntegerValue, "Should be true.");
    }

    [TestMethod]
    public void Contract_Types_CheckBoolFalse()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Types");
        var result = RiscVExecutionBridge.Execute(binary, "checkBoolFalse");
        Assert.IsTrue(result.IsHalt,
            $"checkBoolFalse: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(3u, result.Stack[0].Kind, "Result should be Boolean (kind=3).");
        Assert.AreEqual(0L, result.Stack[0].IntegerValue, "Should be false.");
    }

    [TestMethod]
    public void Contract_Types_CheckInt()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Types");
        var result = RiscVExecutionBridge.Execute(binary, "checkInt");
        Assert.IsTrue(result.IsHalt,
            $"checkInt: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(0u, result.Stack[0].Kind, "Result should be Integer (kind=0).");
        Assert.AreEqual(5L, result.Stack[0].IntegerValue, "Should return 5.");
    }

    [TestMethod]
    public void Contract_Types_CheckString()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Types");
        var result = RiscVExecutionBridge.Execute(binary, "checkString");
        Assert.IsTrue(result.IsHalt,
            $"checkString: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(1u, result.Stack[0].Kind, "Result should be ByteString (kind=1).");
        var text = System.Text.Encoding.UTF8.GetString(result.Stack[0].Bytes!);
        Assert.AreEqual("neo", text, "Should return 'neo'.");
    }

    [TestMethod]
    public void Contract_Types_CheckNull()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Types");
        var result = RiscVExecutionBridge.Execute(binary, "checkNull");
        Assert.IsTrue(result.IsHalt,
            $"checkNull: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(2u, result.Stack[0].Kind, "Result should be Null (kind=2).");
    }

    [TestMethod]
    public void Contract_Types_CheckLong()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Types");
        var result = RiscVExecutionBridge.Execute(binary, "checkLong");
        Assert.IsTrue(result.IsHalt,
            $"checkLong: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(0u, result.Stack[0].Kind, "Result should be Integer (kind=0).");
        Assert.AreEqual(5L, result.Stack[0].IntegerValue, "Should return 5.");
    }

    // -----------------------------------------------------------
    //  Contract_Math: simple math operations
    // -----------------------------------------------------------

    [TestMethod]
    public void Contract_Math_Max()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Math");
        var result = ExecuteWithArgs(binary, "max", IntegerArg(1), IntegerArg(2));
        Assert.IsTrue(result.IsHalt,
            $"max: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(0u, result.Stack[0].Kind, "Result should be Integer (kind=0).");
        Assert.AreEqual(2L, result.Stack[0].IntegerValue, "max(1, 2) should return 2.");
    }

    [TestMethod]
    public void Contract_Math_Min()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Math");
        var result = ExecuteWithArgs(binary, "min", IntegerArg(1), IntegerArg(2));
        Assert.IsTrue(result.IsHalt,
            $"min: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(0u, result.Stack[0].Kind, "Result should be Integer (kind=0).");
        Assert.AreEqual(1L, result.Stack[0].IntegerValue, "min(1, 2) should return 1.");
    }

    [TestMethod]
    public void Contract_Math_Abs()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Math");
        var result = ExecuteWithArgs(binary, "abs", IntegerArg(-1));
        Assert.IsTrue(result.IsHalt,
            $"abs: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(0u, result.Stack[0].Kind, "Result should be Integer (kind=0).");
        Assert.AreEqual(1L, result.Stack[0].IntegerValue, "abs(-1) should return 1.");
    }

    // -----------------------------------------------------------
    //  Contract_Concat: string concatenation
    // -----------------------------------------------------------

    [TestMethod]
    public void Contract_Concat_TestStringAdd1()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Concat");
        var result = ExecuteWithArgs(binary, "testStringAdd1", ByteStringArg("neo"));
        Assert.IsTrue(result.IsHalt,
            $"testStringAdd1: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(1u, result.Stack[0].Kind, "Result should be ByteString (kind=1).");
        Assert.AreEqual("neohello", System.Text.Encoding.UTF8.GetString(result.Stack[0].Bytes!), "Concatenation result should match.");
    }

    // -----------------------------------------------------------
    //  Contract_Switch: switch statement control flow
    // -----------------------------------------------------------

    [TestMethod]
    public void Contract_Switch_SwitchLong()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Switch");
        var result = ExecuteWithArgs(binary, "switchLong", ByteStringArg("20"));
        Assert.IsTrue(result.IsHalt,
            $"switchLong: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(0u, result.Stack[0].Kind, "Result should be Integer (kind=0).");
        Assert.AreEqual(21L, result.Stack[0].IntegerValue, "switchLong(\"20\") should return 21.");
    }

    [TestMethod]
    public void Contract_Switch_Switch6()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Switch");
        var result = ExecuteWithArgs(binary, "switch6", ByteStringArg("5"));
        Assert.IsTrue(result.IsHalt,
            $"switch6: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(0u, result.Stack[0].Kind, "Result should be Integer (kind=0).");
        Assert.AreEqual(6L, result.Stack[0].IntegerValue, "switch6(\"5\") should return 6.");
    }

    // -----------------------------------------------------------
    //  Contract_Integer: integer utility methods
    // -----------------------------------------------------------

    [TestMethod]
    public void Contract_Integer_IsEvenIntegerInt()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Integer");
        var result = ExecuteWithArgs(binary, "isEvenIntegerInt", IntegerArg(4));
        Assert.IsTrue(result.IsHalt,
            $"isEvenIntegerInt: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(3u, result.Stack[0].Kind, "Result should be Boolean (kind=3).");
        Assert.AreEqual(1L, result.Stack[0].IntegerValue, "isEvenIntegerInt(4) should return true.");
    }

    [TestMethod]
    public void Contract_Integer_IsPow2Int()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_Integer");
        var result = ExecuteWithArgs(binary, "isPow2Int", IntegerArg(4));
        Assert.IsTrue(result.IsHalt,
            $"isPow2Int: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(3u, result.Stack[0].Kind, "Result should be Boolean (kind=3).");
        Assert.AreEqual(1L, result.Stack[0].IntegerValue, "isPow2Int(4) should return true.");
    }

    // -----------------------------------------------------------
    //  Contract_BigInteger: BigInteger operations
    // -----------------------------------------------------------

    [TestMethod]
    public void Contract_BigInteger_TestIsEven()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_BigInteger");
        var result = ExecuteWithArgs(binary, "testIsEven", IntegerArg(2));
        Assert.IsTrue(result.IsHalt,
            $"testIsEven: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(3u, result.Stack[0].Kind, "Result should be Boolean (kind=3).");
        Assert.AreEqual(1L, result.Stack[0].IntegerValue, "testIsEven(2) should return true.");
    }

    [TestMethod]
    public void Contract_BigInteger_TestAdd()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_BigInteger");
        var result = ExecuteWithArgs(binary, "testAdd", IntegerArg(123456789), IntegerArg(987654321));
        Assert.IsTrue(result.IsHalt,
            $"testAdd: Expected HALT but got state={result.State}. Error: {result.Error}");
        Assert.AreEqual(1, result.Stack.Length, "Should return one item.");
        Assert.AreEqual(0u, result.Stack[0].Kind, "Result should be Integer (kind=0).");
        Assert.AreEqual(1111111110L, result.Stack[0].IntegerValue, "testAdd should return the sum.");
    }

    // -----------------------------------------------------------
    //  Contract_MissingCheckWitness: Storage.Put via TestHostCallback
    // -----------------------------------------------------------

    [TestMethod]
    public void Contract_MissingCheckWitness_UnsafeUpdate()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_MissingCheckWitness");

        // UnsafeUpdate(byte[] key, byte[] value) does:
        //   Storage.Put(context, key, value)
        // Exercises Storage.GetContext + Storage.Put syscalls via TestHostCallback.
        var host = new RiscVExecutionBridge.TestHostCallback();
        var key = System.Text.Encoding.UTF8.GetBytes("mykey");
        var value = System.Text.Encoding.UTF8.GetBytes("myvalue");
        var initialArgs = new[]
        {
            new RiscVExecutionBridge.ResultStackItem { Kind = 1, Bytes = key },
            new RiscVExecutionBridge.ResultStackItem { Kind = 1, Bytes = value },
        };
        var result = host.Execute(binary, "unsafeUpdate", initialArgs);

        // Debug: dump called APIs
        var apiDump = string.Join(", ", host.CalledApis.Select(a => $"0x{a:X8}"));
        Console.WriteLine($"UnsafeUpdate: CalledApis=[{apiDump}], Storage.Count={host.Storage.Count}, IsHalt={result.IsHalt}, State={result.State}, Error={result.Error}");

        Assert.IsTrue(result.IsHalt,
            $"UnsafeUpdate: Expected HALT but got state={result.State}. Error: {result.Error}");

        // Verify storage was populated by the Put call
        Assert.AreEqual(1, host.Storage.Count, "Storage should have exactly one entry.");
        Assert.IsTrue(host.Storage.ContainsKey(key), "Storage should contain the key.");
        Assert.AreEqual("myvalue",
            System.Text.Encoding.UTF8.GetString(host.Storage[key]),
            "Storage value should match.");
    }

    [TestMethod]
    public void Contract_MissingCheckWitness_SafeUpdate_WithWitness()
    {
        RequireRuntime();
        var binary = LoadBinaryOrSkip("Contract_MissingCheckWitness");

        // SafeUpdate(UInt160 owner, byte[] key, byte[] value) does:
        //   ExecutionEngine.Assert(Runtime.CheckWitness(owner));
        //   Storage.Put(context, key, value)
        // TestHostCallback returns true for CheckWitness, so this should succeed.
        var host = new RiscVExecutionBridge.TestHostCallback();
        var owner = new byte[20]; // zero address, matches Runtime.GetExecutingScriptHash
        var key = System.Text.Encoding.UTF8.GetBytes("safekey");
        var value = System.Text.Encoding.UTF8.GetBytes("safevalue");
        var initialArgs = new[]
        {
            new RiscVExecutionBridge.ResultStackItem { Kind = 1, Bytes = owner },
            new RiscVExecutionBridge.ResultStackItem { Kind = 1, Bytes = key },
            new RiscVExecutionBridge.ResultStackItem { Kind = 1, Bytes = value },
        };
        var result = host.Execute(binary, "safeUpdate", initialArgs);

        var apiDump = string.Join(", ", host.CalledApis.Select(a => $"0x{a:X8}"));

        Assert.IsTrue(result.IsHalt,
            $"SafeUpdate: Expected HALT but got state={result.State}. Error: {result.Error}. CalledApis=[{apiDump}]");

        // Verify storage was populated
        Assert.AreEqual(1, host.Storage.Count,
            $"Storage should have one entry after SafeUpdate. CalledApis=[{apiDump}]");
        Assert.IsTrue(host.Storage.ContainsKey(key),
            $"Storage should contain the safekey. CalledApis=[{apiDump}]");
    }

    // -----------------------------------------------------------
    //  Negative test: invalid method name should fault
    // -----------------------------------------------------------

    [TestMethod]
    public void Execute_InvalidMethod_Faults()
    {
        RequireRuntime();
        var binary = LoadBinary("Contract_Assignment");
        var result = RiscVExecutionBridge.Execute(binary, "nonExistentMethod");
        Assert.IsTrue(result.IsFault,
            "Expected FAULT for non-existent method.");
        Assert.IsNotNull(result.Error,
            "Expected an error message for non-existent method.");
    }

    // -----------------------------------------------------------
    //  Negative test: garbage binary should fail gracefully
    // -----------------------------------------------------------

    [TestMethod]
    public void Execute_GarbageBinary_DoesNotCrash()
    {
        RequireRuntime();
        var garbage = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 };
        var result = RiscVExecutionBridge.Execute(garbage, "test");
        // Should fault (or at least not crash the process).
        Assert.IsTrue(result.IsFault,
            "Expected FAULT for garbage binary.");
    }

    [TestMethod]
    public void ExecuteBuiltin_ReturnsFault_WhenNativeAbiReturnsFalse()
    {
        var initializedField = GetBridgeField("s_initialized");
        var executeField = GetBridgeField("s_executeNativeContractBuiltin");
        var previousInitialized = initializedField.GetValue(null);
        var previousExecute = executeField.GetValue(null);

        try
        {
            initializedField.SetValue(null, true);
            executeField.SetValue(null, Delegate.CreateDelegate(
                executeField.FieldType,
                typeof(UnitTest_RiscVExecution).GetMethod(
                    nameof(ReturnFalseNativeContractBuiltin),
                    BindingFlags.NonPublic | BindingFlags.Static)!));

            var result = RiscVExecutionBridge.ExecuteBuiltin(new byte[] { 0x50, 0x56, 0x4d, 0x00 }, "test");

            Assert.IsTrue(result.IsFault, "Native ABI false should surface as FAULT.");
            Assert.AreEqual("neo_riscv_execute_native_contract_builtin returned false.", result.Error);
        }
        finally
        {
            executeField.SetValue(null, previousExecute);
            initializedField.SetValue(null, previousInitialized);
        }
    }

    [TestMethod]
    public void Execute_WithSerializedInitialStack_PassesPointerAndCountToNativeAbi()
    {
        var initializedField = GetBridgeField("s_initialized");
        var executeField = GetBridgeField("s_executeNativeContract");
        var previousInitialized = initializedField.GetValue(null);
        var previousExecute = executeField.GetValue(null);

        try
        {
            initializedField.SetValue(null, true);
            executeField.SetValue(null, Delegate.CreateDelegate(
                executeField.FieldType,
                typeof(UnitTest_RiscVExecution).GetMethod(
                    nameof(ValidateSerializedInitialStackNativeContract),
                    BindingFlags.NonPublic | BindingFlags.Static)!));

            var stack = SerializeNativeStackItems(new TestNativeStackItem
            {
                Kind = 0,
                IntegerValue = 42,
            });

            var result = RiscVExecutionBridge.Execute(
                new byte[] { 0x50, 0x56, 0x4d, 0x00 },
                "test",
                stack,
                initialStackItemCount: 1);

            Assert.IsTrue(result.IsHalt, result.Error);
        }
        finally
        {
            executeField.SetValue(null, previousExecute);
            initializedField.SetValue(null, previousInitialized);
        }
    }

    [TestMethod]
    public void ExecuteBuiltinInteger_FreesI64FaultErrorWithNativeFree()
    {
        var initializedField = GetBridgeField("s_initialized");
        var i64Field = GetBridgeField("s_executeNativeContractBuiltinI64ById");
        var byIdField = GetBridgeField("s_executeNativeContractBuiltinById");
        var freeField = GetBridgeField("s_freeExecutionResult");
        var previousInitialized = initializedField.GetValue(null);
        var previousI64 = i64Field.GetValue(null);
        var previousById = byIdField.GetValue(null);
        var previousFree = freeField.GetValue(null);

        try
        {
            s_freedNativeIntegerErrors = 0;
            initializedField.SetValue(null, true);
            i64Field.SetValue(null, Delegate.CreateDelegate(
                i64Field.FieldType,
                typeof(UnitTest_RiscVExecution).GetMethod(
                    nameof(ReturnFaultNativeI64ById),
                    BindingFlags.NonPublic | BindingFlags.Static)!));
            byIdField.SetValue(null, Delegate.CreateDelegate(
                byIdField.FieldType,
                typeof(UnitTest_RiscVExecution).GetMethod(
                    nameof(ReturnIntegerNativeById),
                    BindingFlags.NonPublic | BindingFlags.Static)!));
            freeField.SetValue(null, Delegate.CreateDelegate(
                freeField.FieldType,
                typeof(UnitTest_RiscVExecution).GetMethod(
                    nameof(FreeNativeExecutionResultForTest),
                    BindingFlags.NonPublic | BindingFlags.Static)!));

            var value = RiscVExecutionBridge.ExecuteBuiltinInteger(
                new byte[] { 0x50, 0x56, 0x4d, 0x00 },
                "test");

            Assert.AreEqual(7L, value);
            Assert.AreEqual(1, s_freedNativeIntegerErrors,
                "The i64 fast-path error pointer should be released by the native result free callback.");
        }
        finally
        {
            freeField.SetValue(null, previousFree);
            byIdField.SetValue(null, previousById);
            i64Field.SetValue(null, previousI64);
            initializedField.SetValue(null, previousInitialized);
            s_freedNativeIntegerErrors = 0;
        }
    }

    [TestMethod]
    public void HostCallbackInputBytes_AllowsEmptyByteStrings()
    {
        var method = typeof(RiscVExecutionBridge).GetMethod(
            "TryReadInputBytes",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing TryReadInputBytes.");
        var itemSize = Marshal.SizeOf<TestNativeStackItem>();
        var stackPtr = Marshal.AllocHGlobal(itemSize);

        try
        {
            Marshal.StructureToPtr(new TestNativeStackItem
            {
                Kind = 1,
                BytesPtr = IntPtr.Zero,
                BytesLen = 0,
            }, stackPtr, false);

            object?[] args = [stackPtr, (nuint)1, 0, null!];
            var ok = (bool)method.Invoke(null, args)!;

            Assert.IsTrue(ok, "Empty byte strings should be valid byte-like arguments.");
            CollectionAssert.AreEqual(Array.Empty<byte>(), (byte[])args[3]!);
        }
        finally
        {
            Marshal.FreeHGlobal(stackPtr);
        }
    }

    private static FieldInfo GetBridgeField(string name)
    {
        return typeof(RiscVExecutionBridge).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing RiscVExecutionBridge field: {name}");
    }

    private static bool ReturnFalseNativeContractBuiltin(
        IntPtr binaryPtr,
        nuint binaryLen,
        IntPtr methodPtr,
        nuint methodLen,
        IntPtr initialStackPtr,
        nuint initialStackLen,
        byte trigger,
        uint network,
        byte addressVersion,
        ulong timestamp,
        long gasLeft,
        long execFeeFactorPico,
        IntPtr output)
    {
        return false;
    }

    private static bool ValidateSerializedInitialStackNativeContract(
        IntPtr binaryPtr,
        nuint binaryLen,
        IntPtr methodPtr,
        nuint methodLen,
        IntPtr initialStackPtr,
        nuint initialStackLen,
        byte trigger,
        uint network,
        byte addressVersion,
        ulong timestamp,
        long gasLeft,
        long execFeeFactorPico,
        IntPtr userData,
        IntPtr hostCallback,
        IntPtr hostFree,
        IntPtr output)
    {
        var valid = initialStackPtr != IntPtr.Zero && initialStackLen == 1;
        if (valid)
        {
            var item = Marshal.PtrToStructure<TestNativeStackItem>(initialStackPtr);
            valid = item.Kind == 0 && item.IntegerValue == 42;
        }

        Marshal.StructureToPtr(new TestNativeExecutionResult
        {
            State = valid ? 0u : 1u,
        }, output, false);
        return true;
    }

    private static bool ReturnFaultNativeI64ById(
        IntPtr binaryPtr,
        nuint binaryLen,
        uint methodId,
        byte trigger,
        uint network,
        byte addressVersion,
        ulong timestamp,
        long gasLeft,
        long execFeeFactorPico,
        IntPtr output)
    {
        var errorBytes = System.Text.Encoding.UTF8.GetBytes("i64 path fault");
        var errorPtr = Marshal.AllocHGlobal(errorBytes.Length);
        Marshal.Copy(errorBytes, 0, errorPtr, errorBytes.Length);
        Marshal.StructureToPtr(new TestNativeIntegerExecutionResult
        {
            State = 1,
            ErrorPtr = errorPtr,
            ErrorLen = (nuint)errorBytes.Length,
        }, output, false);
        return true;
    }

    private static bool ReturnIntegerNativeById(
        IntPtr binaryPtr,
        nuint binaryLen,
        uint methodId,
        IntPtr initialStackPtr,
        nuint initialStackLen,
        byte trigger,
        uint network,
        byte addressVersion,
        ulong timestamp,
        long gasLeft,
        long execFeeFactorPico,
        IntPtr output)
    {
        var itemSize = Marshal.SizeOf<TestNativeStackItem>();
        var stackPtr = Marshal.AllocHGlobal(itemSize);
        Marshal.StructureToPtr(new TestNativeStackItem
        {
            Kind = 0,
            IntegerValue = 7,
        }, stackPtr, false);
        Marshal.StructureToPtr(new TestNativeExecutionResult
        {
            State = 0,
            StackPtr = stackPtr,
            StackLen = 1,
        }, output, false);
        return true;
    }

    private static void FreeNativeExecutionResultForTest(IntPtr resultPtr)
    {
        var result = Marshal.PtrToStructure<TestNativeExecutionResult>(resultPtr);
        if (result.ErrorPtr != IntPtr.Zero)
        {
            Interlocked.Increment(ref s_freedNativeIntegerErrors);
            Marshal.FreeHGlobal(result.ErrorPtr);
        }
        if (result.StackPtr != IntPtr.Zero)
            Marshal.FreeHGlobal(result.StackPtr);
    }

    private static byte[] SerializeNativeStackItems(params TestNativeStackItem[] items)
    {
        var itemSize = Marshal.SizeOf<TestNativeStackItem>();
        var bytes = new byte[itemSize * items.Length];
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            for (var index = 0; index < items.Length; index++)
                Marshal.StructureToPtr(items[index], IntPtr.Add(ptr, index * itemSize), false);
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestNativeStackItem
    {
        public uint Kind;
        public long IntegerValue;
        public IntPtr BytesPtr;
        public nuint BytesLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestNativeExecutionResult
    {
        public long FeeConsumedPico;
        public uint State;
        public IntPtr StackPtr;
        public nuint StackLen;
        public IntPtr ErrorPtr;
        public nuint ErrorLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestNativeIntegerExecutionResult
    {
        public long FeeConsumedPico;
        public uint State;
        public long Value;
        public IntPtr ErrorPtr;
        public nuint ErrorLen;
    }

    // -----------------------------------------------------------
    //  Test result stack reading
    // -----------------------------------------------------------

    [TestMethod]
    public void Contract_Assignment_ResultStackIsEmpty()
    {
        RequireRuntime();
        var binary = LoadBinary("Contract_Assignment");
        var result = RiscVExecutionBridge.Execute(binary, "testAssignment");
        Assert.IsTrue(result.IsHalt,
            $"Expected HALT. Error: {result.Error}");
        // testAssignment() returns void, so result stack should be empty.
        Assert.AreEqual(0, result.Stack.Length,
            "void method should produce empty result stack.");
    }
}
