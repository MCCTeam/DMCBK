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

## Chaptered tutorial samples

| Sample | Purpose | Guide |
| --- | --- | --- |
| [ClientGuide](ClientGuide/README.md) | Build and verify a client, then connect to a private server | [Client chapters](../docs/client/index.md) |
| [PluginAuthoring](PluginAuthoring/README.md) | Build, load and verify the tutorial plugin | [Plugin chapters](../docs/plugins/tutorial/index.md) |
| [Beacon](Beacon/README.md) | Run the tutorial scripts and practical recipes | [Beacon chapters](../docs/beacon/guide/index.md) |
| [MarketplaceGuide](MarketplaceGuide/README.md) | Pack, resolve, install, pin and uninstall a real plugin | [Marketplace reference](../docs/marketplace-v2.md) |

Use the sample's README for exact commands. Offline checks and scripted sessions do not require a real Minecraft server. The client connection mode needs an explicitly selected server.
