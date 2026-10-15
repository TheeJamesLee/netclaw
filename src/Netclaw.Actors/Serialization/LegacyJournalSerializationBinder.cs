// -----------------------------------------------------------------------
// <copyright file="LegacyJournalSerializationBinder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using Newtonsoft.Json.Serialization;
using Netclaw.Actors.Sessions;

namespace Netclaw.Actors.Serialization;

/// <summary>
/// Reads serializer-ID-1 journal rows whose CLR type no longer exists under its old name.
/// </summary>
/// <remarks>
/// Netclaw 0.14.1 and earlier wrote journal events as Newtonsoft JSON with CLR type
/// metadata. Since 0.14.2, every persisted type uses the protobuf serializer, so this map
/// lists only types that those early builds wrote and that later moved or were removed.
/// A type that first shipped with a protobuf binding never needs an entry here.
/// Each entry matches one exact assembly and type name. Any other name goes to the
/// default binder, so an unknown type still fails recovery loudly.
/// </remarks>
internal sealed class LegacyJournalSerializationBinder : ISerializationBinder
{
    private static readonly DefaultSerializationBinder DefaultBinder = new();

    private static readonly IReadOnlyDictionary<(string Assembly, string Type), Type> LegacyTypes =
        new Dictionary<(string Assembly, string Type), Type>
        {
            // Moved into SessionProtocol by #1486 (0.24.2).
            [("Netclaw.Actors", "Netclaw.Actors.Protocol.TurnRecorded")] = typeof(SessionProtocol.TurnRecorded),
            [("Netclaw.Actors", "Netclaw.Actors.Protocol.SessionTitleSet")] = typeof(SessionProtocol.SessionTitleSet),
            [("Netclaw.Actors", "Netclaw.Actors.Protocol.SessionCompacted")] = typeof(SessionProtocol.SessionCompacted),

            // Removed by #832 (0.16.2). Recovery ignored this event before its removal.
            [("Netclaw.Actors", "Netclaw.Actors.Protocol.SystemPromptSet")] = typeof(LegacySystemPromptSet),

            // V1 recovery was removed by #978. The memory observer still replays its journal.
            [("Netclaw.Actors", "Netclaw.Actors.Sessions.MemoriesDistilled")] = typeof(LegacyMemoriesDistilled),

            // Private nested type until #890 (0.17.2) moved it to Netclaw.Actors.Channels.
            [("Netclaw.Channels.Slack", "Netclaw.Channels.Slack.SlackThreadBindingActor+CursorAdvanced")] =
                typeof(LegacySlackCursorAdvanced)
        };

    public Type BindToType(string? assemblyName, string typeName)
    {
        if (assemblyName is not null
            && new AssemblyName(assemblyName).Name is { } shortName
            && LegacyTypes.TryGetValue((shortName, typeName), out var currentType))
        {
            return currentType;
        }

        return DefaultBinder.BindToType(assemblyName, typeName);
    }

    /// <summary>
    /// Keeps the existing type names for new JSON writes.
    /// </summary>
    public void BindToName(Type serializedType, out string? assemblyName, out string? typeName)
        => DefaultBinder.BindToName(serializedType, out assemblyName, out typeName);
}

/// <summary>
/// Read-only stand-in for the removed <c>SystemPromptSet</c> event. The session actor ignores it.
/// </summary>
internal sealed record LegacySystemPromptSet;

/// <summary>
/// Read-only stand-in for the removed V1 <c>MemoriesDistilled</c> event. V1 carried anchor slugs only.
/// </summary>
internal sealed record LegacyMemoriesDistilled(IReadOnlyList<string> Anchors);

/// <summary>
/// Read-only stand-in for the former private Slack <c>CursorAdvanced</c> event and its <c>CursorTs</c> field.
/// </summary>
public sealed record LegacySlackCursorAdvanced(string CursorTs);
