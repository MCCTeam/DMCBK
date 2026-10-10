# Chapter 1: Create and run a file

You will create `hello.bcn` and run it without a server. This separates script errors from connection errors.

## Prepare the runner

You need the .NET 10 SDK and access to the DMCBK packages. For unpublished packages, use the [local feed instructions](../../getting-started/installation.md).

1. Create a directory named `BeaconGuide`.
2. Open a terminal in that directory.
3. Create the console project.

```sh
dotnet new console --framework net10.0
dotnet add package DMCBK.Beacon --version 0.1.0-preview.6
```

4. Replace `Program.cs` with this complete program.

```csharp
using DMCBK.Core.Beacon;

if (args.Length != 1)
    throw new ArgumentException("Supply one .bcn file path.");

string path = Path.GetFullPath(args[0]);
string source = await File.ReadAllTextAsync(path);
var lint = BeaconLint.LintSource(path, source);
foreach (var diagnostic in lint.Diagnostics)
    Console.Error.WriteLine(diagnostic);
if (!lint.Ok)
    throw new InvalidOperationException("Correct the lint errors before execution.");

var report = await BeaconOfflineRunner.RunSourceAsync(
    path, source, seed: 42, tickSeconds: 30);
foreach (string line in report.Output)
    Console.WriteLine(line);
foreach (var diagnostic in report.Diagnostics)
    Console.Error.WriteLine(diagnostic);
if (!report.Ok)
    throw new InvalidOperationException("The script check failed.");
```

The program reads a file, checks its syntax, and runs it. `seed: 42` makes random choices repeatable. `tickSeconds: 30` advances the test clock once after loading. It does not wait for 30 real seconds.

## Create the script

1. Create `hello.bcn` beside `Program.cs`.
2. Copy this complete script into the file.

```beacon
# beacon 1
show "Hello from Beacon"
```

3. Execute the runner.

```sh
dotnet run -- hello.bcn
```

Expected output:

```text
Hello from Beacon
```

`# beacon 1` selects the supported language version. Every complete script needs this header. Ordinary comments also start with `#`.

`show` writes local output. It does not send a Minecraft message. A real host decides where to display that output.

## Find the first error

The filename must match the terminal argument. If the runner cannot read it, check the current directory and spelling.

If lint reports a line and column, inspect that location first. A missing closing quote can also affect the next line. Correct the first diagnostic before investigating later ones.

Next: [Chapter 2: Work with values](02-values.md).
