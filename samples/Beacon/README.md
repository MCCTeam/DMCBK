# Beacon examples

These files match the [Beacon guides](../../docs/beacon/index.md).

| Script | Purpose |
| --- | --- |
| `total.mcc` | Calculate a total and assert the result |
| `order.mcc` | Import a helper from `lib/math.mcc` |
| `timers.mcc` | Schedule one-shot and recurring output |

Run the files through `BeaconOfflineRunner.RunSourceAsync`. Supply the actual file path so relative imports resolve. For `timers.mcc`, set `tickSeconds: 60` to advance virtual time.

`total.mcc`, `order.mcc` and `timers.mcc` print locally and do not send server chat. See the [offline runner example](../../docs/beacon/getting-started.md#run-without-a-server).

## Practical recipes

See [recipes from MCC](../../docs/beacon/recipes.md) for the complete scripts, triggers and prerequisites.

- [Chat quiz](quiz.mcc)
- [TPS warning with a cooldown](tps-guard.mcc)
- [Inventory report](inventory-report.mcc)
- [Find nearby chests](nearby-chests.mcc)
- [Entity census](census.mcc)
- [A command with an argument](price-command.mcc)
- [A shared counter](shared-counter.mcc)
- [Answer a team-selection dialog](team-dialog.mcc)
- [Use an optional plugin](optional-pricing.mcc)
