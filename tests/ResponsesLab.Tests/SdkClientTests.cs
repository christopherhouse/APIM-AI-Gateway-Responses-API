using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResponsesLab;

namespace ResponsesLab.Tests;

public sealed class SdkClientTests
{
    private const string Key = "synthetic-sdk-test-key";
    private const string CookieName = "APIM-Backend-Affinity";

    [Theory]
    [InlineData("foundry-sdk")]
    [InlineData("agent-framework")]
    public async Task Genuine_clients_interleave_service_chains_and_automatically_isolate_cookies(string mode)
    {
        var members = new Dictionary<string, string>();
        await using var server = new LoopbackServer(call =>
        {
            var session = call.Headers["X-Lab-Session-Id"];
            if (!members.TryGetValue(session, out var member))
                members[session] = member = members.Count % 2 == 0 ? "backend-a" : "backend-b";
            return Reply.Success(call.Number, member, call.Store ? $"pin-{member}" : null, call.Store);
        });
        var options = Options(server.Endpoint);
        using var unusedHttp = ResponsesClient.CreateHttpClient();
        var selection = ClientSelection.Parse(mode, "session-scoped");
        using var sdk = new SdkResponseSender(options, selection);
        var client = new ResponsesClient(unusedHttp, options, sdk, selection);
        var a = new Conversation("stateful", 2);
        var b = new Conversation("stateful", 2);
        var results = new[]
        {
            await client.SendAsync(a, "First A", default),
            await client.SendAsync(b, "First B", default),
            await client.SendAsync(a, "Second A", default),
            await client.SendAsync(b, "Second B", default),
            await client.SendAsync(new Conversation("stateless", 1), "Independent A", default),
            await client.SendAsync(new Conversation("stateless", 1), "Independent B", default)
        };
        Assert.All(results, result =>
        {
            Assert.Equal(TurnOutcome.Success, result.Outcome);
            Assert.Equal(1, result.WireAttempts);
            Assert.Equal(mode, result.ClientMode);
            Assert.Equal("session-scoped", result.CookieProfile);
        });
        Assert.Equal(6, server.Calls.Count);
        Assert.Null(server.Calls[0].Cookie);
        Assert.Null(server.Calls[1].Cookie);
        Assert.Equal($"{CookieName}=pin-backend-a", server.Calls[2].Cookie);
        Assert.Equal($"{CookieName}=pin-backend-b", server.Calls[3].Cookie);
        Assert.Equal("resp_1", server.Calls[2].PreviousResponseId);
        Assert.Equal("resp_2", server.Calls[3].PreviousResponseId);
        foreach (var call in server.Calls)
        {
            Assert.Equal("POST /openai/v1/responses HTTP/1.1", call.RequestLine);
            Assert.Equal(Key, call.Headers["Ocp-Apim-Subscription-Key"]);
            Assert.False(call.Headers.ContainsKey("Authorization"));
            Assert.False(call.Body.GetProperty("stream").GetBoolean());
            Assert.Equal(2048, call.Body.GetProperty("max_output_tokens").GetInt32());
            Assert.Equal("low", call.Body.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal(ConversationScript.Instructions, call.Body.GetProperty("instructions").GetString());
            Assert.False(call.Body.TryGetProperty("conversation", out _));
            Assert.Single(call.Body.GetProperty("input").EnumerateArray());
            var result = results[call.Number - 1];
            Assert.Equal(result.ClientRequestId.ToString("D"), call.Headers["x-ms-client-request-id"]);
            Assert.Equal(result.SessionId.ToString("D"), call.Headers["X-Lab-Session-Id"]);
        }
        Assert.All(server.Calls.Skip(4), call =>
        {
            Assert.False(call.Store);
            Assert.Null(call.PreviousResponseId);
            Assert.Null(call.Cookie);
        });
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(results));
        Assert.DoesNotContain("pin-backend", JsonSerializer.Serialize(results));
    }

