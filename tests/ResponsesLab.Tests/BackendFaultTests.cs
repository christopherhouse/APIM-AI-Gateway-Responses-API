using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ResponsesLab;

namespace ResponsesLab.Tests;

public sealed class BackendFaultTests
{
    [Fact]
    public async Task Fault_and_restore_patch_only_the_same_host_url_with_exact_entity_versions()
    {
        using var f = new Fixture();
        var original = f.Handler.Resources[f.BackendA].DeepClone();
        await f.Fault.PrepareAsync(default);
        await f.Fault.ApplyAsync("backend-a", default);

        Assert.True(File.Exists(f.Options.RecoveryPath));
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(f.Options.RecoveryPath))!;
        Assert.Equal("https://ai-a.example.invalid/openai/v1", journal["originalUrl"]!.GetValue<string>());
        Assert.Equal("https://ai-a.example.invalid/task006-intentionally-missing/openai/v1", journal["faultUrl"]!.GetValue<string>());
        Assert.Null(journal["properties"]);
        Assert.DoesNotContain("synthetic-management-token", journal.ToJsonString());
        await f.Fault.RestoreAsync(default);

        Assert.False(File.Exists(f.Options.RecoveryPath));
        Assert.True(JsonNode.DeepEquals(original, f.Handler.Resources[f.BackendA]));
        Assert.Equal(2, f.Handler.Patches.Count);
        Assert.Equal("\"v1\"", f.Handler.Patches[0].ETag);
        Assert.Equal("\"v2\"", f.Handler.Patches[1].ETag);
        Assert.All(f.Handler.Patches, patch =>
        {
            Assert.Equal(f.BackendA, patch.Path);
            Assert.Single(patch.Body);
            Assert.Single(patch.Body["properties"]!.AsObject());
            Assert.Equal("url", patch.Body["properties"]!.AsObject().Single().Key);
        });
    }

    [Theory]
    [InlineData("West US 3", true)]
    [InlineData("East US", false)]
    [InlineData(null, false)]
    public async Task Region_contract_accepts_ARM_display_form_but_rejects_other_or_missing_regions(string? location, bool accepted)
    {
        using var f = new Fixture();
        f.Handler.Resources[f.Options.ApimId]["location"] = location;
        if (accepted)
            await f.Fault.PrepareAsync(default);
        else
            await Assert.ThrowsAsync<BackendControlException>(() => f.Fault.PrepareAsync(default));
        Assert.Empty(f.Handler.Patches);
        Assert.False(f.Fault.MutationAttempted);
    }

    [Fact]
    public async Task Management_accepts_APIM_UTF8_BOM_through_prepare_conditional_fault_and_restore()
    {
        using var f = new Fixture();
        f.Handler.PrefixUtf8Bom = true;
        await f.Fault.PrepareAsync(default);
        await f.Fault.ApplyAsync("backend-a", default);
        await f.Fault.RestoreAsync(default);
        Assert.Equal(2, f.Handler.Patches.Count);
        Assert.False(File.Exists(f.Options.RecoveryPath));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("host")]
    [InlineData("tls")]
    [InlineData("breaker")]
    [InlineData("pool")]
    [InlineData("credential")]
    [InlineData("etag")]
    public async Task Changed_ownership_host_security_or_pool_contract_prevents_mutation(string change)
    {
        using var f = new Fixture();
        var backend = f.Handler.Resources[f.BackendA]["properties"]!;
        switch (change)
        {
            case "owner": f.Handler.Resources[f.Options.GroupId]["tags"]!["lab-owner"] = "foreign"; break;
            case "host": backend["url"] = "https://foreign.example.invalid/openai/v1"; break;
            case "tls": backend["tls"]!["validateCertificateChain"] = false; break;
            case "breaker": backend["circuitBreaker"]!["rules"]![0]!["tripDuration"] = "PT5M"; break;
            case "pool": f.Handler.Resources[f.Pool]["properties"]!["pool"]!["services"]![1]!["id"] = f.BackendA; break;
            case "credential": backend["credentials"] = new JsonObject { ["header"] = new JsonObject { ["api-key"] = new JsonArray("synthetic-private-key") } }; break;
            case "etag": f.Handler.ETags.Remove(f.BackendA); break;
        }
        await Assert.ThrowsAsync<BackendControlException>(() => f.Fault.PrepareAsync(default));
        Assert.Empty(f.Handler.Patches);
        Assert.False(f.Fault.MutationAttempted);
    }

    [Theory]
    [InlineData("backend")]
    [InlineData("pool")]
    public async Task Concurrent_configuration_change_before_apply_refuses_the_write(string target)
    {
        using var f = new Fixture();
        await f.Fault.PrepareAsync(default);
        f.Handler.ETags[target == "backend" ? f.BackendA : f.Pool] = "\"external-v2\"";
        await Assert.ThrowsAsync<BackendControlException>(() => f.Fault.ApplyAsync("backend-a", default));
        Assert.Empty(f.Handler.Patches);
        Assert.False(File.Exists(f.Options.RecoveryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_change_while_faulted_never_gets_blindly_overwritten(bool propertiesChanged)
    {
        using var f = new Fixture();
        await f.Fault.PrepareAsync(default);
        await f.Fault.ApplyAsync("backend-a", default);
        f.Handler.ETags[f.BackendA] = "\"external-v3\"";
        if (propertiesChanged)
            f.Handler.Resources[f.BackendA]["properties"]!["description"] = "external change";
        var error = await Assert.ThrowsAsync<BackendControlException>(() => f.Fault.RestoreAsync(default));
        Assert.Contains("RESTORATION FAILED", error.Message);
        Assert.Single(f.Handler.Patches);
        Assert.True(File.Exists(f.Options.RecoveryPath));
    }

    [Fact]
    public async Task Failed_conditional_patch_never_falls_back_to_wildcard_or_put()
    {
        using var f = new Fixture();
        await f.Fault.PrepareAsync(default);
        f.Handler.RejectPatch = true;
        var error = await Assert.ThrowsAsync<BackendControlException>(() => f.Fault.ApplyAsync("backend-a", default));
        Assert.Contains("HTTP 412", error.Message);
        Assert.True(f.Fault.MutationAttempted);
        await f.Fault.RestoreAsync(default);
        Assert.Single(f.Handler.Patches);
        Assert.False(File.Exists(f.Options.RecoveryPath));
    }

    [Fact]
    public async Task Ambiguous_transport_failure_after_applied_write_can_be_conditionally_restored()
    {
        using var f = new Fixture();
        await f.Fault.PrepareAsync(default);
        f.Handler.ThrowAfterFirstPatch = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Fault.ApplyAsync("backend-a", default));
        Assert.True(File.Exists(f.Options.RecoveryPath));
        await f.Fault.RestoreAsync(default);
        Assert.Equal(2, f.Handler.Patches.Count);
        Assert.False(File.Exists(f.Options.RecoveryPath));
        Assert.Equal("https://ai-a.example.invalid/openai/v1", f.Handler.Resources[f.BackendA]["properties"]!["url"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_or_async_apply_does_not_claim_cleanup_just_because_an_old_get_still_looks_unchanged(bool asyncAccepted)
    {
        using var f = new Fixture();
        await f.Fault.PrepareAsync(default);
        f.Handler.ThrowBeforeFirstPatch = !asyncAccepted;
        f.Handler.AcceptAsynchronously = asyncAccepted;
        if (asyncAccepted)
            await Assert.ThrowsAsync<BackendControlException>(() => f.Fault.ApplyAsync("backend-a", default));
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => f.Fault.ApplyAsync("backend-a", default));
        var error = await Assert.ThrowsAsync<BackendControlException>(() => f.Fault.RestoreAsync(default));
        Assert.Contains("RESTORATION UNVERIFIED", error.Message);
        Assert.True(File.Exists(f.Options.RecoveryPath));
        Assert.Single(f.Handler.Patches);
        Assert.Equal("https://ai-a.example.invalid/openai/v1", f.Handler.Resources[f.BackendA]["properties"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Missing_apply_etag_is_a_failure_but_does_not_skip_restoration()
    {
        using var f = new Fixture();
        await f.Fault.PrepareAsync(default);
        f.Handler.OmitPatchETag = true;
        await Assert.ThrowsAsync<BackendControlException>(() => f.Fault.ApplyAsync("backend-a", default));
        await f.Fault.RestoreAsync(default);
        Assert.False(File.Exists(f.Options.RecoveryPath));
    }

    [Fact]
    public async Task Unverified_restoration_retains_the_manual_recovery_journal()
    {
        using var f = new Fixture();
        await f.Fault.PrepareAsync(default);
        await f.Fault.ApplyAsync("backend-a", default);
        f.Handler.ChangeDuringRestore = true;
        await Assert.ThrowsAsync<BackendControlException>(() => f.Fault.RestoreAsync(default));
        Assert.True(File.Exists(f.Options.RecoveryPath));
    }

    [Fact]
    public async Task Ownership_change_while_faulted_is_a_visible_restore_failure_without_a_second_write()
    {
        using var f = new Fixture();
        await f.Fault.PrepareAsync(default);
        await f.Fault.ApplyAsync("backend-a", default);
        f.Handler.Resources[f.Options.GroupId]["tags"]!["lab-owner"] = "foreign";
        await Assert.ThrowsAsync<BackendControlException>(() => f.Fault.RestoreAsync(default));
        Assert.Single(f.Handler.Patches);
        Assert.True(File.Exists(f.Options.RecoveryPath));
    }

    [Fact]
    public async Task Existing_unresolved_journal_blocks_a_new_run_before_any_management_call()
    {
        using var f = new Fixture();
        await File.WriteAllTextAsync(f.Options.RecoveryPath, "synthetic unresolved journal");
        await Assert.ThrowsAsync<BackendControlException>(() => f.Fault.PrepareAsync(default));
        Assert.Equal(0, f.Handler.Reads);
        Assert.Empty(f.Handler.Patches);
    }

    [Fact]
    public async Task Management_client_rejects_outside_scope_and_unconditional_mutation()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<BackendControlException>(() => f.Management.SendAsync(
            "/subscriptions/other/resourceGroups/foreign", "2024-03-01", default));
        await Assert.ThrowsAsync<BackendControlException>(() => f.Management.SendAsync(
            f.BackendA, "2025-03-01-preview", default, new JsonObject(), "*"));
        Assert.Equal(0, f.Handler.Reads);
        Assert.Empty(f.Handler.Patches);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("backend")]
    [InlineData("subscription")]
    [InlineData("journal")]
    [InlineData("interval")]
    [InlineData("same-foundry")]
    public void Failure_configuration_rejects_unsafe_targets_before_any_io(string change)
    {
        using var f = new Fixture();
        var values = f.Environment();
        var confirmation = f.Options.Group;
        switch (change)
        {
            case "confirm": confirmation = "other"; break;
            case "backend": values["LAB_BACKEND_A_ID"] = f.Options.ApimId + "/backends/arbitrary"; break;
            case "subscription": values["AZURE_SUBSCRIPTION_ID"] = "not-a-uuid"; break;
            case "journal": values["LAB_FAULT_RECOVERY_PATH"] = "relative.json"; break;
            case "interval": values["LAB_FAILURE_PROBE_INTERVAL_SECONDS"] = "10"; break;
            case "same-foundry": values["LAB_FOUNDRY_B_RESOURCE_ID"] = f.Options.FoundryAId; break;
        }
        Assert.Throws<ConfigurationException>(() => FaultOptions.Load(name => values.GetValueOrDefault(name), confirmation));
    }

    [Fact]
    public void Valid_failure_configuration_requires_both_explicit_mode_and_matching_owned_target()
    {
        using var f = new Fixture();
        var values = f.Environment();
        values["LAB_RESPONSES_ENDPOINT"] = "https://offline.example.invalid/openai/v1/responses";
        values["LAB_MODEL_DEPLOYMENT"] = "responses-chat";
        values["LAB_APIM_SUBSCRIPTION_KEY"] = "synthetic-key-not-real";
        values["LAB_ENABLE_FAULTS"] = "true";
        var loaded = FaultOptions.Load(name => values.GetValueOrDefault(name), f.Options.Group);
        Assert.Equal(f.Options.ApimId, loaded.ApimId);
        Assert.Equal(f.Options.RecoveryPath, loaded.RecoveryPath);
        Assert.NotNull(LabOptions.Load(name => values.GetValueOrDefault(name), allowFaults: true));
        Assert.Throws<ConfigurationException>(() => LabOptions.Load(name => values.GetValueOrDefault(name)));
        values["LAB_ENABLE_FAULTS"] = "false";
        Assert.Throws<ConfigurationException>(() => LabOptions.Load(name => values.GetValueOrDefault(name), allowFaults: true));
        values["AZURE_RESOURCE_GROUP"] += "\n";
        Assert.Throws<ConfigurationException>(() => FaultOptions.Load(name => values.GetValueOrDefault(name), values["AZURE_RESOURCE_GROUP"]!));
    }

    private sealed record Patch(string Path, JsonObject Body, string ETag);

    private sealed class Handler : HttpMessageHandler
    {
        internal Dictionary<string, JsonObject> Resources { get; } = [];
        internal Dictionary<string, string> ETags { get; } = [];
        internal List<Patch> Patches { get; } = [];
        internal int Reads { get; private set; }
        internal bool RejectPatch { get; set; }
        internal bool ThrowAfterFirstPatch { get; set; }
        internal bool ThrowBeforeFirstPatch { get; set; }
        internal bool AcceptAsynchronously { get; set; }
        internal bool OmitPatchETag { get; set; }
        internal bool ChangeDuringRestore { get; set; }
        internal bool PrefixUtf8Bom { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("management.azure.com", request.RequestUri!.Host);
            Assert.Equal("synthetic-management-token", request.Headers.Authorization!.Parameter);
            Assert.False(request.Headers.Contains("Ocp-Apim-Subscription-Key"));
            var path = request.RequestUri.AbsolutePath;
            var resource = Resources[path];
            if (request.Method == HttpMethod.Patch)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                var etag = request.Headers.IfMatch.Single().ToString();
                Patches.Add(new Patch(path, body, etag));
                if (RejectPatch || etag != ETags[path])
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                if (Patches.Count == 1 && ThrowBeforeFirstPatch)
                    throw new HttpRequestException("Synthetic response lost before the mutation outcome is known");
                if (Patches.Count == 1 && AcceptAsynchronously)
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                resource["properties"]!["url"] = body["properties"]!["url"]!.DeepClone();
                ETags[path] = $"\"v{Patches.Count + 1}\"";
                if (Patches.Count == 1 && ThrowAfterFirstPatch)
                    throw new HttpRequestException("Synthetic lost response after applied mutation");
                if (Patches.Count == 2 && ChangeDuringRestore)
                    resource["properties"]!["description"] = "external changed property";
            }
            else
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Reads++;
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent((PrefixUtf8Bom ? "\uFEFF" : "") + resource.ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (ETags.TryGetValue(path, out var version) && !(request.Method == HttpMethod.Patch && OmitPatchETag))
                response.Headers.ETag = new EntityTagHeaderValue(version);
            return response;
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal FaultOptions Options { get; }
        internal Handler Handler { get; } = new();
        internal ManagementClient Management { get; }
        internal BackendFault Fault { get; }
        internal string BackendA => Options.ApimId + "/backends/backend-a";
        internal string Pool => Options.ApimId + "/backends/responses-pool";
        private readonly HttpClient http;
        private readonly string directory;

        internal Fixture()
        {
            directory = Path.GetFullPath(Path.Combine(System.Environment.CurrentDirectory, "artifacts", "006-tests-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            const string subscription = "11111111-1111-4111-8111-111111111111";
            const string group = "rg-responses-offline";
            var groupId = $"/subscriptions/{subscription}/resourceGroups/{group}";
            Options = new FaultOptions
            {
                Subscription = subscription,
                Group = group,
                Environment = "offline",
                Owner = "22222222-2222-4222-8222-222222222222",
                ApimId = groupId + "/providers/Microsoft.ApiManagement/service/gateway",
                FoundryAId = groupId + "/providers/Microsoft.CognitiveServices/accounts/ai-a",
                FoundryBId = groupId + "/providers/Microsoft.CognitiveServices/accounts/ai-b",
                RecoveryPath = Path.Combine(directory, "fault-recovery.json")
            };
            JsonObject Owned(string id) => new()
            {
                ["id"] = id,
                ["location"] = "westus3",
                ["tags"] = new JsonObject
                {
                    ["lab-owner"] = Options.Owner,
                    ["azd-env-name"] = "offline",
                    ["purpose"] = "apim-responses-affinity-lab"
                }
            };
            Handler.Resources[groupId] = Owned(groupId);
            var gateway = Owned(Options.ApimId);
            gateway["sku"] = new JsonObject { ["name"] = "Developer", ["capacity"] = 1 };
            gateway["properties"] = new JsonObject { ["gatewayUrl"] = "https://offline.example.invalid" };
            Handler.Resources[Options.ApimId] = gateway;
            foreach (var suffix in new[] { "a", "b" })
            {
                var accountId = suffix == "a" ? Options.FoundryAId : Options.FoundryBId;
                var account = Owned(accountId);
                account["kind"] = "AIServices";
                account["properties"] = new JsonObject
                {
                    ["endpoints"] = new JsonObject { ["OpenAI Language Model Instance API"] = $"https://ai-{suffix}.example.invalid/" }
                };
                Handler.Resources[accountId] = account;
                var backendId = Options.ApimId + "/backends/backend-" + suffix;
                Handler.Resources[backendId] = new JsonObject
                {
                    ["id"] = backendId,
                    ["properties"] = new JsonObject
                    {
                        ["type"] = "Single",
                        ["protocol"] = "http",
                        ["url"] = $"https://ai-{suffix}.example.invalid/openai/v1",
                        ["tls"] = new JsonObject { ["validateCertificateChain"] = true, ["validateCertificateName"] = true },
                        ["circuitBreaker"] = JsonNode.Parse("""
                            {"rules":[{"name":"responses-breaker","tripDuration":"PT30S","acceptRetryAfter":true,
                            "failureCondition":{"count":1,"interval":"PT1M","statusCodeRanges":[{"min":404,"max":404},{"min":429,"max":429},{"min":500,"max":599}]}}]}
                            """)
                    }
                };
            }
            Handler.Resources[Pool] = new JsonObject
            {
                ["id"] = Pool,
                ["properties"] = new JsonObject
                {
                    ["type"] = "Pool",
                    ["pool"] = new JsonObject
                    {
                        ["services"] = new JsonArray(new[] { "a", "b" }.Select(suffix => (JsonNode)new JsonObject
                        {
                            ["id"] = Options.ApimId + "/backends/backend-" + suffix,
                            ["priority"] = 1,
                            ["weight"] = 1
                        }).ToArray()),
                        ["sessionAffinity"] = new JsonObject
                        {
                            ["sessionId"] = new JsonObject { ["name"] = "APIM-Backend-Affinity", ["source"] = "cookie" }
                        }
                    }
                }
            };
            foreach (var id in Handler.Resources.Keys)
                Handler.ETags[id] = "\"v1\"";
            http = new HttpClient(Handler);
            Management = new ManagementClient(http, Options.GroupId, _ => Task.FromResult("synthetic-management-token"));
            Fault = new BackendFault(Management, Options, new Uri("https://offline.example.invalid/openai/v1/responses"));
        }

        internal Dictionary<string, string?> Environment() => new()
        {
            ["AZURE_SUBSCRIPTION_ID"] = Options.Subscription,
            ["AZURE_RESOURCE_GROUP"] = Options.Group,
            ["AZURE_ENV_NAME"] = Options.Environment,
            ["LAB_OWNER_ID"] = Options.Owner,
            ["LAB_APIM_RESOURCE_ID"] = Options.ApimId,
            ["LAB_BACKEND_A_ID"] = BackendA,
            ["LAB_BACKEND_B_ID"] = Options.ApimId + "/backends/backend-b",
            ["LAB_FOUNDRY_A_RESOURCE_ID"] = Options.FoundryAId,
            ["LAB_FOUNDRY_B_RESOURCE_ID"] = Options.FoundryBId,
            ["LAB_FAULT_RECOVERY_PATH"] = Options.RecoveryPath
        };

        public void Dispose()
        {
            http.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
