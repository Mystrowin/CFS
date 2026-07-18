using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cfs.Core;

internal static class Lzma2Compressor
{
    private const string ToolRelativePath = "tools/7zr.exe";

    public static byte[] Compress(byte[] bytes)
    {
        return CompressRaw(bytes);
    }

    public static byte[] CompressRaw(byte[] bytes)
    {
        var result = NativeCompress(bytes, (nuint)bytes.Length, out var output, out var outputSize);
        if (result != 0) throw new CfsArchiveException($"In-process LZMA2 compression failed: {result}.");
        try { var managed = new byte[(int)outputSize]; Marshal.Copy(output, managed, 0, managed.Length); return managed; }
        finally { NativeFree(output); }
    }

    public static byte[] DecompressRaw(byte[] bytes, long expectedSize)
    {
        if (expectedSize < 0 || expectedSize > int.MaxValue) throw new CfsArchiveException("Invalid LZMA2 output size.");
        var output = new byte[(int)expectedSize];
        var result = NativeDecompress(bytes, (nuint)bytes.Length, output, (nuint)output.Length, out var actual);
        if (result != 0 || actual != (nuint)output.Length) throw new CfsArchiveException("In-process LZMA2 decompression failed.");
        return output;
    }

    [DllImport("cfs-lzma.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cfs_lzma2_compress")]
    private static extern int NativeCompress(byte[] input, nuint inputSize, out IntPtr output, out nuint outputSize);
    [DllImport("cfs-lzma.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cfs_lzma2_decompress")]
    private static extern int NativeDecompress(byte[] input, nuint inputSize, byte[] output, nuint outputSize, out nuint actualSize);
    [DllImport("cfs-lzma.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cfs_lzma_free")]
    private static extern void NativeFree(IntPtr output);

    /* Legacy 7z-container decoder remains below for prototype archives. */
    private static byte[] CompressLegacy(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return [];
        }

        using var workspace = CompressionWorkspace.Create();
        var inputPath = Path.Combine(workspace.Path, "input.bin");
        var archivePath = Path.Combine(workspace.Path, "block.7z");
        File.WriteAllBytes(inputPath, bytes);

        Run7zr(workspace.Path, "a", "-t7z", "-m0=LZMA2", "-mx=5", "-ms=off", archivePath, inputPath);
        return File.ReadAllBytes(archivePath);
    }

    public static byte[] Decompress(byte[] bytes, long expectedSize)
    {
        if (bytes.Length == 0)
        {
            return [];
        }
        if (expectedSize < 0 || expectedSize > int.MaxValue)
            throw new CfsArchiveException("Invalid legacy LZMA2 output size.");

        using var workspace = CompressionWorkspace.Create();
        var archivePath = Path.Combine(workspace.Path, "block.7z");
        File.WriteAllBytes(archivePath, bytes);

        return Run7zrToBytes(workspace.Path, expectedSize, "e", "-so", archivePath);
    }

    private static void Run7zr(string workingDirectory, params string[] arguments)
    {
        using var process = Start7zr(workingDirectory, redirectOutput: true, arguments);
        _ = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new CfsArchiveException($"LZMA2 compression failed: {stderr.Trim()}");
        }
    }

    private static byte[] Run7zrToBytes(string workingDirectory, long maximumOutputBytes, params string[] arguments)
    {
        using var process = Start7zr(workingDirectory, redirectOutput: true, arguments);
        using var output = new MemoryStream(checked((int)Math.Min(maximumOutputBytes, 16L * 1024 * 1024)));
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = process.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length > maximumOutputBytes - read)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new CfsArchiveException("Legacy LZMA2 output exceeded the manifest size bound.");
            }
            output.Write(buffer, 0, read);
        }
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new CfsArchiveException($"LZMA2 decompression failed: {stderr.Trim()}");
        }

        if (output.Length != maximumOutputBytes)
            throw new CfsArchiveException("Legacy LZMA2 output did not match the manifest size.");
        return output.ToArray();
    }

    private static Process Start7zr(string workingDirectory, bool redirectOutput, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveToolPath(),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = redirectOutput,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
               ?? throw new CfsArchiveException("Could not start LZMA2 compressor.");
    }

    private static string ResolveToolPath()
    {
        var configured = Environment.GetEnvironmentVariable("CFS_7ZR_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var outputPath = Path.Combine(AppContext.BaseDirectory, ToolRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var repoPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "third_party", "lzma-sdk", "bin", "x64", "7zr.exe"));
        if (File.Exists(repoPath))
        {
            return repoPath;
        }

        throw new CfsArchiveException("The official LZMA SDK 7zr.exe tool was not found.");
    }

    private sealed class CompressionWorkspace : IDisposable
    {
        private CompressionWorkspace(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static CompressionWorkspace Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CFS", "lzma2", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new CompressionWorkspace(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