    [Theory]
    [InlineData("foundry-sdk")]
    [InlineData("agent-framework")]
    public async Task Unchanged_default_transport_shares_cookie_across_new_conversations(string mode)
    {
        await using var server = new LoopbackServer(call => call.Number <= 2
            ? Reply.Success(call.Number, "backend-a", "stock-shared-pin")
            : Reply.Error(400, "UnexpectedAffinityCookie") with
            {
                Cookie = call.Number == 4 ? $"{CookieName}=; Max-Age=0; Path=/" : null
            });
        var options = Options(server.Endpoint);
        var selection = ClientSelection.Parse(mode, "sdk-default");
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        var client = new ResponsesClient(http, options, sdk, selection);
        var a = new Conversation("stateful", 2);
        var first = await client.SendAsync(a, "First", default);
        var continuation = await client.SendAsync(a, "Continuation", default);
        var second = await client.SendAsync(new Conversation("stateful", 2), "Unrelated", default);
        var stateless = await client.SendAsync(new Conversation("stateless", 1), "Independent", default);
        Assert.Equal(TurnOutcome.Success, first.Outcome);
        Assert.Equal(TurnOutcome.Success, continuation.Outcome);
        Assert.Equal(TurnOutcome.HttpError, second.Outcome);
        Assert.Equal("UnexpectedAffinityCookie", second.ErrorCode);
        Assert.Equal(TurnOutcome.HttpError, stateless.Outcome);
        Assert.Equal(4, server.Calls.Count);
        Assert.Null(server.Calls[0].Cookie);
        Assert.Equal($"{CookieName}=stock-shared-pin", server.Calls[1].Cookie);
        Assert.Equal("resp_1", server.Calls[1].PreviousResponseId);
        Assert.Equal($"{CookieName}=stock-shared-pin", server.Calls[2].Cookie);
        Assert.Null(server.Calls[2].PreviousResponseId);
        Assert.Equal($"{CookieName}=stock-shared-pin", server.Calls[3].Cookie);
        Assert.False(server.Calls[3].Store);
        Assert.NotEqual(server.Calls[0].Headers["X-Lab-Session-Id"], server.Calls[2].Headers["X-Lab-Session-Id"]);
        Assert.Equal(1, second.WireAttempts);
    }

    [Theory]
    [InlineData("foundry-sdk", "session-scoped")]
    [InlineData("agent-framework", "session-scoped")]
    [InlineData("foundry-sdk", "sdk-default")]
    [InlineData("agent-framework", "sdk-default")]
    public async Task Affinity_off_keeps_real_cross_resource_error_and_never_replays_or_retries(string mode, string profile)
    {
        await using var server = new LoopbackServer(call => call.Number == 1
            ? Reply.Success(1, "backend-a", null)
            : new Reply(400, """
                {"error":{"code":null,"param":null,"type":"invalid_request_error","message":"The requested item was created under a different Azure OpenAI resource. Use the same resource that created the item to access it."}}
                """, "backend-b"));
        var options = Options(server.Endpoint, affinity: false);
        var selection = ClientSelection.Parse(mode, profile);
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        var client = new ResponsesClient(http, options, sdk, selection);
        var session = new Conversation("stateful", 3);
        var first = await client.SendAsync(session, "First", default);
        Assert.Equal(TurnOutcome.Success, first.Outcome);
        var second = await client.SendAsync(session, "Second", default);
        Assert.Equal(TurnOutcome.HttpError, second.Outcome);
        Assert.Null(second.ErrorCode);
        Assert.True(second.CrossResourceStateError);
        Assert.Equal("backend-b", second.BackendId);
        Assert.Equal("resp_1", second.PreviousResponseId);
        Assert.Equal("resp_1", session.PreviousResponseId);
        Assert.Equal(0, new RunReport([session], false, true).ExitCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(session, "Do not replay", default));
        Assert.Equal(2, server.Calls.Count);
        Assert.Equal("resp_1", server.Calls[1].PreviousResponseId);
        Assert.All(server.Calls, call => Assert.Null(call.Cookie));
    }

