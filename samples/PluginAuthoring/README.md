# Plugin authoring samples

Start with the [chaptered plugin guide](../../docs/plugins/tutorial/index.md). It builds a plugin in small, testable steps.

| Sample | Purpose |
| --- | --- |
| [TutorialStages](TutorialStages/README.md) | Four incremental source packages, from activation to a localized command |
| [SessionJournal](SessionJournal/README.md) | The final plugin with settings, durable storage, a command and Beacon integration |
| [SessionCounter](SessionCounter/README.md) | A smaller plugin that counts session callbacks |
| [VerifyPlugin](VerifyPlugin/Program.cs) | Runtime loading, session, persistence, reload, Beacon and cleanup checks for SessionJournal |
| [VerifyStages](VerifyStages/Program.cs) | Runtime checks for each incremental chapter |
| [VerifyCounter](VerifyCounter/Program.cs) | Runtime checks for the smaller SessionCounter sample |

The author projects use NuGet packages. The verification programs compile or load the actual package through the plugin runtime. They use bounded in-memory protocol sessions.

These checks do not require a public server or Microsoft account. They do not prove native loading on another platform. Use a controlled live server for plugins that perform game actions.

See [installation](../../docs/getting-started/installation.md) for unpublished local packages and isolated cache commands.

[Advanced working examples](AdvancedExamples/README.md) cover exported contract assemblies, typed services and messages, Beacon integration, and scoped packet work.
