using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResponsesLab;

namespace ResponsesLab.Tests;

public sealed class FailureTests
{
    private const string Key = "synthetic-api-key-not-real";
    private const string Pin = "synthetic-affinity-not-real";
    private static readonly Uri Endpoint = new("https://offline.example.invalid/openai/v1/responses");

    [Fact]
    public async Task Complete_failure_run_proves_fresh_routing_and_recovers_the_exact_original_chain()
    {
        using var f = new Fixture();
        var report = await f.RunAsync();

        Assert.Equal(0, report.ExitCode);
        Assert.True(report.ProofCompleted);
        Assert.Equal(1, f.Fault.Restores);
        Assert.NotNull(report.RecoveredAt);
        var calls = f.Handler.Calls;
        Assert.Equal(8, calls.Count);
        foreach (var index in new[] { 2, 6, 7 })
        {
            Assert.Equal(calls[0].Session, calls[index].Session);
            Assert.Equal("a1", calls[index].Body["previous_response_id"]!.GetValue<string>());
            Assert.Equal("APIM-Backend-Affinity=" + Pin, calls[index].Cookie);
        }
        Assert.Equal(4, calls.Skip(3).Take(4).Select(call => call.Session).Distinct().Count());
        Assert.All(calls.Skip(3).Take(3), call =>
        {
            Assert.Null(call.Cookie);
            Assert.False(call.Body.ContainsKey("previous_response_id"));
        });
        Assert.All(calls, call => Assert.True(call.Body["store"]!.GetValue<bool>()));
        var blocked = Assert.Single(report.Observations, item => item.Phase == "pinned-breaker");
        Assert.Null(blocked.Turn.BackendId);
        Assert.Equal("CircuitBreakerIsOpen", blocked.Turn.GatewayErrorReason);
        Assert.Equal(report.FaultDetectedAt!.Value.AddSeconds(8), report.PinnedBlockedAt);
        report.Write(f.Writer);
        Assert.Contains("Expected-failure assertions=2/2", f.Writer.ToString());
        Assert.DoesNotContain(Pin, f.Writer.ToString());
        Assert.DoesNotContain(Key, f.Writer.ToString());
    }

