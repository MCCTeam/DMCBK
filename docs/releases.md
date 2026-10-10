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
git commit -m "chore: prepare release 0.1.0-preview.7"
git push origin master
```

Replace the example version with your release version.

## Tag and publish

1. Create an annotated tag named `v<version>` at the release commit.
2. Push the tag.

```bash
git tag -a v0.1.0-preview.7 -m "DMCBK 0.1.0-preview.7"
git push origin v0.1.0-preview.7
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

## Check the release result

The release tag identifies one commit. The package version identifies one immutable NuGet release. Keep those identities together in release notes or your release record.

1. Check the build result for each operating system.
2. Check the unit test result for each operating system.
3. Check the package job's final status.
4. Check all nine package IDs at the intended version.
5. Restore a small consumer with that exact version.
6. Build the consumer.
7. Run the relevant sample verification.

NuGet indexing can delay when a new package appears in search. A successful publish job and a working restore provide different evidence. Record both results when you check a release.

If publication stops after some packages upload, rerun the same tag as described above. If source or package contents need a correction, prepare a new version and tag. Do not replace an already published package.

## Keep plugin releases separate

A DMCBK release publishes the library packages. It does not publish every plugin that uses those packages. A plugin author chooses the plugin version, compatible DMCBK range and assets independently.

For plugin release steps, use [the plugin packaging chapter](plugins/tutorial/07-package-and-release.md) and [marketplace v2](marketplace-v2.md). A binary plugin must be rebuilt when its required public contract changes.
