# Local web backend

This sample hosts one DMCBK client in ASP.NET Core. It exposes local HTTP operations. The backend owns the Minecraft TCP connection.

It is a development example. It has no HTTP caller authentication or authorization. Keep it bound to loopback.

## Start the host

From the repository root:

```bash
dotnet run --project samples/WebBackend -- --urls http://127.0.0.1:5080
```

The default Minecraft account is the offline username `WebBackend`. The default server is `localhost:25565`.

ASP.NET Core configuration accepts environment variables:

```bash
Client__Username=WebBot Client__Host=localhost Client__Port=25565 dotnet run --project samples/WebBackend -- --urls http://127.0.0.1:5080
```

In PowerShell, set each variable through `$env:Client__Username`, `$env:Client__Host` and `$env:Client__Port`.

## Check the API

Read status before connecting:

```bash
curl http://127.0.0.1:5080/client
```

Run a local command without a Minecraft server:

```bash
curl -X POST http://127.0.0.1:5080/client/commands -H 'Content-Type: application/json' -d '{"command":"set example=web"}'
```

The command response contains its result and captured body output.

Connect after starting your offline-mode Minecraft Java server:

```bash
curl -X POST http://127.0.0.1:5080/client/connect
```

Send chat after connection:

```bash
curl -X POST http://127.0.0.1:5080/client/chat -H 'Content-Type: application/json' -d '{"message":"Hello from the web host"}'
```

Stop the connection:

```bash
curl -X POST http://127.0.0.1:5080/client/disconnect
```

Stop the backend with Ctrl+C. Its finalization stops the client, and the service container disposes it.

## Endpoints

| Method and path | Behavior |
| --- | --- |
| GET `/client` | Read application identity, capabilities and connection status |
| POST `/client/connect` | Start the configured connection |
| POST `/client/disconnect` | Stop the connection |
| POST `/client/chat` | Send the JSON `message` to the current session |
| POST `/client/commands` | Dispatch the JSON `command` as a local command |
| GET `/client/scripts` | Discover scripts from an attached configuration folder |

The sample attaches Beacon but has no configuration folder. Its script list is therefore empty. To discover files, attach Configuration with an explicit folder. See [Chapter 4](../../docs/client/04-host.md).

A failed connection returns an error response. A completed chat send is not proof that the server displayed the message. The server can filter it.

## Production decisions

A production host needs request authorization, client ownership, operation serialization and bounded request limits. Do not let one user control another user's Minecraft session.

This sample intentionally owns one client. Creating a client per HTTP request loses session continuity. A multi-user host should keep separate client lifetimes and data roots.

A browser cannot directly open the TCP connection used here. It communicates with this backend. DMCBK does not include a browser relay or a complete web application.
