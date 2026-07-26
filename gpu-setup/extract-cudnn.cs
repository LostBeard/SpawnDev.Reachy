// Re-creates SpawnDev.Reachy.Rose/gpu-runtime/cudnn from the two zips saved next
// to this script, so the GPU voice-clone path works on a fresh machine.
//
//   dotnet run gpu-setup/setup-gpu.cs
//
// It extracts cuDNN 9's runtime DLLs plus zlibwapi.dll into gpu-runtime/cudnn.
// The Rose .csproj then copies that folder next to onnxruntime on every build,
// which is where the CUDA execution provider looks for cuDNN. See GPU-SETUP.md.
//
// Prerequisite the script cannot supply: the CUDA 13.x toolkit installed (its
// bin\x64 on PATH provides cudart/cublas). cuDNN + zlib are the only extra bits.

using System.IO.Compression;

// dotnet run compiles the single file to a temp dir, so find the real gpu-setup
// folder (the one holding the zips) relative to the working directory instead.
var scriptDir = FindDir(Directory.GetCurrentDirectory());
if (scriptDir is null) { Console.WriteLine("Run this from the SpawnDev.Reachy repo root (or gpu-setup/): could not find the cuDNN zip."); return 1; }
var dest = Path.GetFullPath(Path.Combine(scriptDir, "..", "SpawnDev.Reachy.Rose", "gpu-runtime", "cudnn"));
Directory.CreateDirectory(dest);

var cudnnZip = Directory.GetFiles(scriptDir, "cudnn-*cuda13*.zip").FirstOrDefault();
var zlibZip = Directory.GetFiles(scriptDir, "zlib*x64*.zip").FirstOrDefault();

if (cudnnZip is null) { Console.WriteLine("MISSING: cudnn-*_cuda13-archive .zip in gpu-setup/. See GPU-SETUP.md for the NVIDIA redist URL."); return 1; }
if (zlibZip is null) { Console.WriteLine("MISSING: zlib123dllx64.zip in gpu-setup/. See GPU-SETUP.md for the source URL."); return 1; }

int n = 0;
n += ExtractDlls(cudnnZip, dest, mustContain: "/bin/");   // cuDNN runtime DLLs live under .../bin/
n += ExtractDlls(zlibZip, dest, mustContain: "zlibwapi"); // the WINAPI zlib cuDNN delay-loads

Console.WriteLine($"\nExtracted {n} DLLs -> {dest}");
Console.WriteLine("Now build Rose (dotnet build -c Release) - the csproj copies these next to onnxruntime.");
return 0;

static int ExtractDlls(string zipPath, string dest, string mustContain)
{
    using var zip = ZipFile.OpenRead(zipPath);
    int count = 0;
    foreach (var e in zip.Entries)
    {
        var name = e.FullName.Replace('\\', '/');
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
        if (!name.Contains(mustContain, StringComparison.OrdinalIgnoreCase)) continue;
        var outPath = Path.Combine(dest, Path.GetFileName(name));
        e.ExtractToFile(outPath, overwrite: true);
        Console.WriteLine($"  {Path.GetFileName(name)}");
        count++;
    }
    return count;
}

// Find the folder holding the zips: check the working dir, its gpu-setup subfolder,
// and each parent's gpu-setup, so it works from the repo root or from gpu-setup/.
static string? FindDir(string start)
{
    var d = new DirectoryInfo(start);
    for (int i = 0; i < 8 && d is not null; i++, d = d.Parent)
    {
        if (d.GetFiles("cudnn-*cuda13*.zip").Length > 0) return d.FullName;
        var sub = Path.Combine(d.FullName, "gpu-setup");
        if (Directory.Exists(sub) && Directory.GetFiles(sub, "cudnn-*cuda13*.zip").Length > 0) return sub;
    }
    return null;
}
