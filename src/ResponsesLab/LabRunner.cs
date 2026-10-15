using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResponsesLab;

internal sealed class RunReport(IReadOnlyList<Conversation> sessions, bool cancelled)
{
    internal IReadOnlyList<Conversation> Sessions { get; } = sessions;
    internal bool Cancelled { get; } = cancelled;
    internal int ExitCode => Cancelled || Sessions.Any(session => !session.Complete || session.Failed) ||
        Sessions.Select(session => session.Scenario).Distinct().Any(scenario => Distribution(scenario).Count != 2) ? 1 : 0;

    internal IReadOnlyDictionary<string, int> Distribution(string scenario) =>
        Sessions.Where(session => session.Scenario == scenario).SelectMany(session => session.Turns)
            .Where(turn => turn.Outcome == TurnOutcome.Success && turn.BackendId is not null)
            .GroupBy(turn => turn.BackendId!)
            .ToDictionary(group => group.Key, group => group.Count());

    internal void Write(TextWriter writer)
    {
        writer.WriteLine("SESSION SUMMARY (backend sets include any observed forwarded outcome)");
        writer.WriteLine("| Scenario | Session | Outcome | Planned | Sent | Completed | Failed | Unattempted | Affinity violations | Backends |");
        writer.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var session in Sessions)
        {
            var turns = session.Turns;
            var backends = string.Join(",", turns.Select(turn => turn.BackendId).OfType<string>().Distinct().Order());
            var outcome = session.Complete ? "complete" : session.Failed ? "failed" : "incomplete";
            writer.WriteLine($"| {session.Scenario} | {session.Id:D} | {outcome} | {session.PlannedTurns} | {turns.Count(turn => turn.Sent)} | {session.Succeeded} | {turns.Count(turn => turn.Outcome != TurnOutcome.Success)} | {session.PlannedTurns - turns.Count} | {turns.Count(turn => turn.Outcome == TurnOutcome.AffinityViolation)} | {backends} |");
        }
        foreach (var scenario in Sessions.Select(session => session.Scenario).Distinct())
        {
            var distribution = Distribution(scenario);
            var group = Sessions.Where(session => session.Scenario == scenario).ToArray();
            writer.WriteLine($"{scenario}: successful requests backend-a={distribution.GetValueOrDefault("backend-a")} backend-b={distribution.GetValueOrDefault("backend-b")}; " +
                $"failed turns={group.Sum(session => session.Turns.Count(turn => turn.Outcome != TurnOutcome.Success))}; " +
                $"incomplete sessions={group.Count(session => !session.Complete)}; " +
                $"distribution={(distribution.Count == 2 ? "observed-both" : "FAILED-missing-member")} (no 50/50 assertion)");
        }
        writer.WriteLine($"Run: {(ExitCode == 0 ? "PASS" : "FAIL")}; cancelled/deadline={Cancelled}; expected-failure scenarios=0 (healthy run).");
    }
}

internal sealed class LabRunner(ResponsesClient client, LabOptions options, TextWriter writer)
{
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async Task<RunReport> RunAsync(string scenario, CancellationToken token)
    {
        var sessions = new List<Conversation>();
        if (scenario is "both" or "stateful")
            sessions.AddRange(Enumerable.Range(0, options.StatefulSessions).Select(_ => new Conversation("stateful", options.TurnsPerSession)));
        if (scenario is "both" or "stateless")
            sessions.AddRange(Enumerable.Range(0, options.StatelessRequests).Select(_ => new Conversation("stateless", 1)));
        if (sessions.Count == 0)
            throw new ArgumentException("Unknown scenario.", nameof(scenario));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.RunTimeout);
        var first = true;
        try
        {
            foreach (var mode in new[] { "stateful", "stateless" })
            {
                for (var turn = 0; turn < (mode == "stateful" ? options.TurnsPerSession : 1); turn++)
                {
                    foreach (var session in sessions.Where(session => session.Scenario == mode && !session.Failed))
                    {
                        if (!first)
                            await Task.Delay(options.RequestInterval, deadline.Token);
                        deadline.Token.ThrowIfCancellationRequested();
                        first = false;
                        var evidence = await client.SendAsync(session, ConversationScript.Prompts[turn], deadline.Token);
                        writer.WriteLine(JsonSerializer.Serialize(evidence, EvidenceJson));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Unattempted work is counted in the final report, never as success.
        }
        return new RunReport(sessions, deadline.IsCancellationRequested);
    }
}
