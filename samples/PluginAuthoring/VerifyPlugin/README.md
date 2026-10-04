# Verify a Session Journal package

This console program loads a real source or compiled Session Journal package. It uses NuGet packages, not sibling source projects.

Run from the DMCBK repository root:

```sh
dotnet run --project samples/PluginAuthoring/VerifyPlugin -- samples/PluginAuthoring/SessionJournal
```

Pass a compiled staging folder to test a compiled asset. See [packaging instructions](../../../docs/plugins/tutorial/07-package-and-release.md).

A failed check throws and gives the process a nonzero exit status. Successful validation prints a PASS line.

Each harness uses one session. The second harness restores the previous count through the same temporary storage root. This is a restart-persistence test, not a reusable-server reconnect test.

The program checks the staged package through the runtime. It does not directly instantiate the plugin class.
