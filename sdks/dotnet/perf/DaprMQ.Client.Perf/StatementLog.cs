using System.Text.RegularExpressions;

namespace DaprMQ.Client.Perf;

public enum StateQueryKind { Read, Write }

/// <summary>One statement against the actor state table, keyed by actor type and (normalised) state name.</summary>
public sealed record StateQuery(StateQueryKind Kind, string ActorType, string StateName);

public sealed record StateKeyCount(string ActorType, string StateName, int Reads, int Writes);

/// <summary>
/// Parses Postgres <c>log_statement=all</c> output from Dapr's postgresql/v2 state store. Every
/// actor-state statement is keyed on <c>$1 = '{appId}||{actorType}||{actorId}||{stateName}'</c>,
/// logged on a DETAIL line from the same backend pid after the (possibly multi-line) statement.
/// </summary>
public static partial class StatementLog
{
    private const string StateTable = "daprmq_state";

    [GeneratedRegex(@"\[(\d+)\] (LOG|DETAIL):  (.*)$")]
    private static partial Regex LogLine();

    [GeneratedRegex(@"^execute [^:]+: ?(.*)$")]
    private static partial Regex Execute();

    [GeneratedRegex(@"\$1 = '((?:[^']|'')*)'")]
    private static partial Regex FirstParameter();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    /// <summary>
    /// Prefixes of state names that end in an arbitrary id (session id, idempotency key, subscriber
    /// id, publish guid), longest first so "session-lock_" wins over "session-".
    /// </summary>
    private static readonly string[] IdPrefixes = ["session-lock_", "session-", "idem_", "circuit_", "publish_"];

    public static IReadOnlyList<StateQuery> Parse(string log)
    {
        var queries = new List<StateQuery>();
        var pending = new Dictionary<string, string>(); // pid -> statement text awaiting its DETAIL
        string? lastPid = null;

        foreach (var line in log.Split('\n'))
        {
            // Continuation of the previous LOG line's multi-line statement.
            if (line.StartsWith('\t'))
            {
                if (lastPid != null && pending.TryGetValue(lastPid, out var text))
                {
                    pending[lastPid] = text + "\n" + line.Trim();
                }
                continue;
            }

            var match = LogLine().Match(line.TrimEnd('\r'));
            if (!match.Success)
            {
                lastPid = null;
                continue;
            }

            var (pid, level, message) = (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
            lastPid = pid;

            if (level == "LOG")
            {
                if (Execute().Match(message) is { Success: true } execute)
                {
                    pending[pid] = execute.Groups[1].Value;
                }
                else
                {
                    pending.Remove(pid);
                }
                continue;
            }

            if (!pending.Remove(pid, out var statement) || !statement.Contains(StateTable)
                || FirstParameter().Match(message) is not { Success: true } parameter)
            {
                continue;
            }

            var key = parameter.Groups[1].Value.Split("||");
            if (key.Length != 4 || Classify(statement) is not { } kind)
            {
                continue;
            }

            queries.Add(new StateQuery(kind, key[1], NormaliseStateName(key[3])));
        }

        return queries;
    }

    public static IReadOnlyList<StateKeyCount> Summarise(IEnumerable<StateQuery> queries) =>
        queries
            .GroupBy(q => (q.ActorType, q.StateName))
            .Select(g => new StateKeyCount(g.Key.ActorType, g.Key.StateName,
                g.Count(q => q.Kind == StateQueryKind.Read), g.Count(q => q.Kind == StateQueryKind.Write)))
            .OrderByDescending(k => k.Reads + k.Writes)
            .ThenBy(k => k.ActorType).ThenBy(k => k.StateName)
            .ToList();

    /// <summary>
    /// Collapses ids and sequence numbers so e.g. every lock or segment is one row. Mirrors the key
    /// formats the actors build: <c>{lockId}-lock</c>, prefix + arbitrary id, otherwise numbers only.
    /// </summary>
    public static string NormaliseStateName(string stateName)
    {
        if (stateName.EndsWith("-lock"))
        {
            return "*-lock";
        }

        foreach (var prefix in IdPrefixes)
        {
            if (stateName.StartsWith(prefix) && stateName.Length > prefix.Length)
            {
                return prefix + "*";
            }
        }

        return Number().Replace(stateName, "*");
    }

    private static StateQueryKind? Classify(string statement) =>
        statement.TrimStart().Split(null, 2)[0].ToUpperInvariant() switch
        {
            "SELECT" => StateQueryKind.Read,
            "INSERT" or "UPDATE" or "DELETE" => StateQueryKind.Write,
            _ => null,
        };
}
