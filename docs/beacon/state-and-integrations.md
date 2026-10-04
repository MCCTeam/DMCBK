# State and integrations

Choose storage according to its lifetime. A local variable, shared value and saved value have different uses.

## Settings and saved state

```beacon
# beacon 1
# setting interval = 30 ; seconds between reports
set reports to saved("reports") or 0
set reports to reports + 1
save "reports" to reports
show "Report {reports}, interval {settings.interval}"
```

Header settings declare scalar defaults and comments. The host overlays user values from `configurations/beacon/<id>.settings.toml`.

`saved(key)` and `save key to value` preserve state under the configuration's Beacon directory. Settings describe user choices. Saved values describe runtime progress.

1. Use settings for editable options.
2. Use saved state for values that must survive restart.
3. Keep temporary calculations in local variables.

`shared` is RAM-only state across scripts in one client. Use namespaced keys, such as `shared["shop.count"]`.

`lock shared` and `end lock` delimit a serialized read-modify-write block. Without that block, two handlers can read the same counter before either writes it.

## Import a library

1. Create `scripts/lib/math.bcn` with this content.

```beacon
# beacon 1
function subtotal(price, count)
  return price * count
end function
```

2. Create `scripts/order.bcn` with this content.

```beacon
# beacon 1
import "lib/math.bcn" as math
show math.subtotal(3, 4)
```

Imports resolve relative to the importing file. Circular imports fail with a diagnostic. Lint includes imported capability requirements.

Libraries contribute functions and top-level constants. Their event and command blocks do not register in the importing script.

## Export script functions

```beacon
# beacon 1
export function subtotal(price, count)
  return price * count
end function
export set prices to {bread: 3, apple: 5}
```

If this script runs as `shop`, another script can use `call "shop.subtotal"(3, 4)` or `call "shop.prices"()`.

The target must be loaded and export the requested name. Calls use the caller's fuel budget. Missing exports and argument mismatches raise catchable errors.

## Add commands

```beacon
# beacon 1
# desc: Calculate a fixed order total.
# example: /order-total
command "/order-total"
  show 3 * 4
end command
```

A command pattern needs its leading slash. `<item>` declares a one-word argument. The body reads it with `arg("item")`.

Commands register with the internal dispatcher and withdraw when the script stops. They do not create server commands.

## Use plugin functions and variables

An `extern` declaration names the provider plugin ID. The [Beacon plugin example](../plugins/beacon-integration.md) offers `subtotal` from `shop-tools`.

A plugin variable namespace exposes a read-only map. Every read obtains a snapshot. Scripts cannot write into the plugin's object graph.

Only the six Beacon value kinds cross the bridge. C# services and session objects cannot cross directly.

1. Declare the function's capability in `# needs:`.
2. Declare the function with `extern name from "plugin-id"`.
3. Catch errors when an optional provider can disappear.

## Files and network

`file_read` and `file_write` require `fs.read` and `fs.write`. Paths remain inside the script's assigned data directory.

`http_get` and `http_post` require `net.fetch`. The host's `beacon.toml` must also allow the destination host:

```toml
[Net]
AllowedHosts = ["api.example.org"]
```

The runtime allows only HTTPS requests. A script capability does not bypass the network allowlist.

1. Declare only the permissions that your script uses.
2. Keep network calls out of high-frequency handlers.
3. Catch network and parsing failures separately when their recovery differs.

Next: [Host APIs and testing](hosting-and-testing.md).