    [Theory]
    [InlineData("foundry-sdk", 429)]
    [InlineData("agent-framework", 429)]
    [InlineData("foundry-sdk", 503)]
    [InlineData("agent-framework", 503)]
    public async Task Retryable_service_errors_are_one_attempt_and_preserve_actual_status(string mode, int status)
    {
        await using var server = new LoopbackServer(_ => Reply.Error(status, "bounded_failure"));
        var options = Options(server.Endpoint);
        var selection = ClientSelection.Parse(mode, null);
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        var session = new Conversation("stateless", 1);
        var result = await new ResponsesClient(http, options, sdk, selection).SendAsync(session, "Question", default);
        Assert.Equal(TurnOutcome.HttpError, result.Outcome);
        Assert.Equal(status, result.HttpStatus);
        Assert.Equal("bounded_failure", result.ErrorCode);
        Assert.Equal(1, result.WireAttempts);
        Assert.Single(server.Calls);
        Assert.Null(session.PreviousResponseId);
    }

    [Theory]
    [InlineData("foundry-sdk", false)]
    [InlineData("agent-framework", false)]
    [InlineData("foundry-sdk", true)]
    [InlineData("agent-framework", true)]
    public async Task Per_request_or_run_cancellation_stops_real_transport_without_retry(string mode, bool callerCancelled)
    {
        await using var server = new LoopbackServer(_ => Reply.Success(1, "backend-a", null),
            delay: TimeSpan.FromSeconds(10));
        var options = Options(server.Endpoint,
            requestTimeout: callerCancelled ? null : TimeSpan.FromMilliseconds(200));
        var selection = ClientSelection.Parse(mode, null);
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        using var cancelled = new CancellationTokenSource();
        if (callerCancelled)
            cancelled.CancelAfter(TimeSpan.FromMilliseconds(200));
        var result = await new ResponsesClient(http, options, sdk, selection)
            .SendAsync(new Conversation("stateless", 1), "Question", cancelled.Token);
        Assert.Equal(callerCancelled ? TurnOutcome.RunCancelled : TurnOutcome.RequestTimeout, result.Outcome);
        Assert.Equal(1, result.WireAttempts);
        Assert.Single(server.Calls);
    }

    [Theory]
    [InlineData("foundry-sdk", false)]
    [InlineData("agent-framework", false)]
    [InlineData("foundry-sdk", true)]
    [InlineData("agent-framework", true)]
    public async Task Cancelled_or_exhausted_run_cannot_start_a_model_attempt(string mode, bool exhausted)
    {
        await using var server = new LoopbackServer(_ => throw new InvalidOperationException("No request expected"));
        var options = Options(server.Endpoint);
        var selection = ClientSelection.Parse(mode, null);
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        var client = new ResponsesClient(http, options, sdk, selection);
        client.SetBudget(0);
        var result = await client.SendAsync(new Conversation("stateless", 1), "Question",
            exhausted ? default : new CancellationToken(true));
        Assert.Equal(exhausted ? TurnOutcome.SdkContractViolation : TurnOutcome.RunCancelled, result.Outcome);
        Assert.False(result.Sent);
        Assert.Equal(0, result.WireAttempts);
        Assert.Empty(server.Calls);
    }

    [Theory]
    [InlineData("foundry-sdk", false)]
    [InlineData("agent-framework", false)]
    [InlineData("foundry-sdk", true)]
    [InlineData("agent-framework", true)]
    public async Task Bounded_response_is_enforced_before_sdk_deserialization(string mode, bool chunked)
    {
        await using var server = new LoopbackServer(_ =>
            new Reply(200, new string('x', ResponsesClient.MaxResponseBytes + 1), Chunked: chunked));
        var options = Options(server.Endpoint);
        var selection = ClientSelection.Parse(mode, null);
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        var result = await new ResponsesClient(http, options, sdk, selection)
            .SendAsync(new Conversation("stateless", 1), "Question", default);
        Assert.Equal(TurnOutcome.ResponseTooLarge, result.Outcome);
        Assert.Equal(1, result.WireAttempts);
        Assert.Equal(200, result.HttpStatus);
        Assert.NotNull(result.GatewayRequestId);
        Assert.Single(server.Calls);
    }

