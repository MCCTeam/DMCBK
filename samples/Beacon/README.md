# Beacon examples

These files match the [Beacon guides](../../docs/beacon/index.md).

| Script | Purpose |
| --- | --- |
| `total.mcc` | Calculate a total and assert the result |
| `order.mcc` | Import a helper from `lib/math.mcc` |
| `timers.mcc` | Schedule one-shot and recurring output |

Run the files through `BeaconOfflineRunner.RunSourceAsync`. Supply the actual file path so relative imports resolve. For `timers.mcc`, set `tickSeconds: 60` to advance virtual time.

The examples print locally and do not send server chat. See the [offline runner example](../../docs/beacon/getting-started.md#run-without-a-server).
