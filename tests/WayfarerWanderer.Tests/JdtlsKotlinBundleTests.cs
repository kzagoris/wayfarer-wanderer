using System.Text.Json;
using Passenger.Core;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>
/// The Kotlin class extension compiles against the installed JDT LS and uses its internal
/// <c>IDelegateCommandHandler</c>. A Java extension update can break either step, so this runs in the
/// default suite whenever JDT LS is installed and fails loudly instead of letting Maven quietly lose Kotlin.
/// </summary>
[Collection(JvmTrustCollection.Name)]
public sealed class JdtlsKotlinBundleTests
{
    public static bool JdtInstalled
    {
        get
        {
            try { return Directory.Exists(Path.Combine(JvmLanguageServers.JdtHome(), "plugins")); }
            catch (InvalidOperationException) { return false; }
        }
    }

    [Fact(SkipUnless = nameof(JdtInstalled), Skip = "JDT LS is not installed (VS Code redhat.java or WAYFARER_JDTLS_HOME).")]
    public async Task ExtensionBuildsAndRegistersItsCommandInTheInstalledJavaServer()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string root = Directory.CreateTempSubdirectory("wayfarer jdtls bundle ").FullName;
        try
        {
            WorkspaceTrust.Trust(root);
            await using JvmLanguageClient java = await JvmLanguageClient.StartAsync(root, "java", cancel);
            Assert.True(java.BundleProblem is null, java.BundleProblem);

            // An unregistered command is an error reply; the loaded handler answers with the modules it attached.
            JsonElement reply = await java.RequestAsync("workspace/executeCommand",
                new { command = KotlinClassBridge.Command, arguments = Array.Empty<string>() }, cancel);
            Assert.Equal(JsonValueKind.Array, reply.ValueKind);
            Assert.Equal(0, reply.GetArrayLength());
        }
        finally
        {
            WorkspaceTrust.TakeBack(root);
            Directory.Delete(root, recursive: true);
        }
    }
}
