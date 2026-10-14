// -----------------------------------------------------------------------
// <copyright file="LegacySnapshotValueObjectJsonConverter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Media;
using Netclaw.Tools;

namespace Netclaw.Actors.Serialization;

/// <summary>
/// Reads primitive fields from legacy session snapshots after a value-object migration.
/// </summary>
internal sealed class LegacySnapshotValueObjectJsonConverter : JsonConverter
{
    public LegacySnapshotValueObjectJsonConverter()
    {
    }

    public override bool CanWrite => false;

    public override bool CanConvert(Type objectType)
    {
        var valueType = Nullable.GetUnderlyingType(objectType) ?? objectType;
        return valueType == typeof(TurnNumber)
               || valueType == typeof(SenderId)
               || valueType == typeof(BackgroundJobId)
               || valueType == typeof(ToolCallId)
               || valueType == typeof(ToolName)
               || valueType == typeof(MimeType);
    }

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer)
    {
        var valueType = Nullable.GetUnderlyingType(objectType) ?? objectType;
        if (reader.TokenType == JsonToken.Null)
        {
            if (Nullable.GetUnderlyingType(objectType) is not null)
                return null;

            throw new JsonSerializationException($"A null value cannot be read as {valueType.Name}.");
        }

        JToken primitive;
        if (reader.TokenType == JsonToken.StartObject)
        {
            var wrapped = JObject.Load(reader);
            var wrappedValue = wrapped.GetValue("Value", StringComparison.OrdinalIgnoreCase);
            if (wrappedValue is null)
            {
                var isEmptyNullableToolCallId = valueType == typeof(ToolCallId)
                                                && Nullable.GetUnderlyingType(objectType) == typeof(ToolCallId)
                                                && wrapped.Count == 2
                                                && wrapped["$id"]?.Type == JTokenType.String
                                                && wrapped["$type"]?.Type == JTokenType.String;
                if (!isEmptyNullableToolCallId)
                    throw new JsonSerializationException("A value-object snapshot has no Value field.");

                return wrapped.ToObject(objectType, serializer)
                       ?? throw new JsonSerializationException("An empty ToolCallId wrapper cannot be null.");
            }

            primitive = wrappedValue;
        }
        else if (reader.TokenType is JsonToken.Integer or JsonToken.String)
        {
            primitive = new JValue(reader.Value);
        }
        else
        {
            throw new JsonSerializationException($"A {reader.TokenType} token cannot be read as {valueType.Name}.");
        }

        if (valueType == typeof(TurnNumber))
        {
            var isAkkaNumericSurrogate = primitive is JObject surrogate
                                         && surrogate.Count == 1
                                         && surrogate["$"]?.Type == JTokenType.String;
            if (primitive.Type != JTokenType.Integer && !isAkkaNumericSurrogate)
                throw new JsonSerializationException("A turn number must be an integer.");

            return new TurnNumber(primitive.ToObject<int>(serializer));
        }

        if (primitive.Type != JTokenType.String || primitive.Value<string>() is not { } text)
            throw new JsonSerializationException($"A {valueType.Name} value must be a string.");

        return valueType == typeof(SenderId) ? new SenderId(text)
            : valueType == typeof(BackgroundJobId) ? new BackgroundJobId(text)
            : valueType == typeof(ToolCallId) ? new ToolCallId(text)
            : valueType == typeof(ToolName) ? new ToolName(text)
            : valueType == typeof(MimeType) ? new MimeType(text)
            : throw new JsonSerializationException($"No legacy snapshot reader exists for {valueType.Name}.");
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        throw new NotSupportedException("This converter reads legacy values only.");
}
