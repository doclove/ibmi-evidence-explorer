using System.Reflection;
using IbmiEvidenceExplorer.Core;
using IbmiEvidenceExplorer.Core.Exploration;
using IbmiEvidenceExplorer.Infrastructure;
using IbmiEvidenceExplorer.Infrastructure.Ollama;

namespace IbmiEvidenceExplorer.Tests;

/// <summary>
/// Core must not depend on storage, networking, or an AI runtime. Only the F2 Ollama adapter
/// assembly may use networking (D-016); the CLI references it but never uses networking types itself.
/// </summary>
public class DependencyBoundaryTests
{
    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void CoreReferencesOnlyTheBaseClassLibrary()
    {
        var references = References(typeof(ISnapshotStore).Assembly);
        Assert.All(references, name => Assert.True(name.StartsWith("System", StringComparison.Ordinal) || name == "netstandard", name));
        Assert.DoesNotContain(references, name => name.StartsWith("IbmiEvidenceExplorer", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("System.Net", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(ISnapshotStore))]
    [InlineData(typeof(FileSystemSnapshotStore))]
    [InlineData(typeof(IbmiEvidenceExplorer.Cli.CliApp))]
    public void ProductAssembliesHaveNoNetworkOrThirdPartyDependencies(Type marker)
    {
        foreach (var name in References(marker.Assembly))
        {
            Assert.True(name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("IbmiEvidenceExplorer", StringComparison.Ordinal)
                || name is "netstandard" or "Microsoft.Win32.Primitives", name);
            Assert.False(name.StartsWith("System.Net", StringComparison.Ordinal), name);
        }
    }

    [Fact]
    public void OnlyTheOllamaAdapterUsesNetworkingAndItHasNoThirdPartyDependencies()
    {
        var references = References(typeof(OllamaChatModel).Assembly);
        Assert.Contains("System.Net.Http", references);
        Assert.All(references, name => Assert.True(name.StartsWith("System", StringComparison.Ordinal) || name == "IbmiEvidenceExplorer.Core", name));
        Assert.Equal(typeof(ILocalChatModel).Assembly, typeof(ISnapshotStore).Assembly);

        // The explorer and explanation orchestration live in Core and never see the adapter type.
        Assert.DoesNotContain(References(typeof(ExplorerService).Assembly), name => name.Contains("Ollama", StringComparison.Ordinal));
        Assert.DoesNotContain(References(typeof(FileSystemSnapshotStore).Assembly), name => name.Contains("Ollama", StringComparison.Ordinal));
    }
}
