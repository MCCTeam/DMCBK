# Chaptered Beacon examples

These files accompany [Make a Beacon script](../../../docs/beacon/guide/index.md). Use the runner in Chapter 1 for game-free examples. Use the test host in Chapter 4 for event examples.

| File | Expected behavior |
| --- | --- |
| `hello.bcn` | Print `Hello from Beacon` |
| `values.bcn` | Print the order size and `Total: 15` |
| `functions.bcn` | Check two calculations and print `Subtotal: 12` |
| `greeter.bcn` | Whisper a greeting for an exact `!hello` event |
| `schedule.bcn` | Print one-shot and recurring timer results |
| `settings.bcn` | Print the default `Hello` setting |
| `saved-counter.bcn` | Increment the persisted count with a configuration folder |
| `price.bcn` | Register `/guide-price <item>` |
| `welcome-helper.bcn` | Greet a sender, persist the greeting count, and register `/guide-stats` |

The test host supplies simulated game observations. A real client supplies the connection and configuration paths. These samples do not start a client by themselves.
