# Contributing to DMCBK

Small changes are easier to review. Explain the behavior you want to change and the clients or plugins it affects. This guide follows UMPK's contribution approach with DMCBK-specific build commands.

## Before you start

1. Read the [documentation](docs/index.md).
2. Check existing issues and pull requests.
3. Open an issue before a large API or dependency change.
4. Use a server that you control for live tests.

## Build and test

1. Install SDK `10.0.401`.
2. Restore the solution.
3. Build the solution.
4. Run unit tests.

```bash
dotnet restore DMCBK.slnx
dotnet build DMCBK.slnx -c Release --no-restore
dotnet test DMCBK.slnx -c Release --no-build
```

## Make a change

1. Reproduce the problem with a focused test.
2. Change the owning module.
3. Run the focused test.
4. Run the full unit suite.
5. Update examples when the public API changes.
6. Review your diff before submitting it.

Keep Core independent of UI frameworks. Use host interfaces for presentation. Keep commands, script runtimes and plugin roots independent between clients. Respect cancellation and release resources when a session or client ends.

Never commit account files, token caches, raw captures, downloaded game files or build output. Store temporary logs outside the repository. A skipped live check is not a pass. State checks that you did not run.

## Pull requests and AI tools

Describe the problem, resulting behavior and checks you ran. Keep each pull request focused. Read every changed line, including generated examples and tests.

AI tools can help draft documentation and repetitive changes. Treat their output as a draft. Do not send credentials, session data or private captures to an external service. Follow [UMPK's contribution guide](https://github.com/MCCTeam/UMPK/blob/master/CONTRIBUTING.md) when a change belongs in the protocol engine.

Read [NuGet release setup](docs/releases.md) before publishing a package version.
