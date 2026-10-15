// -----------------------------------------------------------------------
// <copyright file="LegacyAkkaJsonSnapshotCompatibilityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Serialization;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Media;
using Netclaw.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Netclaw.Actors.Tests.Protocol;

public sealed class LegacyAkkaJsonSnapshotCompatibilityTests : TestKit
{
    public LegacyAkkaJsonSnapshotCompatibilityTests(ITestOutputHelper output) : base(output: output)
    {
    }

    private NewtonSoftJsonSerializer Serializer => new((ExtendedActorSystem)Sys);

    private object DeserializeLegacy(string json)
        => Sys.Serialization.Deserialize(Encoding.UTF8.GetBytes(json.Trim()), 1, typeof(object));

    public static TheoryData<bool, string?> LegacyTurnSourceReminderIds => new()
    {
        { false, null },
        { true, null },
        { true, "reminder:synthetic-fire" }
    };

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithNetclawSerialization();
    }

    [Theory]
    [MemberData(nameof(LegacyTurnSourceReminderIds))]
    public void Serializer_id_1_resolves_legacy_turn_recorded_and_converts_source_reminder_id(
        bool includeReminderId,
        string? reminderId)
    {
        // Arrange
        var document = new JObject
        {
            ["$type"] = "Netclaw.Actors.Protocol.TurnRecorded, Netclaw.Actors",
            ["RecordedAtMs"] = 123,
            ["UserMessage"] = new JObject { ["Content"] = "synthetic user" },
            ["AssistantReply"] = new JObject { ["Content"] = "synthetic reply" }
        };
        if (includeReminderId)
            document["SourceReminderId"] = reminderId is null ? JValue.CreateNull() : new JValue(reminderId);
        var json = document.ToString(Formatting.None);

        // Act
        var result = Sys.Serialization.Deserialize(Encoding.UTF8.GetBytes(json), 1, typeof(object));

        // Assert
        var recorded = Assert.IsType<SessionProtocol.TurnRecorded>(result);
        Assert.Equal(reminderId is null ? null : new ReminderId(reminderId), recorded.SourceReminderId);
        Assert.Equal(123, recorded.RecordedAtMs);
        Assert.Equal("synthetic user", recorded.UserMessage.Content);
        Assert.Equal("synthetic reply", recorded.AssistantReply.Content);
    }

    [Fact]
    public void Serializer_id_1_round_trips_current_turn_source_value_objects()
    {
        // Arrange
        var original = new SessionProtocol.TurnRecorded
        {
            SessionId = new SessionId("synthetic/session"),
            SourceReminderId = new ReminderId("reminder:synthetic-fire"),
            SourceBackgroundJobId = new BackgroundJobId("bg-job:synthetic"),
            RecordedAtMs = 123
        };

        // Act
        var serializer = Serializer;
        var bytes = serializer.ToBinary(original);
        var restored = Sys.Serialization.Deserialize(bytes, serializer.Identifier, typeof(object));

        // Assert
        var recorded = Assert.IsType<SessionProtocol.TurnRecorded>(restored);
        Assert.Equal(original.SessionId, recorded.SessionId);
        Assert.Equal(original.SourceReminderId, recorded.SourceReminderId);
        Assert.Equal(original.SourceBackgroundJobId, recorded.SourceBackgroundJobId);
        Assert.Equal(original.RecordedAtMs, recorded.RecordedAtMs);
    }

    [Fact]
    public void Serializer_id_1_reads_a_0_14_1_turn_recorded_row()
    {
        // Act
        var result = DeserializeLegacy(LegacyJsonJournalFixtures.TurnRecorded);

        // Assert
        var recorded = Assert.IsType<SessionProtocol.TurnRecorded>(result);
        Assert.Equal(new SessionId("C0SYNTH/1700000000.000100"), recorded.SessionId);
        Assert.Equal(ChatRole.User, recorded.UserMessage.Role);
        Assert.Equal("synthetic user", recorded.UserMessage.Content);
        Assert.Equal(ChatRole.Assistant, recorded.AssistantReply.Role);
        Assert.Equal("synthetic reply", recorded.AssistantReply.Content);
        var toolCall = Assert.Single(recorded.AssistantReply.ToolCalls);
        Assert.Equal(new ToolCallId("call-1"), toolCall.CallId);
        Assert.Equal(new ToolName("file_read"), toolCall.Name);
        Assert.Equal(1700000000123, recorded.RecordedAtMs);
        Assert.Equal(new ReminderId("reminder-synthetic:1700000000000"), recorded.SourceReminderId);
        Assert.Null(recorded.SourceBackgroundJobId);
    }

    [Fact]
    public void Serializer_id_1_reads_a_0_14_1_session_title_set_row()
    {
        // Act
        var result = DeserializeLegacy(LegacyJsonJournalFixtures.SessionTitleSet);

        // Assert
        var titleSet = Assert.IsType<SessionProtocol.SessionTitleSet>(result);
        Assert.Equal("synthetic title", titleSet.Title);
        Assert.Equal(1700000000200, titleSet.SetAtMs);
    }

    [Fact]
    public void Serializer_id_1_reads_a_0_14_1_session_compacted_row()
    {
        // Act
        var result = DeserializeLegacy(LegacyJsonJournalFixtures.SessionCompacted);

        // Assert
        var compacted = Assert.IsType<SessionProtocol.SessionCompacted>(result);
        Assert.Equal("synthetic summary", compacted.Summary);
        Assert.Equal("old user", Assert.Single(compacted.CompactedMessages).Content);
        Assert.Equal(4, compacted.TurnCountBefore);
        Assert.Equal(1700000000300, compacted.CompactedAtMs);
        Assert.Null(compacted.WorkingContext);
    }

    [Fact]
    public void Serializer_id_1_reads_a_0_14_1_system_prompt_set_row_as_an_ignored_stand_in()
    {
        // Act
        var result = DeserializeLegacy(LegacyJsonJournalFixtures.SystemPromptSet);

        // Assert
        Assert.IsType<LegacySystemPromptSet>(result);
    }

    [Fact]
    public void Serializer_id_1_reads_a_0_14_1_memories_distilled_v1_row()
    {
        // Act
        var result = DeserializeLegacy(LegacyJsonJournalFixtures.MemoriesDistilled);

        // Assert
        var distilled = Assert.IsType<LegacyMemoriesDistilled>(result);
        Assert.Equal(["anchor-one", "anchor-two"], distilled.Anchors);
    }

    [Fact]
    public void Serializer_id_1_reads_a_0_14_1_slack_cursor_row()
    {
        // Act
        var result = DeserializeLegacy(LegacyJsonJournalFixtures.SlackCursorAdvanced);

        // Assert
        var cursor = Assert.IsType<LegacySlackCursorAdvanced>(result);
        Assert.Equal("1700000000.000900", cursor.CursorTs);
    }

    [Theory]
    [InlineData("Netclaw.Actors.Protocol.TurnRecorded", "Other.Assembly")]
    [InlineData("Netclaw.Other.TurnRecorded", "Netclaw.Actors")]
    [InlineData("Netclaw.Channels.Slack.SlackThreadBindingActor+CursorAdvanced", "Netclaw.Actors")]
    [InlineData("Netclaw.Actors.Protocol.SystemPromptSet[]", "Netclaw.Actors")]
    public void Serializer_id_1_rejects_a_legacy_type_from_an_unapproved_identity(
        string legacyTypeName,
        string assemblyName)
    {
        // Arrange
        var document = new JObject { ["$type"] = $"{legacyTypeName}, {assemblyName}" };

        // Act / Assert
        Assert.Throws<JsonSerializationException>(
            () => Sys.Serialization.Deserialize(Encoding.UTF8.GetBytes(document.ToString(Formatting.None)), 1, typeof(object)));
    }

    [Fact]
    public void Current_session_event_stays_on_protobuf_serializer_150()
    {
        // Arrange
        var original = new SessionProtocol.TurnRecorded
        {
            SessionId = new SessionId("synthetic/session"),
            RecordedAtMs = 123,
            SourceReminderId = new ReminderId("reminder:synthetic-fire")
        };

        // Act
        var serializer = Sys.Serialization.FindSerializerFor(original);
        var bytes = serializer.ToBinary(original);
        var manifest = Assert.IsAssignableFrom<SerializerWithStringManifest>(serializer).Manifest(original);
        var restored = Sys.Serialization.Deserialize(bytes, serializer.Identifier, manifest);

        // Assert
        Assert.Equal(150, serializer.Identifier);
        var recorded = Assert.IsType<SessionProtocol.TurnRecorded>(restored);
        Assert.Equal(original.RecordedAtMs, recorded.RecordedAtMs);
        Assert.Equal(original.SourceReminderId, recorded.SourceReminderId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Serializer_id_1_reads_deployed_primitive_value_shapes(bool useTaggedNullableInteger)
    {
        // Arrange
        var eligibleDeliveryValue = useTaggedNullableInteger
            ? """{"$id":"1","$type":"System.Nullable`1[[System.Int32, System.Private.CoreLib]], System.Private.CoreLib","Value":{"$":"I3"}}"""
            : "3";
        var oldSnapshot = $$"""
            {
              "History": [{
                "Role": 1,
                "Content": "assistant",
                "ToolCallId": "tool-result-1",
                "ToolCalls": [{ "CallId": "call-1", "Name": "read_file", "ArgumentsJson": "{}" }],
                "MediaReferences": [{ "RelativePath": "media/image.png", "MimeType": "image/png", "Modality": { "$": "I1" } }]
              }],
              "TurnCount": { "$": "I4" },
              "EligibleDeliveryTurnNumber": {{eligibleDeliveryValue}},
              "ActiveBackgroundJobs": [{
                "JobId": "job-1", "Command": "read", "Rationale": "test", "StartedAtMs": 10,
                "Audience": 0, "Boundary": "test"
              }],
              "AdoptedContextRecords": [{
                "AuthorizedMessageId": "message-1", "AuthorizerSenderId": "sender-record",
                "Messages": [{ "MessageId": "message-2", "SenderId": "sender-message", "TimestampMs": 20 }]
              }]
            }
            """;

        // Act
        var result = Deserialize(oldSnapshot);

        // Assert
        Assert.Equal(new TurnNumber(3), result.EligibleDeliveryTurnNumber);
        Assert.Equal(new BackgroundJobId("job-1"), result.ActiveBackgroundJobs[0].JobId);
        Assert.Equal(new SenderId("sender-record"), result.AdoptedContextRecords[0].AuthorizerSenderId);
        Assert.Equal(new SenderId("sender-message"), result.AdoptedContextRecords[0].Messages[0].SenderId);
        Assert.Equal(new ToolCallId("tool-result-1"), result.History[0].ToolCallId);
        Assert.Equal(new ToolCallId("call-1"), result.History[0].ToolCalls[0].CallId);
        Assert.Equal(new ToolName("read_file"), result.History[0].ToolCalls[0].Name);
        Assert.Equal(new MimeType("image/png"), result.History[0].MediaReferences[0].MimeType);
    }

    [Fact]
    public void Serializer_id_1_keeps_current_value_shapes_and_collection_order()
    {
        // Arrange
        var snapshot = new SessionSnapshot
        {
            TurnCount = 4,
            EligibleDeliveryTurnNumber = new TurnNumber(3),
            History = new SerializableChatMessage[]
            {
                new SerializableChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = "assistant",
                    ToolCallId = new ToolCallId("tool-result-1"),
                    ToolCalls = new[] { new SerializableToolCall { CallId = new ToolCallId("call-1"), Name = new ToolName("read_file"), ArgumentsJson = "{}" } },
                    MediaReferences = new[] { new SerializableMediaReference { RelativePath = "media/image.png", MimeType = new MimeType("image/png"), Modality = (int)MediaModality.Image } }
                },
                new SerializableChatMessage { Role = ChatRole.User, Content = "next" }
            },
            ActiveBackgroundJobs = new[]
            {
                new ActiveJobInfo
                {
                    JobId = new BackgroundJobId("job-1"), Command = "read", Rationale = "test", StartedAtMs = 10,
                    Audience = Netclaw.Configuration.TrustAudience.Personal, Boundary = new Netclaw.Configuration.TrustBoundary("test")
                }
            },
            AdoptedContextRecords = new[]
            {
                new SessionSnapshot.AdoptedContextSnapshotRecord
                {
                    AuthorizedMessageId = "message-1",
                    AuthorizerSenderId = new SenderId("sender-record"),
                    Messages = new[] { new SessionSnapshot.AdoptedContextSnapshotRecord.AdoptedContextSnapshotMessage { MessageId = "message-2", SenderId = new SenderId("sender-message"), TimestampMs = 20 } }
                }
            }
        };

        // Act
        var serializer = Serializer;
        Assert.Equal(1, serializer.Identifier);
        var bytes = serializer.ToBinary(snapshot);
        var json = JObject.Parse(Encoding.UTF8.GetString(bytes));
        var result = Sys.Serialization.Deserialize(bytes, serializer.Identifier, typeof(SessionSnapshot)) as SessionSnapshot;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(new BackgroundJobId("job-1"), result.ActiveBackgroundJobs[0].JobId);
        Assert.Equal(new SenderId("sender-record"), result.AdoptedContextRecords[0].AuthorizerSenderId);
        Assert.Equal(new SenderId("sender-message"), result.AdoptedContextRecords[0].Messages[0].SenderId);
        Assert.Equal(new ToolCallId("tool-result-1"), result.History[0].ToolCallId);
        Assert.Equal(new ToolCallId("call-1"), result.History[0].ToolCalls[0].CallId);
        Assert.Equal(new ToolName("read_file"), result.History[0].ToolCalls[0].Name);
        Assert.Equal(new MimeType("image/png"), result.History[0].MediaReferences[0].MimeType);
        Assert.Equal(new TurnNumber(3), result.EligibleDeliveryTurnNumber);
        Assert.Equal(new[] { "assistant", "next" }, result.History.Select(message => message.Content));
    }

    [Fact]
    public void Serializer_id_1_preserves_explicit_null_for_nullable_values()
    {
        // Arrange
        const string oldSnapshot = """
            {
              "History": [{ "Role": 3, "Content": "tool", "ToolCallId": null }],
              "EligibleDeliveryTurnNumber": null,
              "AdoptedContextRecords": [{ "AuthorizerSenderId": null }]
            }
            """;

        // Act
        var result = Deserialize(oldSnapshot);

        // Assert
        Assert.Null(result.EligibleDeliveryTurnNumber);
        Assert.Null(result.AdoptedContextRecords[0].AuthorizerSenderId);
        Assert.Null(result.History[0].ToolCallId);
    }

    [Fact]
    public void Serializer_id_1_reads_its_default_and_empty_string_value_shapes()
    {
        // Arrange
        var snapshot = new SessionSnapshot
        {
            History = new SerializableChatMessage[]
            {
                new SerializableChatMessage
                {
                    Role = ChatRole.Tool,
                    Content = "default",
                    ToolCallId = default(ToolCallId),
                    ToolCalls = new[] { new SerializableToolCall { CallId = default, Name = default } },
                    MediaReferences = new[] { new SerializableMediaReference { MimeType = default } }
                },
                new SerializableChatMessage
                {
                    Role = ChatRole.Tool,
                    Content = "empty",
                    ToolCallId = new ToolCallId(string.Empty),
                    ToolCalls = new[] { new SerializableToolCall { CallId = new ToolCallId(string.Empty), Name = new ToolName(string.Empty) } },
                    MediaReferences = new[] { new SerializableMediaReference { MimeType = new MimeType(string.Empty) } }
                }
            },
            ActiveBackgroundJobs = new[]
            {
                new ActiveJobInfo
                {
                    JobId = default, Command = "", Rationale = "", StartedAtMs = 0,
                    Audience = Netclaw.Configuration.TrustAudience.Personal, Boundary = new Netclaw.Configuration.TrustBoundary("test")
                }
            },
            AdoptedContextRecords = new[]
            {
                new SessionSnapshot.AdoptedContextSnapshotRecord
                {
                    AuthorizerSenderId = default,
                    Messages = new[] { new SessionSnapshot.AdoptedContextSnapshotRecord.AdoptedContextSnapshotMessage { SenderId = default } }
                },
                new SessionSnapshot.AdoptedContextSnapshotRecord
                {
                    AuthorizerSenderId = new SenderId(string.Empty),
                    Messages = new[] { new SessionSnapshot.AdoptedContextSnapshotRecord.AdoptedContextSnapshotMessage { SenderId = new SenderId(string.Empty) } }
                },
                new SessionSnapshot.AdoptedContextSnapshotRecord
                {
                    AuthorizerSenderId = default(SenderId)
                }
            }
        };

        // Act
        var serializer = Serializer;
        var bytes = serializer.ToBinary(snapshot);
        var json = JObject.Parse(Encoding.UTF8.GetString(bytes));
        var result = Sys.Serialization.Deserialize(bytes, serializer.Identifier, typeof(SessionSnapshot)) as SessionSnapshot;

        // Assert
        Assert.NotNull(result);
        var history = (JArray)json["History"]!["$values"]!;
        var jobs = (JArray)json["ActiveBackgroundJobs"]!["$values"]!;
        Assert.Null(history[0]?["ToolCallId"]?["Value"]);
        Assert.Null(jobs[0]?["JobId"]);
        Assert.Equal("System.Nullable`1[[Netclaw.Tools.ToolCallId, Netclaw.Tools.Abstractions]], System.Private.CoreLib", (string?)history[0]?["ToolCallId"]?["$type"]);
        Assert.Equal(new[] { "$id", "$type" }, history[0]!["ToolCallId"]!.Children<JProperty>().Select(property => property.Name));
        Assert.Equal(string.Empty, (string?)history[1]?["ToolCallId"]?["Value"]);
        Assert.Equal(string.Empty, (string?)history[1]?["ToolCalls"]?["$values"]?[0]?["CallId"]?["Value"]);
        Assert.Equal(string.Empty, (string?)history[1]?["ToolCalls"]?["$values"]?[0]?["Name"]?["Value"]);
        Assert.Equal(string.Empty, (string?)json["AdoptedContextRecords"]?["$values"]?[1]?["AuthorizerSenderId"]?["Value"]);
        Assert.Equal(string.Empty, (string?)json["AdoptedContextRecords"]?["$values"]?[1]?["Messages"]?["$values"]?[0]?["SenderId"]?["Value"]);
        Assert.Equal("System.Nullable`1[[Netclaw.Actors.Protocol.SenderId, Netclaw.Actors]], System.Private.CoreLib", (string?)json["AdoptedContextRecords"]?["$values"]?[2]?["AuthorizerSenderId"]?["$type"]);
        Assert.Null(json["AdoptedContextRecords"]?["$values"]?[2]?["AuthorizerSenderId"]?["Value"]);
        Assert.Equal(string.Empty, result.History[1].ToolCallId?.Value);
        Assert.Equal(string.Empty, result.History[1].ToolCalls[0].CallId.Value);
        Assert.Equal(string.Empty, result.History[1].ToolCalls[0].Name.Value);
        Assert.Equal(string.Empty, result.AdoptedContextRecords[1].AuthorizerSenderId?.Value);
        Assert.Equal(string.Empty, result.AdoptedContextRecords[1].Messages[0].SenderId.Value);
        Assert.True(result.History[0].ToolCallId.HasValue);
        Assert.Null(result.History[0].ToolCallId.GetValueOrDefault().Value);
        Assert.True(result.AdoptedContextRecords[2].AuthorizerSenderId.HasValue);
        Assert.Null(result.AdoptedContextRecords[2].AuthorizerSenderId.GetValueOrDefault().Value);
    }

    [Fact]
    public void Serializer_id_1_rejects_an_empty_nullable_wrapper_with_wrong_type_metadata()
    {
        // Arrange
        const string json = """
            {
              "History": [{
                "ToolCallId": {
                  "$id": "1",
                  "$type": "Other.Type, Other.Assembly"
                }
              }]
            }
            """;

        // Act / Assert
        Assert.ThrowsAny<Exception>(() => Deserialize(json));
    }

    [Theory]
    [InlineData("\"3\"")]
    [InlineData("true")]
    [InlineData("{ \"Other\": 3 }")]
    [InlineData("2147483648")]
    public void Serializer_id_1_rejects_invalid_legacy_value_tokens(string value)
    {
        // Arrange
        var json = "{\"EligibleDeliveryTurnNumber\":" + value + "}";

        // Act / Assert
        Assert.ThrowsAny<Exception>(() => Deserialize(json));
    }

    private SessionSnapshot Deserialize(string json)
    {
        var result = Sys.Serialization.Deserialize(Encoding.UTF8.GetBytes(json), 1, typeof(SessionSnapshot));
        return Assert.IsType<SessionSnapshot>(result);
    }
}
