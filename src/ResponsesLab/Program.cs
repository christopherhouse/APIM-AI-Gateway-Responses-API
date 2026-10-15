using ResponsesLab;

if (args is ["--help"])
{
    Console.WriteLine("""
        ResponsesLab --allow-requests [--scenario both|stateful|stateless]
        ResponsesLab --allow-requests --scenario failure --allow-faults --confirm-resource-group <exact-group>
        Requires approved LAB_RESPONSES_ENDPOINT, LAB_MODEL_DEPLOYMENT and
        process-only LAB_APIM_SUBSCRIPTION_KEY. No calls without --allow-requests.
        Configuration, bounds, evidence and wrapper commands: src\ResponsesLab\README.md
        Failure requires the guarded wrapper, separate live authorization, and exact owned-group confirmation.
        """);
    return 0;
}

var scenario = "both";
string? faultConfirmation = null;
if (args is ["--allow-requests", "--scenario", "failure", "--allow-faults", "--confirm-resource-group", var group])
{
    scenario = "failure";
    faultConfirmation = group;
}
else if (args is ["--allow-requests", "--scenario", var selected] &&
    selected is "both" or "stateful" or "stateless")
    scenario = selected;
else if (args is not ["--allow-requests"])
{
    Console.Error.WriteLine("Usage: ResponsesLab --allow-requests [--scenario both|stateful|stateless]. See --help for the separately authorized failure scenario.");
    return 2;
}

try
{
    var options = LabOptions.Load(Environment.GetEnvironmentVariable, scenario == "failure");
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
        var report = await new LabRunner(new ResponsesClient(http, options), options, Console.Out)
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
