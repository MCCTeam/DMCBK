# Host APIs and testing

`client.Scripts` is the per-client script service. It becomes available after `.UseCommands().UseBeacon()`.

## Script operations

| API | Purpose |
| --- | --- |
| `Discover()` | List top-level script files and their running state |
| `LoadDocumentAsync(id)` | Read source with a content hash |
| `RunAsync(id, source, fileName)` | Load in-memory source |
| `RunFileAsync(id)` | Read and run a discovered file |
| `Stop(id)`, `StopAll()` | Stop one or all scripts |
| `ReloadAsync(id)` | Reload a file as fresh execution state |
| `Lint(fileName, source)` | Return diagnostics without execution |
| `Format(fileName, source)` | Return formatted text and a diff |
| `Create(template, id)` | Create a script from a built-in template |
| `Delete(id)` | Stop and delete source, retaining settings and saved state |
| `SetWatch(enabled)` | Enable or disable file watching |
| `SetMuted(enabled)` | Control script chat while logic continues |
| `EvaluateAsync(line, seed)` | Evaluate against persistent REPL locals |
| `ReplLocals()` | Read the REPL's current local values |

File operations need a configured source folder. `RunAsync` does not need discovery. For imports, supply a filename that represents the script's actual location.

These APIs let a host build its own editor or dashboard. MCC supplies its own terminal and TUI frontends. DMCBK does not require those interfaces.

## Lint and format without a client

```csharp
using DMCBK.Core.Beacon;

string source = "# beacon 1\nset total to 3 * 4\nassert(total is 12, \"total\")\n";
var lint = BeaconLint.LintSource("total.bcn", source);
var formatted = BeaconFormat.FormatSource("total.bcn", source);
Console.WriteLine(formatted.Formatted);
if (!lint.Ok || formatted.HadErrors)
    throw new InvalidOperationException("The script has errors.");
```

Formatting returns text. It does not write a file. A formatted script can still contain errors.

1. Show diagnostics with their source locations.
2. Show the formatting diff before replacing an editor buffer.
3. Lint the final source before saving or executing it.

## Save an editor document

`LoadDocumentAsync` returns source and a content hash. `SaveDocumentAsync(document, source)` compares that hash with the current file.

| Outcome | Host behavior |
| --- | --- |
| `Saved` | Update the editor's saved document snapshot |
| `ValidationErrors` | Show diagnostics and retain the buffer |
| `ExternalConflict` | Offer reload, explicit overwrite or cancel |

`overwriteConflict: true` is an explicit overwrite choice. Atomic replacement prevents a partially written source file.

A save does not imply reload when the host disables watching. The host should expose the running state separately from the saved source.

## Edit declared settings

`GetSettingsAsync(id)` returns declarations, resolved values and diagnostics. `SaveSettingAsync(id, key, value)` returns `Saved`, `AppliedLive` and `Error`.

1. Build editors from the declared setting kinds.
2. Reject unknown keys in the host interface.
3. Inspect the result before reporting that a change applied live.

## Test scripts

1. Put assertions in a separate test script.
2. Supply a fixed random seed.
3. Advance virtual time for timer tests.
4. Fail the test when `report.Ok` is false.

`BeaconOfflineRunner.RunSourceAsync(path, source, seed: 42, tickSeconds: 60)` advances virtual time after loading.

Offline tests cover calculations, branching, imported functions, assertions and deterministic scheduled work. They cannot validate packet arrival, world tracking or server acceptance.

For integration tests, use `DMCBK.Testing` and then a controlled live server for game behavior. See [plugin testing](../plugins/testing-and-release.md).

## Diagnose failures

| Symptom | Likely cause | Next action |
| --- | --- | --- |
| Header error | Missing or unsupported `# beacon 1` | Add the supported header |
| Capability diagnostic | Header does not declare a used operation | Add the reported requirement |
| Missing script folder | No configuration source folder | Supply an explicit configuration path |
| Import error | Wrong relative path or circular imports | Inspect the importing file and path |
| Missing export | Target script is inactive or has no export | Load the provider and inspect its declaration |
| Action failure | No session, disabled tracking or unsupported action | Inspect session capabilities and configuration |
| Budget failure | Too much work or excessive recursion | Split the work and add yielding waits |
| Network refusal | Missing grant, HTTPS or allowed host | Inspect the header and Beacon network configuration |

Return to the [Beacon index](index.md).

## Build an editor around explicit results

Treat saved source and running source as separate states. A buffer can contain unsaved changes while the runtime still uses the previous file. Show both states when an editor offers save and reload.

Discovery returns script files. It does not prove that the files passed lint or started. Inspect the run result and current running state before displaying a success indicator.

Reload resets script globals. Persistent settings and saved state have a different lifetime. Tell the user when a reset action removes those files.

## Avoid stale reference examples

Check examples against the same DMCBK version that the application consumes. Preview packages can change. Reusing an old NuGet cache can test an earlier preview even when the version text matches.

An in-memory script test should supply a realistic filename. Imports depend on that filename. A timer test should use an explicit virtual clock rather than a long real sleep.

## Test the negative path

Check a chat record that must not match. Check a missing item, missing field, and absent optional provider. Check an error result as well as the successful result.

A test must fail when the action is absent. Counting that a handler ran is not sufficient for an inventory or chat expectation. Check the output or recorded action itself.

[Chapter 8](guide/08-testing.md) contains a complete timer boundary test. [Chapter 4](guide/04-events.md) contains a positive and negative chat test.

## Display script command output

A client composed with Commands and Beacon binds valid script `command` blocks to its internal dispatcher. Load the script before dispatching its command. Use the command name without its declaration's slash in `DispatchAsync`.

Script `show` output from a command appears in `CmdResult.Message`. Read that message as well as captured body output. A runtime error returns a failed command result. Check `IsSuccess` before reporting completion.

The binding belongs to the script. Reload replaces it. Stop and module disposal remove it. A command name that conflicts with an existing host name or alias produces a warning. Rename the script command instead of trying to replace a built-in command.

The [complete helper](guide/09-complete-helper.md) demonstrates the entire path from a `.bcn` command declaration to local host output.
