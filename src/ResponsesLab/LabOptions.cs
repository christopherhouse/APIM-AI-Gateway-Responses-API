using System.Globalization;
using System.Text.RegularExpressions;

namespace ResponsesLab;

internal sealed class ConfigurationException(string message) : Exception(message);

internal sealed class LabOptions
{
    internal required Uri Endpoint { get; init; }
    internal required string Model { get; init; }
    internal required string SubscriptionKey { get; init; }
    internal string CookieName { get; init; } = "APIM-Backend-Affinity";
    internal int StatefulSessions { get; init; } = 4;
    internal int TurnsPerSession { get; init; } = 3;
    internal int StatelessRequests { get; init; } = 12;
    internal int MaxOutputTokens { get; init; } = 2048;
    internal string ReasoningEffort { get; init; } = "low";
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    internal TimeSpan RunTimeout { get; init; } = TimeSpan.FromSeconds(600);
    internal TimeSpan RequestInterval { get; init; } = TimeSpan.FromSeconds(10);

    internal static LabOptions Load(Func<string, string?> read)
    {
        string Required(string name)
        {
            var value = read(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new ConfigurationException($"Set {name} through the approved local/process configuration.");
            return value;
        }

        int Number(string name, int fallback, int minimum, int maximum)
        {
            var text = read(name);
            if (text is null)
                return fallback;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value < minimum || value > maximum)
                throw new ConfigurationException($"{name} must be an integer in [{minimum}, {maximum}].");
            return value;
        }

        var endpointText = Required("LAB_RESPONSES_ENDPOINT");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort ||
            endpoint.AbsolutePath != "/openai/v1/responses" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ConfigurationException("LAB_RESPONSES_ENDPOINT must be the exported HTTPS Responses URL without credentials, query, fragment, or custom port.");

        var model = Required("LAB_MODEL_DEPLOYMENT");
        if (!Regex.IsMatch(model, "^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}$"))
            throw new ConfigurationException("LAB_MODEL_DEPLOYMENT is not a valid deployment name.");
        var key = Required("LAB_APIM_SUBSCRIPTION_KEY");
        if (!Regex.IsMatch(key, "^[a-zA-Z0-9+/=_-]{8,512}$"))
            throw new ConfigurationException("LAB_APIM_SUBSCRIPTION_KEY has an invalid format; its value is withheld.");
        var cookieName = read("LAB_AFFINITY_COOKIE_NAME") ?? "APIM-Backend-Affinity";
        if (cookieName != "APIM-Backend-Affinity")
            throw new ConfigurationException("LAB_AFFINITY_COOKIE_NAME must match the fixed gateway contract.");
        var effort = read("LAB_REASONING_EFFORT") ?? "low";
        if (effort is not ("low" or "medium" or "high"))
            throw new ConfigurationException("LAB_REASONING_EFFORT must be low, medium, or high.");
        _ = Number("LAB_MAX_RETRIES", 0, 0, 0);
        if (read("LAB_ENABLE_FAULTS") is { } faults && faults != "false")
            throw new ConfigurationException("Fault execution is not implemented in task 005; LAB_ENABLE_FAULTS must be absent or false.");

        return new LabOptions
        {
            Endpoint = endpoint,
            Model = model,
            SubscriptionKey = key,
            CookieName = cookieName,
            StatefulSessions = Number("LAB_STATEFUL_SESSION_COUNT", 4, 2, 8),
            TurnsPerSession = Number("LAB_TURNS_PER_SESSION", 3, 2, 3),
            StatelessRequests = Number("LAB_STATELESS_REQUEST_COUNT", 12, 2, 24),
            MaxOutputTokens = Number("LAB_MAX_OUTPUT_TOKENS", 2048, 256, 4096),
            ReasoningEffort = effort,
            RequestTimeout = TimeSpan.FromSeconds(Number("LAB_REQUEST_TIMEOUT_SECONDS", 60, 5, 120)),
            RunTimeout = TimeSpan.FromSeconds(Number("LAB_RUN_TIMEOUT_SECONDS", 600, 30, 1800)),
            RequestInterval = TimeSpan.FromSeconds(Number("LAB_REQUEST_INTERVAL_SECONDS", 10, 10, 30))
        };
    }
}