    [Fact]
    public async Task Propagation_success_advances_only_to_the_latest_successful_response()
    {
        using var f = new Fixture();
        f.Handler.Replies.Insert(2, Success("a-before-fault", "backend-a"));
        var report = await f.RunAsync();
        Assert.Equal(0, report.ExitCode);
        Assert.All(f.Handler.Calls.Skip(3).Where(call => call.Session == f.Handler.Calls[0].Session),
            call => Assert.Equal("a-before-fault", call.Body["previous_response_id"]!.GetValue<string>()));
        Assert.Equal(2, report.Observations.Count(item => item.Phase == "fault-detection"));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Unrelated_initial_errors_never_count_as_the_forwarded_fault(int status)
    {
        using var f = new Fixture();
        f.Handler.Replies[2] = Error(status, status == 503 ? null : "backend-a", "CircuitBreakerIsOpen");
        var report = await f.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.False(report.ProofCompleted);
        Assert.Equal(1, f.Fault.Restores);
        Assert.NotEmpty(report.Errors);
        Assert.Equal(3, f.Handler.Calls.Count);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("backend-a", "CircuitBreakerIsOpen")]
    [InlineData(null, "OtherReason")]
    public async Task Pinned_503_requires_gateway_breaker_reason_and_no_executed_member(string? member, string? reason)
    {
        using var f = new Fixture();
        f.Handler.Replies[6] = Error(503, member, reason);
        var report = await f.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.False(report.ProofCompleted);
        Assert.Equal(1, f.Fault.Restores);
        Assert.Equal(7, f.Handler.Calls.Count);
    }

    [Theory]
    [InlineData("backend-a", true)]
    [InlineData("backend-b", false)]
    public async Task Fresh_requests_must_all_be_successful_on_the_healthy_member(string member, bool hasCookie)
    {
        using var f = new Fixture();
        f.Handler.Replies[4] = Success("fresh-wrong", member, hasCookie ? Pin : null);
        var report = await f.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(1, f.Fault.Restores);
        Assert.False(report.ProofCompleted);
    }

    [Fact]
    public async Task Latency_that_exhausts_the_thirty_second_window_fails_instead_of_claiming_immediate_failover()
    {
        using var f = new Fixture();
        f.Handler.BeforeReply = index =>
        {
            if (index is >= 3 and <= 5)
                f.Clock.Advance(TimeSpan.FromSeconds(8));
        };
        var report = await f.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.Null(report.PinnedBlockedAt);
        Assert.Equal(1, f.Fault.Restores);
        Assert.Contains(report.Errors, value => value.Contains("inside the observed breaker window"));
    }

    [Fact]
    public async Task Slow_forwarded_404_does_not_start_a_new_thirty_second_window_when_its_body_finishes()
    {
        using var f = new Fixture();
        f.Handler.BeforeReply = index =>
        {
            if (index == 2)
                f.Clock.Advance(TimeSpan.FromSeconds(31));
        };
        var report = await f.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(3, f.Handler.Calls.Count);
        Assert.Equal(1, f.Fault.Restores);
        Assert.Contains(report.Errors, value => value.Contains("already elapsed"));
    }

    [Fact]
    public async Task Successful_forwarded_response_cannot_also_claim_a_gateway_only_breaker_error()
    {
        using var f = new Fixture();
        f.Handler.Replies[0].Headers.Add("X-Lab-Error-Reason", "CircuitBreakerIsOpen");
        var report = await f.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.False(f.Fault.MutationAttempted);
    }

    [Fact]
    public async Task Unobserved_second_backend_prevents_any_mutation()
    {
        using var f = new Fixture();
        f.Handler.Replies[1] = Success("a-second", "backend-a");
        var report = await f.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.False(f.Fault.MutationAttempted);
        Assert.Equal(0, f.Fault.Restores);
        Assert.Equal(2, f.Handler.Calls.Count);
    }

    [Fact]
    public async Task Cancellation_while_faulted_uses_an_independent_uncancelled_restoration_token()
    {
        using var f = new Fixture();
        using var stop = new CancellationTokenSource();
        f.Handler.BeforeReply = index => { if (index == 2) stop.Cancel(); };
        var report = await f.RunAsync(stop.Token);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(1, f.Fault.Restores);
        Assert.False(f.Fault.RestoreTokenCancelled);
        Assert.NotNull(report.RestoredAt);
    }

    [Fact]
    public async Task Ambiguous_apply_failure_still_restores_and_keeps_the_scenario_failure()
    {
        using var f = new Fixture();
        f.Fault.FailApply = true;
        var report = await f.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.NotNull(report.RestoredAt);
        Assert.Equal(1, f.Fault.Restores);
        Assert.Contains("Synthetic ambiguous apply", Assert.Single(report.Errors));
    }

    [Fact]
    public async Task Scenario_and_restoration_failures_are_reported_separately()
    {
        using var f = new Fixture();
        f.Fault.FailApply = true;
        f.Fault.FailRestore = true;
        var report = await f.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.NotEmpty(report.Errors);
        Assert.Equal("Synthetic restoration failed", report.RestorationError);
        Assert.Null(report.RestoredAt);
        Assert.Contains("RESTORATION FAILED", f.Writer.ToString());
    }

    [Fact]
    public async Task Failed_restoration_never_proceeds_to_recovery_or_returns_success()
    {
        using var f = new Fixture();
        f.Fault.FailRestore = true;
        var report = await f.RunAsync();
        Assert.True(report.ProofCompleted);
        Assert.Equal(1, report.ExitCode);
        Assert.Null(report.RecoveredAt);
        Assert.Equal(7, f.Handler.Calls.Count);
    }

    [Fact]
    public async Task Restoration_has_its_own_enforced_deadline()
    {
        using var f = new Fixture();
        f.Fault.BlockRestore = true;
        var report = await f.RunAsync(restoreTimeout: TimeSpan.FromMilliseconds(20));
        Assert.Equal(1, report.ExitCode);
        Assert.Null(report.RestoredAt);
        Assert.NotNull(report.RestorationError);
        Assert.Equal(1, f.Fault.Restores);
        Assert.Contains("Canceled", report.RestorationError);
    }

    [Fact]
    public async Task Propagation_observations_are_bounded_even_when_the_fault_never_reaches_the_gateway()
    {
        using var f = new Fixture();
        f.Handler.Replies.RemoveRange(2, f.Handler.Replies.Count - 2);
        f.Handler.Replies.AddRange(Enumerable.Range(0, 8).Select(index => Success("a-wait-" + index, "backend-a")));
        var report = await f.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(8, report.Observations.Count(item => item.Phase == "fault-detection"));
        Assert.Equal(10, f.Handler.Calls.Count);
        Assert.Equal(1, f.Fault.Restores);
        Assert.False(report.ProofCompleted);
    }

    [Fact]
    public async Task Recovery_is_bounded_and_preserves_the_same_chain_during_breaker_pause()
    {
        using var f = new Fixture();
        f.Handler.Replies.RemoveAt(7);
        f.Handler.Replies.AddRange(Enumerable.Range(0, 8).Select(_ => Error(503, null, "CircuitBreakerIsOpen")));
        var report = await f.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.NotNull(report.RestoredAt);
        Assert.Null(report.RecoveredAt);
        Assert.Equal(8, report.Observations.Count(item => item.Phase == "recovery"));
        Assert.All(f.Handler.Calls.Skip(7), call => Assert.Equal("a1", call.Body["previous_response_id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Excessive_retry_after_does_not_extend_the_run_or_skip_restoration()
    {
        using var f = new Fixture();
        f.Handler.Replies[6].Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(121));
        var report = await f.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.NotNull(report.RestoredAt);
        Assert.Null(report.RecoveredAt);
        Assert.Contains(report.Errors, value => value.Contains("Retry-After exceeds"));
    }

    [Fact]
    public async Task Normal_client_cannot_resume_an_expected_fault_without_the_explicit_probe_path()
    {
        using var handler = new ResponseHandler([Success("a1", "backend-a"), Error(404, "backend-a")]);
        using var http = new HttpClient(handler);
        var client = new ResponsesClient(http, Lab());
        var session = new Conversation("stateful", 3);
        await client.SendAsync(session, "setup", default);
        await client.SendAsync(session, "fault", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(session, "no replay", default));
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Probe_path_cannot_resume_unexpected_failures()
    {
        using var handler = new ResponseHandler([Success("a1", "backend-a"), Error(500, "backend-a")]);
        using var http = new HttpClient(handler);
        var client = new ResponsesClient(http, Lab());
        var session = new Conversation("stateful", 3);
        await client.SendAsync(session, "setup", default);
        await client.SendFaultProbeAsync(session, "fault", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendFaultProbeAsync(session, "no replay", default));
    }

    private static LabOptions Lab() => new()
    {
        Endpoint = Endpoint,
        Model = "responses-chat",
        SubscriptionKey = Key,
        StatefulSessions = 2,
        RequestInterval = TimeSpan.FromSeconds(10)
    };

    private static FaultOptions FaultOptions(TimeSpan? restoreTimeout = null) => new()
    {
        Subscription = "11111111-1111-4111-8111-111111111111",
        Group = "rg-responses-offline",
        Environment = "offline",
        Owner = "22222222-2222-4222-8222-222222222222",
        ApimId = "/unused",
        FoundryAId = "/unused-a",
        FoundryBId = "/unused-b",
        RecoveryPath = "unused",
        RestoreTimeout = restoreTimeout ?? TimeSpan.FromSeconds(180)
    };

    private static HttpResponseMessage Success(string id, string member, string? cookie = Pin)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id,
                status = "completed",
                output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Synthetic architecture answer" } } } }
            }), Encoding.UTF8, "application/json")
        };
        response.Headers.Add("X-Lab-Request-Id", Guid.NewGuid().ToString("D"));
        response.Headers.Add("X-Lab-Backend-Id", member);
        if (cookie is not null)
            response.Headers.Add("Set-Cookie", $"APIM-Backend-Affinity={cookie}; Secure; HttpOnly; Path=/");
        return response;
    }

