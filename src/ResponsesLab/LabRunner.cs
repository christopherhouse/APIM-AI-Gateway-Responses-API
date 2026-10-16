using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResponsesLab;

internal sealed class RunReport(IReadOnlyList<Conversation> sessions, bool cancelled, bool expectUnpinnedFailure = false)
{
    internal IReadOnlyList<Conversation> Sessions { get; } = sessions;
    internal bool Cancelled { get; } = cancelled;
    internal int UnpinnedFailures => Sessions.Count(IsExpectedUnpinnedFailure);
    internal int ExitCode => (expectUnpinnedFailure
        ? Cancelled || UnpinnedFailures == 0 || Sessions.Any(session => !session.Complete && !IsExpectedUnpinnedFailure(session))
        : Cancelled || Sessions.Any(session => !session.Complete || session.Failed) ||
            Sessions.Select(session => session.Scenario).Distinct().Any(scenario => Distribution(scenario).Count != 2)) ? 1 : 0;

    private static bool IsExpectedUnpinnedFailure(Conversation session)
    {
        if (session.Turns.Count < 2)
            return false;
        var last = session.Turns[^1];
        var previous = session.Turns[^2];
        return session.Turns.Take(session.Turns.Count - 1).All(turn => turn.Outcome == TurnOutcome.Success) &&
            last.Outcome == TurnOutcome.HttpError && last.Sent && last.HttpStatus is 400 or 404 &&
            (last.ErrorCode == "previous_response_not_found" || last.CrossResourceStateError) && last.GatewayRequestId is not null &&
            last.GatewayErrorReason is null && last.BackendId is "backend-a" or "backend-b" &&
            last.BackendId != session.OriginalBackend && session.OriginalBackend is not null &&
            previous.ResponseId is not null && last.PreviousResponseId == previous.ResponseId;
    }

    internal IReadOnlyDictionary<string, int> Distribution(string scenario) =>
        Sessions.Where(session => session.Scenario == scenario).SelectMany(session => session.Turns)
            .Where(turn => turn.Outcome == TurnOutcome.Success && turn.BackendId is not null)
            .GroupBy(turn => turn.BackendId!)
            .ToDictionary(group => group.Key, group => group.Count());

    internal void Write(TextWriter writer)
    {
        writer.WriteLine($"Model HTTP attempts: actual={Sessions.Sum(session => session.Turns.Sum(turn => turn.WireAttempts))}; " +
            $"planned maximum={Sessions.Sum(session => session.PlannedTurns)}; no transport retries.");
        writer.WriteLine("SESSION SUMMARY (backend sets include any observed forwarded outcome)");
        writer.WriteLine("| Scenario | Session | Outcome | Planned | Sent | Completed | Failed | Unattempted | Affinity violations | Backends |");
        writer.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var session in Sessions)
        {
            var turns = session.Turns;
            var backends = string.Join(",", turns.Select(turn => turn.BackendId).OfType<string>().Distinct().Order());
            var outcome = expectUnpinnedFailure && IsExpectedUnpinnedFailure(session)
                ? "expected-cross-backend-failure" : session.Complete ? "complete" : session.Failed ? "failed" : "incomplete";
            writer.WriteLine($"| {session.Scenario} | {session.Id:D} | {outcome} | {session.PlannedTurns} | {turns.Count(turn => turn.Sent)} | {session.Succeeded} | {turns.Count(turn => turn.Outcome != TurnOutcome.Success)} | {session.PlannedTurns - turns.Count} | {turns.Count(turn => turn.Outcome == TurnOutcome.AffinityViolation)} | {backends} |");
        }
        foreach (var scenario in Sessions.Select(session => session.Scenario).Distinct())
        {
            var distribution = Distribution(scenario);
            var group = Sessions.Where(session => session.Scenario == scenario).ToArray();
            writer.WriteLine($"{scenario}: successful requests backend-a={distribution.GetValueOrDefault("backend-a")} backend-b={distribution.GetValueOrDefault("backend-b")}; " +
                $"failed turns={group.Sum(session => session.Turns.Count(turn => turn.Outcome != TurnOutcome.Success))}; " +
                $"incomplete sessions={group.Count(session => !session.Complete)}; " +
                $"distribution={(distribution.Count == 2 ? "observed-both" : expectUnpinnedFailure ? "observed-one" : "FAILED-missing-member")} (no 50/50 assertion)");
        }
        if (expectUnpinnedFailure)
            writer.WriteLine($"Affinity OFF: {(ExitCode == 0 ? "PASS" : "INCONCLUSIVE/FAIL")}; cross-backend response-state failures={UnpinnedFailures}; " +
                $"exact different-resource errors={Sessions.Count(session => IsExpectedUnpinnedFailure(session) && session.Turns[^1].CrossResourceStateError)}; " +
                $"cancelled/deadline={Cancelled}. Same-backend success is not proof.");
        else
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
        var unpinned = scenario == "affinity-off";
        if ((unpinned || scenario is "both" or "stateful") && options.SessionAffinityEnabled == unpinned)
            throw new ConfigurationException("Use --scenario affinity-off only with exported LAB_SESSION_AFFINITY_ENABLED=false; healthy stateful scenarios require true.");
        if (unpinned || scenario is "both" or "stateful")
            sessions.AddRange(Enumerable.Range(0, options.StatefulSessions).Select(_ => new Conversation("stateful", options.TurnsPerSession)));
        if (scenario is "both" or "stateless")
            sessions.AddRange(Enumerable.Range(0, options.StatelessRequests).Select(_ => new Conversation("stateless", 1)));
        if (sessions.Count == 0)
            throw new ArgumentException("Unknown scenario.", nameof(scenario));
        client.SetBudget(sessions.Sum(session => session.PlannedTurns));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.RunTimeout);
        var first = true;
        var schedule = unpinned
            ? sessions.SelectMany(session => Enumerable.Repeat(session, session.PlannedTurns))
            : Enumerable.Range(0, options.TurnsPerSession)
                .SelectMany(_ => sessions.Where(session => session.Scenario == "stateful"))
                .Concat(sessions.Where(session => session.Scenario == "stateless"));
        try
        {
            foreach (var session in schedule.Where(session => !session.Failed))
            {
                if (!first)
                    await Task.Delay(options.RequestInterval, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                first = false;
                var evidence = await client.SendAsync(session, ConversationScript.Prompts[session.Turns.Count], deadline.Token);
                writer.WriteLine(JsonSerializer.Serialize(evidence, EvidenceJson));
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Unattempted work is counted in the final report, never as success.
        }
        return new RunReport(sessions, deadline.IsCancellationRequested, unpinned);
    }
}
