# Publish DMCBK to NuGet

The repository has two workflows. `build.yml` builds the solution and runs unit tests on Windows, Linux and macOS. `publish.yml` checks a release tag, builds packages and publishes them through NuGet trusted publishing.

All nine packages share the version in `Directory.Build.props`. Plugin versions are independent. The source repository uses `master`.

## Configure GitHub

1. Open [DMCBK repository settings](https://github.com/MCCTeam/DMCBK/settings).
2. Open **Environments**.
3. Create an environment named `nuget`.
4. Add an environment secret named `NUGET_USER`.
5. Set its value to your NuGet.org profile name.
6. Add a required reviewer if your GitHub plan supports that rule for private repositories.

Use the NuGet profile name, not your email address. The publish job requests `id-token: write`. GitHub supplies the short-lived identity token. The workflow obtains a temporary NuGet API key.

## Configure NuGet.org

1. Sign in to [NuGet.org](https://www.nuget.org/).
2. Open **Trusted Publishing** from your account menu.
3. Create a GitHub policy with the values below.
4. Select the owner that will own the DMCBK packages.
5. Permit package creation and new versions for `DMCBK*`.

| Field | Value |
| --- | --- |
| Repository owner | `MCCTeam` |
| Repository | `DMCBK` |
| Workflow file | `publish.yml` |
| Environment | `nuget` |
| Package pattern | `DMCBK*` |

Enter only `publish.yml` as the workflow file name. A policy for a private repository can start with a seven-day activation window. A successful publication completes activation. Restart the window if it expires before your first publication. See [Microsoft's trusted publishing guide](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) for policy ownership and activation details.

The GitHub repository can stay private. NuGet.org packages are public. The current package configuration includes SourceLink and can embed untracked source files. Review package contents before publication if the source must remain private.

## Create a release

1. Set the release version in `Directory.Build.props`.
2. Update matching internal versions in `Directory.Packages.props`.
3. Run the solution build and unit tests.
4. Commit the version change on `master`.
5. Create an annotated tag that matches the version.
6. Push that tag.
7. Review the publish job in GitHub Actions.

```bash
git tag -a v0.1.0-preview.1 -m "DMCBK 0.1.0-preview.1"
git push origin v0.1.0-preview.1
```

The workflow rejects a tag outside `master` history or a tag that differs from the package version. Manual dispatch accepts an existing annotated tag through `release_tag`. Build and unit tests run before publication. The protected job packs the packages and obtains a temporary key with `NuGet/login@v1`.

Do not reuse a version for changed package contents. NuGet.org treats a published package version as immutable. The workflow skips already published versions during a retry. Check all nine package pages after a partial retry.
