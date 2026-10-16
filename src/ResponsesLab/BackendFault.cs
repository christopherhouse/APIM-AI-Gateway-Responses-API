using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ResponsesLab;

internal sealed class BackendControlException(string message) : Exception(message)
{
    internal int? HttpStatus { get; init; }
}

internal interface IBackendFault
{
    bool MutationAttempted { get; }
    Task PrepareAsync(CancellationToken token);
    Task ApplyAsync(string member, CancellationToken token);
    Task RestoreAsync(CancellationToken token);
}

internal sealed record ManagementResponse(JsonObject Body, string? ETag);

internal sealed class ManagementClient(HttpClient http, string groupId, Func<CancellationToken, Task<string>> getToken)
{
    internal async Task<ManagementResponse> SendAsync(string path, string version, CancellationToken token,
        JsonObject? body = null, string? etag = null)
    {
        if (!path.Equals(groupId, StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(groupId + "/", StringComparison.OrdinalIgnoreCase))
            throw new BackendControlException("Management destination is outside the exact lab group.");
        if (body is not null && (string.IsNullOrWhiteSpace(etag) || etag == "*"))
            throw new BackendControlException("A concrete entity ETag is required for conditional URL-only PATCH.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Patch,
            $"https://management.azure.com{path}?api-version={version}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await getToken(timeout.Token));
        if (body is not null)
        {
            request.Headers.Add("If-Match", etag);
            request.Content = JsonContent.Create(body);
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new BackendControlException($"Management {(body is null ? "GET" : "PATCH")} did not return the documented synchronous HTTP 200: HTTP {(int)response.StatusCode}; body withheld. No unconditional overwrite.")
            {
                HttpStatus = (int)response.StatusCode
            };
        var bytes = await ResponsesClient.ReadBodyAsync(response, timeout.Token);
        if (bytes is null)
            throw new BackendControlException("Management response exceeded the body limit.");
        try
        {
            var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
            return new ManagementResponse(JsonNode.Parse(bytes.AsSpan(offset)) as JsonObject ??
                throw new BackendControlException("Management response is not an object."), response.Headers.ETag?.ToString());
        }
        catch (JsonException)
        {
            throw new BackendControlException("Management response was invalid JSON; body withheld.");
        }
    }
}

internal static class AzureCliToken
{
    internal static async Task<string> GetAsync(string subscription, CancellationToken token)
    {
        if (!Guid.TryParseExact(subscription, "D", out _))
            throw new BackendControlException("Invalid explicit management subscription.");
        var active = await RunAsync("account show --query id --output tsv --only-show-errors", token);
        if (!active.Equals(subscription, StringComparison.OrdinalIgnoreCase))
            throw new BackendControlException("Active CLI subscription changed; no subscription was switched.");
        return await RunAsync($"account get-access-token --subscription {subscription} --resource https://management.azure.com/ --query accessToken --output tsv --only-show-errors", token);
    }

    private static async Task<string> RunAsync(string arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "az",
            Arguments = OperatingSystem.IsWindows() ? "/d /s /c az " + arguments : arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Remove("LAB_APIM_SUBSCRIPTION_KEY");
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            if (!process.Start())
                throw new BackendControlException("Could not start Azure CLI.");
            started = true;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var value = (await output).Trim();
            _ = await error;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(value))
                throw new BackendControlException("Azure CLI management authentication failed; output withheld.");
            return value;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new BackendControlException("Azure CLI could not be started; install/sign in separately.");
        }
        finally
        {
            if (started && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}

internal sealed class BackendFault(ManagementClient management, FaultOptions options, Uri responsesEndpoint) : IBackendFault
{
    private const string BackendVersion = "2025-03-01-preview";
    private sealed record Snapshot(string Id, JsonObject Properties, string ETag);
    private readonly Dictionary<string, Snapshot> snapshots = [];
    private Snapshot? original;
    private JsonObject? faultProperties;
    private string? appliedETag;
    private Snapshot? poolSnapshot;
    private bool applyRejected;
    public bool MutationAttempted { get; private set; }

    public async Task PrepareAsync(CancellationToken token)
    {
        if (File.Exists(options.RecoveryPath))
            throw new BackendControlException("An unresolved fault-recovery.json exists. Inspect/reconcile it manually before another run.");
        var group = (await management.SendAsync(options.GroupId, "2024-03-01", token)).Body;
        Owned(group, options.GroupId);
        var gateway = (await management.SendAsync(options.ApimId, BackendVersion, token)).Body;
        Owned(gateway, options.ApimId);
        if (Text(gateway["sku"]?["name"]) != "Developer" || gateway["sku"]?["capacity"]?.GetValue<int>() != 1 ||
            !Uri.TryCreate(Text(gateway["properties"]?["gatewayUrl"]), UriKind.Absolute, out var gatewayUrl) ||
            gatewayUrl.Scheme != "https" || gatewayUrl.Authority != responsesEndpoint.Authority)
            throw new BackendControlException("Fault target gateway SKU/URL is not the exact Developer lab.");
        foreach (var member in new[] { "backend-a", "backend-b" })
        {
            var accountId = member == "backend-a" ? options.FoundryAId : options.FoundryBId;
            var account = (await management.SendAsync(accountId, "2025-06-01", token)).Body;
            Owned(account, accountId);
            if (Text(account["kind"]) != "AIServices" ||
                !Uri.TryCreate(Text(account["properties"]?["endpoints"]?["OpenAI Language Model Instance API"]), UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != "https" || !endpoint.IsDefaultPort || endpoint.UserInfo.Length != 0 ||
                endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/")
                throw new BackendControlException("Owned Foundry OpenAI endpoint is missing or unsafe.");
            var snapshot = await ReadAsync($"{options.ApimId}/backends/{member}", token);
            var p = snapshot.Properties;
            if (Text(p["type"]) != "Single" || Text(p["protocol"]) != "http" ||
                Text(p["url"]) != new Uri(endpoint, "openai/v1").AbsoluteUri ||
                p["tls"]?["validateCertificateChain"]?.GetValue<bool>() != true ||
                p["tls"]?["validateCertificateName"]?.GetValue<bool>() != true ||
                !Empty(p["credentials"]) || !Empty(p["proxy"]))
                throw new BackendControlException("Backend URL/TLS/authentication differs from the owned lab; no fault applied.");
            var rules = p["circuitBreaker"]?["rules"] as JsonArray;
            var rule = rules?.Count == 1 ? rules[0] : null;
            if (Text(rule?["tripDuration"]) != "PT30S" || rule?["acceptRetryAfter"]?.GetValue<bool>() != true ||
                rule?["failureCondition"]?["count"]?.GetValue<int>() != 1 ||
                Text(rule?["failureCondition"]?["interval"]) != "PT1M" ||
                !JsonNode.DeepEquals(rule?["failureCondition"]?["statusCodeRanges"],
                    JsonNode.Parse("""[{"min":404,"max":404},{"min":429,"max":429},{"min":500,"max":599}]""")))
                throw new BackendControlException("Backend breaker configuration changed; revisit the design rather than assuming its window.");
            snapshots.Add(member, snapshot);
        }
        poolSnapshot = await ReadAsync(options.ApimId + "/backends/responses-pool", token);
        var pool = poolSnapshot.Properties;
        var services = pool["pool"]?["services"] as JsonArray;
        if (services is not { Count: 2 })
            throw new BackendControlException("Native pool must contain exactly two services.");
        if (Text(pool["type"]) != "Pool" ||
            Text(pool["pool"]?["sessionAffinity"]?["sessionId"]?["name"]) != "APIM-Backend-Affinity" ||
            Text(pool["pool"]?["sessionAffinity"]?["sessionId"]?["source"]) != "cookie" ||
            !snapshots.Values.All(snapshot => services.Count(item =>
                string.Equals(Text(item?["id"]), snapshot.Id, StringComparison.OrdinalIgnoreCase) &&
                item?["priority"]?.GetValue<int>() == 1 && item?["weight"]?.GetValue<int>() == 1) == 1))
            throw new BackendControlException("Native affinity pool does not contain exactly the two approved equal-weight backends.");
    }

    public async Task ApplyAsync(string member, CancellationToken token)
    {
        if (MutationAttempted || snapshots.Count != 2 || !snapshots.TryGetValue(member, out var selected))
            throw new BackendControlException("Fault requires one prepared allow-listed member and cannot be applied twice.");
        Owned((await management.SendAsync(options.GroupId, "2024-03-01", token)).Body, options.GroupId);
        foreach (var snapshot in snapshots.Values.Append(poolSnapshot ??
            throw new BackendControlException("Native pool was not prepared.")))
        {
            var current = await ReadAsync(snapshot.Id, token);
            if (current.ETag != snapshot.ETag || !JsonNode.DeepEquals(current.Properties, snapshot.Properties))
                throw new BackendControlException("Concurrent backend configuration change detected before fault injection.");
        }
        original = selected;
        faultProperties = (JsonObject)selected.Properties.DeepClone();
        var faultUrl = new UriBuilder(Text(selected.Properties["url"])!)
        {
            Path = "/task006-intentionally-missing/openai/v1"
        }.Uri.AbsoluteUri;
        faultProperties["url"] = faultUrl;
        var journal = new
        {
            backendId = selected.Id,
            originalUrl = Text(selected.Properties["url"]),
            faultUrl,
            originalETag = selected.ETag,
            originalPropertiesSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selected.Properties.ToJsonString()))),
            owner = options.Owner,
            recordedAt = DateTimeOffset.UtcNow
        };
        using (var file = new FileStream(options.RecoveryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, journal);
            file.Flush(flushToDisk: true);
        }
        MutationAttempted = true;
        ManagementResponse patched;
        try
        {
            patched = await management.SendAsync(selected.Id, BackendVersion, token, UrlOnly(faultUrl), selected.ETag);
        }
        catch (BackendControlException error) when (error.HttpStatus == 412)
        {
            applyRejected = true;
            throw;
        }
        appliedETag = patched.ETag ?? throw new BackendControlException("Conditional fault response omitted its entity ETag.");
        if (!JsonNode.DeepEquals(patched.Body["properties"], faultProperties))
            throw new BackendControlException("Conditional fault response changed fields other than the approved URL.");
    }

    public async Task RestoreAsync(CancellationToken token)
    {
        if (!MutationAttempted || original is null || faultProperties is null)
            return;
        Owned((await management.SendAsync(options.GroupId, "2024-03-01", token)).Body, options.GroupId);
        var current = await ReadAsync(original.Id, token);
        if (current.ETag == original.ETag && JsonNode.DeepEquals(current.Properties, original.Properties))
        {
            if (!applyRejected)
                throw new BackendControlException("RESTORATION UNVERIFIED: original configuration is currently visible, but an ambiguous apply may still be pending. Retained fault-recovery.json for manual reconciliation.");
            File.Delete(options.RecoveryPath);
            return;
        }
        if (!JsonNode.DeepEquals(current.Properties, faultProperties) ||
            (appliedETag is not null && current.ETag != appliedETag))
            throw new BackendControlException("RESTORATION FAILED: concurrent backend change; refusing blind overwrite. Retained fault-recovery.json for manual reconciliation.");
        _ = await management.SendAsync(original.Id, BackendVersion, token,
            UrlOnly(Text(original.Properties["url"])!), current.ETag);
        var restored = await ReadAsync(original.Id, token);
        if (!JsonNode.DeepEquals(restored.Properties, original.Properties))
            throw new BackendControlException("RESTORATION FAILED: original backend configuration was not verified. Retained fault-recovery.json.");
        File.Delete(options.RecoveryPath);
    }

    private async Task<Snapshot> ReadAsync(string id, CancellationToken token)
    {
        var response = await management.SendAsync(id, BackendVersion, token);
        if (!string.Equals(Text(response.Body["id"]), id, StringComparison.OrdinalIgnoreCase) ||
            response.Body["properties"] is not JsonObject properties ||
            string.IsNullOrWhiteSpace(response.ETag) || response.ETag == "*")
            throw new BackendControlException("Backend identity/properties/concrete ETag missing; no unconditional mutation is permitted.");
        return new Snapshot(id, (JsonObject)properties.DeepClone(), response.ETag);
    }

    private void Owned(JsonObject resource, string id)
    {
        if (!string.Equals(Text(resource["id"]), id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Text(resource["location"])?.Replace(" ", "", StringComparison.Ordinal),
                "westus3", StringComparison.OrdinalIgnoreCase) ||
            Text(resource["tags"]?["lab-owner"]) != options.Owner ||
            Text(resource["tags"]?["azd-env-name"]) != options.Environment ||
            Text(resource["tags"]?["purpose"]) != "apim-responses-affinity-lab")
            throw new BackendControlException("Resource identity/location/ownership mismatch; refusing a fault on unowned resources.");
    }

    private static string? Text(JsonNode? value) =>
        value is JsonValue text && text.TryGetValue<string>(out var result) ? result : null;

    private static bool Empty(JsonNode? value) =>
        value is null || value is JsonObject obj && obj.All(property => Empty(property.Value)) ||
        value is JsonArray array && array.Count == 0;

    private static JsonObject UrlOnly(string url) => new() { ["properties"] = new JsonObject { ["url"] = url } };
}
