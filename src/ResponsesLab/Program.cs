using ResponsesLab;

if (args is ["--help"])
{
    Console.WriteLine("""
        ResponsesLab --allow-requests [--scenario both|stateful|stateless|affinity-off]
          [--client http|foundry-sdk|agent-framework] [--cookie-profile manual|sdk-default|session-scoped]
        ResponsesLab --allow-requests --scenario failure --allow-faults --confirm-resource-group <exact-group>
        Requires approved LAB_RESPONSES_ENDPOINT, LAB_MODEL_DEPLOYMENT and
        process-only LAB_APIM_SUBSCRIPTION_KEY. No calls without --allow-requests.
        Configuration, bounds, evidence and wrapper commands: src\ResponsesLab\README.md
        Failure requires the guarded wrapper, separate live authorization, and exact owned-group confirmation.
        affinity-off requires the deployed pool's exported affinity setting to be false.
        Default: http/manual. SDK modes default to session-scoped cookies.
        sdk-default leaves the pinned SDK's cookie transport unchanged; it is not an isolation guarantee.
        SDK modes support healthy and affinity-off scenarios, not controlled failure.
        """);
    return 0;
}

try
{
    var invocation = Invocation.Parse(args);
    var scenario = invocation.Scenario;
    var selection = invocation.Client;
    var faultConfirmation = invocation.FaultConfirmation;
    var options = LabOptions.Load(Environment.GetEnvironmentVariable, scenario == "failure");
    if (scenario == "failure" && !options.SessionAffinityEnabled)
        throw new ConfigurationException("The controlled failure scenario requires LAB_SESSION_AFFINITY_ENABLED=true.");
    var faultOptions = faultConfirmation is null ? null : FaultOptions.Load(Environment.GetEnvironmentVariable, faultConfirmation);
    using var cancelled = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancelled.Cancel();
    };
    Console.CancelKeyPress += cancel;
    try
    {
        using var http = ResponsesClient.CreateHttpClient();
        if (faultOptions is not null)
        {
            using var managementHttp = ResponsesClient.CreateHttpClient();
            var management = new ManagementClient(managementHttp, faultOptions.GroupId,
                token => AzureCliToken.GetAsync(faultOptions.Subscription, token));
            var failure = await new FailureRunner(new ResponsesClient(http, options), options, faultOptions,
                new BackendFault(management, faultOptions, options.Endpoint), Console.Out, new FailureClock())
                .RunAsync(cancelled.Token);
            failure.Write(Console.Out);
            return failure.ExitCode;
        }
        using var sdk = selection.IsSdk ? new SdkResponseSender(options, selection) : null;
        Console.WriteLine($"Client: {selection.Mode}; cookies: {selection.CookieProfile}; retries=0.");
        if (selection.IsSdk)
            Console.WriteLine($"Pinned SDKs: {ClientSelection.Versions}. Cookie fingerprints observe response/session jars, not transport-injected outbound headers.");
        var report = await new LabRunner(new ResponsesClient(http, options, sdk, selection), options, Console.Out)
            .RunAsync(scenario, cancelled.Token);
        report.Write(Console.Out);
        return report.ExitCode;
    }
    finally
    {
        Console.CancelKeyPress -= cancel;
    }
}
catch (ConfigurationException error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}
