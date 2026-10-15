// -----------------------------------------------------------------------
// <copyright file="LegacyJsonJournalSqliteRecoveryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.Sql.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Replays a session journal whose rows hold 0.14.1 serializer-ID-1 JSON through the real session actor.
/// </summary>
public sealed class LegacyJsonJournalSqliteRecoveryTests : LlmSessionTestBase
{
    private const string SessionValue = "C0SYNTH/1700000000.000100";
    private TestSessionTempDirectory? _sqliteDirectory;
    private readonly FakeChatClient _chatClient = new();

    public LegacyJsonJournalSqliteRecoveryTests(ITestOutputHelper output) : base(output) { }

    private string DatabasePath => Path.Combine(
        _sqliteDirectory?.Path ?? throw new InvalidOperationException("The test SQLite directory was not configured."),
        "journal.db");

    protected override void ConfigureSessionPersistence(AkkaConfigurationBuilder builder)
    {
        _sqliteDirectory = TestSessionTempDirectory.Create("netclaw-legacy-json-");
        builder.WithSqlPersistence($"Data Source={DatabasePath};Pooling=False", "SQLite.MS");
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities { ModelId = "legacy-json-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig { Tuning = new SessionTuning { TitleGenerationInterval = 0 } });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton(new ToolRegistry());
    }

    protected override void DeleteOwnedDirectories()
    {
        SqliteConnection.ClearAllPools();
        _sqliteDirectory?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Session_actor_recovers_a_0_14_1_json_journal()
    {
        // Arrange: the journal order of a 0.14.1 session that started before 0.9.
        var sessionId = new SessionId(SessionValue);
        await SeedJsonJournalAsync($"session-{SessionValue}",
        [
            LegacyJsonJournalFixtures.SystemPromptSet,
            LegacyJsonJournalFixtures.TurnRecorded,
            LegacyJsonJournalFixtures.SessionTitleSet
        ]);

        // Act
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("legacy-json-session");
        var joined = await JoinSessionAsync(manager, subscriber, sessionId, OutputFilter.Full);

        // Assert: the session keeps the old turn and title and calls no model.
        Assert.Equal(1, joined.TurnCount);
        Assert.Equal("synthetic title", joined.Title);
        Assert.NotNull(joined.RecentMessages);
        Assert.Contains(joined.RecentMessages, static message => message.Content == "synthetic user");
        Assert.Contains(joined.RecentMessages, static message => message.Content == "synthetic reply");
        Assert.Equal(0, _chatClient.CallCount);
    }

    /// <summary>
    /// Writes one placeholder event per row through Akka, then replaces each row's payload with the
    /// captured JSON under serializer ID 1. Only the payload columns change, so Akka owns the schema.
    /// </summary>
    private async Task SeedJsonJournalAsync(string persistenceId, IReadOnlyList<string> rows)
    {
        var seeder = Sys.ActorOf(Props.Create(() => new PlaceholderSeeder(persistenceId)), "legacy-json-seeder");
        var probe = CreateTestProbe("legacy-json-seeder-ack");
        probe.Watch(seeder);
        foreach (var _ in rows)
        {
            probe.Send(seeder, new CursorAdvanced("placeholder"));
            await probe.ExpectMsgAsync<Done>(TimeSpan.FromSeconds(10),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        Sys.Stop(seeder);
        await probe.ExpectTerminatedAsync(seeder, cancellationToken: TestContext.Current.CancellationToken);

        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        for (var index = 0; index < rows.Count; index++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE journal SET message = $message, identifier = 1, manifest = ''
                WHERE persistence_id = $persistenceId AND sequence_number = $sequenceNumber
                """;
            command.Parameters.AddWithValue("$message", Encoding.UTF8.GetBytes(rows[index].Trim()));
            command.Parameters.AddWithValue("$persistenceId", persistenceId);
            command.Parameters.AddWithValue("$sequenceNumber", index + 1);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
    }

    private sealed class PlaceholderSeeder : ReceivePersistentActor
    {
        public override string PersistenceId { get; }

        public PlaceholderSeeder(string persistenceId)
        {
            PersistenceId = persistenceId;
            RecoverAny(_ => { });
            Command<CursorAdvanced>(placeholder =>
            {
                var reply = Sender;
                Persist(placeholder, _ => reply.Tell(Done.Instance));
            });
        }
    }

    private sealed class SingleClientProvider(IChatClient client) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => client;
    }
}
