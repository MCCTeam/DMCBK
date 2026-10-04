# Dependencies and assembly loading

A plugin dependency and a private DLL dependency solve different problems. The first selects another plugin. The second supplies implementation code inside one package.

## Declare plugin dependencies

```toml
[requires]
shared-tools = "^2.1.0"

[optional]
alerts = ">=3.0.0 <4.0.0"
```

Required dependencies must be installed at compatible versions. Resolution includes their transitive requirements before installation changes begin.

Optional dependencies are not installed automatically. An incompatible optional provider is unavailable to the consumer. The consumer must handle its absence.

One active version exists per plugin ID. Conflicting required ranges, missing providers and required dependency cycles reject the plan.

Pins restrict updates. Yanked releases remain in history but normal installation and update selection exclude them.

1. Use required dependencies for functionality that must exist.
2. Use optional dependencies for enhancements that can be absent.
3. Specify the versions that you tested with your contract.

## Publish shared contracts

A provider can export an assembly containing public interfaces and message types:

```toml
[exports]
assemblies = ["SharedTools.Contracts.dll"]
```

The consumer declares the provider under `[requires]`. The runtime resolves the exported assembly through the provider's load context.

That shared type identity matters. Two private copies of the same interface name are different CLR types.

1. Put public contracts in a small separate assembly.
2. Keep implementation dependencies out of that contract assembly.
3. Build consumers against the matching contract package.
4. Declare the provider and its version range in each consumer manifest.
5. Include the exported contract in the provider package.

## Services and messages

| API | Purpose |
| --- | --- |
| `Services.Register<T>(instance)` | Publish a class or interface service |
| `Services.TryGet<T>(out service)` | Request a service without assuming availability |
| `Messenger.Subscribe<T>(callback)` | Receive typed notifications |
| `Messenger.Publish<T>(message)` | Publish a notification |
| `Messenger.RegisterResponder<TRequest,TResponse>(callback)` | Register a typed request handler |
| `Messenger.TryRequest<TRequest,TResponse>(request, out response)` | Request a response when a handler exists |

Registration handles can be disposed to withdraw a service or subscription early. The runtime also cleans up plugin-owned registrations on unload.

Custom message and service types must have compatible assembly identity. Shared framework types and explicitly exported provider contracts satisfy that boundary.

Avoid retaining another plugin's service after its provider unloads. Re-resolve optional services at the point of use.

## Private managed dependencies

The manifest lists private helper DLLs in `deps`:

```toml
deps = ["lib/ExampleHelpers.dll"]
```

Private dependencies resolve in the plugin's collectible load context. Different plugins can use different private library versions.

The runtime shares DMCBK, UMPK and designated framework contracts with the host. It rejects packages containing duplicate host contracts.

## Native dependencies

1. Publish a compiled asset for each supported runtime target.
2. Include the entry assembly's `.deps.json` file when using native dependency resolution.
3. Include the corresponding native libraries and private managed helpers.
4. Test loading on the actual operating system and process architecture.

The runtime uses `AssemblyDependencyResolver` for managed and native lookup. File names, dependency metadata and packaged paths must agree.

An `any` asset is appropriate for portable managed code. It cannot hide an architecture-specific native dependency.

Collectible contexts enable unloading, but references, running tasks and native handles can delay release. Never depend on overwriting a loaded DLL.

Next: [Testing and release](testing-and-release.md).