    [Theory]
    [InlineData("foundry-sdk", "json", "InvalidResponse")]
    [InlineData("agent-framework", "json", "InvalidResponse")]
    [InlineData("foundry-sdk", "sdk-schema", "InvalidResponse")]
    [InlineData("agent-framework", "sdk-schema", "InvalidResponse")]
    [InlineData("foundry-sdk", "incomplete", "IncompleteResponse")]
    [InlineData("agent-framework", "incomplete", "IncompleteResponse")]
    [InlineData("foundry-sdk", "refusal", "Refused")]
    [InlineData("agent-framework", "refusal", "Refused")]
    [InlineData("foundry-sdk", "tool", "InvalidResponse")]
    [InlineData("agent-framework", "tool", "InvalidResponse")]
    public async Task Failed_typed_results_cannot_be_replaced_with_a_success_shaped_raw_fallback(string mode, string shape, string outcome)
    {
        var reply = Reply.Success(1, "backend-a", null);
        var body = JsonNode.Parse(reply.Body)!.AsObject();
        if (shape == "sdk-schema")
            body["created_at"] = "not-a-timestamp";
        else if (shape == "incomplete")
            body["status"] = "incomplete";
        else if (shape == "refusal")
            body["output"]![0]!["content"] = JsonNode.Parse("""[{"type":"refusal","refusal":"Synthetic refusal."}]""");
        else if (shape == "tool")
            body["output"] = JsonNode.Parse("""[{"type":"function_call","name":"not_registered","arguments":"{}","call_id":"call_synthetic","id":"fc_synthetic","status":"completed"}]""");
        reply = reply with { Body = shape == "json" ? "{" : body.ToJsonString() };
        await using var server = new LoopbackServer(_ => reply);
        var options = Options(server.Endpoint);
        var selection = ClientSelection.Parse(mode, null);
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        var session = new Conversation("stateless", 1);
        var result = await new ResponsesClient(http, options, sdk, selection).SendAsync(session, "Question", default);
        Assert.Equal(outcome, result.Outcome.ToString());
        Assert.Null(session.PreviousResponseId);
        Assert.Equal(1, result.WireAttempts);
        Assert.Single(server.Calls);
        Assert.Equal(200, result.HttpStatus);
    }

    [Theory]
    [InlineData("foundry-sdk")]
    [InlineData("agent-framework")]
    public async Task Exact_body_limit_is_accepted_and_sdk_wide_cookie_values_are_redacted(string mode)
    {
        var reply = Reply.Success(1, "backend-a", "known-private-pin");
        await using var server = new LoopbackServer(call =>
        {
            if (call.Number == 1)
                return reply;
            var body = JsonNode.Parse(Reply.Success(2, "backend-b", null).Body)!.AsObject();
            body["output"]![0]!["content"]![0]!["text"] = "known-private-pin " + Key;
            body["metadata"] = new JsonObject { ["padding"] = "" };
            var padding = ResponsesClient.MaxResponseBytes - Encoding.UTF8.GetByteCount(body.ToJsonString());
            body["metadata"]!["padding"] = new string('x', padding);
            var json = body.ToJsonString();
            Assert.Equal(ResponsesClient.MaxResponseBytes, Encoding.UTF8.GetByteCount(json));
            return new Reply(200, json, "backend-b");
        });
        var options = Options(server.Endpoint);
        var selection = ClientSelection.Parse(mode, null);
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        var client = new ResponsesClient(http, options, sdk, selection);
        Assert.Equal(TurnOutcome.Success,
            (await client.SendAsync(new Conversation("stateful", 2), "First", default)).Outcome);
        var result = await client.SendAsync(new Conversation("stateless", 1), "Second", default);
        Assert.Equal(TurnOutcome.Success, result.Outcome);
        Assert.Equal("[REDACTED] [REDACTED]", result.OutputText);
        Assert.Equal(2, server.Calls.Count);
        Assert.Null(server.Calls[1].Cookie);
    }

