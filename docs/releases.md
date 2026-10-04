# Create a DMCBK release

All nine NuGet packages share the version in `Directory.Build.props`. Matching DMCBK dependencies use that version in `Directory.Packages.props`. Plugin versions are independent.

## Prepare the release

1. Set the release version in `Directory.Build.props`.
2. Update matching DMCBK package versions in `Directory.Packages.props`.
3. Restore the solution.
4. Build the solution.
5. Run the unit tests.

```bash
dotnet restore DMCBK.slnx
dotnet build DMCBK.slnx -c Release --no-restore
dotnet test DMCBK.slnx -c Release --no-build
```

6. Review the version changes.
7. Commit the changes on `master`.
8. Push `master` before pushing the release tag.

```bash
git add Directory.Build.props Directory.Packages.props
git commit -m "chore: prepare release 0.1.0-preview.1"
git push origin master
```

Replace the example version with your release version.

## Tag and publish

1. Create an annotated tag named `v<version>` at the release commit.
2. Push the tag.

```bash
git tag -a v0.1.0-preview.1 -m "DMCBK 0.1.0-preview.1"
git push origin v0.1.0-preview.1
```

3. Open [GitHub Actions](https://github.com/MCCTeam/DMCBK/actions/workflows/publish.yml).
4. Review the **Publish** workflow for that tag.
5. Approve the publication if the environment requests a review.
6. Check that all nine package versions are available on NuGet.org.

The workflow checks the tag annotation, ancestry in `master` history and agreement with the package version. It runs builds and unit tests on Windows, Linux and macOS before packing and publishing.

## Retry an existing release

1. Open the **Publish** workflow in GitHub Actions.
2. Select **Run workflow**.
3. Enter the existing annotated tag in `release_tag`.
4. Start the workflow.
5. Check all nine package versions after completion.

The workflow skips package versions that already exist. Use the same tag to retry a partial publication.

Use a new version for changed package contents. Do not move a published release tag or reuse its package version.
