# Beacon examples

These files match the [Beacon guides](../../docs/beacon/index.md).

| Script | Purpose |
| --- | --- |
| `total.bcn` | Calculate a total and assert the result |
| `order.bcn` | Import a helper from `lib/math.bcn` |
| `timers.bcn` | Schedule one-shot and recurring output |

Run the files through `BeaconOfflineRunner.RunSourceAsync`. Supply the actual file path so relative imports resolve. For `timers.bcn`, set `tickSeconds: 60` to advance virtual time.

`total.bcn`, `order.bcn` and `timers.bcn` print locally and do not send server chat. See the [offline runner example](../../docs/beacon/getting-started.md#run-without-a-server).

## Practical recipes

See [recipes from MCC](../../docs/beacon/recipes.md) for the complete scripts, triggers and prerequisites.

- [Chat quiz](quiz.bcn)
- [TPS warning with a cooldown](tps-guard.bcn)
- [Inventory report](inventory-report.bcn)
- [Find nearby chests](nearby-chests.bcn)
- [Entity census](census.bcn)
- [A command with an argument](price-command.bcn)
- [A shared counter](shared-counter.bcn)
- [Answer a team-selection dialog](team-dialog.bcn)
- [Use an optional plugin](optional-pricing.bcn)

The [chaptered guide samples](guide/README.md) add beginner exercises for values, events, timers, commands, and persistent state.
