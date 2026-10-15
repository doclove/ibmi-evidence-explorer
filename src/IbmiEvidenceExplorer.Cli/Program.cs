using IbmiEvidenceExplorer.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return CliApp.Run(args, Console.Out, Console.Error, Environment.GetEnvironmentVariable, cancellation.Token);
