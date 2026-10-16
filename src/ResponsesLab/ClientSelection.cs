namespace ResponsesLab;

internal sealed record ClientSelection(string Mode = "http", string CookieProfile = "manual")
{
    internal bool IsSdk => Mode != "http";

    internal static ClientSelection Parse(string? mode, string? profile)
    {
        mode ??= "http";
        profile ??= mode == "http" ? "manual" : "session-scoped";
        if (mode is not ("http" or "foundry-sdk" or "agent-framework"))
            throw new ConfigurationException("--client must be http, foundry-sdk or agent-framework.");
        if (mode == "http" ? profile != "manual" : profile is not ("sdk-default" or "session-scoped"))
            throw new ConfigurationException("Use manual cookies for http; sdk-default or session-scoped for SDK modes.");
        return new ClientSelection(mode, profile);
    }

    internal static string Versions =>
        "Azure.AI.Extensions.OpenAI=3.0.0-beta.3; Microsoft.Agents.AI.OpenAI=1.25.0; OpenAI=2.14.0; System.ClientModel=1.15.0";
}

internal sealed record Invocation(string Scenario, ClientSelection Client, string? FaultConfirmation)
{
    internal static Invocation Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var requests = false;
        var faults = false;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg == "--allow-requests" && !requests)
                requests = true;
            else if (arg == "--allow-faults" && !faults)
                faults = true;
            else if (arg is "--scenario" or "--client" or "--cookie-profile" or "--confirm-resource-group" &&
                index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) &&
                values.TryAdd(arg, args[index + 1]))
                index++;
            else
                throw new ConfigurationException("Unknown, duplicate or incomplete argument. See --help.");
        }
        if (!requests)
            throw new ConfigurationException("No model calls without --allow-requests. See --help.");
        var scenario = values.GetValueOrDefault("--scenario") ?? "both";
        if (scenario is not ("both" or "stateful" or "stateless" or "affinity-off" or "failure"))
            throw new ConfigurationException("Unknown scenario. See --help.");
        var selection = ClientSelection.Parse(values.GetValueOrDefault("--client"), values.GetValueOrDefault("--cookie-profile"));
        var confirmation = values.GetValueOrDefault("--confirm-resource-group");
        if (scenario == "failure" ? !faults || confirmation is null || selection.IsSdk : faults || confirmation is not null)
            throw new ConfigurationException("Failure requires raw http, --allow-faults and exact --confirm-resource-group; fault arguments are forbidden in other scenarios.");
        return new Invocation(scenario, selection, confirmation);
    }
}