    private static HttpResponseMessage Error(int status, string? member = null, string? reason = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("<html>synthetic error</html>") };
        response.Headers.Add("X-Lab-Request-Id", Guid.NewGuid().ToString("D"));
        if (member is not null)
            response.Headers.Add("X-Lab-Backend-Id", member);
        if (reason is not null)
            response.Headers.Add("X-Lab-Error-Reason", reason);
        return response;
    }

    private sealed class Clock : IFailureClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);
        internal void Advance(TimeSpan delay) => UtcNow += delay;
        public Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Advance(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class Fault : IBackendFault
    {
        public bool MutationAttempted { get; private set; }
        internal bool FailApply { get; set; }
        internal bool FailRestore { get; set; }
        internal bool BlockRestore { get; set; }
        internal int Restores { get; private set; }
        internal bool RestoreTokenCancelled { get; private set; }
        public Task PrepareAsync(CancellationToken token) => Task.CompletedTask;
        public Task ApplyAsync(string member, CancellationToken token)
        {
            MutationAttempted = true;
            if (FailApply)
                throw new BackendControlException("Synthetic ambiguous apply");
            return Task.CompletedTask;
        }
        public async Task RestoreAsync(CancellationToken token)
        {
            Restores++;
            RestoreTokenCancelled = token.IsCancellationRequested;
            if (FailRestore)
                throw new BackendControlException("Synthetic restoration failed");
            if (BlockRestore)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
    }

    private sealed record Call(string Session, JsonObject Body, string? Cookie);

    private sealed class ResponseHandler(List<HttpResponseMessage> replies) : HttpMessageHandler
    {
        internal List<HttpResponseMessage> Replies { get; } = replies;
        internal List<Call> Calls { get; } = [];
        internal Action<int>? BeforeReply { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Calls.Count;
            Calls.Add(new Call(request.Headers.GetValues("X-Lab-Session-Id").Single(),
                JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject(),
                request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : null));
            BeforeReply?.Invoke(index);
            return Replies[index];
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (var response in Replies)
                    response.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal Clock Clock { get; } = new();
        internal Fault Fault { get; } = new();
        internal StringWriter Writer { get; } = new();
        internal ResponseHandler Handler { get; } = new([
            Success("a1", "backend-a"), Success("b1", "backend-b"), Error(404, "backend-a"),
            Success("fresh1", "backend-b"), Success("fresh2", "backend-b"), Success("fresh3", "backend-b"),
            Error(503, null, "CircuitBreakerIsOpen"), Success("a-recovered", "backend-a", null)
        ]);
        private readonly HttpClient http;

        internal Fixture() => http = new HttpClient(Handler);
        internal Task<FailureReport> RunAsync(CancellationToken token = default, TimeSpan? restoreTimeout = null) =>
            new FailureRunner(new ResponsesClient(http, Lab()), Lab(), FaultOptions(restoreTimeout), Fault, Writer, Clock).RunAsync(token);
        public void Dispose()
        {
            http.Dispose();
            Writer.Dispose();
        }
    }
}
