using System.Diagnostics;
using Passenger.Core;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>On Maven, Java sees Kotlin declarations only through the class folders the bridge attaches to JDT LS.</summary>
[Collection(JvmTrustCollection.Name)]
public sealed class KotlinClassBridgeTests
{
    public static bool RunLanguageAcceptance => Environment.GetEnvironmentVariable("WAYFARER_LANGUAGE_ACCEPTANCE") == "1";

    [Fact(SkipUnless = nameof(RunLanguageAcceptance), Skip = "Set WAYFARER_LANGUAGE_ACCEPTANCE=1 and install JDT LS to run the Maven wrapper and the Java server.")]
    public async Task JavaCompletesKotlinDeclarationsAndSeesTheirChangesAfterRefresh()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string root = Directory.CreateTempSubdirectory("wayfarer kotlin bridge ").FullName;
        try
        {
            JvmFixtures.Copy("maven", root);
            WorkspaceTrust.Trust(root);
            WorkspaceSnapshot snapshot = await new ProjectHostImporter().ImportAsync(root, 1, cancel);
            Assert.True(KotlinClassBridge.Applies(snapshot));
            string service = Path.Combine(root, "service/src/main/java/example/Service.java");
            string greeting = Path.Combine(root, "library/src/main/kotlin/example/Greeting.kt");

            using var bridge = new KotlinClassBridge();
            await using JvmLanguageClient java = await JvmLanguageClient.StartAsync(root, "java", cancel);
            Assert.Null(java.BundleProblem);
            IReadOnlyList<string> attached = await bridge.RefreshAsync(java, snapshot, cancel: cancel);
            Assert.Equal([Path.GetFullPath(Path.Combine(root, "library"))], attached);

            // GreetingKt.message is declared only in Kotlin; Service.java calls it.
            await JvmMeasurementTests.CompleteAsync(java, "java", service, 17, 26, "message", cancel);

            // A new Kotlin function reaches Java after the next refresh, as it does after a save.
            await File.AppendAllTextAsync(greeting, "\nfun farewell(name: String) = \"Bye \" + name\n", cancel);
            await bridge.RefreshAsync(java, snapshot, cancel: cancel);
            var request = new { textDocument = new { uri = new Uri(service).AbsoluteUri }, position = new { line = 17, character = 26 } };
            var clock = Stopwatch.StartNew();
            while (!JvmMeasurementTests.Labels(await java.RequestAsync("textDocument/completion", request, cancel))
                .Any(label => label.StartsWith("farewell", StringComparison.Ordinal)))
            {
                Assert.True(clock.Elapsed < TimeSpan.FromMinutes(2), "Java never completed the new Kotlin function 'farewell'.");
                await Task.Delay(500, cancel);
            }

            // Eclipse metadata stays out of the user's tree; JDT LS keeps it in its own data folder.
            string[] metadata = [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) is ".classpath" or ".project" or ".settings" or ".factorypath")];
            Assert.Empty(metadata);
        }
        finally
        {
            WorkspaceTrust.TakeBack(root);
            Directory.Delete(root, recursive: true);
        }
    }
}
