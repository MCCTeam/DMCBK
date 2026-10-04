# Accounts

`accounts.toml` holds account details and is gitignored. MCC never stores a password.

## Kinds

    offline             a bare username, for servers with online-mode=false
    microsoft           device code: MCC prints a code, you enter it on a web page
    microsoft-browser   opens your browser instead
    yggdrasil           an old-style auth server, prompted at login

`Active` picks which entry to use. Empty uses the first.

## Caching

    SessionCache      none | memory | disk
    ProfileKeyCache   none | memory | disk
    CacheDirectory    relative to configurations/

The session cache saves the token so you do not re-authenticate every start. The profile key cache
saves the chat-signing certificate, which 1.19+ servers need to accept signed chat.

Set both to `none` on a shared machine. You will authenticate every time.

## Switching

`/reco <account>` reconnects as a different entry. `/connect <server> <account>` does both at once.

> [!NOTE]
> An online account's `Login` is an email address and is treated as a secret. Commands that show
> your identity print the authenticated profile name instead.
