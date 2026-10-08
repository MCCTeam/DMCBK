# DMCBK

## Project overview

DMCBK is a .NET 10 library for building Minecraft Java clients on top of UMPK. It owns client hosting, game API adapters, commands, Beacon scripting, plugins and marketplace installation. UMPK owns the protocol and game engine. UI frameworks belong to host applications.

The repository uses MIT for DMCBK code. The current package version is `0.1.0-preview.2`, plugin API version is `1.0`, and marketplace schema version is `2`. Read `Directory.Build.props`, `Directory.Packages.props` and `global.json` for current versions before editing.

## Local skills

| Skill | Use for |
| --- | --- |
| `.skills/csharp-best-practices/SKILL.md` | C# implementation, naming and async review |
| `.skills/csharp-solid-principles/SKILL.md` | Design, refactoring and API boundaries |
| `.skills/asd-ste100/SKILL.md` | Clear procedures, diagnostics and agent instructions |
| `.skills/dotnet-performance-profiling-and-optimization/SKILL.md` | Measured performance diagnosis |
| `.skills/dotnet-security-review/SKILL.md` | Security and dependency review |

Read the relevant `SKILL.md` before applying a skill. Resolve relative references from its directory. These skills come from UMPK. Its integration-testing skill is intentionally excluded because DMCBK has different test infrastructure.

`.skills` is the canonical directory. `.claude/skills`, `.agents/skills` and `.codex/skill` link to it. `.codex/skills` also links to it for tools that discover the plural path.

## Commands

Use SDK `10.0.401` from `global.json`. Run commands from the repository root.

| Task | Command |
| --- | --- |
| Restore | `dotnet restore DMCBK.slnx` |
| Build | `dotnet build DMCBK.slnx -c Release --no-restore` |
| Unit tests | `dotnet test DMCBK.slnx -c Release --no-build` |
| Focused tests | `dotnet test tests/DMCBK.Core.Tests/DMCBK.Core.Tests.csproj -c Release --filter FullyQualifiedName~TestName` |
| Formatting check | `dotnet format DMCBK.slnx --verify-no-changes` |
| Pack | `dotnet pack DMCBK.slnx -c Release --no-build -o /tmp/dmcbk-packages` |

Build before using `--no-build`. Keep logs and temporary package feeds outside the repository. The ignore rules exclude `artifacts/`, `bin/`, `obj/` and test output.

See `docs/getting-started/installation.md` for local package consumption. Samples reference NuGet packages rather than sibling source projects. Use an isolated package cache when testing newly packed packages with an unchanged preview version.

## Architecture and ownership

Read the diagram in `README.md` and the package dependencies in `docs/reference/packages.md`.

| Area | Responsibility |
| --- | --- |
| `src/DMCBK.Core` | Client lifecycle, authentication orchestration, typed configuration, game APIs and host/module contracts |
| `src/DMCBK.Commands` | Dispatch, completion, registration scopes and portable built-in commands |
| `src/DMCBK.Configuration` | Explicit TOML loading, validation, profiles and persistence |
| `src/DMCBK.Beacon` | Script interpreter, scheduling, lint/format APIs and client integration |
| `src/DMCBK.PluginSdk` | Author contracts, manifests, settings, storage, services and communication |
| `src/DMCBK.Plugins` | Activation, unloading, source compilation, managed/native loading and crash budgets |
| `src/DMCBK.Marketplace` | Catalogues, release selection, dependency resolution, downloads, locks and transactions |
| `src/DMCBK.Testing` | Script doubles and in-memory protocol sessions for plugin tests |
| `src/DMCBK` | Convenience package for Core, Commands and Configuration |
| `tests/DMCBK.Core.Tests` | Unit, lifecycle, script, plugin, marketplace and architecture regressions |
| `samples` | Headless, web-backend, Beacon and plugin author examples |
| `docs` | Host, script, plugin, marketplace and release documentation |

Core must not reference optional implementation packages. Compose modules through `ClientBuilder` extensions. Commands must be attached before Beacon and Plugins. Module resources belong to one client instance.

Protocol changes belong in UMPK. Consume its published packages here. Do not introduce source-checkout discovery, submodules or sibling-project references for UMPK.

## Development procedure

1. Inspect the checkout and preserve unrelated changes.
2. Identify the behavior and owning package before editing.
3. Reproduce a behavior defect with a focused test.
4. Implement the change through existing capability and lifetime boundaries.
5. Run focused tests for the affected behavior.
6. Run adjacent lifecycle, reconnect and dependency regressions when relevant.
7. Run the full build and unit suite for runtime or public API changes.
8. Pack affected libraries when package boundaries change.
9. Compile affected samples and documentation examples against the packed packages.
10. Record executed checks and any checks that you did not run.