    [Theory]
    [InlineData("foundry-sdk")]
    [InlineData("agent-framework")]
    public async Task Run_wide_budget_is_consumed_by_actual_attempts_across_sessions(string mode)
    {
        await using var server = new LoopbackServer(call => Reply.Success(call.Number, "backend-a", null));
        var options = Options(server.Endpoint);
        var selection = ClientSelection.Parse(mode, null);
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        var client = new ResponsesClient(http, options, sdk, selection);
        client.SetBudget(1);
        Assert.Equal(TurnOutcome.Success,
            (await client.SendAsync(new Conversation("stateless", 1), "First", default)).Outcome);
        var result = await client.SendAsync(new Conversation("stateless", 1), "Second", default);
        Assert.Equal(TurnOutcome.SdkContractViolation, result.Outcome);
        Assert.False(result.Sent);
        Assert.Equal(0, result.WireAttempts);
        Assert.Single(server.Calls);
    }

    [Theory]
    [InlineData("foundry-sdk", "missing", "MissingAffinity")]
    [InlineData("agent-framework", "missing", "MissingAffinity")]
    [InlineData("foundry-sdk", "empty", "MissingAffinity")]
    [InlineData("agent-framework", "empty", "MissingAffinity")]
    [InlineData("foundry-sdk", "duplicate", "InvalidAffinity")]
    [InlineData("agent-framework", "duplicate", "InvalidAffinity")]
    public async Task Unusable_native_cookies_are_failures_not_silently_accepted(string mode, string kind, string outcome)
    {
        var cookie = kind switch
        {
            "missing" => null,
            "empty" => $"{CookieName}=\"\"; Path=/",
            _ => $"{CookieName}=one; Path=/\r\nSet-Cookie: {CookieName}=two; Path=/"
        };
        await using var server = new LoopbackServer(_ => Reply.Success(1, "backend-a", null) with { Cookie = cookie });
        var options = Options(server.Endpoint);
        var selection = ClientSelection.Parse(mode, null);
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        var session = new Conversation("stateful", 2);
        var result = await new ResponsesClient(http, options, sdk, selection).SendAsync(session, "First", default);
        Assert.Equal(outcome, result.Outcome.ToString());
        Assert.Null(session.PreviousResponseId);
        Assert.Single(server.Calls);
    }

    [Theory]
    [InlineData("foundry-sdk", "sdk-default")]
    [InlineData("agent-framework", "sdk-default")]
    [InlineData("foundry-sdk", "session-scoped")]
    [InlineData("agent-framework", "session-scoped")]
    public async Task Redirects_cannot_bypass_the_exact_gateway_route_or_duplicate_attempts(string mode, string profile)
    {
        await using var server = new LoopbackServer(_ => new Reply(307, "{}", Location: "/must-not-follow"));
        var options = Options(server.Endpoint);
        var selection = ClientSelection.Parse(mode, profile);
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        var result = await new ResponsesClient(http, options, sdk, selection)
            .SendAsync(new Conversation("stateless", 1), "Question", default);
        Assert.Equal(TurnOutcome.HttpError, result.Outcome);
        Assert.Equal(307, result.HttpStatus);
        Assert.Equal(1, result.WireAttempts);
        Assert.Single(server.Calls);
    }

    [Theory]
    [InlineData("foundry-sdk")]
    [InlineData("agent-framework")]
    public async Task Missing_cookie_stops_only_configured_session_before_continuation(string mode)
    {
        await using var server = new LoopbackServer(_ => Reply.Success(1, "backend-a", "expiring-pin"));
        var options = Options(server.Endpoint);
        var selection = ClientSelection.Parse(mode, null);
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        var client = new ResponsesClient(http, options, sdk, selection);
        var session = new Conversation("stateful", 2);
        Assert.Equal(TurnOutcome.Success, (await client.SendAsync(session, "First", default)).Outcome);
        foreach (Cookie cookie in session.Cookies.GetAllCookies())
            cookie.Expired = true;
        var result = await client.SendAsync(session, "Second", default);
        Assert.Equal(TurnOutcome.MissingAffinity, result.Outcome);
        Assert.False(result.Sent);
        Assert.Single(server.Calls);
    }

