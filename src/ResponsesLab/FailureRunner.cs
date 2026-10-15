using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResponsesLab;

internal sealed class FailureScenarioException(string message) : Exception(message);

internal interface IFailureClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken token);
}

internal sealed class FailureClock : IFailureClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
}

internal sealed record FailureObservation(string Phase, DateTimeOffset StartedAt, DateTimeOffset ObservedAt, TurnEvidence Turn);

internal sealed class FailureReport
{
    internal List<FailureObservation> Observations { get; } = [];
    internal List<string> Errors { get; } = [];
    internal string? RestorationError { get; set; }
    internal string? FaultMember { get; set; }
    internal DateTimeOffset? FaultAppliedAt { get; set; }
    internal DateTimeOffset? FaultDetectedAt { get; set; }
    internal DateTimeOffset? PinnedBlockedAt { get; set; }
    internal DateTimeOffset? RestoredAt { get; set; }
    internal DateTimeOffset? RecoveredAt { get; set; }
    internal bool ProofCompleted { get; set; }
    internal int ExitCode => ProofCompleted && RestoredAt is not null && RecoveredAt is not null &&
        Errors.Count == 0 && RestorationError is null ? 0 : 1;

    internal void Write(TextWriter writer)
    {
        writer.WriteLine("CONTROLLED FAILURE SUMMARY (expected HTTP failures do not make arbitrary errors pass)");
        writer.WriteLine("| Phase | Attempts | Successes | HTTP errors | Other failures | Backend set |");
        writer.WriteLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var group in Observations.GroupBy(item => item.Phase))
            writer.WriteLine($"| {group.Key} | {group.Count()} | {group.Count(item => item.Turn.Outcome == TurnOutcome.Success)} | " +
                $"{group.Count(item => item.Turn.Outcome == TurnOutcome.HttpError)} | " +
                $"{group.Count(item => item.Turn.Outcome is not (TurnOutcome.Success or TurnOutcome.HttpError))} | " +
                $"{string.Join(",", group.Select(item => item.Turn.BackendId).OfType<string>().Distinct().Order())} |");
        writer.WriteLine("SESSION EVIDENCE (HTTP errors remain visible, including expected fault observations)");
        writer.WriteLine("| Session | Sent | Successful | HTTP errors | Other failures | Backend set |");
        writer.WriteLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var session in Observations.GroupBy(item => item.Turn.SessionId))
            writer.WriteLine($"| {session.Key:D} | {session.Count(item => item.Turn.Sent)} | " +
                $"{session.Count(item => item.Turn.Outcome == TurnOutcome.Success)} | " +
                $"{session.Count(item => item.Turn.Outcome == TurnOutcome.HttpError)} | " +
                $"{session.Count(item => item.Turn.Outcome is not (TurnOutcome.Success or TurnOutcome.HttpError))} | " +
                $"{string.Join(",", session.Select(item => item.Turn.BackendId).OfType<string>().Distinct().Order())} |");
        writer.WriteLine($"Fault member={FaultMember ?? "unobserved"}; applied={Stamp(FaultAppliedAt)}; backend-404={Stamp(FaultDetectedAt)}; " +
            $"pinned-breaker-503={Stamp(PinnedBlockedAt)}; restored={Stamp(RestoredAt)}; recovered={Stamp(RecoveredAt)}");
        writer.WriteLine($"Expected-failure assertions={(ProofCompleted ? "2/2" : "incomplete")}; " +
            $"fresh healthy sessions={Observations.Count(item => item.Phase == "fresh-during-fault" && item.Turn.Outcome == TurnOutcome.Success)}/3; " +
            $"scenario-errors={JsonSerializer.Serialize(Errors)}; restoration-error={JsonSerializer.Serialize(RestorationError)}; " +
            $"result={(ExitCode == 0 ? "PASS" : "FAIL")}");
    }

    private static string Stamp(DateTimeOffset? value) => value?.ToString("O") ?? "unobserved";
}

