// Copyright (C) 2015-2026 The Neo Project.
//
// Contract.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Services;
using System.ComponentModel;
using System.Numerics;

namespace DualTarget;

/// <summary>
/// A simple counter contract that demonstrates dual-target compilation.
/// This contract can be compiled to both NeoVM (.nef) and RISC-V (.polkavm).
/// </summary>
[DisplayName("DualTargetCounter")]
[ContractDescription("A counter contract compilable to both NeoVM and RISC-V")]
public class DualTargetContract : SmartContract
{
    private static readonly StorageMap CounterMap = new(Storage.CurrentContext, "counter");

    /// <summary>
    /// Gets the current counter value.
    /// </summary>
    [Safe]
    public static int Get()
    {
        var value = CounterMap.Get("value");
        if (value == null) return 0;
        return (int)(BigInteger)value;
    }

    /// <summary>
    /// Increments the counter by 1 and returns the new value.
    /// </summary>
    public static int Increment()
    {
        int current = Get();
        int next = current + 1;
        CounterMap.Put("value", next);
        return next;
    }

    /// <summary>
    /// Decrements the counter by 1 and returns the new value.
    /// </summary>
    public static int Decrement()
    {
        int current = Get();
        int next = current - 1;
        CounterMap.Put("value", next);
        return next;
    }

    /// <summary>
    /// Resets the counter to zero.
    /// </summary>
    public static void Reset()
    {
        CounterMap.Put("value", 0);
    }

    /// <summary>
    /// Adds an arbitrary amount to the counter.
    /// </summary>
    public static int Add(int amount)
    {
        int current = Get();
        int next = current + amount;
        CounterMap.Put("value", next);
        return next;
    }
}
