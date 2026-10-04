# Chapter 8: Test and diagnose

Test a script in layers. Start with calculations. Then deliver events. Finally check the real connection and server behavior.

## Use a repeatable check

1. Read the actual `.bcn` file.
2. Pass its full path to lint and execution.
3. Set a fixed random seed for logic tests.
4. Check the result's success flag.
5. Check the expected output or recorded action.
6. Remove the script after an engine test.

Reading the real file catches mistakes in the delivered sample. A copied string inside a test can pass while the file remains wrong.

A successful load does not execute every event handler. Deliver each relevant event explicitly. Include a record that should match and a record that should not match.

## Test scheduled work

```csharp
using DMCBK.Core.Beacon;
using DMCBK.Testing;

string source = """
    # beacon 1
    in 5 seconds do
      show "due"
    end in
    """;
var clock = new VirtualClock();
var engine = new BeaconEngine(new ScriptTestHost(), clock, new SeededRng(42));
try
{
    var loaded = await engine.RunScriptAsync("scheduled", source, fileName: "scheduled.bcn");
    if (!loaded.Success)
        throw new InvalidOperationException("The timer did not load.");
    clock.Advance(TimeSpan.FromSeconds(4));
    if ((await engine.TickOnceAsync()).Count != 0)
        throw new InvalidOperationException("The timer ran early.");
    clock.Advance(TimeSpan.FromSeconds(1));
    var runs = await engine.TickOnceAsync();
    if (runs.Count != 1 || !runs[0].Result.LocalOutput.Contains("due"))
        throw new InvalidOperationException("The timer result is incorrect.");
    if ((await engine.TickOnceAsync()).Count != 0)
        throw new InvalidOperationException("The one-shot timer ran twice.");
    Console.WriteLine("Timer checks passed.");
}
finally
{
    engine.RemoveScript("scheduled");
}
```

The clock starts at a known point. Four seconds must not trigger the timer. The fifth second must trigger it once.

## Read a diagnostic

| Information | Meaning | What to inspect |
| --- | --- | --- |
| File and line | Source location | The statement and its surrounding block |
| Code | Stable diagnostic category | The related parser, capability, or runtime failure |
| Message | What failed | The input values and current session |
| Hint | Suggested correction | Whether the correction matches your intended action |

Check errors before warnings. Do not remove a capability declaration only to hide a warning. First check whether the operation belongs in the script.

## Separate three different failures

A syntax failure means Beacon cannot understand the file. For example, `end if` cannot close a `for each` block.

A capability failure means the script lacks a required declared or available operation. A declaration alone cannot enable world tracking.

A game failure means the action cannot complete in the current session. The client can be disconnected, the container can close, or the server can reject the action.

## Check a real client

1. Use a server that you control.
2. Enable the tracking required by the script.
3. Connect the client before invoking game actions.
4. Send the trigger from a second player when the test needs a sender.
5. Check the client diagnostic and server result.
6. Disconnect the client after the check.

A test host records requested actions. It does not prove packet encoding, permission checks on the server, or movement arrival. The [testing package guide](../../plugins/testing-and-release.md) explains the available session tools.

Next: [Chapter 9: Run the complete helper](09-complete-helper.md).