For documentation-only changes, check links, sample parity and executable examples. Do not add tests that merely mirror a mechanical rename or reversible text edit.

Skipped checks, missing prerequisites and aborted runs are not passes. Distinguish simulated tests, cross-builds and execution on the actual target.

## Runtime invariants

- Keep each client's options, modules, variables, plugin root and script runtime independent.
- Treat a client lifetime and a connection session as different lifetimes.
- Use fresh session scopes after reconnect.
- Honor caller cancellation and session detach tokens.
- Assign an owner to each timer, task, transport and registration.
- Dispose plugin-created resources during deactivation.
- Keep packet callbacks short and copy payloads before retaining them.
- Use capability and tracking gates before game actions.
- Distinguish absent data, unsupported actions and disconnected sessions.
- Keep console formatting, navigation, prompts and application defaults in the host.
- Core accepts typed options. File configuration requires explicit host-selected paths.

## Beacon and plugin changes

Read `docs/beacon/index.md` and `docs/plugins/index.md` before changing their public behavior.

- Keep Beacon syntax, capabilities, lint diagnostics and examples consistent.
- Preserve `.bcn` files and the `mcc` command verb unless the user explicitly requests a language change.
- Test event ordering, timers, reconnect cancellation and registration withdrawal when changing script scheduling.
- Beacon initializes lazily. Initialize the engine before plugin activation for pre-session script calls into plugins.
- Source plugin installation compiles one entry file. It must not restore arbitrary NuGet dependencies or build arbitrary projects.
- Keep Roslyn in the plugin runtime, outside the author SDK and Core.
- Share only designated host contracts and explicitly exported provider contracts.
- Keep private dependencies in the plugin's collectible load context.
- Check native loading on the actual OS and process architecture.
- Inspect each plugin's loaded state instead of treating an aggregate load result as proof of activation.

## Marketplace changes

Read `docs/marketplace-v2.md` before changing manifests, catalogues, resolution or storage.

- Use schema `2` exclusively. Do not silently accept legacy marketplaces.
- Resolve the required dependency graph before changing installation state.
- Select one compatible asset per plugin. Detect the running process architecture.
- Require explicit source fallback for mixed releases when compiled assets do not match.
- Preserve pins, marketplace bindings and installed dependent constraints.
- Keep version directories immutable and user settings/data outside packages.
- Check hashes, archive paths, limits and manifest/catalogue agreement before activation.
- Preserve symlink protections, transaction recovery and rollback behavior.
- Never overwrite a loaded DLL or promise to undo arbitrary plugin side effects.

## Coding and documentation standards

- Follow `.editorconfig`, nullable annotations and warnings-as-errors.
- Use C# 14 conventions and the `Dmcbk` prefix for previously MCC-prefixed client types.
- Use `ILogger` and the owning translation system for library diagnostics and user text.
- Keep plugin text in the plugin's `lang` resources.
- Update XML documentation, examples and consumer references when public contracts change.
- Keep library messages with their owning package. Do not depend on MCC resources.
- Write each prose paragraph and list item on one physical line.
- Use clear, direct explanations. Apply Humanizer when available and ASD-STE100 principles to instructions.
- Keep procedures imperative, with one action per sentence.
- Do not use em dashes.
- Use concise commit subjects in the form `<type>: <description>`.
- Use `feat`, `fix`, `refactor`, `docs`, `test` or `chore` as the type.
- Keep commit subjects at 72 characters or fewer, with a lowercase imperative description and no trailing period.

DMCBK currently uses Roslyn and reflection for plugins. Native AOT is outside the current scope. Do not describe it as supported or add AOT requirements without an explicit task.

## Boundaries

### Always

- Check the actual APIs before documenting examples.
- Use isolated directories, ports and bounded waits for live tests.
- Check the exact packages and binaries used by consumers.
- Keep credentials, account files, token caches, raw logs and downloaded game artifacts outside Git.
- State which checks ran and their practical limits.

### Obtain authorization when it is absent

- Publish packages, create releases or push to a remote repository.
- Use public Minecraft servers or authenticated accounts for tests.
- Delete user data or change resources owned by another run.

Existing explicit user authorization remains valid. Do not ask again for actions that the user already authorized. Routine reversible edits and tests within the task do not require another approval.

### Never

- Weaken validation, archive protections or test expectations to hide a failure.
- Add server-specific hacks or broad exception suppression.
- Retain a stale session scope across reconnects.
- Add UI framework dependencies to the portable library packages.
- Stop or change a server, session or process owned by another run.
