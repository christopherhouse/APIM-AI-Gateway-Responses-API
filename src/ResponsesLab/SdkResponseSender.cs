using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using Azure.AI.Extensions.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

// The pinned Responses APIs carry this evaluation marker; keep the opt-in local to the adapter.
#pragma warning disable OPENAI001

namespace ResponsesLab;

internal sealed record SdkTurnResult(HttpResponseMessage Response, string? ResponseId, string? OutputText, bool Parsed,
    bool BodyTooLarge = false, Exception? TransportFailure = null);
internal sealed class SdkBodyTooLargeException : Exception;
internal sealed class SdkContractException : Exception;

internal sealed class SdkResponseSender(LabOptions options, ClientSelection selection) : IDisposable
{
    private readonly Dictionary<Guid, SessionClient> clients = [];
    private readonly HashSet<string> privateCookieValues = new(StringComparer.Ordinal);
    private int remainingAttempts = options.StatefulSessions * options.TurnsPerSession + options.StatelessRequests;

    internal IEnumerable<string> PrivateCookieValues => privateCookieValues;

    internal void SetBudget(int attempts) => remainingAttempts = attempts;

    internal async Task<SdkTurnResult> SendAsync(Conversation conversation, string prompt, Guid requestId,
        Action sending, CancellationToken token)
    {
        if (!clients.TryGetValue(conversation.Id, out var client))
        {
            client = CreateClient(conversation);
            clients.Add(conversation.Id, client);
        }
        var call = new WireCall(conversation, prompt, requestId, () =>
        {
            if (remainingAttempts <= 0)
                throw new SdkContractException();
            remainingAttempts--;
            sending();
        });
        client.Policy.Call = call;
        try
        {
            string? id;
            string? text;
            if (selection.Mode == "foundry-sdk")
            {
                var create = CreateOptions(conversation);
                create.Instructions = ConversationScript.Instructions;
                create.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));
                create.PreviousResponseId = conversation.PreviousResponseId;
                var result = await client.Responses.CreateResponseAsync(create, cancellationToken: token);
                id = result.Value.Id;
                text = result.Value.GetOutputText();
            }
            else
            {
                client.AgentSession ??= await client.Agent!.CreateSessionAsync(token);
                var result = await client.Agent!.RunAsync(prompt, client.AgentSession, cancellationToken: token);
                id = result.ResponseId;
                text = result.Text;
                if (conversation.Scenario == "stateful" &&
                    (client.AgentSession is not ChatClientAgentSession session || session.ConversationId != id))
                    return Result(null, null, false);
            }
            return Result(id, text, true);
        }
        catch (ClientResultException) when (call.Response is not null)
        {
            return Result(null, null, false);
        }
        catch (Exception error) when (call.Response is not null &&
            error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            // Typed serializers also use these exceptions for invalid JSON field types and ranges.
            return Result(null, null, false);
        }
        catch (SdkBodyTooLargeException) when (call.Response is not null)
        {
            return Result(null, null, false) with { BodyTooLarge = true };
        }
        catch (OperationCanceledException error) when (call.Response is not null)
        {
            return Result(null, null, false) with { TransportFailure = error };
        }
        catch (HttpRequestException error) when (call.Response is not null)
        {
            return Result(null, null, false) with { TransportFailure = error };
        }
        catch (IOException error) when (call.Response is not null)
        {
            return Result(null, null, false) with { TransportFailure = error };
        }
        finally
        {
            client.Policy.Call = null;
            call.Response?.Dispose();
        }

        SdkTurnResult Result(string? id, string? text, bool parsed)
        {
            var response = call.Response ?? throw new SdkContractException();
            call.Response = null;
            return new SdkTurnResult(response, id, text, parsed);
        }
    }

    private CreateResponseOptions CreateOptions(Conversation conversation) => new()
    {
        Model = options.Model,
        StoredOutputEnabled = conversation.Scenario == "stateful",
        StreamingEnabled = false,
        MaxOutputTokenCount = options.MaxOutputTokens,
        ReasoningOptions = new ResponseReasoningOptions
        {
            ReasoningEffortLevel = new ResponseReasoningEffortLevel(options.ReasoningEffort)
        }
    };

    private SessionClient CreateClient(Conversation conversation)
    {
        var policy = new WirePolicy(options, privateCookieValues);
        var sdkOptions = new ProjectOpenAIClientOptions
        {
            Endpoint = new Uri(options.Endpoint, "."),
            RetryPolicy = new ClientRetryPolicy(0),
            NetworkTimeout = options.RequestTimeout,
            EnableDistributedTracing = false,
            ClientLoggingOptions = new ClientLoggingOptions
            {
                EnableLogging = false,
                EnableMessageLogging = false,
                EnableMessageContentLogging = false
            }
        };
        HttpClient? http = null;
        if (selection.CookieProfile == "session-scoped")
        {
            http = new HttpClient(new SocketsHttpHandler
            {
                UseCookies = conversation.Scenario == "stateful" && options.SessionAffinityEnabled,
                CookieContainer = conversation.Cookies,
                AllowAutoRedirect = false
            })
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            sdkOptions.Transport = new HttpClientPipelineTransport(http);
        }
        sdkOptions.AddPolicy(policy, PipelinePosition.BeforeTransport);
        var auth = ApiKeyAuthenticationPolicy.CreateHeaderApiKeyPolicy(
            new ApiKeyCredential(options.SubscriptionKey), "Ocp-Apim-Subscription-Key");
        var responses = new ProjectOpenAIClient(auth, sdkOptions).GetProjectResponsesClientForModel(options.Model);
        var agent = selection.Mode == "agent-framework"
            ? responses.AsAIAgent(new ChatClientAgentOptions
            {
                UseProvidedChatClientAsIs = true,
                ChatOptions = new ChatOptions
                {
                    Instructions = ConversationScript.Instructions,
                    ModelId = options.Model,
                    RawRepresentationFactory = _ => CreateOptions(conversation)
                }
            })
            : null;
        return new SessionClient(responses, agent, policy, http);
    }

    public void Dispose()
    {
        foreach (var client in clients.Values)
            client.Http?.Dispose();
    }

    private sealed class SessionClient(ProjectResponsesClient responses, ChatClientAgent? agent, WirePolicy policy, HttpClient? http)
    {
        internal ProjectResponsesClient Responses { get; } = responses;
        internal ChatClientAgent? Agent { get; } = agent;
        internal AgentSession? AgentSession { get; set; }
        internal WirePolicy Policy { get; } = policy;
        internal HttpClient? Http { get; } = http;
    }

    private sealed class WireCall(Conversation conversation, string prompt, Guid requestId, Action sending)
    {
        internal Conversation Conversation { get; } = conversation;
        internal string Prompt { get; } = prompt;
        internal Guid RequestId { get; } = requestId;
        internal Action Sending { get; } = sending;
        internal bool Sent { get; set; }
        internal HttpResponseMessage? Response { get; set; }
    }

    private sealed class WirePolicy(LabOptions options, HashSet<string> privateCookieValues) : PipelinePolicy
    {
        internal WireCall? Call { get; set; }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
            throw new SdkContractException();

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            var call = Call ?? throw new SdkContractException();
            if (call.Sent || message.Request.Method != "POST" || message.Request.Uri != options.Endpoint)
                throw new SdkContractException();
            using var content = new MemoryStream();
            await message.Request.Content!.WriteToAsync(content, message.CancellationToken);
            using var json = JsonDocument.Parse(content.ToArray());
            var root = json.RootElement;
            var stateful = call.Conversation.Scenario == "stateful";
            if (root.ValueKind != JsonValueKind.Object ||
                !IsText(root, "model", options.Model) ||
                !IsText(root, "instructions", ConversationScript.Instructions) ||
                !root.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.False ||
                !root.TryGetProperty("store", out var store) || store.ValueKind != (stateful ? JsonValueKind.True : JsonValueKind.False) ||
                !root.TryGetProperty("max_output_tokens", out var maximum) ||
                maximum.ValueKind != JsonValueKind.Number || !maximum.TryGetInt32(out var tokens) || tokens != options.MaxOutputTokens ||
                !root.TryGetProperty("reasoning", out var reasoning) || !IsText(reasoning, "effort", options.ReasoningEffort) ||
                root.TryGetProperty("conversation", out _) ||
                root.TryGetProperty("tools", out var tools) && (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() != 0) ||
                root.TryGetProperty("background", out var background) && background.ValueKind != JsonValueKind.False)
                throw new SdkContractException();
            var hasPrevious = root.TryGetProperty("previous_response_id", out var previous);
            if (call.Conversation.PreviousResponseId is { } expected
                ? !hasPrevious || previous.ValueKind != JsonValueKind.String || previous.GetString() != expected
                : hasPrevious)
                throw new SdkContractException();
            if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array || input.GetArrayLength() != 1 ||
                !IsText(input[0], "role", "user") || !input[0].TryGetProperty("content", out var parts) ||
                parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != 1 ||
                !IsText(parts[0], "type", "input_text") || !IsText(parts[0], "text", call.Prompt))
                throw new SdkContractException();

            message.Request.Headers.Set("X-Lab-Session-Id", call.Conversation.Id.ToString("D"));
            message.Request.Headers.Set("X-Lab-Scenario", call.Conversation.Scenario);
            message.Request.Headers.Set("x-ms-client-request-id", call.RequestId.ToString("D"));
            message.BufferResponse = false;
            call.Sending();
            call.Sent = true;
            await ProcessNextAsync(message, pipeline, currentIndex);
            var raw = message.Response ?? throw new SdkContractException();
            using var response = new HttpResponseMessage((HttpStatusCode)raw.Status);
            response.Content = new StreamContent(raw.ContentStream ?? Stream.Null);
            call.Response = new HttpResponseMessage((HttpStatusCode)raw.Status);
            foreach (var name in raw.Headers.Select(header => header.Key).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!raw.Headers.TryGetValues(name, out var values) || values is null)
                    throw new SdkContractException();
                var all = values.ToArray();
                response.Headers.TryAddWithoutValidation(name, all);
                response.Content.Headers.TryAddWithoutValidation(name, all);
                call.Response.Headers.TryAddWithoutValidation(name, all);
            }
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var cookie in cookies.Where(value => value.StartsWith(options.CookieName + "=", StringComparison.Ordinal)))
                {
                    var value = cookie.Split(';', 2)[0][(options.CookieName.Length + 1)..];
                    if (value.Length != 0)
                        privateCookieValues.Add(value);
                }
            var bytes = await ResponsesClient.ReadBodyAsync(response, message.CancellationToken)
                ?? throw new SdkBodyTooLargeException();
            call.Response.Content = new ByteArrayContent(bytes);
            raw.ContentStream = new MemoryStream(bytes, writable: false);
            await raw.BufferContentAsync(message.CancellationToken);
        }

        private static bool IsText(JsonElement root, string name, string expected) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String && value.GetString() == expected;
    }
}
