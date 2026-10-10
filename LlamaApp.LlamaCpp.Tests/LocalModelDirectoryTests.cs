using LlamaApp.Llama;
using Xunit;

namespace LlamaApp.LlamaCpp.Tests;

public sealed class LocalModelDirectoryTests
{
    [Fact]
    public void CombinesFoldersAndDeduplicatesOverlappingRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "llama-multi-folder-test-" + Guid.NewGuid());
        try
        {
            var first = Path.Combine(root, "A", "First.gguf");
            var second = Path.Combine(root, "B", "Second.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            File.WriteAllText(first, "GGUF");
            File.WriteAllText(second, "GGUF");

            var models = LocalModelDirectory.Scan([Path.Combine(root, "missing"),
                Path.Combine(root, "A"), root, Path.Combine(root, "B")]);
            Assert.Equal(2, models.Count);
            Assert.Contains(first, models.Values);
            Assert.Contains(second, models.Values);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindsNestedWeightsWithStableDistinctIdsAndSkipsProjectors()
    {
        var root = Path.Combine(Path.GetTempPath(), "llama-local-model-test-" + Guid.NewGuid());
        try
        {
            var first = Path.Combine(root, "Publisher A", "Model.gguf");
            var second = Path.Combine(root, "Publisher B", "Model.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            File.WriteAllText(first, "GGUF");
            File.WriteAllText(second, "GGUF");
            File.WriteAllText(Path.Combine(root, "Publisher B", "mmproj-Model.gguf"), "GGUF");

            var models = LocalModelDirectory.Scan([root]);
            Assert.Equal(2, models.Count);
            Assert.Contains(first, models.Values);
            Assert.Contains(second, models.Values);
            Assert.Equal(models.Keys.Order(), LocalModelDirectory.Scan([root]).Keys.Order());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
