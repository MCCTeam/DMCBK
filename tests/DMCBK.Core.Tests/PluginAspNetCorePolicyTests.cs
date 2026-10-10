using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using DMCBK.PluginSdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class PluginAspNetCorePolicyTests
{
    [Fact]
    public async Task FlatHostSharesAspNetCoreRootOnlyWithDeclaredCapability()
    {
        string root = Path.Combine(Path.GetTempPath(), "dmcbk-aspnetcore-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (Environment.GetEnvironmentVariable("DMCBK_ASPNETCORE_POLICY_RESULT") is { } resultPath)
            {
                AssertFlatHostPolicy(root);
                File.WriteAllText(resultPath, "passed");
                return;
            }

            // A child process keeps the synthetic framework identity out of other tests.
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(PluginAspNetCorePolicyTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + GetType().FullName + "." + nameof(FlatHostSharesAspNetCoreRootOnlyWithDeclaredCapability));
            string result = Path.Combine(root, "result.txt");
            start.Environment["DMCBK_ASPNETCORE_POLICY_RESULT"] = result;
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            Assert.True(process.ExitCode == 0, await output + await errors);
            Assert.Equal("passed", File.ReadAllText(result));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertFlatHostPolicy(string root)
    {
        var references = new CompilationReferenceProvider(AppContext.BaseDirectory).GetReferencePaths()
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Microsoft.AspNetCore",
            [CSharpSyntaxTree.ParseText("public sealed class FlatHostFrameworkMarker { }")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        string path = Path.Combine(root, "Microsoft.AspNetCore.dll");
        using (var stream = File.Create(path))
        {
            var emitted = compilation.Emit(stream);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        }
        Assembly hostAssembly;
        using (var stream = File.OpenRead(path))
            hostAssembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        var allowed = new CollectiblePluginLoadContext("aspnetcore-allowed", null, [], includeAspNetCore: true);
        var denied = new CollectiblePluginLoadContext("aspnetcore-denied", null, []);
        try
        {
            Assert.Same(hostAssembly, allowed.LoadFromAssemblyName(hostAssembly.GetName()));
            Assert.Throws<FileNotFoundException>(() => denied.LoadFromAssemblyName(hostAssembly.GetName()));
            InvalidDataException duplicate = Assert.Throws<InvalidDataException>(() => PluginAssemblyPolicy.ValidatePackage(root, includeAspNetCore: true));
            Assert.Equal("plugin.duplicate-host-contract:Microsoft.AspNetCore", duplicate.Message);
        }
        finally
        {
            allowed.Unload();
            denied.Unload();
        }
    }
}