    [Theory]
    [InlineData("foundry-sdk", "backend-b", "AffinityViolation")]
    [InlineData("agent-framework", "backend-b", "AffinityViolation")]
    [InlineData("foundry-sdk", null, "MissingEvidence")]
    [InlineData("agent-framework", null, "MissingEvidence")]
    public async Task Common_gateway_evidence_checks_apply_to_real_sdk_results(string mode, string? member, string outcome)
    {
        await using var server = new LoopbackServer(call => Reply.Success(call.Number,
            call.Number == 1 ? "backend-a" : member, "constant-pin"));
        var options = Options(server.Endpoint);
        var selection = ClientSelection.Parse(mode, null);
        using var http = ResponsesClient.CreateHttpClient();
        using var sdk = new SdkResponseSender(options, selection);
        var client = new ResponsesClient(http, options, sdk, selection);
        var session = new Conversation("stateful", 2);
        Assert.Equal(TurnOutcome.Success, (await client.SendAsync(session, "First", default)).Outcome);
        var result = await client.SendAsync(session, "Second", default);
        Assert.Equal(outcome, result.Outcome.ToString());
        Assert.Equal("resp_1", session.PreviousResponseId);
        Assert.Equal(2, server.Calls.Count);
    }

    [Theory]
    [InlineData("--allow-requests --client http --cookie-profile session-scoped")]
    [InlineData("--allow-requests --client foundry-sdk --cookie-profile manual")]
    [InlineData("--allow-requests --client unknown")]
    [InlineData("--allow-requests --client")]
    [InlineData("--allow-requests --client foundry-sdk --client agent-framework")]
    [InlineData("--client foundry-sdk")]
    [InlineData("--allow-requests --scenario failure --client agent-framework --allow-faults --confirm-resource-group exact")]
    [InlineData("--allow-requests --scenario stateful --allow-faults")]
    public void Unsupported_invocations_fail_before_configuration_or_credentials(string command) =>
        Assert.Throws<ConfigurationException>(() => Invocation.Parse(command.Split(' ')));

    [Fact]
    public void Existing_invocations_and_explicit_sdk_profiles_remain_unambiguous()
    {
        Assert.Equal(new ClientSelection(), Invocation.Parse(["--allow-requests"]).Client);
        var sdk = Invocation.Parse(["--client", "agent-framework", "--allow-requests", "--scenario", "affinity-off",
            "--cookie-profile", "sdk-default"]);
        Assert.Equal("affinity-off", sdk.Scenario);
        Assert.Equal(new ClientSelection("agent-framework", "sdk-default"), sdk.Client);
        Assert.Equal("session-scoped", ClientSelection.Parse("foundry-sdk", null).CookieProfile);
        Assert.Equal("exact", Invocation.Parse(["--allow-requests", "--scenario", "failure", "--allow-faults",
            "--confirm-resource-group", "exact"]).FaultConfirmation);
    }

