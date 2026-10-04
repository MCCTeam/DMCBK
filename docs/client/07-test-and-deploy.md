# Chapter 7: Test and distribute the client

A successful build checks API usage. A protocol test checks more: login, events, snapshots and outgoing packets. A real server test checks behavior that the fake server does not model.

## Run the guide's executable check

From the DMCBK repository root:

```bash
dotnet run --project samples/ClientGuide -- --self-test
```

The expected output is:

```text
PASS: local command before connection
PASS: handshake, login, configuration and play
PASS: incoming chat event
PASS: health snapshot and placement distinction
PASS: inventory and entity snapshots
PASS: outgoing chat packet
PASS: clean shutdown
```

The check uses `DMCBK.Testing.PluginTestHost`. Despite its name, that host also exposes a client for game API tests. No plugin needs to be installed for this check.

The host drives a real Minecraft packet exchange over an in-memory pipe. The check sends a system-chat packet and a health packet. It checks the client snapshot and decodes the outgoing chat text.

The fake server does not send a complete world or player-placement sequence. The check explicitly expects `HasSpawned == false`. It cannot prove terrain, navigation, real authentication or interoperability with every server implementation.

## Inspect the test

[ClientChecks.cs](../../samples/ClientGuide/ClientChecks.cs) contains every assertion. A failed assertion throws and makes the process fail. Each asynchronous wait uses the host's bounded token.

Use this pattern for your own client tests:

1. Create a test host.
2. Register observers before the session starts.
3. Run a session.
4. Send the packet that represents your scenario.
5. Await the resulting event or state change.
6. Check an observable result.
7. Dispose the host.

A packet send can complete before the application observes the resulting event. Await the result. Do not replace that wait with an arbitrary long sleep.

## Check a real server

1. Start a server that you control.
2. Run the ClientGuide real connection mode.
3. Check player placement and chat delivery.
4. Check the process exit code.
5. Repeat with your selected authentication flow.
6. Check explicit reconnect in your application.
7. Check shutdown during connection and during play.

The complete sample returns `0` after its action succeeds, `1` after connection or action failure, and `2` for invalid arguments.

The protocol check runs without a network server. Real-server results depend on your server, account and selected version. Do not report one kind of check as the other.

## Publish an application

An ordinary managed host can publish a framework-dependent application:

```bash
dotnet publish -c Release -o publish
```

The target computer needs the matching .NET runtime. A self-contained publish includes the runtime:

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -o publish/linux-x64
```

Use the RID for the target OS and process architecture. Test the published application on that target.

DMCBK does not currently promise Native AOT support. Source plugins need accessible reference assemblies for Roslyn. Do not enable trimming or single-file packaging without checking the selected modules and supplying their required reference assets.

## Use a web backend

The [WebBackend sample](../../samples/WebBackend/README.md) hosts one client behind local HTTP endpoints. The backend owns the Minecraft TCP connection. The browser communicates with the backend.

A browser cannot directly open the TCP connection used by this client. DMCBK does not include a browser-to-Minecraft relay.

The sample has no account authentication or authorization for its HTTP callers. Bind it to loopback for local tests. Add request authorization, per-user ownership and operation limits before exposing game control remotely.

## Move to desktop or mobile

The same Core client can run behind a supported .NET UI host. Implement presentation and authentication through host interfaces. Dispatch view updates to the UI thread.

Mobile deployment depends on platform runtime and dynamic-loading restrictions. Test on the actual device. Current source compilation and plugin loading cannot run unchanged on a platform that requires Native AOT.

DMCBK includes no complete desktop or mobile application. The library supplies the client services. Your application supplies screen navigation, application lifetime and platform storage.

Return to the [client guide index](index.md) or continue with [plugin development](../plugins/index.md) and [Beacon scripting](../beacon/index.md).
