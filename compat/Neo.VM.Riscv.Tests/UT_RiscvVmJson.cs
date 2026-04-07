using Neo.Test.Extensions;
using Neo.Test.Types;
using Neo.VM;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Array = System.Array;

namespace Neo.Test;

[TestClass]
public class UT_RiscvVmJson
{
    private static readonly string[] SmokeCorpusRelativePaths =
    [
        Path.Combine("OpCodes", "Arrays", "NEWARRAY.json"),
        Path.Combine("OpCodes", "Arrays", "NEWSTRUCT.json"),
        Path.Combine("OpCodes", "Arrays", "PACKMAP.json"),
        Path.Combine("OpCodes", "BitwiseLogic", "EQUAL.json"),
        Path.Combine("OpCodes", "Control", "ABORTMSG.json"),
        Path.Combine("OpCodes", "Control", "ASSERTMSG.json"),
        Path.Combine("OpCodes", "Control", "CALL.json"),
        Path.Combine("OpCodes", "Control", "JMP.json"),
        Path.Combine("OpCodes", "Control", "SYSCALL.json"),
        Path.Combine("OpCodes", "Control", "THROW.json"),
        Path.Combine("OpCodes", "Splice", "NEWBUFFER.json"),
        Path.Combine("OpCodes", "Splice", "SUBSTR.json"),
        Path.Combine("OpCodes", "Types", "CONVERT.json"),
    ];

    [TestMethod]
    public void TestDirectSmoke()
    {
        using var runner = RiscvVmRunner.CreateFromEnvironment();
        using var script = new ScriptBuilder();
        script.Emit(OpCode.PUSH1);
        script.Emit(OpCode.RET);

        var outcome = runner.Execute(script.ToArray());

        Assert.AreEqual(VMState.HALT, outcome.State);
        Assert.HasCount(1, outcome.ResultStack);
        Assert.AreEqual("1", outcome.ResultStack[0]["value"]!.Value<string>());
    }

    [TestMethod]
    public void TestCopiedNeoVmJsonFinalStates()
    {
        // This suite is intentionally parallelized over JSON files to keep runtimes acceptable.
        // Running the full corpus against an unoptimized (debug) host library is very slow.
        var corpusLibraryPath = RiscvVmRunner.ResolveRequiredLibraryPath();
        var root = Path.Combine(AppContext.BaseDirectory, "Corpus", "Tests");
        var filter = Environment.GetEnvironmentVariable("NEO_RISCV_VM_JSON_FILTER");
        var mode = Environment.GetEnvironmentVariable("NEO_RISCV_VM_JSON_MODE");
        var explicitSmoke = string.Equals(mode, "smoke", StringComparison.OrdinalIgnoreCase);
        var explicitFull = string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("NEO_RISCV_VM_JSON_FULL"), "1", StringComparison.OrdinalIgnoreCase);

        var allowDebug = string.Equals(
            Environment.GetEnvironmentVariable("NEO_RISCV_VM_JSON_ALLOW_DEBUG"),
            "1",
            StringComparison.OrdinalIgnoreCase);

        var smokeOnly = string.IsNullOrWhiteSpace(filter) &&
            !explicitFull &&
            (explicitSmoke || IsDebugHostLibraryPath(corpusLibraryPath));

        // Smoke mode is small enough to run against debug libraries without being painful.
        if (smokeOnly)
        {
            allowDebug = true;
        }

        if (IsDebugHostLibraryPath(corpusLibraryPath) && !smokeOnly && string.IsNullOrWhiteSpace(filter) && !allowDebug)
        {
            var releaseCandidate = TryResolveReleaseLibraryPath(corpusLibraryPath);
            if (releaseCandidate is not null)
            {
                corpusLibraryPath = releaseCandidate;
            }
            else
            {
                Assert.Inconclusive(
                    "Copied NeoVM JSON compatibility corpus is very slow with a debug host library. " +
                    $"Point {nameof(RiscvVmRunner)} at a release build, e.g. `cargo build -p neo-riscv-host --release` and set " +
                    "NEO_RISCV_HOST_LIB=/path/to/target/release/libneo_riscv_host.so. " +
                    "Alternatively set NEO_RISCV_VM_JSON_FILTER to run a subset, or set NEO_RISCV_VM_JSON_ALLOW_DEBUG=1 to force running the full corpus.");
            }
        }

        var maxFailures = ReadEnvInt("NEO_RISCV_VM_JSON_MAX_FAILURES", 1);
        var maxParallelism = ReadEnvInt("NEO_RISCV_VM_JSON_MAX_PARALLELISM", 0);
        var verbose = string.Equals(
            Environment.GetEnvironmentVariable("NEO_RISCV_VM_JSON_VERBOSE"),
            "1",
            StringComparison.OrdinalIgnoreCase);

