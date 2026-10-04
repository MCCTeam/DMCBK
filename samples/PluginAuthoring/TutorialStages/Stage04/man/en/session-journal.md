# Session journal

The plugin stores a count of session starts. Reconnect adds another increment.

Run `journal-count` through the host's internal command dispatcher. The command returns local text. It does not send server chat.

Set `Increment` in the user settings to a value from 1 to 100. Reload the plugin after editing this setting.

Beacon scripts declare `journal.read` and import `journal_count` from `session-journal`.

The count persists in the assigned data directory. This example saves once per session. It does not count packets or players.
