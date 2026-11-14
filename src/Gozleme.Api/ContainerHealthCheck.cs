namespace Gozleme.Api;

/// <summary>Calls the local health endpoint and maps the result to a process exit code.</summary>
internal static class ContainerHealthCheck
{
    public static async Task<int> RunAsync(string probe)
    {
        if (probe is not ("live" or "ready"))
        {
            return 2;
        }

        var port = Environment.GetEnvironmentVariable("GOZLEME_HEALTHCHECK_PORT") ?? "8080";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health/{probe}"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
