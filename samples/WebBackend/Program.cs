using DMCBK.Core;
using DMCBK.Core.Commands;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => new ClientBuilder()
    .UseUsername(builder.Configuration["Client:Username"] ?? "WebBackend")
    .UseServer(builder.Configuration["Client:Host"] ?? "localhost",
        ushort.TryParse(builder.Configuration["Client:Port"], out ushort port) ? port : (ushort)25565)
    .UseCommands().UseBeacon()
    .UseApplication(new HostApplication("web-backend-sample", "0.1.0-preview.1", new HashSet<string>()))
    .Build());
var app = builder.Build();
Client client = app.Services.GetRequiredService<Client>();
app.MapGet("/client", () => new { client.Application, client.AvailableCapabilities, client.Status });
app.MapPost("/client/connect", async (CancellationToken cancellationToken) =>
{
    await client.StartAsync(cancellationToken);
    return Results.Ok(new { client.Status });
});
app.MapPost("/client/commands", async (CommandRequest request, CancellationToken cancellationToken) =>
    Results.Ok(await client.Commands.DispatchAsync(request.Command, cancellationToken)));
app.MapGet("/client/scripts", () => client.Scripts.Discover());
app.Run();

internal sealed record CommandRequest(string Command);
