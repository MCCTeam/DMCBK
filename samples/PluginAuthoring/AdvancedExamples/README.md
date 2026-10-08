# Advanced plugin examples

These examples use NuGet packages for DMCBK and UMPK. The project references connect only the example projects in this directory. No MCC or DMCBK source project is referenced.

Read [the advanced guide](../../../docs/plugins/advanced-examples.md) for the explanation.

## Run the checks

1. Restore DMCBK `0.1.0-preview.3` from your configured feed.
2. Open a terminal in this directory.
3. Run the verifier in Release configuration.

```sh
dotnet run --project Verify/Verify.csproj -c Release
```

Expected output:

```text
PASS exported contracts, services, request/response and notifications
PASS script export, custom event and variable snapshot
PASS session scheduler, packet observer, cancellation and command cleanup
```

The verifier builds the provider, consumer and observer. It stages only their intended plugin payloads in temporary directories. The consumer deliberately receives no private copy of the contract assembly.

It then loads the actual compiled plugins. Services, typed requests and notifications cross the provider-owned contract identity. Beacon exports, a custom event and a variable snapshot execute through the client script service. An in-memory protocol session checks tick work, delayed work, packet observation and detach cancellation.

The check does not contact a public server or need a Minecraft account. It does not test server-specific gameplay or native library loading. It creates one client and one modeled session. It does not prove reconnect on the same client.

## Files

| Project or file | Purpose |
| --- | --- |
| `Contracts` | Public interface, request, response and notification types |
| `Provider` | Price service and typed responder, with exported contract manifest |
| `Consumer` | Dependent plugin using the service, response and notification |
| `BridgeObserver` | Plugin-to-script exports, custom events, snapshots and scoped session work |
| `workflow.bcn` | Exported calculation and custom event assertions |
| `Verify` | Executable runtime checks and explicit payload staging |

The manifests are complete compiled-package manifests. The author build outputs also contain host dependencies. The verifier excludes those dependencies when it stages the plugin packages.

Do not install an entire `bin` directory as a plugin payload. Package only the entry, intended private dependencies, resources and exported contracts. Use [the packaging guide](../../../docs/plugins/testing-and-release.md).

The verifier attempts to remove its temporary package directory after disposal. A loaded DLL can remain locked until its collectible context releases it. If cleanup cannot finish, the verifier reports the retained path on standard error. Remove that temporary directory after the process exits.
