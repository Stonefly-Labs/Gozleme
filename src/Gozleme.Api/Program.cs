using Gozleme.Api;
using Gozleme.Api.Identity;
using Gozleme.Api.Persistence;

switch (args.FirstOrDefault())
{
    case "migrate":
        return await MigrationRunner.RunAsync(args[1..]);
    case var command when HandleOperatorCommands.Handles(command):
        // Operator-only handle namespace: handle-reserve | handle-release | handle-reclaim (GOZ-0005, owner login).
        return await HandleOperatorCommands.RunAsync(command!, args[1..], Console.Out, Console.Error);
    case var command when OperatorCommands.Handles(command):
        // Operator-only account maintenance: seed | reset-password | unlock (never exposed over HTTP).
        return await OperatorCommands.RunAsync(command!, args[1..], Console.In, Console.Out, Console.Error);
    case "healthcheck":
        // Container probe without shipping curl in the runtime image: `healthcheck [live|ready]`.
        return await ContainerHealthCheck.RunAsync(args.ElementAtOrDefault(1) ?? "live");
}

var app = GozlemeApp.Build(args);
try
{
    await app.RunAsync();
}
catch (Exception ex) when (Environment.ProcessId == 1)
{
    // The host has logged the cause (e.g. a registry origin mismatch, GOZ-0005 FR-003). As PID 1 in a container
    // an unhandled exception's abort() is never delivered and the process spins, so exit with a failure code.
    await Console.Error.WriteLineAsync($"Host failed: {ex.GetType().Name}");
    return 1;
}

return 0;

/// <summary>Entry point marker for WebApplicationFactory-based tests.</summary>
public partial class Program;
