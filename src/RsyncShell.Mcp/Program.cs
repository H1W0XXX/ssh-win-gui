using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RsyncShell.Mcp;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, cancellationToken) =>
    {
        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The SDK replaces unknown exceptions with a generic sentence. These tools
            // raise messages written for the model (and already stripped of key paths),
            // so pass them on: "oldString was not found" is what lets it recover.
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = error.Message }],
            };
        }
    }))
    .WithTools<SshTools>()
    .WithTools<FileTools>()
    .WithTools<JobTools>();

try
{
    await builder.Build().RunAsync();
}
finally
{
    SshConnectionPool.Shared.Dispose();
}
