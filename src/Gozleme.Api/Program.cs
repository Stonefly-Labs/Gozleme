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
await app.RunAsync();
return 0;

/// <summary>Entry point marker for WebApplicationFactory-based tests.</summary>
public partial class Program;
