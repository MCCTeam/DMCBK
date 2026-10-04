# Beacon language

Beacon uses labeled blocks and one assignment form: `set name to value`. Each script owns its globals. Function parameters and locals belong to that function call.

## Values and expressions

| Kind | Example | Notes |
| --- | --- | --- |
| Text | `"Steve"` | Interpolation uses `{expression}`. Literal braces use `{{` and `}}`. |
| Number | `12.5` | Arithmetic and numeric comparisons use numbers. |
| Yes/no | `yes`, `no` | Conditions require this kind. |
| List | `[3, 5, 7]` | `for each` visits its values. |
| Map | `{bread: 3}` | Read a field with `.bread` or `["bread"]`. |
| None | `none` | Represents an absent value. |

Equality uses `is` and `is not`. The aliases `==` and `!=` also parse. `=` does not assign.

`and` combines conditions. `or` also supplies defaults: `saved("count") or 0`. Only `no` and `none` select that fallback. Zero, empty text and empty lists do not.

1. Compare values explicitly in conditions.
2. Use `is set` before reading an optional nested value.
3. Use bracket access for map keys that contain spaces or punctuation.

## Branches and loops

```beacon
# beacon 1
set total to 0
repeat 3 times
  set total to total + 2
end repeat
if total is 6 then
  show "Expected total"
else
  show "Unexpected total"
end if
while total < 8
  set total to total + 1
end while
assert(total is 8, "loop result")
```

`else if` adds another branch. `stop` exits a loop. `skip` continues with its next iteration. A long loop can exhaust the execution budget.

## Functions

```beacon
# beacon 1
function subtotal(price, count)
  return price * count
end function
set result to subtotal(3, 4)
assert(result is 12, "subtotal")
show result
```

Functions can return any Beacon value. Recursion has a depth limit. Pure helper functions are easier to test than functions that also send chat.

## Tasks and time

`start worker()` starts background work. `tasks()` lists live task records, including their IDs. `await id` waits for completion. `cancel task id` cancels one.

`wait 2 seconds` yields the current task. It does not block the connection loop. The minimum wait is 100 milliseconds.

```beacon
# beacon 1
function later()
  wait 1 second
  show "Task completed"
end function
start later()
set active to tasks()
if len(active) > 0 then
  await active[0].id
end if
```

Use [timer blocks](events-and-game.md#timers) for recurring work. Session changes cancel pending sleeps and one-shot timers. A script reload creates fresh execution state.

## Handle errors

```beacon
# beacon 1
try
  assert(no, "example failure")
catch err
  show "Caught: {err.message}"
finally
  show "Attempt finished"
end try
```

Caught errors expose `message`, `code` and `line`. Wrong argument kinds, missing providers and failed session actions can raise errors.

1. Catch failures near the action that can fail.
2. Include a useful local diagnostic.
3. Put cleanup in `finally` when it must run after either outcome.

Do not repeatedly retry a failed game action without a delay. The action can remain unavailable for the entire session.

## Text and data helpers

| Group | Functions |
| --- | --- |
| Text | `len`, `lower`, `upper`, `trim`, `split`, `join`, `slice`, `replace`, `index_of` |
| Collections | `sort`, `reverse`, `unique`, `keys`, `values`, `has_key` |
| Conversion | `text`, `number`, `json_parse`, `json_stringify` |
| Matching | `match`, `match_all`, `escape_regex` |
| Numbers | `min`, `max`, `clamp`, `round`, `abs`, `log` |
| Randomness | `random`, `pick`, `chance` |
| Tests | `assert(condition, label)` |

`sort` returns a new list of numbers or text. `unique` preserves first-seen order. `index_of` returns a zero-based position or `none`.

Next: [Events and game APIs](events-and-game.md).
