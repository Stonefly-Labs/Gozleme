using Gozleme.Api;
using Gozleme.Api.Persistence;

switch (args.FirstOrDefault())
{
    case "migrate":
        return await MigrationRunner.RunAsync(args[1..]);
    case "healthcheck":
        // Container probe without shipping curl in the runtime image: `healthcheck [live|ready]`.
        return await ContainerHealthCheck.RunAsync(args.ElementAtOrDefault(1) ?? "live");
}

var app = GozlemeApp.Build(args);
await app.RunAsync();
return 0;

/// <summary>Entry point marker for WebApplicationFactory-based tests.</summary>
public partial class Program;
