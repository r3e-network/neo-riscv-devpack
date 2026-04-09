using Neo.VM;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Neo.Test;

internal sealed class RiscvVmRunner : IDisposable
{
    private const string LibraryPathEnvironmentVariable = "NEO_RISCV_HOST_LIB";
    private const string DefaultLibraryName = "libneo_riscv_host.so";
    private const string PreferDebugLibraryEnvironmentVariable = "NEO_RISCV_TEST_PREFER_DEBUG_LIB";

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeExecutionResult
    {
        public long FeeConsumedPico;
        public uint State;
        public IntPtr StackPtr;
        public nuint StackLen;
        public IntPtr ErrorPtr;
        public nuint ErrorLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStackItem
    {
        public uint Kind;
        public long IntegerValue;
        public IntPtr BytesPtr;
        public nuint BytesLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeHostResult
    {
        public IntPtr StackPtr;
        public nuint StackLen;
        public IntPtr ErrorPtr;
        public nuint ErrorLen;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool ExecuteScriptWithHostDelegate(
        IntPtr scriptPtr,
        nuint scriptLen,
        nuint initialInstructionPointer,
        byte trigger,
        uint networkMagic,
        byte addressVersion,
        ulong persistingTimestamp,
        long gasLeft,
        long execFeeFactorPico,
        IntPtr initialStackPtr,
        nuint initialStackLen,
        IntPtr userData,
        IntPtr hostCallback,
        IntPtr hostFree,
        out NativeExecutionResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool HostCallbackDelegate(
        IntPtr userData,
        uint api,
        nuint instructionPointer,
        byte trigger,
        uint networkMagic,
        byte addressVersion,
        ulong persistingTimestamp,
        long gasLeft,
        IntPtr inputStackPtr,
        nuint inputStackLen,
        out NativeHostResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HostFreeCallbackDelegate(IntPtr userData, ref NativeHostResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeExecutionResultDelegate(ref NativeExecutionResult result);

    private sealed class CallbackState
    {
        public required RiscvVmRunner Runner { get; init; }
    }

    private readonly IntPtr _libraryHandle;
    private readonly ExecuteScriptWithHostDelegate _executeScriptWithHost;
    private readonly FreeExecutionResultDelegate _freeExecutionResult;
    private readonly HostCallbackDelegate _hostCallback;
    private readonly HostFreeCallbackDelegate _hostFreeCallback;
    private readonly IntPtr _hostCallbackPtr;
    private readonly IntPtr _hostFreeCallbackPtr;
    private ulong _nextInteropHandle = 1;
    private readonly Dictionary<ulong, string> _interopTypeNames = new();
    private readonly GCHandle _callbackStateHandle;
    private readonly IntPtr _callbackStatePtr;

    public string LibraryPath { get; }

    public RiscvVmRunner(string libraryPath)
    {
        LibraryPath = libraryPath;
        _hostCallback = StaticHostCallback;
        _hostFreeCallback = StaticHostFreeCallback;
        _hostCallbackPtr = Marshal.GetFunctionPointerForDelegate(_hostCallback);
        _hostFreeCallbackPtr = Marshal.GetFunctionPointerForDelegate(_hostFreeCallback);

        _libraryHandle = NativeLibrary.Load(libraryPath);
        _executeScriptWithHost = Marshal.GetDelegateForFunctionPointer<ExecuteScriptWithHostDelegate>(
            NativeLibrary.GetExport(_libraryHandle, "neo_riscv_execute_script_with_host"));
        _freeExecutionResult = Marshal.GetDelegateForFunctionPointer<FreeExecutionResultDelegate>(
            NativeLibrary.GetExport(_libraryHandle, "neo_riscv_free_execution_result"));

        _callbackStateHandle = GCHandle.Alloc(new CallbackState { Runner = this });
        _callbackStatePtr = GCHandle.ToIntPtr(_callbackStateHandle);
    }

    public static RiscvVmRunner CreateFromEnvironment()
    {
        return new RiscvVmRunner(ResolveRequiredLibraryPath());
    }

    internal static string ResolveRequiredLibraryPath()
    {
        var libraryPath = ResolveLibraryPath();
        if (libraryPath is null)
        {
            Assert.Inconclusive(
                $"{LibraryPathEnvironmentVariable} is not set and the RISC-V host library could not be located. " +
                $"Set {LibraryPathEnvironmentVariable} explicitly, or build it with `cargo build -p neo-riscv-host --release`. " +
                $"Tried: {string.Join(", ", CandidateLibraryPaths())}");
        }

        return libraryPath;
    }

    private static string? ResolveLibraryPath()
    {
        var env = Environment.GetEnvironmentVariable(LibraryPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (File.Exists(env))
            {
                // Debug builds of the host library can make the corpus suite painfully slow.
                // If a matching release library exists, prefer it unless explicitly overridden.
                if (!PreferDebugLibrary() && TryResolveReleaseLibraryPath(env) is string releasePath)
                {
                    return releasePath;
                }

                return env;
            }

            Assert.Inconclusive($"{LibraryPathEnvironmentVariable} is set but the file does not exist: {env}");
        }

        foreach (var candidate in CandidateLibraryPaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool PreferDebugLibrary()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable(PreferDebugLibraryEnvironmentVariable),
            "1",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryResolveReleaseLibraryPath(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var needle = $"{Path.DirectorySeparatorChar}debug{Path.DirectorySeparatorChar}";
        var idx = normalized.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var candidate = normalized[..idx] + $"{Path.DirectorySeparatorChar}release{Path.DirectorySeparatorChar}" + normalized[(idx + needle.Length)..];
        return File.Exists(candidate) ? candidate : null;
    }

    private static IEnumerable<string> CandidateLibraryPaths()
    {
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            yield return Path.Combine(baseDir, DefaultLibraryName);
        }

        yield return Path.Combine(Environment.CurrentDirectory, DefaultLibraryName);

        // Common Rust build outputs when running `dotnet test` from the repo root.
        foreach (var root in EnumerateCandidateRoots(Environment.CurrentDirectory))
        {
            yield return Path.Combine(root, "target", "release", DefaultLibraryName);
            yield return Path.Combine(root, "target", "debug", DefaultLibraryName);
        }

        foreach (var root in EnumerateCandidateRoots(AppContext.BaseDirectory))
        {
            yield return Path.Combine(root, "target", "release", DefaultLibraryName);
            yield return Path.Combine(root, "target", "debug", DefaultLibraryName);
        }
    }

    private static IEnumerable<string> EnumerateCandidateRoots(string? startDir)
    {
        if (string.IsNullOrWhiteSpace(startDir))
        {
            yield break;
        }

        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(startDir);
        }
        catch
        {
            yield break;
        }

        for (var i = 0; i < 6 && dir is not null; i++)
        {
            yield return dir.FullName;
            dir = dir.Parent;
        }
    }

    public ExecutionOutcome Execute(byte[] script)
    {
        if (script is null) throw new ArgumentNullException(nameof(script));

        _nextInteropHandle = 1;
        _interopTypeNames.Clear();

        var pinnedScript = GCHandle.Alloc(script, GCHandleType.Pinned);
        NativeExecutionResult nativeResult = default;

        try
        {
            var scriptPtr = pinnedScript.AddrOfPinnedObject();
            if (!_executeScriptWithHost(
                    scriptPtr,
                    (nuint)script.Length,
                    0,
                    0,
                    0,
                    0,
                    0,
                    long.MaxValue / 4,
                    0,
                    IntPtr.Zero,
                    0,
                    _callbackStatePtr,
                    _hostCallbackPtr,
                    _hostFreeCallbackPtr,
                    out nativeResult))
            {
                throw new InvalidOperationException("Native RISC-V ABI call failed.");
            }

            var state = nativeResult.State == 0 ? VMState.HALT : VMState.FAULT;
            var faultMessage = nativeResult.ErrorPtr == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUTF8(nativeResult.ErrorPtr, checked((int)nativeResult.ErrorLen));

            return new ExecutionOutcome(
                state,
                ReadStackAsJson(nativeResult.StackPtr, nativeResult.StackLen),
                faultMessage);
        }
        finally
        {
            if (nativeResult.StackPtr != IntPtr.Zero || nativeResult.ErrorPtr != IntPtr.Zero)
            {
                _freeExecutionResult(ref nativeResult);
            }

            if (pinnedScript.IsAllocated)
            {
                pinnedScript.Free();
            }
        }
    }

    public void Dispose()
    {
        if (_callbackStateHandle.IsAllocated)
        {
            _callbackStateHandle.Free();
        }

        if (_libraryHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_libraryHandle);
        }
    }

    private static bool StaticHostCallback(
        IntPtr userData,
        uint api,
        nuint instructionPointer,
        byte trigger,
        uint networkMagic,
        byte addressVersion,
        ulong persistingTimestamp,
        long gasLeft,
        IntPtr inputStackPtr,
        nuint inputStackLen,
        out NativeHostResult result)
    {
        var handle = GCHandle.FromIntPtr(userData);
        var state = (CallbackState)handle.Target!;
        return state.Runner.HandleHostCallback(api, inputStackPtr, inputStackLen, out result);
    }

    private bool HandleHostCallback(uint api, IntPtr inputStackPtr, nuint inputStackLen, out NativeHostResult result)
    {
        var inputStack = ReadStackAsJson(inputStackPtr, inputStackLen);

        if (api == 0x77777777)
        {
            var next = inputStack.ToList();
            next.Add(CreateInteropJson(RegisterInterop("Object"), "Object"));
            result = CreateNativeHostResult(next);
            return true;
        }

        if (api == 0xaddeadde)
        {
            result = CreateNativeHostError("error");
            return true;
        }

        result = CreateNativeHostError($"unsupported syscall 0x{api:x8}");
        return true;
    }

    private static void StaticHostFreeCallback(IntPtr userData, ref NativeHostResult result)
    {
        FreeNativeHostResult(result);
        result.StackPtr = IntPtr.Zero;
        result.StackLen = 0;
        result.ErrorPtr = IntPtr.Zero;
        result.ErrorLen = 0;
    }

    private NativeHostResult CreateNativeHostResult(IReadOnlyList<JObject> stack)
    {
        if (stack.Count == 0)
        {
            return new NativeHostResult
            {
                StackPtr = IntPtr.Zero,
                StackLen = 0,
                ErrorPtr = IntPtr.Zero,
                ErrorLen = 0,
            };
        }

        var itemSize = Marshal.SizeOf<NativeStackItem>();
        var stackPtr = Marshal.AllocHGlobal(itemSize * stack.Count);

        for (var index = 0; index < stack.Count; index++)
        {
            var nativeItem = CreateNativeStackItem(stack[index]);
            Marshal.StructureToPtr(nativeItem, IntPtr.Add(stackPtr, index * itemSize), false);
        }

        return new NativeHostResult
        {
            StackPtr = stackPtr,
            StackLen = (nuint)stack.Count,
            ErrorPtr = IntPtr.Zero,
            ErrorLen = 0,
        };
    }

    private static NativeHostResult CreateNativeHostError(string message)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message);
        var errorPtr = bytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(bytes.Length);
        if (bytes.Length > 0)
        {
            Marshal.Copy(bytes, 0, errorPtr, bytes.Length);
        }

        return new NativeHostResult
        {
            StackPtr = IntPtr.Zero,
            StackLen = 0,
            ErrorPtr = errorPtr,
            ErrorLen = (nuint)bytes.Length,
        };
    }

    private NativeStackItem CreateNativeStackItem(JObject item)
    {
        var type = item["type"]?.Value<string>() ?? throw new InvalidOperationException("Stack item type is required.");
        return type switch
        {
            "Null" => new NativeStackItem { Kind = 2 },
            "Boolean" => new NativeStackItem { Kind = 3, IntegerValue = item["value"]!.Value<bool>() ? 1 : 0 },
            "Integer" => new NativeStackItem { Kind = 0, IntegerValue = item["value"]!.Value<long>() },
            "ByteString" => CreateByteStringItem(ParseByteStringToken(item["value"])),
            "Buffer" => CreateBufferItem(ParseByteStringToken(item["value"])),
            "Pointer" => new NativeStackItem { Kind = 10, IntegerValue = item["value"]!.Value<int>() },
            "Array" => CreateArrayLikeItem(4, (JArray)item["value"]!),
            "Struct" => CreateArrayLikeItem(7, (JArray)item["value"]!),
            "Map" => CreateMapItem((JObject)item["value"]!),
            "Interop" => new NativeStackItem
            {
                Kind = 9,
                IntegerValue = checked((long)(item["_handle"]?.Value<ulong>() ?? item["value"]!.Value<ulong>()))
            },
            _ => throw new InvalidOperationException($"Unsupported compatibility stack item type: {type}.")
        };
    }

    private static NativeStackItem CreateByteStringItem(byte[] bytes)
    {
        var bytesPtr = bytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(bytes.Length);
        if (bytes.Length > 0)
        {
            Marshal.Copy(bytes, 0, bytesPtr, bytes.Length);
        }

        return new NativeStackItem
        {
            Kind = 1,
            IntegerValue = 0,
            BytesPtr = bytesPtr,
            BytesLen = (nuint)bytes.Length
        };
    }

    private static NativeStackItem CreateBufferItem(byte[] bytes)
    {
        var bytesPtr = bytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(bytes.Length);
        if (bytes.Length > 0)
        {
            Marshal.Copy(bytes, 0, bytesPtr, bytes.Length);
        }

        return new NativeStackItem
        {
            Kind = 11,
            IntegerValue = 0,
            BytesPtr = bytesPtr,
            BytesLen = (nuint)bytes.Length
        };
    }

    private NativeStackItem CreateArrayLikeItem(uint kind, JArray array)
    {
        var children = array.Select(token => (JObject)token).ToArray();
        var nested = CreateNativeHostResult(children);
        return new NativeStackItem
        {
            Kind = kind,
            IntegerValue = 0,
            BytesPtr = nested.StackPtr,
            BytesLen = nested.StackLen
        };
    }

    private NativeStackItem CreateMapItem(JObject map)
    {
        var entries = new List<JObject>();
        foreach (var property in map.Properties())
        {
            entries.Add(new JObject
            {
                ["type"] = "ByteString",
                ["value"] = property.Name
            });
            entries.Add((JObject)property.Value);
        }

        var nested = CreateNativeHostResult(entries);
        return new NativeStackItem
        {
            Kind = 8,
            IntegerValue = 0,
            BytesPtr = nested.StackPtr,
            BytesLen = nested.StackLen
        };
    }

    private static void FreeNativeHostResult(NativeHostResult result)
    {
        if (result.StackPtr != IntPtr.Zero)
        {
            FreeNativeStackItems(result.StackPtr, result.StackLen);
        }

        if (result.ErrorPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(result.ErrorPtr);
        }
    }

    private static void FreeNativeStackItems(IntPtr stackPtr, nuint stackLen)
    {
        var itemSize = Marshal.SizeOf<NativeStackItem>();
        for (var index = 0; index < (int)stackLen; index++)
        {
            var itemPtr = IntPtr.Add(stackPtr, index * itemSize);
            var item = Marshal.PtrToStructure<NativeStackItem>(itemPtr);
            switch (item.Kind)
            {
                case 1:
                case 5:
                    if (item.BytesPtr != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(item.BytesPtr);
                    }
                    break;
                case 4:
                case 7:
                case 8:
                    if (item.BytesPtr != IntPtr.Zero)
                    {
                        FreeNativeStackItems(item.BytesPtr, item.BytesLen);
                    }
                    break;
            }
        }

        Marshal.FreeHGlobal(stackPtr);
    }

    private List<JObject> ReadStackAsJson(IntPtr stackPtr, nuint stackLen)
    {
        var items = new List<JObject>((int)stackLen);
        if (stackPtr == IntPtr.Zero || stackLen == 0)
        {
            return items;
        }

        var itemSize = Marshal.SizeOf<NativeStackItem>();
        for (var index = 0; index < (int)stackLen; index++)
        {
            var nativeItem = Marshal.PtrToStructure<NativeStackItem>(IntPtr.Add(stackPtr, index * itemSize));
            items.Add(ReadStackItemAsJson(nativeItem));
        }

        return items;
    }

    private JObject ReadStackItemAsJson(NativeStackItem item)
    {
        return item.Kind switch
        {
            0 => new JObject { ["type"] = "Integer", ["value"] = item.IntegerValue.ToString() },
            1 => new JObject { ["type"] = "ByteString", ["value"] = ByteArrayToHex(ReadBytes(item.BytesPtr, item.BytesLen)) },
            11 => new JObject { ["type"] = "Buffer", ["value"] = ByteArrayToHex(ReadBytes(item.BytesPtr, item.BytesLen)) },
            2 => new JObject { ["type"] = "Null" },
            3 => new JObject { ["type"] = "Boolean", ["value"] = item.IntegerValue != 0 },
            4 => new JObject { ["type"] = "Array", ["value"] = new JArray(ReadStackAsJson(item.BytesPtr, item.BytesLen)) },
            5 => new JObject { ["type"] = "Integer", ["value"] = new System.Numerics.BigInteger(ReadBytes(item.BytesPtr, item.BytesLen)).ToString() },
            7 => new JObject { ["type"] = "Struct", ["value"] = new JArray(ReadStackAsJson(item.BytesPtr, item.BytesLen)) },
            8 => ReadMapAsJson(item),
            9 => CreateInteropJson((ulong)item.IntegerValue, ResolveInteropTypeName((ulong)item.IntegerValue)),
            10 => new JObject { ["type"] = "Pointer", ["value"] = (int)item.IntegerValue },
            _ => throw new InvalidOperationException($"Unsupported native stack item kind {item.Kind}.")
        };
    }

    private JObject ReadMapAsJson(NativeStackItem item)
    {
        var children = ReadStackAsJson(item.BytesPtr, item.BytesLen);
        var map = new JObject();
        for (var i = 0; i < children.Count; i += 2)
        {
            var key = children[i];
            var keyHex = NormalizeMapKey(key);
            map[keyHex] = children[i + 1];
        }

        return new JObject
        {
            ["type"] = "Map",
            ["value"] = map
        };
    }

    private ulong RegisterInterop(string typeName)
    {
        var handle = _nextInteropHandle++;
        _interopTypeNames[handle] = typeName;
        return handle;
    }

    private string ResolveInteropTypeName(ulong handle)
    {
        return _interopTypeNames.TryGetValue(handle, out var name) ? name : "Object";
    }

    private static JObject CreateInteropJson(ulong handle, string typeName)
    {
        return new JObject
        {
            ["type"] = "Interop",
            ["value"] = typeName,
            ["_handle"] = handle
        };
    }

    private static byte[] ReadBytes(IntPtr ptr, nuint len)
    {
        if (ptr == IntPtr.Zero || len == 0)
        {
            return Array.Empty<byte>();
        }

        var bytes = new byte[(int)len];
        Marshal.Copy(ptr, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] ParseByteStringToken(JToken? token)
    {
        if (token is null)
            return Array.Empty<byte>();

        var value = token.Value<string>() ?? string.Empty;
        return Neo.Test.Extensions.StringExtensions.FromHexString(value);
    }

    private static string ByteArrayToHex(byte[] value)
    {
        return value.Length == 0 ? "0x" : Neo.Test.Extensions.StringExtensions.ToHexString(value);
    }

    private static string NormalizeMapKey(JObject key)
    {
        var type = key["type"]?.Value<string>() ?? throw new InvalidOperationException("Map key type is required.");
        return type switch
        {
            "Integer" => NormalizeScalarKey(long.Parse(key["value"]!.Value<string>()!)),
            "Boolean" => ByteArrayToHex(new[] { key["value"]!.Value<bool>() ? (byte)1 : (byte)0 }),
            "ByteString" => key["value"]!.Value<string>() ?? "0x",
            "Null" => "0x",
            _ => throw new InvalidOperationException($"Unsupported map key type: {type}.")
        };
    }

    private static string NormalizeScalarKey(long value)
    {
        return value == 0 ? string.Empty : ByteArrayToHex(EncodeInteger(value));
    }

    private static byte[] EncodeInteger(long value)
    {
        if (value == 0)
        {
            return new byte[] { 0 };
        }

        var bytes = BitConverter.GetBytes(value).ToList();
        if (value > 0)
        {
            while (bytes.Count > 1 && bytes[^1] == 0)
            {
                if ((bytes[^2] & 0x80) != 0)
                {
                    break;
                }
                bytes.RemoveAt(bytes.Count - 1);
            }
        }
        else
        {
            while (bytes.Count > 1 && bytes[^1] == 0xff)
            {
                if ((bytes[^2] & 0x80) == 0)
                {
                    break;
                }
                bytes.RemoveAt(bytes.Count - 1);
            }
        }

        return bytes.ToArray();
    }
}

internal sealed record ExecutionOutcome(VMState State, IReadOnlyList<JObject> ResultStack, string? FaultMessage);
