# Chat

## Sending

Anything that is not an MCC command goes to the server. `/send <text>` forces that, which matters
when the text starts with a slash or a `%variable%`.

`[Chat]` in `client.toml` sets a minimum interval between messages. Servers kick for spam, and the
limit is enforced client-side so you do not have to think about it.

## Receiving

Chat arrives as a Minecraft component tree, not as text: colours, hover text, translation keys and
click actions are all structure. MCC renders that tree with the vanilla translation table for the
protocol it is speaking, so `chat.type.text` looks the way it does in a real client.

Manual pages are Markdown and are rendered by a different path. Chat is never treated as Markdown,
so a player typing asterisks cannot restyle your console.

## Visibility

`/console-chat off` hides incoming chat without disconnecting. Chat log files and plugins keep
receiving it. `console.toml` `[General] DisplayChat` sets the startup state.

## Filtering

`[Logging]` takes a regex and a filter mode. It applies to what is logged, not to what plugins see.

## Signing

1.19 and later sign chat with your profile key. An unsigned message shows a standing marker: the
server can tell it was not signed, and so can you. Without a cached profile key some servers reject
your messages outright.
