using System.Net;

namespace ResponsesLab;

internal enum TurnOutcome
{
    Success,
    HttpError,
    MissingEvidence,
    AffinityViolation,
    MissingAffinity,
    InvalidAffinity,
    InvalidResponse,
    IncompleteResponse,
    Refused,
    ResponseTooLarge,
    RequestTimeout,
    TransportError,
    RunCancelled,
    SdkContractViolation
}

internal sealed record TurnEvidence(
    string Scenario,
    Guid SessionId,
    int Turn,
    string Prompt,
    string? PreviousResponseId,
    Guid ClientRequestId)
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset FinishedAt { get; init; }
    public string? ResponseId { get; init; }
    public string? BackendId { get; init; }
    public string? GatewayRequestId { get; init; }
    public string? AffinityFingerprint { get; init; }
    public int? HttpStatus { get; init; }
    public string? ResponseStatus { get; init; }
    public string? OutputText { get; init; }
    public string? ErrorCode { get; init; }
    public bool CrossResourceStateError { get; init; }
    public string? GatewayErrorReason { get; init; }
    public double? RetryAfterSeconds { get; init; }
    public TurnOutcome Outcome { get; init; }
    public bool Sent { get; init; }
    public string ClientMode { get; init; } = "http";
    public string CookieProfile { get; init; } = "manual";
    public int WireAttempts { get; init; }

    internal bool IsControlledFault(string? member) =>
        Outcome == TurnOutcome.HttpError && Sent && GatewayRequestId is not null &&
        ((HttpStatus == 404 && BackendId == member && member is not null && GatewayErrorReason is null) ||
         (HttpStatus == 503 && BackendId is null && GatewayErrorReason == "CircuitBreakerIsOpen"));
}

internal sealed class Conversation(string scenario, int plannedTurns)
{
    internal Guid Id { get; } = Guid.NewGuid();
    internal string Scenario { get; } = scenario;
    internal int PlannedTurns { get; } = plannedTurns;
    internal CookieContainer Cookies { get; } = new();
    internal string? PreviousResponseId { get; set; }
    internal string? OriginalBackend { get; set; }
    internal List<TurnEvidence> Turns { get; } = [];
    internal bool Failed => Turns.Any(turn => turn.Outcome != TurnOutcome.Success);
    internal int Succeeded => Turns.Count(turn => turn.Outcome == TurnOutcome.Success);
    internal bool Complete => Succeeded == PlannedTurns;
}

internal static class ConversationScript
{
    internal const string Instructions = """
        You are an Azure architecture expert helping a developer build a small
        synthetic APIM Responses API lab. Explain state ownership and native
        cookie affinity accurately. Do not claim that APIM stores model history.
        Give concise answers of at most 120 words. Do not call tools.
        """;

    internal static readonly string[] Prompts =
    [
        "I am a developer using APIM in front of two identical Foundry Responses deployments. What must stay together when I use previous_response_id, and where does the conversation state live?",
        "For that same design, explain how a session-local native affinity cookie differs from my application session UUID. How should independent stateless calls behave?",
        "Continue our design review: what should happen when my pinned backend is unavailable, and which gateway evidence would prove continuity or an explicit failure without silently replaying history?"
    ];
}
