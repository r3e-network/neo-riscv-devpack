// Copyright (C) 2015-2026 The Neo Project.
//
// Contract_NEP17Receiver.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.

using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Services;
using System.ComponentModel;
using System.Numerics;

namespace Neo.Compiler.CSharp.TestContracts
{
    [DisplayName(nameof(Contract_NEP17Receiver))]
    [ContractPermission(Permission.Any, Method.Any)]
    public class Contract_NEP17Receiver : SmartContract.Framework.SmartContract
    {
        [DisplayName("onNEP17Payment")]
        public static void OnNEP17Payment(UInt160 from, BigInteger amount, object data)
        {
            Runtime.Log("onNEP17Payment");
        }
    }
}
