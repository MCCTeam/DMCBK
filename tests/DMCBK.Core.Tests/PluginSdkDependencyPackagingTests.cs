using System.IO.Compression;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class PluginSdkDependencyPackagingTests
{
    [Fact]
    public void PacksSdkDependenciesWithoutBundlingFrameworkContracts()
    {
        string root = Path.Combine(Path.GetTempPath(), "dmcbk-sdk-package-" + Guid.NewGuid().ToString("N"));
        try
        {
            string payload = Path.Combine(root, "payload");
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "plugin.toml"), """
                schema-version = 2
                id = "sdk-package-test"
                version = "1.0.0"
                api-version = "1.0"
                kind = "compiled"
                target = "any"
                framework = "net10.0"
                entry = "Entry.dll"
                dmcbk = "*"
                umpk = "*"
                """);
            File.Copy(typeof(PluginSdkDependencyPackagingTests).Assembly.Location, Path.Combine(payload, "Entry.dll"));
            File.Copy(typeof(System.ClientModel.ApiKeyCredential).Assembly.Location, Path.Combine(payload, "System.ClientModel.dll"));
            File.Copy(typeof(BinaryData).Assembly.Location, Path.Combine(payload, "System.Memory.Data.dll"));
            File.Copy(typeof(IPlugin).Assembly.Location, Path.Combine(payload, "DMCBK.PluginSdk.dll"));
            PackedPlugin package = PluginPackageBuilder.Pack(payload, payload, Path.Combine(root, "output"), "compiled", "any", new Uri("https://assets.test/"));
            using var archive = ZipFile.OpenRead(package.ArchivePath);
            Assert.Contains(archive.Entries, entry => entry.FullName == "System.ClientModel.dll");
            Assert.Contains(archive.Entries, entry => entry.FullName == "System.Memory.Data.dll");
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "DMCBK.PluginSdk.dll");
            string extracted = Path.Combine(root, "extracted");
            ZipFile.ExtractToDirectory(package.ArchivePath, extracted);
            PluginAssemblyPolicy.ValidatePackage(extracted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
