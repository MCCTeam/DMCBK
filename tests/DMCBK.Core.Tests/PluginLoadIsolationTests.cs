using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using DMCBK.PluginSdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class PluginLoadIsolationTests
{
    [Fact]
    public void PrivateVersionsDoNotUnifyWithEachOtherOrAnAlreadyLoadedHostAssembly()
    {
        using var fixture = new Fixture();
        string identity = "PrivateProbe_" + Guid.NewGuid().ToString("N");
        string first = fixture.Compile(identity, "one", """
            using System.Reflection;
            [assembly: AssemblyVersion("1.0.0.0")]
            namespace PrivateFixture;
            public static class Api { public static string Value => "one"; }
            """);
        string second = fixture.Compile(identity, "two", """
            using System.Reflection;
            [assembly: AssemblyVersion("2.0.0.0")]
            namespace PrivateFixture;
            public static class Api { public static string Value => "two"; }
            """);
        using (var stream = File.OpenRead(first)) AssemblyLoadContext.Default.LoadFromStream(stream);
        AssertIsolation(identity, first, second);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertIsolation(string identity, string first, string second)
    {
        var one = new CollectiblePluginLoadContext("one", null, [first]);
        var two = new CollectiblePluginLoadContext("two", null, [second]);
        try
        {
            Assembly loadedOne = one.LoadFromAssemblyName(new AssemblyName(identity));
            Assembly loadedTwo = two.LoadFromAssemblyName(new AssemblyName(identity));
            Assert.Equal("one", loadedOne.GetType("PrivateFixture.Api")!.GetProperty("Value")!.GetValue(null));
            Assert.Equal("two", loadedTwo.GetType("PrivateFixture.Api")!.GetProperty("Value")!.GetValue(null));
            Assert.NotSame(loadedOne, loadedTwo);
            Assert.Same(typeof(IPlugin).Assembly, two.LoadFromAssemblyName(typeof(IPlugin).Assembly.GetName()));
            Assert.True(one.IsCollectible && two.IsCollectible);
        }
        finally { one.Unload(); two.Unload(); }
    }

    [Fact]
    public async Task NativeDependencyResolvesThroughPackagedDepsJson()
    {
        if (Environment.GetEnvironmentVariable("DMCBK_NATIVE_PROBE_ENTRY") is { } childEntry)
        {
            AssertNative(childEntry);
            string result = Environment.GetEnvironmentVariable("DMCBK_NATIVE_PROBE_RESULT")
                ?? throw new InvalidOperationException("The native probe result path is missing.");
            File.WriteAllText(result, "passed");
            return;
        }

        using var fixture = new Fixture();
        string nativeRoot = Path.Combine(fixture.Root, "native"); Directory.CreateDirectory(nativeRoot);
        await File.WriteAllTextAsync(Path.Combine(nativeRoot, "CMakeLists.txt"), """
            cmake_minimum_required(VERSION 3.16)
            project(dmcbk_native_probe C)
            add_library(dmcbk_native_probe SHARED probe.c)
            """);
        await File.WriteAllTextAsync(Path.Combine(nativeRoot, "probe.c"), """
            #ifdef _WIN32
            __declspec(dllexport)
            #endif
            int probe_value(void) { return 42; }
            """);
        var configure = new List<string> { "-S", nativeRoot, "-B", Path.Combine(nativeRoot, "build") };
        if (OperatingSystem.IsWindows()) configure.AddRange(["-A", RuntimeInformation.ProcessArchitecture == Architecture.X86 ? "Win32" : "x64"]);
        await RunCmakeAsync(configure);
        await RunCmakeAsync(["--build", Path.Combine(nativeRoot, "build"), "--config", "Release"]);
        string nativeName = OperatingSystem.IsWindows() ? "dmcbk_native_probe.dll"
            : OperatingSystem.IsMacOS() ? "libdmcbk_native_probe.dylib" : "libdmcbk_native_probe.so";
        string built = Directory.GetFiles(Path.Combine(nativeRoot, "build"), nativeName, SearchOption.AllDirectories).Single();
        string entry = fixture.Compile("NativeEntry", "package", """
            using System.Runtime.InteropServices;
            public static class NativeApi
            {
                [DllImport("dmcbk_native_probe", CallingConvention = CallingConvention.Cdecl)]
                public static extern int probe_value();
            }
            """);
        string package = Path.GetDirectoryName(entry)!;
        string rid = PlatformTarget.Detect();
        string relativeNative = "runtimes/" + rid + "/native/" + nativeName;
        string destination = Path.Combine(package, nativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(built, destination);
        string framework = ".NETCoreApp,Version=v10.0/" + rid;
        object metadata = new
        {
            runtimeTarget = new { name = framework, signature = "" },
            targets = new Dictionary<string, object>
            {
                [framework] = new Dictionary<string, object>
                {
                    ["NativeEntry/1.0.0"] = new
                    {
                        runtime = new Dictionary<string, object> { ["NativeEntry.dll"] = new { } },
                        native = new Dictionary<string, object> { [relativeNative] = new { } },
                    },
                }
            },
            libraries = new Dictionary<string, object> { ["NativeEntry/1.0.0"] = new { type = "project", serviceable = false, sha512 = "" } },
        };
        await File.WriteAllTextAsync(Path.ChangeExtension(entry, ".deps.json"), JsonSerializer.Serialize(metadata));
        await AssertNativeInChildProcessAsync(entry);
    }

    // Native handles can outlive managed contexts on Windows. Run the real
    // loader in a child test process so cleanup follows process termination.
    private static async Task AssertNativeInChildProcessAsync(string entry)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(PluginLoadIsolationTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName="
            + typeof(PluginLoadIsolationTests).FullName + "." + nameof(NativeDependencyResolvesThroughPackagedDepsJson));
        string resultPath = Path.ChangeExtension(entry, ".probe-result");
        start.Environment["DMCBK_NATIVE_PROBE_ENTRY"] = entry;
        start.Environment["DMCBK_NATIVE_PROBE_RESULT"] = resultPath;
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await errors);
        Assert.Equal("passed", File.ReadAllText(resultPath));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertNative(string entry)
    {
        var context = new CollectiblePluginLoadContext("native", entry, []);
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(entry);
            Assert.Equal(42, assembly.GetType("NativeApi")!.GetMethod("probe_value")!.Invoke(null, null));
        }
        finally { context.Unload(); }
    }

    private static async Task RunCmakeAsync(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("cmake") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "dmcbk-isolation-" + Guid.NewGuid().ToString("N"));
        public string Compile(string identity, string directory, string source)
        {
            string folder = Path.Combine(Root, directory); Directory.CreateDirectory(folder);
            var references = new CompilationReferenceProvider(AppContext.BaseDirectory).GetReferencePaths().Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create(identity, [CSharpSyntaxTree.ParseText(source)], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            string path = Path.Combine(folder, identity + ".dll");
            using var stream = File.Create(path);
            var result = compilation.Emit(stream);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return path;
        }
        public void Dispose()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                try { Directory.Delete(Root, recursive: true); return; }
                catch (Exception exception) when (attempt < 4 && exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(20);
                }
            }
        }
    }
}
