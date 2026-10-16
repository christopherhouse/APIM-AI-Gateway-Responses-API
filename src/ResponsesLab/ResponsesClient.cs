using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResponsesLab;

internal sealed class ResponsesClient(HttpClient http, LabOptions options, SdkResponseSender? sdk = null,
    ClientSelection? selection = null)
{
    internal ClientSelection Selection { get; } = selection ?? new ClientSelection();
    internal const int MaxResponseBytes = 1024 * 1024;

    internal void SetBudget(int attempts) => sdk?.SetBudget(attempts);

    internal static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal Task<TurnEvidence> SendAsync(Conversation session, string prompt, CancellationToken cancellationToken) =>
        SendCoreAsync(session, prompt, cancellationToken, false);

    internal Task<TurnEvidence> SendFaultProbeAsync(Conversation session, string prompt, CancellationToken cancellationToken) =>
        SendCoreAsync(session, prompt, cancellationToken, true);

    private async Task<TurnEvidence> SendCoreAsync(Conversation session, string prompt, CancellationToken cancellationToken, bool faultProbe)
    {
        if (faultProbe && (session.Scenario != "stateful" || sdk is not null))
            throw new InvalidOperationException("Controlled fault probes require a stateful session.");
        if ((session.Failed && (!faultProbe || session.Turns.Any(turn =>
                turn.Outcome != TurnOutcome.Success && !turn.IsControlledFault(session.OriginalBackend)))) ||
            session.Turns.Count >= session.PlannedTurns)
            throw new InvalidOperationException("Cannot resume a failed or completed conversation.");

        var evidence = new TurnEvidence(session.Scenario, session.Id, session.Turns.Count + 1,
            prompt, session.PreviousResponseId, Guid.NewGuid())
        {
            ClientMode = Selection.Mode,
            CookieProfile = Selection.CookieProfile
        };
        var privateValues = new HashSet<string>(StringComparer.Ordinal) { options.SubscriptionKey };
        foreach (Cookie cookie in session.Cookies.GetAllCookies())
            if (cookie.Value.Length != 0)
                privateValues.Add(cookie.Value);

        TurnEvidence Finish(TurnOutcome outcome)
        {
            if (sdk is not null)
                privateValues.UnionWith(sdk.PrivateCookieValues);
            string? Redact(string? value)
            {
                foreach (var secret in privateValues.OrderByDescending(value => value.Length))
                    value = value?.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
                return value;
            }

            var result = evidence with
            {
                Outcome = outcome,
                FinishedAt = DateTimeOffset.UtcNow,
                OutputText = Redact(evidence.OutputText),
                ErrorCode = Redact(evidence.ErrorCode)
            };
            session.Turns.Add(result);
            return result;
        }

        if (cancellationToken.IsCancellationRequested)
            return Finish(TurnOutcome.RunCancelled);
        var stateful = session.Scenario == "stateful";
        var cookies = AffinityCookies(session);
        if (stateful && options.SessionAffinityEnabled && session.PreviousResponseId is not null &&
            Selection.CookieProfile != "sdk-default" && !ValidCookie(cookies))
            return Finish(TurnOutcome.MissingAffinity);

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
        request.Headers.Add("Ocp-Apim-Subscription-Key", options.SubscriptionKey);
        request.Headers.Add("X-Lab-Session-Id", session.Id.ToString("D"));
        request.Headers.Add("X-Lab-Scenario", session.Scenario);
        request.Headers.Add("x-ms-client-request-id", evidence.ClientRequestId.ToString("D"));
        if (sdk is null && stateful && options.SessionAffinityEnabled && session.PreviousResponseId is not null)
        {
            request.Headers.Add("Cookie", $"{options.CookieName}={cookies[0].Value}");
            evidence = evidence with { AffinityFingerprint = Fingerprint(cookies[0].Value) };
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = options.Model,
            ["instructions"] = ConversationScript.Instructions,
            ["input"] = prompt,
            ["stream"] = false,
            ["store"] = stateful,
            ["max_output_tokens"] = options.MaxOutputTokens,
            ["reasoning"] = new { effort = options.ReasoningEffort }
        };
        if (stateful && session.PreviousResponseId is not null)
            body["previous_response_id"] = session.PreviousResponseId;
        request.Content = JsonContent.Create(body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            SdkTurnResult? sdkResult = null;
            HttpResponseMessage responseMessage;
            if (sdk is null)
            {
                evidence = evidence with { Sent = true, WireAttempts = 1 };
                responseMessage = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            else
            {
                sdkResult = await sdk.SendAsync(session, prompt, evidence.ClientRequestId,
                    () => evidence = evidence with { Sent = true, WireAttempts = evidence.WireAttempts + 1 }, timeout.Token);
                responseMessage = sdkResult.Response;
            }
            using var response = responseMessage;
            var backend = SingleHeader(response, "X-Lab-Backend-Id");
            var requestId = SingleHeader(response, "X-Lab-Request-Id");
            var retryAfter = response.Headers.RetryAfter;
            evidence = evidence with
            {
                HttpStatus = (int)response.StatusCode,
                BackendId = backend is "backend-a" or "backend-b" ? backend : null,
                GatewayRequestId = Guid.TryParseExact(requestId, "D", out _) ? requestId : null,
                GatewayErrorReason = SingleHeader(response, "X-Lab-Error-Reason") == "CircuitBreakerIsOpen"
                    ? "CircuitBreakerIsOpen" : null,
                RetryAfterSeconds = retryAfter?.Delta?.TotalSeconds ??
                    (retryAfter?.Date is { } date ? Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds) : null)
            };
            var selected = response.Headers.TryGetValues("Set-Cookie", out var setCookies)
                ? setCookies.Where(value => value.StartsWith(options.CookieName + "=", StringComparison.Ordinal)).ToArray()
                : [];
            foreach (var header in selected)
            {
                var value = header.Split(';', 2)[0][(options.CookieName.Length + 1)..];
                if (value.Length != 0)
                    privateValues.Add(value);
            }

            if (sdkResult?.TransportFailure is { } failure)
                ExceptionDispatchInfo.Capture(failure).Throw();
            if (sdkResult?.BodyTooLarge == true)
                return Finish(TurnOutcome.ResponseTooLarge);
            var bytes = await ReadBodyAsync(response, timeout.Token);
            if (bytes is null)
                return Finish(TurnOutcome.ResponseTooLarge);
            if (!response.IsSuccessStatusCode)
            {
                var httpError = ParseError(bytes);
                evidence = evidence with { ErrorCode = httpError.Code, CrossResourceStateError = httpError.CrossResourceStateError };
                return Finish(TurnOutcome.HttpError);
            }
            if (evidence.BackendId is null || evidence.GatewayRequestId is null || evidence.GatewayErrorReason is not null)
                return Finish(TurnOutcome.MissingEvidence);
            if (stateful && session.OriginalBackend is not null && backend != session.OriginalBackend)
                return Finish(TurnOutcome.AffinityViolation);

            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) &&
                error.ValueKind != JsonValueKind.Null)
                return Finish(TurnOutcome.InvalidResponse);
            var id = Text(root, "id");
            var status = Text(root, "status");
            if (id is null || !Regex.IsMatch(id, "^[a-zA-Z0-9_.-]{1,256}$") ||
                id == session.PreviousResponseId || privateValues.Any(id.Contains))
                return Finish(TurnOutcome.InvalidResponse);
            var text = new StringBuilder();
            var refused = false;
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (Text(item, "type") != "message" || Text(item, "role") != "assistant" ||
                        !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var part in content.EnumerateArray())
                    {
                        if (Text(part, "type") == "output_text")
                            text.Append(Text(part, "text"));
                        if (Text(part, "type") == "refusal")
                            refused = true;
                    }
                }
            }
            evidence = evidence with
            {
                ResponseId = id,
                ResponseStatus = status is "completed" or "incomplete" or "failed" or "in_progress" or "queued" or "cancelled" ? status : null,
                OutputText = text.ToString()
            };
            if (status != "completed")
                return Finish(TurnOutcome.IncompleteResponse);
            if (refused)
                return Finish(TurnOutcome.Refused);
            if (string.IsNullOrWhiteSpace(evidence.OutputText))
                return Finish(TurnOutcome.InvalidResponse);
            if (sdkResult is not null && (!sdkResult.Parsed || sdkResult.ResponseId != id || sdkResult.OutputText != evidence.OutputText))
                return Finish(TurnOutcome.InvalidResponse);

            if (stateful)
            {
                if (selected.Length > 1)
                    return Finish(TurnOutcome.InvalidAffinity);
                if (options.SessionAffinityEnabled)
                {
                    foreach (var header in selected)
                        session.Cookies.SetCookies(options.Endpoint, header);
                    cookies = AffinityCookies(session);
                    if (!ValidCookie(cookies))
                        return Finish(TurnOutcome.MissingAffinity);
                    evidence = evidence with { AffinityFingerprint = Fingerprint(cookies[0].Value) };
                }
                else if (selected.Length != 0)
                    return Finish(TurnOutcome.InvalidAffinity);
                session.PreviousResponseId = id;
                session.OriginalBackend ??= backend;
            }
            return Finish(TurnOutcome.Success);
        }
        catch (OperationCanceledException)
        {
            return Finish(cancellationToken.IsCancellationRequested ? TurnOutcome.RunCancelled : TurnOutcome.RequestTimeout);
        }
        catch (HttpRequestException)
        {
            return Finish(TurnOutcome.TransportError);
        }
        catch (IOException)
        {
            return Finish(TurnOutcome.TransportError);
        }
        catch (JsonException)
        {
            return Finish(TurnOutcome.InvalidResponse);
        }
        catch (CookieException)
        {
            return Finish(TurnOutcome.InvalidAffinity);
        }
        catch (SdkBodyTooLargeException)
        {
            return Finish(TurnOutcome.ResponseTooLarge);
        }
        catch (SdkContractException)
        {
            return Finish(TurnOutcome.SdkContractViolation);
        }
    }

    private Cookie[] AffinityCookies(Conversation session) =>
        session.Cookies.GetCookies(options.Endpoint).Cast<Cookie>()
            .Where(cookie => cookie.Name == options.CookieName).ToArray();

    private static bool ValidCookie(Cookie[] cookies) =>
        cookies.Length == 1 && !cookies[0].Expired && !string.IsNullOrWhiteSpace(cookies[0].Value.Trim('"'));

    private static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];

    private static string? SingleHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
            return null;
        var selected = values.ToArray();
        return selected.Length == 1 ? selected[0] : null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (string? Code, bool CrossResourceStateError) ParseError(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = Text(error, "code");
                var crossResource = error.TryGetProperty("code", out var rawCode) && rawCode.ValueKind == JsonValueKind.Null &&
                    error.TryGetProperty("param", out var param) && param.ValueKind == JsonValueKind.Null &&
                    Text(error, "type") == "invalid_request_error" &&
                    Text(error, "message") == "The requested item was created under a different Azure OpenAI resource. Use the same resource that created the item to access it.";
                return (code is not null && Regex.IsMatch(code, "^[a-zA-Z0-9_.-]{1,64}$") ? code : null, crossResource);
            }
        }
        catch (JsonException)
        {
            // HTTP errors can have non-JSON bodies; the status remains a failure.
        }
        return (null, false);
    }

    internal static async Task<byte[]?> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
