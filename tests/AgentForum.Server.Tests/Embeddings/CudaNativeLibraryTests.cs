using AgentForum.Server.Embeddings;

namespace AgentForum.Server.Tests.Embeddings;

public sealed class CudaNativeLibraryTests
{
    [Fact]
    public void ResolveLibraryPath_ReturnsPackagedCuda12Library()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-forum-cuda-{Guid.NewGuid():N}");
        var relativePath = GetCurrentPlatformLibraryPath();
        var expectedPath = Path.Combine(root, relativePath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllBytes(expectedPath, []);

            var result = CudaNativeLibrary.ResolveLibraryPath(root);

            Assert.Equal(Path.GetFullPath(expectedPath), result);
            Assert.Contains("cuda12", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveLibraryPath_RejectsMissingCuda12Library()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-forum-cuda-{Guid.NewGuid():N}");

        var exception = Assert.Throws<FileNotFoundException>(
            () => CudaNativeLibrary.ResolveLibraryPath(root));

        Assert.Contains("CUDA 12", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cuda12", exception.FileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCurrentPlatformLibraryPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine("runtimes", "win-x64", "native", "cuda12", "llama.dll");
        }

        if (OperatingSystem.IsLinux())
        {
            return Path.Combine("runtimes", "linux-x64", "native", "cuda12", "libllama.so");
        }

        throw new PlatformNotSupportedException();
    }
}