internal sealed class FailureRunner(ResponsesClient client, LabOptions lab, FaultOptions options,
    IBackendFault fault, TextWriter writer, IFailureClock clock)
{
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async Task<FailureReport> RunAsync(CancellationToken cancellationToken)
    {
        var report = new FailureReport();
        Conversation? pinned = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(lab.RunTimeout);
        var token = deadline.Token;

        async Task<TurnEvidence> CallAsync(Conversation session, string phase, CancellationToken callToken)
        {
            callToken.ThrowIfCancellationRequested();
            var startedAt = clock.UtcNow;
            var turn = await client.SendFaultProbeAsync(session, ConversationScript.Prompts[session.PreviousResponseId is null ? 0 : 2], callToken);
            var observation = new FailureObservation(phase, startedAt, clock.UtcNow, turn);
            report.Observations.Add(observation);
            writer.WriteLine(JsonSerializer.Serialize(observation, EvidenceJson));
            return turn;
        }

        try
        {
            await fault.PrepareAsync(token);
            var members = new HashSet<string>();
            for (var i = 0; i < lab.StatefulSessions; i++)
            {
                if (i != 0)
                    await clock.DelayAsync(lab.RequestInterval, token);
                var session = new Conversation("stateful", 18);
                var first = await CallAsync(session, "healthy-setup", token);
                Require(first.Outcome == TurnOutcome.Success, "Healthy setup failed; no fault may be applied.");
                pinned ??= session;
                members.Add(first.BackendId!);
                if (members.Count == 2)
                    break;
            }
            Require(members.Count == 2 && pinned is not null, "Both healthy members were not established within the setup budget.");
            var known = pinned!;
            report.FaultMember = known.OriginalBackend;
            await fault.ApplyAsync(known.OriginalBackend!, token);
            report.FaultAppliedAt = clock.UtcNow;

            TurnEvidence? detected = null;
            for (var i = 0; i < 8; i++)
            {
                await clock.DelayAsync(options.PropagationInterval, token);
                var turn = await CallAsync(known, "fault-detection", token);
                if (turn.Outcome == TurnOutcome.Success)
                    continue;
                Require(turn.IsControlledFault(known.OriginalBackend) && turn.HttpStatus == 404,
                    "Expected a real forwarded 404 on the original member, not an arbitrary error or unproven breaker rejection.");
                detected = turn;
                report.FaultDetectedAt = clock.UtcNow;
                break;
            }
            Require(detected is not null, "Fault did not propagate within eight observations.");
            var closesAt = report.Observations[^1].StartedAt + options.BreakerWindow;
            Require(clock.UtcNow < closesAt, "Conservative breaker observation window already elapsed during the forwarded fault response.");
            var previous = known.PreviousResponseId;
            var pin = detected!.AffinityFingerprint;
            using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
            window.CancelAfter(closesAt - clock.UtcNow);
            var healthy = known.OriginalBackend == "backend-a" ? "backend-b" : "backend-a";
            for (var i = 0; i < 3; i++)
            {
                await clock.DelayAsync(options.ProbeInterval, window.Token);
                Require(clock.UtcNow < closesAt, "Breaker observation window expired before fresh-session proof.");
                var fresh = await CallAsync(new Conversation("stateful", 1), "fresh-during-fault", window.Token);
                Require(clock.UtcNow < closesAt && fresh.Outcome == TurnOutcome.Success && fresh.BackendId == healthy,
                    "Three consecutive fresh sessions did not reach the healthy member inside the observed breaker window.");
            }
            await clock.DelayAsync(options.ProbeInterval, window.Token);
            Require(clock.UtcNow < closesAt, "Breaker observation window expired before pinned-session proof.");
            var blocked = await CallAsync(known, "pinned-breaker", window.Token);
            Require(clock.UtcNow < closesAt && blocked.IsControlledFault(known.OriginalBackend) &&
                blocked.HttpStatus == 503 && known.PreviousResponseId == previous &&
                pin is not null && blocked.AffinityFingerprint == pin,
                "Original chain was not explicitly rejected by CircuitBreakerIsOpen with unchanged response ID/cookie.");
            report.PinnedBlockedAt = clock.UtcNow;
            report.ProofCompleted = true;
        }
        catch (Exception error) when (Expected(error))
        {
            report.Errors.Add(SafeError(error));
        }
        finally
        {
            if (fault.MutationAttempted)
            {
                using var restore = new CancellationTokenSource(options.RestoreTimeout);
                try
                {
                    await fault.RestoreAsync(restore.Token);
                    report.RestoredAt = clock.UtcNow;
                }
                catch (Exception error) when (Expected(error))
                {
                    report.RestorationError = SafeError(error);
                    writer.WriteLine("RESTORATION FAILED; inspect retained fault-recovery.json. Scenario failure and restoration failure are separate.");
                }
            }
        }

        if (report.ProofCompleted && report.RestoredAt is not null && pinned is not null)
        {
            try
            {
                var pause = Math.Max(options.BreakerWindow.TotalSeconds, report.Observations
                    .Select(item => item.Turn.RetryAfterSeconds ?? 0).DefaultIfEmpty().Max());
                Require(pause <= 120, "Observed Retry-After exceeds the bounded recovery wait; restoration completed but recovery remains unverified.");
                await clock.DelayAsync(TimeSpan.FromSeconds(pause), token);
                for (var i = 0; i < 8; i++)
                {
                    if (i != 0)
                        await clock.DelayAsync(options.PropagationInterval, token);
                    var recovered = await CallAsync(pinned, "recovery", token);
                    if (recovered.Outcome == TurnOutcome.Success)
                    {
                        report.RecoveredAt = clock.UtcNow;
                        break;
                    }
                    Require(recovered.IsControlledFault(pinned.OriginalBackend),
                        "Recovery encountered an unexpected error, invalid affinity, or backend switch.");
                }
                Require(report.RecoveredAt is not null, "Original response-ID/cookie chain did not recover within eight observations.");
            }
            catch (Exception error) when (Expected(error))
            {
                report.Errors.Add(SafeError(error));
            }
        }
        return report;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new FailureScenarioException(message);
    }

    private static bool Expected(Exception error) => error is FailureScenarioException or BackendControlException or
        OperationCanceledException or HttpRequestException or IOException or UnauthorizedAccessException or
        InvalidOperationException or JsonException or FormatException or OverflowException or System.ComponentModel.Win32Exception;

    private static string SafeError(Exception error) =>
        error is FailureScenarioException or BackendControlException ? error.Message :
            $"Failure scenario stopped: {error.GetType().Name}; raw details withheld.";
}
