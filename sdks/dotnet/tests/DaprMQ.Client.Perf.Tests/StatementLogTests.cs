using DaprMQ.Client.Perf;

namespace DaprMQ.Client.Perf.Tests;

public class StatementLogTests
{
    // Verbatim shape of postgres log_statement=all output for Dapr's postgresql/v2 state store:
    // multi-line statements continue on tab-indented lines, and bound parameters follow on a
    // DETAIL line from the same backend pid.
    private const string Log = """
        2026-10-04 08:57:30.070 UTC [84] LOG:  execute stmtcache_ffb4: INSERT INTO dapr_metadata (key, value) VALUES ('migrations-state-v2-daprmq_', $1) ON CONFLICT (key) DO UPDATE SET value = $1
        2026-10-04 08:57:30.070 UTC [84] DETAIL:  parameters: $1 = '2'
        2026-10-04 08:57:30.346 UTC [83] LOG:  execute stmtcache_792b:
        	SELECT
        	  value, etag, expires_at
        	FROM daprmq_state
        	WHERE
        	  key = $1
        2026-10-04 08:57:30.346 UTC [91] LOG:  execute stmtcache_aaaa:
        	DELETE FROM daprmq_state WHERE key = $1
        2026-10-04 08:57:30.346 UTC [83] DETAIL:  parameters: $1 = 'daprmq-api||QueueActor||orders||metadata'
        2026-10-04 08:57:30.347 UTC [91] DETAIL:  parameters: $1 = 'daprmq-api||QueueActor||orders||3f2b8c1e-9a7d-4c2e-8f1a-0b9c8d7e6f5a-lock'
        2026-10-04 08:57:30.356 UTC [83] LOG:  statement: begin
        2026-10-04 08:57:30.357 UTC [83] LOG:  execute stmtcache_dbea:
        	INSERT INTO daprmq_state AS t
        	  (key, value, etag, expires_at)
        	VALUES
        	  ($1, $2, gen_random_uuid(),NULL)
        2026-10-04 08:57:30.357 UTC [83] DETAIL:  parameters: $1 = 'daprmq-api||QueueActor||orders||queue_1_seg_12', $2 = '\x7b22'
        2026-10-04 08:57:30.358 UTC [83] LOG:  execute stmtcache_792b:
        	SELECT
        	  value, etag, expires_at
        	FROM daprmq_state
        	WHERE
        	  key = $1
        2026-10-04 08:57:30.358 UTC [83] DETAIL:  parameters: $1 = 'daprmq-api||QueueActor||other-queue||metadata'
        2026-10-04 08:57:30.359 UTC [83] LOG:  statement: commit
        """;

    [Fact]
    public void Parse_CountsOnlyActorStateStatements_ClassifiedAsReadOrWrite()
    {
        var queries = StatementLog.Parse(Log);

        Assert.Equal(4, queries.Count);
        Assert.Equal(2, queries.Count(q => q.Kind == StateQueryKind.Read));
        Assert.Equal(2, queries.Count(q => q.Kind == StateQueryKind.Write));
    }

    [Fact]
    public void Parse_MatchesParametersToTheirStatementByBackendPid()
    {
        var queries = StatementLog.Parse(Log);

        Assert.Contains(queries, q => q is { Kind: StateQueryKind.Write, StateName: "*-lock" });
        Assert.Contains(queries, q => q is { Kind: StateQueryKind.Read, StateName: "metadata" });
    }

    [Fact]
    public void Parse_NormalisesVariablePartsOfStateNames()
    {
        var queries = StatementLog.Parse(Log);

        Assert.Contains(queries, q => q is { ActorType: "QueueActor", StateName: "queue_*_seg_*", Kind: StateQueryKind.Write });
    }

    [Fact]
    public void Summarise_GroupsByActorTypeAndStateName_AcrossActorIds()
    {
        var keys = StatementLog.Summarise(StatementLog.Parse(Log));

        var metadata = Assert.Single(keys, k => k.StateName == "metadata");
        Assert.Equal("QueueActor", metadata.ActorType);
        Assert.Equal(2, metadata.Reads);
        Assert.Equal(0, metadata.Writes);
        Assert.Equal(3, keys.Count);
    }

    [Theory]
    [InlineData("item_20", "item_*")]
    [InlineData("publish_9b1c0d2e4f6a4b8c9d0e1f2a3b4c5d6e", "publish_*")]
    [InlineData("locks_exp_1759568250", "locks_exp_*")]
    [InlineData("subscriber-set-generation-0", "subscriber-set-generation-*")]
    [InlineData("queue_1_seg_12", "queue_*_seg_*")]
    // Keys embedding an arbitrary id (lock, session, idempotency key, subscriber, publish):
    // the whole id goes, even when it contains digits or dashes.
    [InlineData("uAHqyQcWBAE-lock", "*-lock")]
    [InlineData("z9TpvgeaSMg-lock", "*-lock")]
    [InlineData("session-lock_order-42", "session-lock_*")]
    [InlineData("session-order-42", "session-*")]
    [InlineData("idem_client-key-7", "idem_*")]
    [InlineData("circuit_sub-a", "circuit_*")]
    [InlineData("publish_9b1c0d2e", "publish_*")]
    [InlineData("metadata", "metadata")]
    public void NormaliseStateName_ReplacesIdsAndNumbers(string raw, string expected)
    {
        Assert.Equal(expected, StatementLog.NormaliseStateName(raw));
    }
}
