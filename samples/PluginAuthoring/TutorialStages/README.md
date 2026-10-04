# Incremental plugin snapshots

Each folder is a complete source package for one [tutorial chapter](../../../docs/plugins/tutorial/index.md).

| Folder | Behavior |
| --- | --- |
| Stage01 | Declares an entry and activates |
| Stage02 | Counts session starts in memory |
| Stage03 | Adds validated settings and persistent storage |
| Stage04 | Adds a localized command and a manual |

Build any Stage.csproj to check its author project. Use VerifyStages to compile the same entry through the runtime:

```sh
dotnet run --project samples/PluginAuthoring/VerifyStages -- samples/PluginAuthoring/TutorialStages/Stage03 3
```

The final argument is the stage number. The expected result is `PASS: tutorial stage 3.`

All stages use NuGet packages. They do not reference DMCBK or MCC source projects.

Each test uses one in-memory session. These checks do not prove same-client reconnect.
