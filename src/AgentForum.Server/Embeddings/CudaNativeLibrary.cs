using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using LLama.Native;

namespace AgentForum.Server.Embeddings;

internal static class CudaNativeLibrary
{
    private static readonly List<nint> DependencyHandles = [];

    internal static void Configure()
    {
        var libraryPath = ResolveLibraryPath(AppContext.BaseDirectory);
        LoadDependencies(AppContext.BaseDirectory, Path.GetDirectoryName(libraryPath)!);

        NativeLibraryConfig.All.WithLogCallback((level, message) =>
            Console.Error.Write($"[LLamaSharp Native] [{level}] {message}"));

        // Pin LLamaSharp to the packaged CUDA 12 backend. Automatic selection can
        // silently choose a CPU backend when a different CUDA toolkit is detected.
        NativeLibraryConfig.LLama.WithLibrary(libraryPath);
    }

    internal static string ResolveLibraryPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                $"The packaged CUDA 12 backend supports only x64 processes; current architecture: {RuntimeInformation.ProcessArchitecture}.");
        }

        var relativePath = OperatingSystem.IsWindows()
            ? Path.Combine("runtimes", "win-x64", "native", "cuda12", "llama.dll")
            : OperatingSystem.IsLinux()
                ? Path.Combine("runtimes", "linux-x64", "native", "cuda12", "libllama.so")
                : throw new PlatformNotSupportedException(
                    "The packaged CUDA 12 backend supports only Windows x64 and Linux x64.");

        var libraryPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        if (!File.Exists(libraryPath))
        {
            throw new FileNotFoundException(
                "The packaged LLamaSharp CUDA 12 native library was not found. " +
                "Restore and publish the LLamaSharp.Backend.Cuda12 package before starting the server.",
                libraryPath);
        }

        return libraryPath;
    }

    private static void LoadDependencies(string baseDirectory, string cudaDirectory)
    {
        foreach (var runtimeLibrary in GetCudaRuntimeLibraryNames())
        {
            if (!NativeLibrary.TryLoad(runtimeLibrary, out var handle))
            {
                throw new DllNotFoundException(
                    $"CUDA 12 runtime library '{runtimeLibrary}' was not found. " +
                    "Install NVIDIA CUDA Toolkit 12.x, open a new terminal, and start the server again.");
            }

            DependencyHandles.Add(handle);
        }

        LoadPackagedDependency(Path.Combine(
            cudaDirectory,
            OperatingSystem.IsWindows() ? "ggml-base.dll" : "libggml-base.so"));

        var cpuVariant = Avx512F.IsSupported
            ? "avx512"
            : Avx2.IsSupported
                ? "avx2"
                : Avx.IsSupported
                    ? "avx"
                    : "noavx";
        LoadPackagedDependency(Path.Combine(
            baseDirectory,
            "runtimes",
            OperatingSystem.IsWindows() ? "win-x64" : "linux-x64",
            "native",
            cpuVariant,
            OperatingSystem.IsWindows() ? "ggml-cpu.dll" : "libggml-cpu.so"));

        LoadPackagedDependency(Path.Combine(
            cudaDirectory,
            OperatingSystem.IsWindows() ? "ggml-cuda.dll" : "libggml-cuda.so"));
    }

    private static string[] GetCudaRuntimeLibraryNames() => OperatingSystem.IsWindows()
        ? ["cudart64_12.dll", "cublasLt64_12.dll", "cublas64_12.dll"]
        : ["libcudart.so.12", "libcublasLt.so.12", "libcublas.so.12"];

    private static void LoadPackagedDependency(string libraryPath)
    {
        if (!File.Exists(libraryPath))
        {
            throw new FileNotFoundException(
                "A native dependency required by the packaged CUDA 12 backend was not found.",
                libraryPath);
        }

        DependencyHandles.Add(NativeLibrary.Load(libraryPath));
    }
}
