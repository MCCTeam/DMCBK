using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using DMCBK.Marketplace;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class DevelopmentImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dmcbk-import-test-" + Guid.NewGuid().ToString("N"));
    private const string Manifest = """
        schema-version = 2
        id = "fetched"
        version = "1.2.0"
        kind = "source"
        target = "any"
        entry = "Fetched.cs"
        framework = "net10.0"
        api-version = "1.0"
        dmcbk = "*"
        umpk = "*"
        """;

    public DevelopmentImporterTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("auto-eat", false)]
    [InlineData("auto-eat@official", false)]
    [InlineData("someone/plugin", true)]
    [InlineData("someone/plugin@v1", true)]
    [InlineData("https://example.invalid/p.zip", true)]
    [InlineData("https://example.invalid/p.git#main", true)]
    public void DirectSourcesAreDistinctFromMarketplaceBindings(string input, bool expected)
        => Assert.Equal(expected, DevelopmentPackageImporter.IsDirect(input));

    [Fact]
    public async Task UrlImportCapturesDigestAndSeedsOneImmutableAsset()
    {
        byte[] bytes = Archive(("plugin.toml", Manifest), ("Fetched.cs", "// source"));
        var handler = new StubHandler(bytes);
        using var http = new HttpClient(handler);
        DevelopmentImport result = await new DevelopmentPackageImporter(http, _root).ImportAsync("https://example.invalid/p.zip");
        Assert.Equal(1, handler.Requests);
        Assert.Equal("fetched", result.Package.Manifest.Id);
        Assert.Equal(result.Revision, Assert.Single(result.Package.Release.Assets).Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.Package.ArchivePath));
    }

    [Fact]
    public async Task DirectoryImportSkipsBuildOutputAndRejectsOversizedPackages()
    {
        string source = CreateSource();
        Directory.CreateDirectory(Path.Combine(source, "obj"));
        File.WriteAllText(Path.Combine(source, "obj", "junk.txt"), "junk");
        using var http = new HttpClient(new StubHandler([]));
        DevelopmentImport result = await new DevelopmentPackageImporter(http, _root).ImportAsync(source);
        using ZipArchive zip = ZipFile.OpenRead(result.Package.ArchivePath);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("obj/", StringComparison.Ordinal));
        await Assert.ThrowsAsync<MarketplaceException>(() =>
            new DevelopmentPackageImporter(http, _root, new(DownloadBytes: 10)).ImportAsync(source));
    }

    [Fact]
    public async Task GitImportRecordsTheExactCommitAndDoesNotCopyGitMetadata()
    {
        string source = CreateSource();
        await Git("-C", source, "init", "-b", "main");
        await Git("-C", source, "add", ".");
        await Git("-C", source, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture");
        string commit = (await Git("-C", source, "rev-parse", "HEAD")).Trim();
        using var http = new HttpClient(new StubHandler([]));
        DevelopmentImport result = await new DevelopmentPackageImporter(http, _root).ImportAsync("git+" + new Uri(source).AbsoluteUri + "#" + commit);
        Assert.Equal(commit, result.Revision);
        using ZipArchive zip = ZipFile.OpenRead(result.Package.ArchivePath);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith(".git/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrLegacyManifestIsRejected(bool legacy)
    {
        using var http = new HttpClient(new StubHandler(legacy
            ? Archive(("plugin.toml", "id = \"old\"\nentry = \"Old.cs\""))
            : Archive(("README.md", "catalogue"))));
        await Assert.ThrowsAsync<MarketplaceException>(() =>
            new DevelopmentPackageImporter(http, _root).ImportAsync("https://example.invalid/p.zip"));
    }

    [Fact]
    public async Task DownloadLimitIsCheckedBeforeBuffering()
    {
        using var http = new HttpClient(new StubHandler(new byte[1024]));
        await Assert.ThrowsAsync<MarketplaceException>(() =>
            new DevelopmentPackageImporter(http, _root, new(DownloadBytes: 100)).ImportAsync("https://example.invalid/p.zip"));
    }

    private string CreateSource()
    {
        string source = Path.Combine(_root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "plugin.toml"), Manifest);
        File.WriteAllText(Path.Combine(source, "Fetched.cs"), "// source");
        return source;
    }
    private static async Task<string> Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
    private static byte[] Archive(params (string Path, string Text)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open()); writer.Write(file.Text); }
        return stream.ToArray();
    }
    private sealed class StubHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }
    }
    public void Dispose() => DevelopmentPackageImporter.DeleteTemporaryDirectory(_root);
}
