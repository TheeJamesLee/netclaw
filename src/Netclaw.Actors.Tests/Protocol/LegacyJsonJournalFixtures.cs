// -----------------------------------------------------------------------
// <copyright file="LegacyJsonJournalFixtures.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Tests.Protocol;

/// <summary>
/// Serializer-ID-1 journal payloads that the 0.14.1 types wrote, byte for byte.
/// </summary>
/// <remarks>
/// A throwaway console program built against tag 0.14.1 created these strings. It called
/// Akka's NewtonSoftJsonSerializer on synthetic events of each type that 0.14.1 journaled
/// as JSON and that later moved or was removed. Do not edit them by hand: the value of
/// this fixture is that old code, not a test author, chose each shape.
/// </remarks>
internal static class LegacyJsonJournalFixtures
{
    public const string TurnRecorded = """
        {"$id":"1","$type":"Netclaw.Actors.Protocol.TurnRecorded, Netclaw.Actors","SessionId":{"$id":"2","$type":"Netclaw.Actors.Protocol.SessionId, Netclaw.Actors","Value":"C0SYNTH/1700000000.000100"},"UserMessage":{"$id":"3","$type":"Netclaw.Actors.Protocol.SerializableChatMessage, Netclaw.Actors","Content":"synthetic user","ToolCalls":{"$type":"System.Collections.Generic.List`1[[Netclaw.Actors.Protocol.SerializableToolCall, Netclaw.Actors]], System.Private.CoreLib","$values":[]},"MediaReferences":{"$type":"System.Collections.Generic.List`1[[Netclaw.Actors.Protocol.SerializableMediaReference, Netclaw.Actors]], System.Private.CoreLib","$values":[]}},"AssistantReply":{"$id":"4","$type":"Netclaw.Actors.Protocol.SerializableChatMessage, Netclaw.Actors","Role":1,"Content":"synthetic reply","ToolCalls":{"$type":"System.Collections.Generic.List`1[[Netclaw.Actors.Protocol.SerializableToolCall, Netclaw.Actors]], System.Private.CoreLib","$values":[{"$id":"5","$type":"Netclaw.Actors.Protocol.SerializableToolCall, Netclaw.Actors","CallId":"call-1","Name":"file_read","ArgumentsJson":"{}"}]},"MediaReferences":{"$type":"System.Collections.Generic.List`1[[Netclaw.Actors.Protocol.SerializableMediaReference, Netclaw.Actors]], System.Private.CoreLib","$values":[]}},"RecordedAtMs":1700000000123,"SourceReminderId":"reminder-synthetic:1700000000000","RecordedAt":"2023-11-14T22:13:20.123+00:00"}
        """;

    public const string SessionTitleSet = """
        {"$id":"1","$type":"Netclaw.Actors.Protocol.SessionTitleSet, Netclaw.Actors","SessionId":{"$id":"2","$type":"Netclaw.Actors.Protocol.SessionId, Netclaw.Actors","Value":"C0SYNTH/1700000000.000100"},"Title":"synthetic title","SetAtMs":1700000000200,"SetAt":"2023-11-14T22:13:20.2+00:00"}
        """;

    public const string SessionCompacted = """
        {"$id":"1","$type":"Netclaw.Actors.Protocol.SessionCompacted, Netclaw.Actors","SessionId":{"$id":"2","$type":"Netclaw.Actors.Protocol.SessionId, Netclaw.Actors","Value":"C0SYNTH/1700000000.000100"},"Summary":"synthetic summary","CompactedMessages":{"$type":"System.Collections.Generic.List`1[[Netclaw.Actors.Protocol.SerializableChatMessage, Netclaw.Actors]], System.Private.CoreLib","$values":[{"$id":"3","$type":"Netclaw.Actors.Protocol.SerializableChatMessage, Netclaw.Actors","Content":"old user","ToolCalls":{"$type":"System.Collections.Generic.List`1[[Netclaw.Actors.Protocol.SerializableToolCall, Netclaw.Actors]], System.Private.CoreLib","$values":[]},"MediaReferences":{"$type":"System.Collections.Generic.List`1[[Netclaw.Actors.Protocol.SerializableMediaReference, Netclaw.Actors]], System.Private.CoreLib","$values":[]}}]},"TurnCountBefore":{"$":"I4"},"CompactedAtMs":1700000000300,"CompactedAt":"2023-11-14T22:13:20.3+00:00"}
        """;

    public const string SystemPromptSet = """
        {"$id":"1","$type":"Netclaw.Actors.Protocol.SystemPromptSet, Netclaw.Actors","SessionId":{"$id":"2","$type":"Netclaw.Actors.Protocol.SessionId, Netclaw.Actors","Value":"C0SYNTH/1700000000.000100"},"Content":"synthetic prompt","SetAtMs":1700000000050,"SetAt":"2023-11-14T22:13:20.05+00:00"}
        """;

    public const string MemoriesDistilled = """
        {"$id":"1","$type":"Netclaw.Actors.Sessions.MemoriesDistilled, Netclaw.Actors","Anchors":{"$type":"System.String[], System.Private.CoreLib","$values":["anchor-one","anchor-two"]},"TimestampMs":1700000000400}
        """;

    public const string SlackCursorAdvanced = """
        {"$id":"1","$type":"Netclaw.Channels.Slack.SlackThreadBindingActor+CursorAdvanced, Netclaw.Channels.Slack","CursorTs":"1700000000.000900"}
        """;
}
