// Copyright (C) 2015-2026 The Neo Project.
//
// ScriptConverter.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Test.Extensions;
using Neo.VM;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace Neo.Test.Converters;

internal class ScriptConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(byte[]) || objectType == typeof(string);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        switch (reader.TokenType)
        {
            case JsonToken.String:
                {
                    if (reader.Value is string str)
                    {
                        Assert.StartsWith("0x", str, $"'0x' prefix required for value: '{str}'");
                        return str.FromHexString();
                    }
                    break;
                }
            case JsonToken.Bytes:
                {
                    if (reader.Value is byte[] data) return data;
                    break;
                }
            case JsonToken.StartArray:
                {
                    using var script = new ScriptBuilder();

                    foreach (var entry in JArray.Load(reader))
                    {
                        var mul = 1;
                        var value = entry.Value<string>()
                            ?? throw new FormatException("Script array entry must be a string.");

                        if (Enum.IsDefined(typeof(OpCode), value) && Enum.TryParse<OpCode>(value, out var opCode))
                        {
                            for (int x = 0; x < mul; x++)
                            {
                                script.Emit(opCode);
                            }
                        }
                        else
                        {
                            for (int x = 0; x < mul; x++)
                            {
                                Assert.StartsWith("0x", value, $"'0x' prefix required for value: '{value}'");
                                script.EmitRaw(value.FromHexString());
                            }
                        }
                    }

                    return script.ToArray();
                }
        }

        throw new FormatException();
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        throw new NotSupportedException("The copied RISC-V compatibility suite only uses ScriptConverter for reading.");
    }
}
