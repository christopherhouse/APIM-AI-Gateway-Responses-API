using ResponsesLab;

if (args is ["--help"])
{
    Console.WriteLine("""
        ResponsesLab --allow-requests [--scenario both|stateful|stateless]
        Requires approved LAB_RESPONSES_ENDPOINT, LAB_MODEL_DEPLOYMENT and
        process-only LAB_APIM_SUBSCRIPTION_KEY. No calls without --allow-requests.
        Configuration, bounds, evidence and wrapper commands: src\ResponsesLab\README.md
        Task 005 supports healthy stateful/stateless checks only, not fault injection.
        """);
    return 0;
}

var scenario = "both";
if (args is ["--allow-requests", "--scenario", var selected] &&
    selected is "both" or "stateful" or "stateless")
    scenario = selected;
else if (args is not ["--allow-requests"])
{
    Console.Error.WriteLine("Usage: ResponsesLab --allow-requests [--scenario both|stateful|stateless]. Live inference requires separate authorization.");
    return 2;
}

try
{
    var options = LabOptions.Load(Environment.GetEnvironmentVariable);
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
