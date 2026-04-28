// Copyright (C) 2015-2026 The Neo Project.
//
// RiscVBuildHelper.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Neo.SmartContract;

namespace Neo.Compiler.Backend.RiscV;

public static class RiscVBuildHelper
{
    private static readonly byte[] PolkaVmMagic = [0x50, 0x56, 0x4d, 0x00];

    /// <summary>
    /// Build a Rust crate directory into a .polkavm binary.
    /// </summary>
    /// <param name="crateDir">Path to the Cargo crate directory (containing Cargo.toml and src/)</param>
    /// <param name="outputPath">Path where the .polkavm binary should be written</param>
    /// <returns>True if build succeeded</returns>
    public static bool BuildCrate(string crateDir, string outputPath)
    {
        try
        {
            // Get original target JSON from polkatool
            var origTargetJson = RunCommand("polkatool", ["get-target-json-path", "-b", "32"])?.Trim();
            if (string.IsNullOrEmpty(origTargetJson)) return false;

            // Fix target JSON: add "abi" field required by newer nightly rustc
            var targetJson = Path.Combine(Path.GetTempPath(), $"neo-riscv32-polkavm-{Guid.NewGuid():N}.json");
            try
            {
                FixTargetJson(origTargetJson!, targetJson);

                // Newer nightly toolchains accept JSON target paths directly.
                var buildResult = RunCommand("cargo",
                    ["+nightly", "build", "--manifest-path", Path.Combine(crateDir, "Cargo.toml"), "--release", "--target", targetJson, "-Zbuild-std=core,alloc"],
                    workingDir: crateDir);
                if (buildResult == null) return false;

                // Link — the output dir uses the JSON file's stem name
                var target = Path.GetFileNameWithoutExtension(targetJson);
                var name = Path.GetFileName(crateDir);
                var elf = Path.Combine(crateDir, "target", target, "release", name);
                RunCommand("polkatool", ["link", "--strip", "-o", outputPath, elf], workingDir: crateDir);
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

            return File.Exists(outputPath);
        }
        catch
        {
            return false;
        }
    }

    public static NefFile CreateDeployableNef(NefFile sourceNef, byte[] polkaVmBinary)
    {
        if (sourceNef is null) throw new ArgumentNullException(nameof(sourceNef));
        if (polkaVmBinary is null) throw new ArgumentNullException(nameof(polkaVmBinary));
        if (polkaVmBinary.Length < PolkaVmMagic.Length ||
            !polkaVmBinary.AsSpan(0, PolkaVmMagic.Length).SequenceEqual(PolkaVmMagic))
            throw new ArgumentException("RISC-V NEF payload must be a PolkaVM binary beginning with PVM magic.", nameof(polkaVmBinary));

        var nef = new NefFile
        {
            Compiler = sourceNef.Compiler,
            Source = sourceNef.Source,
            Tokens = sourceNef.Tokens,
            Script = (byte[])polkaVmBinary.Clone(),
        };
        nef.CheckSum = NefFile.ComputeChecksum(nef);
        return nef;
    }

    /// <summary>
    /// Run a shell command and return stdout. Returns null on failure.
    /// </summary>
    /// <param name="command">The command to run</param>
    /// <param name="args">Arguments to pass to the command</param>
    /// <param name="workingDir">Optional working directory for the process</param>
    /// <returns>Standard output on success, null on failure</returns>
    public static string? RunCommand(string command, string args, string? workingDir = null)
    {
        return RunCommandCore(command, args, null, workingDir);
    }

    public static string? RunCommand(string command, IReadOnlyList<string> args, string? workingDir = null)
    {
        return RunCommandCore(command, null, args, workingDir);
    }

    private static string? RunCommandCore(string command, string? argumentString, IReadOnlyList<string>? argumentList, string? workingDir)
    {
        try
        {
            var cargoBin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cargo", "bin");
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var newPath = Directory.Exists(cargoBin)
                ? cargoBin + Path.PathSeparator + currentPath
                : currentPath;

            var psi = new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (argumentList is null)
            {
                psi.Arguments = argumentString ?? string.Empty;
            }
            else
            {
                foreach (var argument in argumentList)
                {
                    psi.ArgumentList.Add(argument);
                }
            }
            psi.EnvironmentVariables["PATH"] = newPath;
            if (workingDir != null)
            {
                psi.WorkingDirectory = workingDir;
            }

            var proc = Process.Start(psi);
            if (proc == null) return null;

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(300000)) // 5 min timeout
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Process may have exited between the timeout and kill attempt.
                }
                Console.Error.WriteLine($"Command '{FormatCommand(command, argumentString, argumentList)}' timed out.");
                return null;
            }

            var stdoutText = stdout.GetAwaiter().GetResult();
            var stderrText = stderr.GetAwaiter().GetResult();
            if (proc.ExitCode == 0)
            {
                return stdoutText;
            }

            var message = $"Command '{FormatCommand(command, argumentString, argumentList)}' failed with exit code {proc.ExitCode}.";
            if (!string.IsNullOrWhiteSpace(stderrText))
            {
                message += $" Stderr: {stderrText.Trim()}";
            }
            Console.Error.WriteLine(message);
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to run command '{FormatCommand(command, argumentString, argumentList)}': {ex.Message}");
            return null;
        }
    }

    private static string FormatCommand(string command, string? argumentString, IReadOnlyList<string>? argumentList)
    {
        if (argumentList is null)
            return string.IsNullOrWhiteSpace(argumentString) ? command : $"{command} {argumentString}";
        return $"{command} {string.Join(" ", argumentList)}";
    }

    /// <summary>
    /// Fix the polkatool-generated target JSON to add the "abi" field.
    /// </summary>
    /// <param name="sourcePath">Path to the original target JSON</param>
    /// <param name="destPath">Path where the fixed JSON should be written</param>
    public static void FixTargetJson(string sourcePath, string destPath)
    {
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sourcePath));
        var root = json.RootElement;

        // Check if "abi" field already exists
        if (root.TryGetProperty("abi", out _))
        {
            File.Copy(sourcePath, destPath, overwrite: true);
            return;
        }

        // Rebuild JSON with "abi" field inserted
        using var stream = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        foreach (var prop in root.EnumerateObject())
        {
            prop.WriteTo(writer);
        }
        // Add abi field matching llvm-abiname
        var abiName = root.TryGetProperty("llvm-abiname", out var abiname) ? abiname.GetString() ?? "ilp32e" : "ilp32e";
        writer.WriteString("abi", abiName);
        writer.WriteEndObject();
        writer.Flush();
        File.WriteAllBytes(destPath, stream.ToArray());
    }
}
