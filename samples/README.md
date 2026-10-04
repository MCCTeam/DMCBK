# Hosting samples

Both samples use NuGet packages. They build without MCC, DMCBK or UMPK source references.

## Headless client

The headless sample uses Core. It connects with an offline account, reads player state, sends chat and stops.

1. Pack DMCBK from the repository root.
2. Restore the sample with the local feed.
3. Start an offline Minecraft server.
4. Run the sample with the server address.

```bash
dotnet pack DMCBK.slnx -c Release -o artifacts/packages
dotnet restore samples/HeadlessClient --configfile NuGet.Local.Config
dotnet run --project samples/HeadlessClient --no-restore -- Tester localhost:25565 auto
```

## Web backend

The web backend hosts the Minecraft TCP connection. It adds Commands and Beacon. A browser calls the HTTP endpoints.

1. Restore the sample with the local feed.
2. Run the backend on the local address.
3. Send requests to the endpoints below.

```bash
dotnet restore samples/WebBackend --configfile NuGet.Local.Config
dotnet run --project samples/WebBackend --no-restore -- --urls http://127.0.0.1:5080
```

Set `Client__Host`, `Client__Port` and `Client__Username` to override the defaults.

| Request | Purpose |
| --- | --- |
| `GET /client` | Read status and capabilities |
| `POST /client/connect` | Start the Minecraft connection |
| `POST /client/commands` | Dispatch a command, such as `{ "command": "help" }` |
| `GET /client/scripts` | List available scripts |

Keep the sample bound to localhost during development. Before remote use, add authentication, authorization, request limits and account provisioning.

See [hosting](../docs/hosting.md) for lifecycle and UI integration guidance.
