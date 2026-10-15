using System.Globalization;
using System.Text.RegularExpressions;

namespace ResponsesLab;

internal sealed class FaultOptions
{
    internal required string Subscription { get; init; }
    internal required string Group { get; init; }
    internal required string Environment { get; init; }
    internal required string Owner { get; init; }
    internal required string ApimId { get; init; }
    internal required string FoundryAId { get; init; }
    internal required string FoundryBId { get; init; }
    internal required string RecoveryPath { get; init; }
    internal string GroupId => $"/subscriptions/{Subscription}/resourceGroups/{Group}";
    internal TimeSpan ProbeInterval { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan PropagationInterval { get; init; } = TimeSpan.FromSeconds(15);
    internal TimeSpan BreakerWindow { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan RestoreTimeout { get; init; } = TimeSpan.FromSeconds(180);

    internal static FaultOptions Load(Func<string, string?> read, string confirmation)
    {
        string Required(string name)
        {
            var value = read(name);
            return !string.IsNullOrWhiteSpace(value) ? value :
                throw new ConfigurationException($"Failure scenario requires {name} from the guarded lifecycle wrapper.");
        }
        var subscription = Required("AZURE_SUBSCRIPTION_ID");
        var group = Required("AZURE_RESOURCE_GROUP");
        var environment = Required("AZURE_ENV_NAME");
        var owner = Required("LAB_OWNER_ID");
        if (subscription.Length != 36 || owner.Length != 36 ||
            !Guid.TryParseExact(subscription, "D", out _) || !Guid.TryParseExact(owner, "D", out _) ||
            !Regex.IsMatch(group, "^[a-zA-Z0-9][a-zA-Z0-9_-]{1,63}\\z") ||
            !Regex.IsMatch(environment, "^[a-z0-9](?:[a-z0-9-]{0,14}[a-z0-9])\\z") || confirmation != group)
            throw new ConfigurationException("Failure target/ownership or exact group confirmation is invalid.");
        var prefix = $"/subscriptions/{subscription}/resourceGroups/{group}/providers/";
        string Resource(string name, string type)
        {
            var value = Required(name);
            if (!Regex.IsMatch(value, "^" + Regex.Escape(prefix + type + "/") + "[a-zA-Z0-9][a-zA-Z0-9-]{0,63}\\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new ConfigurationException($"Failure target {name} is outside the exact owned resource scope.");
            return value;
        }
        var apim = Resource("LAB_APIM_RESOURCE_ID", "Microsoft.ApiManagement/service");
        foreach (var member in new[] { "A", "B" })
            if (!string.Equals(Required($"LAB_BACKEND_{member}_ID"), $"{apim}/backends/backend-{member.ToLowerInvariant()}", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException("Failure targets must be the two exact allow-listed backend entities.");
        var path = Required("LAB_FAULT_RECOVERY_PATH");
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) != "fault-recovery.json" ||
            !Directory.Exists(Path.GetDirectoryName(path)))
            throw new ConfigurationException("Failure recovery journal must be the existing local environment directory's fault-recovery.json.");
        var interval = read("LAB_FAILURE_PROBE_INTERVAL_SECONDS") ?? "2";
        if (!int.TryParse(interval, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds != 2)
            throw new ConfigurationException("LAB_FAILURE_PROBE_INTERVAL_SECONDS must be 2 for the bounded breaker observation.");
        var foundryA = Resource("LAB_FOUNDRY_A_RESOURCE_ID", "Microsoft.CognitiveServices/accounts");
        var foundryB = Resource("LAB_FOUNDRY_B_RESOURCE_ID", "Microsoft.CognitiveServices/accounts");
        if (foundryA.Equals(foundryB, StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationException("Failure scenario requires two distinct owned Foundry resources.");
        return new FaultOptions
        {
            Subscription = subscription,
            Group = group,
            Environment = environment,
            Owner = owner,
            ApimId = apim,
            FoundryAId = foundryA,
            FoundryBId = foundryB,
            RecoveryPath = path,
            ProbeInterval = TimeSpan.FromSeconds(seconds)
        };
    }
}
