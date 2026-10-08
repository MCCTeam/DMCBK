using DMCBK.Core;
using DMCBK.Core.Commands;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => new ClientBuilder()
    .UseUsername(builder.Configuration["Client:Username"] ?? "WebBackend")
    .UseServer(builder.Configuration["Client:Host"] ?? "localhost",
        ushort.TryParse(builder.Configuration["Client:Port"], out ushort port) ? port : (ushort)25565)
    .UseCommands().UseBeacon()
    .UseApplication(new HostApplication("web-backend-sample", "0.1.0-preview.3", new HashSet<string>()))
    .Build());

await using var app = builder.Build();
Client client = app.Services.GetRequiredService<Client>();

app.MapGet("/client", () => new { client.Application, client.AvailableCapabilities, client.Status });
app.MapPost("/client/connect", async (CancellationToken cancellationToken) =>
{
    try
    {
        await client.StartAsync(cancellationToken);
        return Results.Ok(new { client.Status });
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        return Results.Problem(error.Message, statusCode: StatusCodes.Status409Conflict);
    }
});
app.MapPost("/client/disconnect", async () =>
{
    await client.StopAsync();
    return Results.Ok(new { client.Status });
});
app.MapPost("/client/chat", async (ChatRequest request, CancellationToken cancellationToken) =>
{
    try
    {
        await client.Game.Chat.SendAsync(request.Message, cancellationToken);
        return Results.Ok();
    }
    catch (InvalidOperationException error)
    {
        return Results.Problem(error.Message, statusCode: StatusCodes.Status409Conflict);
    }
});
app.MapPost("/client/commands", async (CommandRequest request, CancellationToken cancellationToken) =>
{
    var execution = await client.Commands.DispatchCapturedAsync(request.Command, cancellationToken);
    return Results.Ok(new { execution.Result, execution.Output });
});
app.MapGet("/client/scripts", () => client.Scripts.Discover());

try
{
    await app.RunAsync();
}
finally
{
    await client.StopAsync();
}

internal sealed record CommandRequest(string Command);
internal sealed record ChatRequest(string Message);
