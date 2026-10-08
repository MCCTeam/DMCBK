using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Tomlet;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class PluginStorageTests
{
    [Fact]
    public void Save_PreservesAnOpenSnapshotAndPublishesTheCompleteReplacement()
    {
        string root = Path.Combine(Path.GetTempPath(), "dmcbk-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new PluginStorage(root, NullLogger.Instance);
            string oldValue = new('a', 65536);
            string newValue = new('b', 65536);
            storage.Set("value", oldValue);
            storage.Save();
            string path = Path.Combine(root, "storage.toml");
            using (var snapshot = new StreamReader(new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
            {
                storage.Set("value", newValue);
                storage.Save();

                // A reader that opened the old file must never observe truncation or replacement bytes.
                Dictionary<string, string> oldStore = TomletMain.To<Dictionary<string, string>>(snapshot.ReadToEnd());
                Assert.Equal(oldValue, oldStore["value"]);
                Dictionary<string, string> newStore = TomletMain.To<Dictionary<string, string>>(File.ReadAllText(path));
                Assert.Equal(newValue, newStore["value"]);
            }

            var reloaded = new PluginStorage(root, NullLogger.Instance);
            Assert.True(reloaded.TryGet("value", out string? loaded));
            Assert.Equal(newValue, loaded);
            Assert.Equal("storage.toml", Path.GetFileName(Assert.Single(Directory.GetFiles(root))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