    private static LabOptions Options(Uri endpoint, bool affinity = true, TimeSpan? requestTimeout = null) => new()
    {
        Endpoint = endpoint,
        Model = "responses-chat",
        SubscriptionKey = Key,
        SessionAffinityEnabled = affinity,
        StatefulSessions = 2,
        TurnsPerSession = 2,
        StatelessRequests = 2,
        RequestInterval = TimeSpan.Zero,
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5)
    };

    private sealed record Call(int Number, string RequestLine, Dictionary<string, string> Headers, JsonElement Body)
    {
        internal string? Cookie => Headers.GetValueOrDefault("Cookie");
        internal bool Store => Body.GetProperty("store").GetBoolean();
        internal string? PreviousResponseId => Body.TryGetProperty("previous_response_id", out var id) ? id.GetString() : null;
    }

    private sealed record Reply(int Status, string Body, string? Member = "backend-a", string? Cookie = null,
        bool Chunked = false, string? Location = null)
    {
        internal static Reply Error(int status, string code) => new(status,
            JsonSerializer.Serialize(new { error = new { code, message = "Synthetic gateway rejection" } }));

        internal static Reply Success(int number, string? member, string? pin, bool store = true) => new(200,
            JsonSerializer.Serialize(new
            {
                id = $"resp_{number}",
                @object = "response",
                created_at = 1700000000,
                status = "completed",
                model = "responses-chat",
                output = new[]
                {
                    new
                    {
                        id = $"msg_{number}", type = "message", role = "assistant", status = "completed",
                        content = new[] { new { type = "output_text", text = "Synthetic architecture answer.", annotations = Array.Empty<object>() } }
                    }
                },
                store,
                usage = new { input_tokens = 10, output_tokens = 10, total_tokens = 20 }
            }), member, pin is null ? null : $"{CookieName}={pin}; Path=/; HttpOnly");
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopped = new();
        private readonly Task worker;
        internal Uri Endpoint { get; }
        internal List<Call> Calls { get; } = [];

        internal LoopbackServer(Func<Call, Reply> reply, TimeSpan? delay = null)
        {
            listener.Start();
            Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/openai/v1/responses");
            worker = ServeAsync(reply, delay ?? TimeSpan.Zero);
        }

        private async Task ServeAsync(Func<Call, Reply> reply, TimeSpan delay)
        {
            try
            {
                while (!stopped.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stopped.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.Latin1, false, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync(stopped.Token) ?? throw new IOException("No request line");
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stopped.Token)))
                    {
                        var colon = line.IndexOf(':');
                        headers.Add(line[..colon], line[(colon + 1)..].Trim());
                    }
                    var body = new StringBuilder();
                    if (headers.TryGetValue("Content-Length", out var length))
                        body.Append(await ReadCharactersAsync(reader, int.Parse(length, CultureInfo.InvariantCulture), stopped.Token));
                    else
                    {
                        Assert.Equal("chunked", headers["Transfer-Encoding"]);
                        int size;
                        while ((size = int.Parse((await reader.ReadLineAsync(stopped.Token))!.Split(';')[0],
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture)) != 0)
                        {
                            body.Append(await ReadCharactersAsync(reader, size, stopped.Token));
                            Assert.Equal("", await reader.ReadLineAsync(stopped.Token));
                        }
                        Assert.Equal("", await reader.ReadLineAsync(stopped.Token));
                    }
                    using var json = JsonDocument.Parse(Encoding.Latin1.GetBytes(body.ToString()));
                    var call = new Call(Calls.Count + 1, requestLine, headers, json.RootElement.Clone());
                    Calls.Add(call);
                    var response = reply(call);
                    await Task.Delay(delay, stopped.Token);
                    var bytes = Encoding.UTF8.GetBytes(response.Body);
                    var framing = response.Chunked ? "Transfer-Encoding: chunked" : $"Content-Length: {bytes.Length}";
                    var responseHeaders = $"HTTP/1.1 {response.Status} Synthetic\r\nContent-Type: application/json\r\n{framing}\r\nConnection: close\r\nX-Lab-Request-Id: {Guid.NewGuid():D}\r\n";
                    if (response.Member is not null)
                        responseHeaders += $"X-Lab-Backend-Id: {response.Member}\r\n";
                    if (response.Cookie is not null)
                        responseHeaders += $"Set-Cookie: {response.Cookie}\r\n";
                    if (response.Location is not null)
                        responseHeaders += $"Location: {response.Location}\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(responseHeaders + "\r\n"), stopped.Token);
                    try
                    {
                        if (response.Chunked)
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{bytes.Length:X}\r\n"), stopped.Token);
                        await stream.WriteAsync(bytes, stopped.Token);
                        if (response.Chunked)
                            await stream.WriteAsync("\r\n0\r\n\r\n"u8.ToArray(), stopped.Token);
                    }
                    catch (IOException) when (bytes.Length > ResponsesClient.MaxResponseBytes)
                    {
                        // A bounded client can close the connection without consuming the oversized body.
                    }
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested)
            {
            }
        }

        private static async Task<string> ReadCharactersAsync(StreamReader reader, int count, CancellationToken token)
        {
            var chars = new char[count];
            var read = 0;
            while (read != count)
            {
                var next = await reader.ReadAsync(chars.AsMemory(read), token);
                if (next == 0)
                    throw new IOException("Truncated synthetic request");
                read += next;
            }
            return new string(chars);
        }

        public async ValueTask DisposeAsync()
        {
            await stopped.CancelAsync();
            listener.Stop();
            try
            {
                await worker;
            }
            finally
            {
                stopped.Dispose();
            }
        }
    }
}
