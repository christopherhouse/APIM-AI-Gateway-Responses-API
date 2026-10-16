using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResponsesLab;

namespace ResponsesLab.Tests;

public sealed class HarnessTests
{
    private const string Key = "synthetic-key-not-a-real-credential";
    private const string PinA = "synthetic-private-pin-a";
    private const string PinB = "synthetic-private-pin-b";
    private static readonly Uri Endpoint = new("https://offline.example.invalid/openai/v1/responses");

    private static LabOptions Options(TimeSpan? requestTimeout = null, int statelessRequests = 2, TimeSpan? runTimeout = null,
        bool affinityEnabled = true) => new()
        {
            Endpoint = Endpoint,
            Model = "responses-chat",
            SubscriptionKey = Key,
            StatefulSessions = 2,
            TurnsPerSession = 3,
            StatelessRequests = statelessRequests,
            RequestInterval = TimeSpan.Zero,
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5),
            RunTimeout = runTimeout ?? TimeSpan.FromSeconds(30),
            SessionAffinityEnabled = affinityEnabled
        };

    private static HttpResponseMessage Success(string id, string backend = "backend-a", string? cookie = PinA,
        string text = "Foundry owns response state; APIM supplies routing continuity.", string status = "completed")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id,
                status,
                output = new[]
                {
                    new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } }
                }
            }), Encoding.UTF8, "application/json")
        };
        response.Headers.Add("X-Lab-Backend-Id", backend);
        response.Headers.Add("X-Lab-Request-Id", Guid.NewGuid().ToString("D"));
        if (cookie is not null)
            response.Headers.Add("Set-Cookie", $"APIM-Backend-Affinity={cookie}; Secure; HttpOnly; Path=/");
        return response;
    }

    [Fact]
    public async Task Interleaved_sessions_keep_response_chains_and_cookies_isolated()
    {
        using var handler = new FakeHandler(
            Success("a1"), Success("b1", "backend-b", PinB),
            Success("a2", cookie: null), Success("b2", "backend-b", null));
        using var http = new HttpClient(handler);
        var client = new ResponsesClient(http, Options());
        var a = new Conversation("stateful", 2);
        var b = new Conversation("stateful", 2);

        foreach (var session in new[] { a, b, a, b })
            Assert.Equal(TurnOutcome.Success, (await client.SendAsync(session, "Architecture question", default)).Outcome);

        Assert.Null(handler.Calls[0].Cookie);
        Assert.Null(handler.Calls[1].Cookie);
        Assert.False(handler.Calls[0].Body.ContainsKey("previous_response_id"));
        Assert.False(handler.Calls[1].Body.ContainsKey("previous_response_id"));
        Assert.Equal("APIM-Backend-Affinity=" + PinA, handler.Calls[2].Cookie);
        Assert.Equal("APIM-Backend-Affinity=" + PinB, handler.Calls[3].Cookie);
        Assert.Equal("a1", handler.Calls[2].Body["previous_response_id"]!.GetValue<string>());
        Assert.Equal("b1", handler.Calls[3].Body["previous_response_id"]!.GetValue<string>());
        Assert.Equal("a2", a.PreviousResponseId);
        Assert.Equal("b2", b.PreviousResponseId);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(4, handler.Calls.Select(call => call.ClientRequestId).Distinct().Count());
        Assert.All(handler.Calls, call =>
        {
            Assert.Equal(ConversationScript.Instructions, call.Body["instructions"]!.GetValue<string>());
            Assert.True(call.Body["store"]!.GetValue<bool>());
            Assert.False(call.Body["stream"]!.GetValue<bool>());
            Assert.Equal(2048, call.Body["max_output_tokens"]!.GetValue<int>());
            Assert.Equal("low", call.Body["reasoning"]!["effort"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task Stateless_requests_ignore_set_cookie_and_never_chain()
    {
        using var handler = new FakeHandler(Success("one"), Success("two", "backend-b", PinB));
        using var http = new HttpClient(handler);
        var client = new ResponsesClient(http, Options());
        foreach (var id in Enumerable.Range(0, 2))
        {
            var session = new Conversation("stateless", 1);
            var result = await client.SendAsync(session, ConversationScript.Prompts[0], default);
            Assert.Equal(TurnOutcome.Success, result.Outcome);
            Assert.Null(session.PreviousResponseId);
            Assert.Empty(session.Cookies.GetAllCookies().Cast<Cookie>());
            Assert.Null(result.AffinityFingerprint);
        }
        Assert.All(handler.Calls, call =>
        {
            Assert.Null(call.Cookie);
            Assert.False(call.Body.ContainsKey("previous_response_id"));
            Assert.False(call.Body["store"]!.GetValue<bool>());
        });
    }

    [Theory]
    [InlineData("X-Lab-Backend-Id", "missing")]
    [InlineData("X-Lab-Backend-Id", "responses-pool")]
    [InlineData("X-Lab-Request-Id", "missing")]
    [InlineData("X-Lab-Request-Id", "not-a-uuid")]
    [InlineData("X-Lab-Request-Id", "duplicate")]
    public async Task Missing_or_untrusted_gateway_evidence_cannot_advance_a_chain(string header, string replacement)
    {
        var response = Success("first");
        response.Headers.Remove(header);
        if (replacement == "duplicate")
            response.Headers.Add(header, new[] { Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") });
        else if (replacement != "missing")
            response.Headers.Add(header, replacement);
        using var handler = new FakeHandler(response);
        using var http = new HttpClient(handler);
        var session = new Conversation("stateful", 3);
        var result = await new ResponsesClient(http, Options()).SendAsync(session, "Question", default);
        Assert.Equal(TurnOutcome.MissingEvidence, result.Outcome);
        Assert.True(result.Sent);
        Assert.Null(session.PreviousResponseId);
        Assert.Null(session.OriginalBackend);
        Assert.Empty(session.Cookies.GetAllCookies().Cast<Cookie>());
    }

    [Fact]
    public async Task Backend_switch_is_a_failure_and_cannot_be_retried_or_restarted()
    {
        using var handler = new FakeHandler(Success("first"), Success("second", "backend-b", PinB));
        using var http = new HttpClient(handler);
        var client = new ResponsesClient(http, Options());
        var session = new Conversation("stateful", 3);
        await client.SendAsync(session, "First", default);
        var result = await client.SendAsync(session, "Second", default);
        Assert.Equal(TurnOutcome.AffinityViolation, result.Outcome);
        Assert.Equal("backend-b", result.BackendId);
        Assert.Equal("first", session.PreviousResponseId);
        Assert.Equal("backend-a", session.OriginalBackend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(session, "Do not replay", default));
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    public async Task Affinity_off_proves_real_cross_backend_missing_response_without_cookies_or_retries(int status)
    {
        using var handler = new FakeHandler((call, _) =>
        {
            var response = Success($"response-{call.Number}", cookie: null);
            if (call.Body.ContainsKey("previous_response_id"))
            {
                response.StatusCode = (HttpStatusCode)status;
                response.Headers.Remove("X-Lab-Backend-Id");
                response.Headers.Add("X-Lab-Backend-Id", "backend-b");
                response.Content = new StringContent("""{"error":{"code":"previous_response_not_found"}}""");
            }
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var writer = new StringWriter();
        var options = Options(affinityEnabled: false);
        var report = await new LabRunner(new ResponsesClient(http, options), options, writer).RunAsync("affinity-off", default);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(2, report.UnpinnedFailures);
        Assert.Equal(4, handler.Calls.Count);
        Assert.All(handler.Calls, call => Assert.Null(call.Cookie));
        Assert.Equal("response-1", handler.Calls[1].Body["previous_response_id"]!.GetValue<string>());
        Assert.Equal("response-3", handler.Calls[3].Body["previous_response_id"]!.GetValue<string>());
        Assert.All(report.Sessions, session => Assert.Equal(2, session.Turns.Count));
        report.Write(writer);
        Assert.Contains("Affinity OFF: PASS", writer.ToString());
        Assert.Contains("expected-cross-backend-failure", writer.ToString());
    }

    [Theory]
    [InlineData("invalid_request_error", null, null, true, true)]
    [InlineData("server_error", null, null, true, false)]
    [InlineData("invalid_request_error", "unrelated", null, true, false)]
    [InlineData("invalid_request_error", null, "model", true, false)]
    [InlineData("invalid_request_error", null, null, false, false)]
    public async Task Off_demo_classifies_only_the_observed_exact_cross_resource_error_shape(
        string type, string? code, string? param, bool exactMessage, bool expected)
    {
        using var handler = new FakeHandler((call, _) =>
        {
            var response = Success($"response-{call.Number}", cookie: null);
            if (call.Body.ContainsKey("previous_response_id"))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                response.Headers.Remove("X-Lab-Backend-Id");
                response.Headers.Add("X-Lab-Backend-Id", "backend-b");
                response.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        type,
                        code,
                        param,
                        message = exactMessage
                            ? "The requested item was created under a different Azure OpenAI resource. Use the same resource that created the item to access it."
                            : "An unrelated invalid request."
                    }
                }));
            }
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var writer = new StringWriter();
        var options = Options(affinityEnabled: false);
        var report = await new LabRunner(new ResponsesClient(http, options), options, writer).RunAsync("affinity-off", default);
        Assert.Equal(expected ? 0 : 1, report.ExitCode);
        Assert.Equal(expected ? 2 : 0, report.UnpinnedFailures);
        Assert.All(report.Sessions, session => Assert.Equal(expected, session.Turns[^1].CrossResourceStateError));
        Assert.All(report.Sessions, session => Assert.Equal(code, session.Turns[^1].ErrorCode));
        report.Write(writer);
        Assert.Contains($"exact different-resource errors={(expected ? 2 : 0)}", writer.ToString());
    }

    [Theory]
    [InlineData(400, "previous_response_not_found", "backend-a", false)]
    [InlineData(400, "AffinityCookieRequired", "backend-b", false)]
    [InlineData(503, "previous_response_not_found", "backend-b", false)]
    [InlineData(400, "previous_response_not_found", "backend-b", true)]
    public async Task Off_demo_rejects_same_backend_policy_unrelated_and_uncorrelated_errors(
        int status, string code, string backend, bool missingRequestId)
    {
        using var handler = new FakeHandler((call, _) =>
        {
            var response = Success($"response-{call.Number}", cookie: null);
            if (call.Body.ContainsKey("previous_response_id"))
            {
                response.StatusCode = (HttpStatusCode)status;
                response.Headers.Remove("X-Lab-Backend-Id");
                response.Headers.Add("X-Lab-Backend-Id", backend);
                if (missingRequestId)
                    response.Headers.Remove("X-Lab-Request-Id");
                response.Content = new StringContent(JsonSerializer.Serialize(new { error = new { code } }));
            }
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var writer = new StringWriter();
        var options = Options(affinityEnabled: false);
        var report = await new LabRunner(new ResponsesClient(http, options), options, writer).RunAsync("affinity-off", default);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(0, report.UnpinnedFailures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Off_demo_does_not_claim_same_backend_success_or_successful_switch_as_expected_failure(bool switchBackend)
    {
        using var handler = new FakeHandler((call, _) => Task.FromResult(Success(
            $"response-{call.Number}", switchBackend && call.Body.ContainsKey("previous_response_id") ? "backend-b" : "backend-a", null)));
        using var http = new HttpClient(handler);
        using var writer = new StringWriter();
        var options = Options(affinityEnabled: false);
        var report = await new LabRunner(new ResponsesClient(http, options), options, writer).RunAsync("affinity-off", default);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(0, report.UnpinnedFailures);
        report.Write(writer);
        Assert.Contains("INCONCLUSIVE/FAIL", writer.ToString());
    }

    [Fact]
    public async Task Scenario_mode_mismatch_fails_before_HTTP_and_off_mode_does_not_hide_stale_gateway_affinity()
    {
        using var handler = new FakeHandler(Success("first"));
        using var http = new HttpClient(handler);
        using var writer = new StringWriter();
        var enabled = Options();
        await Assert.ThrowsAsync<ConfigurationException>(() =>
            new LabRunner(new ResponsesClient(http, enabled), enabled, writer).RunAsync("affinity-off", default));
        var disabled = Options(affinityEnabled: false);
        await Assert.ThrowsAsync<ConfigurationException>(() =>
            new LabRunner(new ResponsesClient(http, disabled), disabled, writer).RunAsync("stateful", default));
        Assert.Empty(handler.Calls);
        var session = new Conversation("stateful", 2);
        var result = await new ResponsesClient(http, disabled).SendAsync(session, "First", default);
        Assert.Equal(TurnOutcome.InvalidAffinity, result.Outcome);
        Assert.Null(session.PreviousResponseId);
    }

    [Theory]
    [InlineData("missing", "MissingAffinity")]
    [InlineData("empty", "MissingAffinity")]
    [InlineData("quoted-empty", "MissingAffinity")]
    [InlineData("duplicate", "InvalidAffinity")]
    [InlineData("expired", "MissingAffinity")]
    [InlineData("wrong-path", "MissingAffinity")]
    [InlineData("wrong-domain", "InvalidAffinity")]
    public async Task Unusable_affinity_is_not_a_successful_first_turn(string kind, string outcome)
    {
        var response = Success("first", cookie: null);
        var header = kind switch
        {
            "empty" => "APIM-Backend-Affinity=; Secure; Path=/",
            "quoted-empty" => "APIM-Backend-Affinity=\"\"; Secure; Path=/",
            "expired" => $"APIM-Backend-Affinity={PinA}; Max-Age=0; Secure; Path=/",
            "wrong-path" => $"APIM-Backend-Affinity={PinA}; Secure; Path=/other",
            "wrong-domain" => $"APIM-Backend-Affinity={PinA}; Secure; Domain=foreign.example.invalid; Path=/",
            _ => $"APIM-Backend-Affinity={PinA}; Secure; Path=/"
        };
        if (kind != "missing")
            response.Headers.Add("Set-Cookie", header);
        if (kind == "duplicate")
            response.Headers.Add("Set-Cookie", $"APIM-Backend-Affinity={PinB}; Secure; Path=/");
        using var handler = new FakeHandler(response);
        using var http = new HttpClient(handler);
        var session = new Conversation("stateful", 3);
        var result = await new ResponsesClient(http, Options()).SendAsync(session, "Question", default);
        Assert.Equal(outcome, result.Outcome.ToString());
        Assert.Null(session.PreviousResponseId);
    }

    [Fact]
    public async Task Expired_cookie_blocks_the_next_turn_before_any_HTTP_and_does_not_reset_state()
    {
        using var handler = new FakeHandler(Success("first"));
        using var http = new HttpClient(handler);
        var client = new ResponsesClient(http, Options());
        var session = new Conversation("stateful", 3);
        await client.SendAsync(session, "First", default);
        session.Cookies.GetCookies(Endpoint)[0].Expires = DateTime.UtcNow.AddSeconds(-1);
        var result = await client.SendAsync(session, "Second", default);
        Assert.Equal(TurnOutcome.MissingAffinity, result.Outcome);
        Assert.False(result.Sent);
        Assert.Single(handler.Calls);
        Assert.Equal("first", session.PreviousResponseId);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    [InlineData("in_progress")]
    public async Task Noncompleted_model_output_is_recorded_but_never_chained(string status)
    {
        using var handler = new FakeHandler(Success("partial", status: status));
        using var http = new HttpClient(handler);
        var session = new Conversation("stateful", 3);
        var result = await new ResponsesClient(http, Options()).SendAsync(session, "Question", default);
        Assert.Equal(TurnOutcome.IncompleteResponse, result.Outcome);
        Assert.Equal("partial", result.ResponseId);
        Assert.NotEmpty(result.OutputText!);
        Assert.Null(session.PreviousResponseId);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("{\"id\":\"no-text\",\"status\":\"completed\",\"output\":[]}")]
    [InlineData("{\"id\":\"unsafe\\nidentity\",\"status\":\"completed\",\"output\":[]}")]
    [InlineData("{\"id\":\"error\",\"status\":\"completed\",\"error\":{\"code\":\"Unexpected\"},\"output\":[]}")]
    public async Task Invalid_response_contracts_are_failures(string body)
    {
        var response = Success("unused");
        response.Content = new StringContent(body);
        using var handler = new FakeHandler(response);
        using var http = new HttpClient(handler);
        var session = new Conversation("stateful", 3);
        var result = await new ResponsesClient(http, Options()).SendAsync(session, "Question", default);
        Assert.Equal(TurnOutcome.InvalidResponse, result.Outcome);
        Assert.Null(session.PreviousResponseId);
    }

    [Fact]
    public async Task Refusal_and_reasoning_only_output_are_not_successful_architecture_answers()
    {
        var response = Success("refused");
        response.Content = new StringContent("""
            {"id":"refused","status":"completed","output":[
              {"type":"reasoning","summary":[{"text":"private reasoning must not be printed"}]},
              {"type":"message","role":"assistant","content":[{"type":"refusal","refusal":"Declined"}]}]}
            """);
        using var handler = new FakeHandler(response);
        using var http = new HttpClient(handler);
        var result = await new ResponsesClient(http, Options()).SendAsync(new Conversation("stateful", 3), "Question", default);
        Assert.Equal(TurnOutcome.Refused, result.Outcome);
        Assert.DoesNotContain("private reasoning", result.OutputText!);
    }

    [Theory]
    [InlineData("<html>secret synthetic-private-pin-a</html>", null)]
    [InlineData("{\"error\":{\"code\":\"CircuitBreakerIsOpen\",\"message\":\"secret\"}}", "CircuitBreakerIsOpen")]
    public async Task HTTP_errors_keep_status_without_printing_bodies_or_advancing_history(string body, string? code)
    {
        using var handler = new FakeHandler(Success("first"), new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(body)
        });
        using var http = new HttpClient(handler);
        var client = new ResponsesClient(http, Options());
        var session = new Conversation("stateful", 3);
        await client.SendAsync(session, "First", default);
        var result = await client.SendAsync(session, "Second", default);
        Assert.Equal(TurnOutcome.HttpError, result.Outcome);
        Assert.Equal(503, result.HttpStatus);
        Assert.Equal(code, result.ErrorCode);
        Assert.Null(result.OutputText);
        Assert.Null(result.BackendId);
        Assert.Equal("first", session.PreviousResponseId);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Known_keys_and_raw_cookies_are_redacted_even_if_model_text_echoes_them()
    {
        using var handler = new FakeHandler(Success("first", text: $"Do not expose {Key} or {PinA}."));
        using var http = new HttpClient(handler);
        var result = await new ResponsesClient(http, Options()).SendAsync(new Conversation("stateful", 3), "Question", default);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(Key, json);
        Assert.DoesNotContain(PinA, json);
        Assert.Contains("[REDACTED]", result.OutputText);
        Assert.Equal(12, result.AffinityFingerprint!.Length);
    }

    [Theory]
    [InlineData(false, 0, "Success")]
    [InlineData(false, 1, "ResponseTooLarge")]
    [InlineData(true, 0, "Success")]
    [InlineData(true, 1, "ResponseTooLarge")]
    public async Task Body_limit_applies_at_exact_boundary_with_or_without_content_length(bool unknownLength, int extra, string outcome)
    {
        var response = Success("bounded", cookie: null, text: "");
        var original = await response.Content.ReadAsStringAsync();
        var padding = ResponsesClient.MaxResponseBytes + extra - Encoding.UTF8.GetByteCount(original);
        var body = original.Replace("\"text\":\"\"", "\"text\":\"" + new string('x', padding) + "\"", StringComparison.Ordinal);
        Assert.Equal(ResponsesClient.MaxResponseBytes + extra, Encoding.UTF8.GetByteCount(body));
        response.Content = unknownLength ? new UnknownLengthContent(body) : new StringContent(body);
        using var handler = new FakeHandler(response);
        using var http = new HttpClient(handler);
        var result = await new ResponsesClient(http, Options()).SendAsync(new Conversation("stateless", 1), "Question", default);
        Assert.Equal(outcome, result.Outcome.ToString());
    }

    [Fact]
    public async Task Timeout_is_not_retried_and_does_not_leak_transport_details()
    {
        using var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        using var http = new HttpClient(handler);
        var session = new Conversation("stateful", 3);
        var result = await new ResponsesClient(http, Options(TimeSpan.FromMilliseconds(20))).SendAsync(session, "Question", default);
        Assert.Equal(TurnOutcome.RequestTimeout, result.Outcome);
        Assert.Single(handler.Calls);
        Assert.Null(session.PreviousResponseId);
    }

    [Theory]
    [InlineData(false, "RequestTimeout")]
    [InlineData(true, "TransportError")]
    public async Task Body_read_is_deadline_bounded_and_transport_failures_remain_reportable(bool failImmediately, string outcome)
    {
        var response = Success("unread");
        response.Content = new StreamContent(new UnreadableStream(failImmediately));
        using var handler = new FakeHandler(response);
        using var http = new HttpClient(handler);
        var result = await new ResponsesClient(http, Options(TimeSpan.FromMilliseconds(20)))
            .SendAsync(new Conversation("stateful", 3), "Question", default);
        Assert.Equal(outcome, result.Outcome.ToString());
        Assert.Single(handler.Calls);
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task A_repeated_response_ID_cannot_advance_history()
    {
        using var handler = new FakeHandler(Success("same"), Success("same"));
        using var http = new HttpClient(handler);
        var client = new ResponsesClient(http, Options());
        var session = new Conversation("stateful", 3);
        await client.SendAsync(session, "First", default);
        var result = await client.SendAsync(session, "Second", default);
        Assert.Equal(TurnOutcome.InvalidResponse, result.Outcome);
        Assert.Equal("same", session.PreviousResponseId);
    }

    [Fact]
    public async Task Transport_failure_is_a_failed_turn_not_an_unhandled_exception_or_retry()
    {
        using var handler = new FakeHandler((_, _) => throw new HttpRequestException(Key));
        using var http = new HttpClient(handler);
        var result = await new ResponsesClient(http, Options()).SendAsync(new Conversation("stateful", 3), "Question", default);
        Assert.Equal(TurnOutcome.TransportError, result.Outcome);
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(result));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Runner_reports_each_scenario_and_counts_actual_uneven_distribution()
    {
        using var handler = HealthyHandler();
        using var http = new HttpClient(handler);
        using var writer = new StringWriter();
        var options = Options(statelessRequests: 3);
        var report = await new LabRunner(new ResponsesClient(http, options), options, writer).RunAsync("both", default);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(5, report.Sessions.Count);
        Assert.Equal(9, handler.Calls.Count);
        Assert.Equal(3, report.Distribution("stateful")["backend-a"]);
        Assert.Equal(3, report.Distribution("stateful")["backend-b"]);
        Assert.Equal(2, report.Distribution("stateless")["backend-a"]);
        Assert.Equal(1, report.Distribution("stateless")["backend-b"]);
        Assert.All(report.Sessions, session => Assert.True(session.Complete));
        report.Write(writer);
        Assert.Contains("SESSION SUMMARY", writer.ToString());
        Assert.Contains("stateless: successful requests", writer.ToString());
        Assert.Contains("expected-failure scenarios=0", writer.ToString());
        Assert.DoesNotContain(Key, writer.ToString());
        Assert.DoesNotContain(PinA, writer.ToString());
    }

    [Fact]
    public async Task Single_backend_observation_is_a_failed_assertion_not_fabricated_balance()
    {
        using var handler = new FakeHandler((call, _) => Task.FromResult(Success($"response-{call.Number}")));
        using var http = new HttpClient(handler);
        using var writer = new StringWriter();
        var options = Options();
        var report = await new LabRunner(new ResponsesClient(http, options), options, writer).RunAsync("both", default);
        Assert.All(report.Sessions, session => Assert.True(session.Complete));
        Assert.Equal(1, report.ExitCode);
        Assert.Single(report.Distribution("stateful"));
        Assert.Single(report.Distribution("stateless"));
        report.Write(writer);
        Assert.Contains("FAILED-missing-member", writer.ToString());
    }

    [Fact]
    public async Task Failed_chain_stops_only_its_session_and_summary_counts_incomplete_work()
    {
        using var handler = HealthyHandler(switchFirstChain: true);
        using var http = new HttpClient(handler);
        using var writer = new StringWriter();
        var options = Options();
        var report = await new LabRunner(new ResponsesClient(http, options), options, writer).RunAsync("both", default);
        var failed = Assert.Single(report.Sessions, session => session.Failed);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(1, failed.Succeeded);
        Assert.Equal(2, failed.Turns.Count);
        Assert.Equal(TurnOutcome.AffinityViolation, failed.Turns[1].Outcome);
        Assert.Equal(3, report.Sessions.Count(session => session.Complete));
        report.Write(writer);
        Assert.Contains("incomplete sessions=1", writer.ToString());
        Assert.Contains("failed turns=1", writer.ToString());
    }

    [Fact]
    public async Task Cancellation_between_requests_keeps_unattempted_sessions_in_the_final_report()
    {
        using var cancelled = new CancellationTokenSource();
        using var handler = HealthyHandler();
        using var http = new HttpClient(handler);
        using var writer = new CancellingWriter(cancelled);
        var options = Options();
        var report = await new LabRunner(new ResponsesClient(http, options), options, writer).RunAsync("both", cancelled.Token);
        Assert.True(report.Cancelled);
        Assert.Equal(1, report.ExitCode);
        Assert.Single(handler.Calls);
        Assert.Equal(4, report.Sessions.Count);
        Assert.Equal(3, report.Sessions.Count(session => session.Turns.Count == 0));
        Assert.Single(report.Sessions.SelectMany(session => session.Turns));
    }

    [Fact]
    public async Task Whole_run_deadline_stops_an_inflight_request_before_its_longer_request_timeout()
    {
        using var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        using var http = new HttpClient(handler);
        using var writer = new StringWriter();
        var options = Options(runTimeout: TimeSpan.FromMilliseconds(20));
        var report = await new LabRunner(new ResponsesClient(http, options), options, writer).RunAsync("both", default);
        Assert.True(report.Cancelled);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(TurnOutcome.RunCancelled, Assert.Single(report.Sessions.SelectMany(session => session.Turns)).Outcome);
        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData("LAB_STATEFUL_SESSION_COUNT", "1")]
    [InlineData("LAB_TURNS_PER_SESSION", "4")]
    [InlineData("LAB_TURNS_PER_SESSION", "1")]
    [InlineData("LAB_STATELESS_REQUEST_COUNT", "25")]
    [InlineData("LAB_MAX_OUTPUT_TOKENS", "4097")]
    [InlineData("LAB_REQUEST_TIMEOUT_SECONDS", "0")]
    [InlineData("LAB_RUN_TIMEOUT_SECONDS", "1801")]
    [InlineData("LAB_REQUEST_INTERVAL_SECONDS", "")]
    [InlineData("LAB_REQUEST_INTERVAL_SECONDS", "9")]
    [InlineData("LAB_MAX_RETRIES", "1")]
    [InlineData("LAB_ENABLE_FAULTS", "true")]
    [InlineData("LAB_REASONING_EFFORT", "unbounded")]
    [InlineData("LAB_AFFINITY_COOKIE_NAME", "foreign")]
    [InlineData("LAB_SESSION_AFFINITY_ENABLED", "False")]
    [InlineData("LAB_SESSION_AFFINITY_ENABLED", "typo")]
    [InlineData("LAB_RESPONSES_ENDPOINT", "http://offline.example.invalid/openai/v1/responses")]
    [InlineData("LAB_RESPONSES_ENDPOINT", "https://offline.example.invalid/openai/v1/responses?key=secret")]
    public void Invalid_configuration_is_not_coerced_or_echoed(string name, string value)
    {
        var config = new Dictionary<string, string>
        {
            ["LAB_RESPONSES_ENDPOINT"] = Endpoint.ToString(),
            ["LAB_MODEL_DEPLOYMENT"] = "responses-chat",
            ["LAB_APIM_SUBSCRIPTION_KEY"] = Key,
            [name] = value
        };
        var exception = Assert.Throws<ConfigurationException>(() => LabOptions.Load(key => config.GetValueOrDefault(key)));
        Assert.Contains(name, exception.Message);
        Assert.DoesNotContain(Key, exception.Message);
        Assert.DoesNotContain("key=secret", exception.Message);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(4096)]
    public async Task Accepted_output_token_boundaries_are_sent_without_coercion(int tokens)
    {
        var config = new Dictionary<string, string>
        {
            ["LAB_RESPONSES_ENDPOINT"] = Endpoint.ToString(),
            ["LAB_MODEL_DEPLOYMENT"] = "responses-chat",
            ["LAB_APIM_SUBSCRIPTION_KEY"] = Key,
            ["LAB_MAX_OUTPUT_TOKENS"] = tokens.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var options = LabOptions.Load(name => config.GetValueOrDefault(name));
        using var handler = new FakeHandler(Success("bounded"));
        using var http = new HttpClient(handler);
        var result = await new ResponsesClient(http, options).SendAsync(new Conversation("stateless", 1), "Question", default);
        Assert.Equal(TurnOutcome.Success, result.Outcome);
        Assert.Equal(tokens, Assert.Single(handler.Calls).Body["max_output_tokens"]!.GetValue<int>());
    }

    private static FakeHandler HealthyHandler(bool switchFirstChain = false)
    {
        var members = new Dictionary<Guid, string>();
        return new FakeHandler((call, _) =>
        {
            if (!members.TryGetValue(call.SessionId, out var backend))
            {
                backend = members.Count % 2 == 0 ? "backend-a" : "backend-b";
                members.Add(call.SessionId, backend);
            }
            if (switchFirstChain && call.Number == 3)
                backend = "backend-b";
            return Task.FromResult(Success($"response-{call.Number}", backend, backend == "backend-a" ? PinA : PinB));
        });
    }

    private sealed record Call(int Number, Guid SessionId, Guid ClientRequestId, JsonObject Body, string? Cookie);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<Call, CancellationToken, Task<HttpResponseMessage>> reply;
        internal List<Call> Calls { get; } = [];

        internal FakeHandler(params HttpResponseMessage[] replies)
        {
            var queue = new Queue<HttpResponseMessage>(replies);
            reply = (_, _) => Task.FromResult(queue.Dequeue());
        }

        internal FakeHandler(Func<Call, CancellationToken, Task<HttpResponseMessage>> reply) => this.reply = reply;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Endpoint, request.RequestUri);
            Assert.Equal(Key, Assert.Single(request.Headers.GetValues("Ocp-Apim-Subscription-Key")));
            var body = Assert.IsType<JsonObject>(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
            var call = new Call(Calls.Count + 1,
                Guid.Parse(Assert.Single(request.Headers.GetValues("X-Lab-Session-Id"))),
                Guid.Parse(Assert.Single(request.Headers.GetValues("x-ms-client-request-id"))),
                body, request.Headers.TryGetValues("Cookie", out var cookies) ? Assert.Single(cookies) : null);
            Assert.Equal(body["store"]!.GetValue<bool>() ? "stateful" : "stateless", Assert.Single(request.Headers.GetValues("X-Lab-Scenario")));
            Calls.Add(call);
            return await reply(call, cancellationToken);
        }
    }

    private sealed class UnknownLengthContent(string body) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();
    }

    private sealed class CancellingWriter(CancellationTokenSource source) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            source.Cancel();
        }
    }

    private sealed class UnreadableStream(bool failImmediately) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (failImmediately)
                throw new IOException(Key);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
