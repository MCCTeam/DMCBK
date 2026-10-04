# Run your first Beacon script

A script is a UTF-8 file with the `.mcc` extension. Its first line selects the language version.

## Write a script

1. Create a file named `total.mcc`.
2. Copy this example into the file.

```beacon
# beacon 1
set prices to [3, 5, 7]
set total to 0
for each price in prices
  set total to total + price
end for
assert(total is 15, "total")
show "Total: {total}"
```

The output is `Total: 15`. `show` writes local output. It does not send chat and needs no capability.

`# needs:` declares permissions that the script requires. `# wants:` declares optional integrations. Missing requirements prevent loading. Missing optional integrations produce diagnostics.

## Run without a server

1. Add the `DMCBK.Beacon` package to a .NET 10 console project.
2. Run this program from the directory that contains `total.mcc`.

```csharp
using DMCBK.Core.Beacon;

string path = Path.GetFullPath("total.mcc");
string source = await File.ReadAllTextAsync(path);
var report = await BeaconOfflineRunner.RunSourceAsync(
    path, source, seed: 42, tickSeconds: 0);
foreach (string line in report.Output)
    Console.WriteLine(line);
if (!report.Ok)
    throw new InvalidOperationException("The script failed. Inspect report.Diagnostics.");
```

Offline execution uses an inert host, a virtual clock and seeded randomness. It proves script logic and syntax. It cannot prove server responses or game actions.

## Attach Beacon to a client

1. Add `DMCBK.Commands` and `DMCBK.Beacon` to the host project.
2. Register Commands before Beacon.
3. Lint the source before running it.

```csharp
using DMCBK.Core;

await using Client client = new ClientBuilder()
    .UseServer("localhost")
    .UseUsername("BeaconBot")
    .UseCommands()
    .UseBeacon()
    .Build();

string source = "# beacon 1\nshow 2 + 3\n";
var diagnostics = client.Scripts.Lint("hello.mcc", source);
if (!diagnostics.Ok)
    throw new InvalidOperationException("The script has lint errors.");
await client.Scripts.RunAsync("hello", source, "hello.mcc");
client.Scripts.Stop("hello");
```

`RunAsync` loads a script. It does not connect the client. Follow [Your first client](../getting-started/first-client.md) for connection and shutdown.

File discovery needs a configuration source folder. If that folder is `/app/configurations`, scripts belong in `/app/scripts`. Nested files can serve as imported libraries.

A host can use in-memory source without file discovery. File settings, saved state and file watching need the relevant configured paths.

You can also copy the [ready-to-run example files](../../samples/Beacon/README.md).

Next: [Learn the language](language.md).