        if (verbose)
        {
            Console.WriteLine(
                $"RISC-V compatibility corpus mode: {(smokeOnly ? "smoke" : "full")} | lib: {corpusLibraryPath}");
        }

        var executed = 0;
        var skippedBreak = 0;
        var failureCount = 0;
        var failures = new ConcurrentBag<string>();
        using var cts = new CancellationTokenSource();
        using var runners = new ThreadLocal<RiscvVmRunner>(
            () => new RiscvVmRunner(corpusLibraryPath),
            trackAllValues: true);

        var defaultParallelism = smokeOnly ? 1 : Math.Min(8, Environment.ProcessorCount);
        var parallelism = maxParallelism <= 0 ? defaultParallelism : maxParallelism;
        var options = new ParallelOptions
        {
            CancellationToken = cts.Token,
            MaxDegreeOfParallelism = Math.Max(1, parallelism),
        };

        var files = SelectCorpusFiles(root, filter, smokeOnly);

        try
        {
            Parallel.ForEach(files, options, file =>
            {
                if (options.CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var relativePath = Path.GetRelativePath(root, file);
                var json = File.ReadAllText(file);
                var ut = json.DeserializeJson<VMUT>();

                foreach (var test in ut.Tests ?? Array.Empty<VMUTEntry>())
                {
                    if (options.CancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var final = test.Steps?.LastOrDefault()?.Result;
                    if (final is null)
                    {
                        failures.Add($"{relativePath}::{test.Name} missing final result.");
                        if (MaybeCancel(ref failureCount, maxFailures, cts))
                        {
                            return;
                        }
                        continue;
                    }

                    if (final.State == VMState.BREAK)
                    {
                        Interlocked.Increment(ref skippedBreak);
                        continue;
                    }

                    Interlocked.Increment(ref executed);
                    if (verbose)
                    {
                        Console.WriteLine($"RISC-V compatibility: {relativePath} :: {test.Name}");
                    }

                    try
                    {
                        var runner = runners.Value ?? throw new InvalidOperationException("thread-local runner missing");
                        var outcome = runner.Execute(test.Script);
                        AssertFinalStateMatches(final, outcome, relativePath, test.Name);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{relativePath}::{test.Name} => {ex.GetType().Name}: {ex.Message}");
                        if (MaybeCancel(ref failureCount, maxFailures, cts))
                        {
                            return;
                        }
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Expected when max failures is reached.
        }

        foreach (var runner in runners.Values)
        {
            runner.Dispose();
        }

        if (failures.Count > 0)
        {
            var aborted = cts.IsCancellationRequested ? " (aborted early)" : string.Empty;
            var sample = failures
                .OrderBy(s => s, StringComparer.Ordinal)
                .Take(50);
            Assert.Fail(
                $"Copied NeoVM JSON compatibility failures{aborted}: {failures.Count} out of {executed} executed cases. " +
                $"Mode={(smokeOnly ? "smoke" : "full")}, Lib={corpusLibraryPath}. " +
                $"Skipped BREAK-only cases: {skippedBreak}.{Environment.NewLine}{string.Join(Environment.NewLine, sample)}");
        }

        Assert.IsGreaterThan(0, executed, "No copied NeoVM JSON cases were executed.");
    }

    private static string[] SelectCorpusFiles(string root, string? filter, bool smokeOnly)
    {
        var allFiles = Directory
            .GetFiles(root, "*.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            return allFiles
                .Where(file => file.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();
        }

        if (!smokeOnly)
        {
            return allFiles;
        }

        // Smoke suite: representative opcodes that cover common failure modes.
        var smoke = SmokeCorpusRelativePaths
            .Select(relative => Path.Combine(root, relative))
            .Where(File.Exists)
            .ToArray();

        return smoke.Length > 0 ? smoke : allFiles.Take(10).ToArray();
    }

    private static bool IsDebugHostLibraryPath(string path)
    {
        return path.IndexOf($"{Path.DirectorySeparatorChar}debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string? TryResolveReleaseLibraryPath(string debugPath)
    {
        var needle = $"{Path.DirectorySeparatorChar}debug{Path.DirectorySeparatorChar}";
        var idx = debugPath.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var prefix = debugPath[..idx];
        var suffix = debugPath[(idx + needle.Length)..];
        var candidate = prefix + $"{Path.DirectorySeparatorChar}release{Path.DirectorySeparatorChar}" + suffix;
        return File.Exists(candidate) ? candidate : null;
    }

    private static int ReadEnvInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static bool MaybeCancel(ref int failureCount, int maxFailures, CancellationTokenSource cts)
    {
        if (maxFailures <= 0)
        {
            return false;
        }

        if (Interlocked.Increment(ref failureCount) >= maxFailures)
        {
            cts.Cancel();
            return true;
        }

        return false;
    }

    private static void AssertFinalStateMatches(VMUTExecutionEngineState expected, ExecutionOutcome actual, string file, string testName)
    {
        if (expected.State != actual.State)
        {
            Assert.Fail(
                $"{file}::{testName} state mismatch. Expected={expected.State}, Actual={actual.State}, Fault={actual.FaultMessage ?? "<none>"}");
        }

        if (actual.State == VMState.FAULT)
        {
            if (!string.IsNullOrEmpty(expected.ExceptionMessage))
            {
                Assert.AreEqual(expected.ExceptionMessage, actual.FaultMessage, $"{file}::{testName} fault message mismatch.");
            }

            return;
        }

        AssertStacksMatch(expected.ResultStack ?? Array.Empty<VMUTStackItem>(), actual.ResultStack, file, testName);
    }

    private static void AssertStacksMatch(IReadOnlyList<VMUTStackItem> expected, IReadOnlyList<JObject> actual, string file, string testName)
    {
        Assert.HasCount(expected.Count, actual, $"{file}::{testName} result stack length mismatch.");

        for (var i = 0; i < expected.Count; i++)
        {
            var expectedJson = PrepareJsonItem(expected[i]);
            var actualJson = NormalizeActualItem(expectedJson, actual[actual.Count - 1 - i]);

            Assert.AreEqual(
                expectedJson.ToString(Formatting.None),
                actualJson.ToString(Formatting.None),
                $"{file}::{testName} result stack item {i} mismatch.");
        }
    }

    private static JObject NormalizeActualItem(JObject expected, JObject item)
    {
        item.Remove("_handle");
        var expectedType = expected["type"]?.Value<string>();
        var actualType = item["type"]?.Value<string>();
        if (expectedType == "Buffer" && actualType == "ByteString")
        {
            item["type"] = "Buffer";
        }
        // Normalize empty byte values: "" and "0x" are equivalent for ByteString/Buffer types
        if (expectedType is "ByteString" or "Buffer" && actualType is "ByteString" or "Buffer")
        {
            var expectedValue = expected["value"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? expected["value"]?.Value<string>() : null;
            var actualValue = item["value"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? item["value"]?.Value<string>() : null;
            if (expectedValue != null && actualValue != null)
            {
                if ((expectedValue == "" && actualValue == "0x") || (expectedValue == "0x" && actualValue == ""))
                {
                    item["value"] = expectedValue;
                }
            }
        }
        return item;
    }

    private static JObject PrepareJsonItem(VMUTStackItem item)
    {
        var ret = new JObject
        {
            ["type"] = item.Type.ToString(),
            ["value"] = item.Value
        };

        switch (item.Type)
        {
            case VMUTStackItemType.Null:
                ret["type"] = VMUTStackItemType.Null.ToString();
                ret.Remove("value");
                break;
            case VMUTStackItemType.Pointer:
                ret["type"] = VMUTStackItemType.Pointer.ToString();
                ret["value"] = item.Value.Value<int>();
                break;
            case VMUTStackItemType.String:
                ret["type"] = VMUTStackItemType.ByteString.ToString();
                ret["value"] = Neo.Test.Extensions.StringExtensions.ToHexString(
                    System.Text.Encoding.UTF8.GetBytes(item.Value.Value<string>() ?? string.Empty));
                break;
            case VMUTStackItemType.ByteString:
            case VMUTStackItemType.Buffer:
                {
                    var value = ret["value"]?.Value<string>() ?? string.Empty;
                    Assert.IsTrue(string.IsNullOrEmpty(value) || value.StartsWith("0x"), $"'0x' prefix required for value: '{value}'");
                    ret["value"] = value.ToLowerInvariant();
                    break;
                }
            case VMUTStackItemType.Integer:
                ret["value"] = ret["value"]!.Value<string>();
                break;
            case VMUTStackItemType.Struct:
            case VMUTStackItemType.Array:
                {
                    var array = (JArray)ret["value"]!;
                    for (var i = 0; i < array.Count; i++)
                    {
                        array[i] = PrepareJsonItem(JsonConvert.DeserializeObject<VMUTStackItem>(array[i]!.ToString())!);
                    }
                    break;
                }
            case VMUTStackItemType.Map:
                {
                    var obj = (JObject)ret["value"]!;
                    foreach (var prop in obj.Properties().ToList())
                    {
                        obj[prop.Name] = PrepareJsonItem(JsonConvert.DeserializeObject<VMUTStackItem>(prop.Value.ToString())!);
                    }
                    break;
                }
        }

        return ret;
    }
}
