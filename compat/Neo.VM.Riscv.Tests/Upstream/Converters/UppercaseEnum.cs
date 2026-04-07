// Copyright (C) 2015-2026 The Neo Project.
//
// UppercaseEnum.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Newtonsoft.Json;
using System;

namespace Neo.Test.Converters;

internal class UppercaseEnum : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType.IsEnum;
    }

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var raw = reader.Value?.ToString()
            ?? throw new JsonSerializationException($"Enum value for {objectType.Name} cannot be null.");
        return Enum.Parse(objectType, raw, true);
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        var text = value.ToString()
            ?? throw new JsonSerializationException($"Unable to serialize enum value for {value.GetType().Name}.");
        writer.WriteValue(text.ToUpperInvariant());
    }
}
