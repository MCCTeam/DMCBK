# Session counter example

This source package accompanies the [first plugin guide](../../../docs/plugins/getting-started.md). It counts session starts and saves the count.

## Run its verification program

Run this command from the DMCBK repository root:

```sh
dotnet run --project samples/PluginAuthoring/VerifyCounter -- samples/PluginAuthoring/SessionCounter
```

The expected final line is `PASS: SessionCounter loaded, received a session and saved its count.`

The verifier compiles the actual entry through the runtime. It checks the loaded state, runs one in-memory protocol session, waits for the callback, and checks the storage file.

This test needs no Minecraft account or server. It does not prove live-server gameplay.

## Load it in your host

1. Attach Commands.
2. Attach Plugins.
3. Set an explicit plugin-data root.
4. Add this package folder to `DevelopmentFolders`.
5. Load the plugin.
6. Check its individual loaded state.
7. Start the client.

User data belongs under the host-selected plugin root. Keep it separate from the source package.

For commands, settings, Beacon, compiled packaging, and restart tests, use the [Session Journal tutorial](../../../docs/plugins/tutorial/index.md).
