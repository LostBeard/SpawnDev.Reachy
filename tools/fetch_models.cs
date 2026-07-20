// Downloads the speech models Rose needs into ./models.
//
// They are not in git - roughly 250MB - so this is the one-time setup step on a
// fresh machine. Safe to re-run; anything already present is skipped.
//
// Usage: dotnet run tools/fetch_models.cs

using System.Diagnostics;

var root = Directory.GetCurrentDirectory();
var models = Path.Combine(root, "models");
Directory.CreateDirectory(models);

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

const string Releases = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models";

// Silero VAD decides where an utterance starts and stops. An energy threshold
// cannot do this job - a child pauses mid-sentence constantly.
var vad = Path.Combine(models, "silero_vad.onnx");
if (File.Exists(vad))
{
    Console.WriteLine($"  silero_vad.onnx already present ({new FileInfo(vad).Length / 1024}KB)");
}
else
{
    Console.WriteLine("  downloading silero_vad.onnx ...");
    await using var s = await http.GetStreamAsync($"{Releases}/silero_vad.onnx");
    await using var f = File.Create(vad);
    await s.CopyToAsync(f);
    Console.WriteLine($"    done ({new FileInfo(vad).Length / 1024}KB)");
}

// Whisper base.en: accurate enough for a child's speech, small enough to stay
// well under a second per utterance on CPU.
var whisperDir = Path.Combine(models, "sherpa-onnx-whisper-base.en");
if (Directory.Exists(whisperDir) && File.Exists(Path.Combine(whisperDir, "base.en-tokens.txt")))
{
    Console.WriteLine("  whisper base.en already present");
}
else
{
    var archive = Path.Combine(models, "whisper.tar.bz2");
    Console.WriteLine("  downloading whisper base.en (~200MB, this takes a minute) ...");

    await using (var s = await http.GetStreamAsync($"{Releases}/sherpa-onnx-whisper-base.en.tar.bz2"))
    await using (var f = File.Create(archive))
        await s.CopyToAsync(f);

    Console.WriteLine("    extracting ...");
    var tar = Process.Start(new ProcessStartInfo("tar", $"xjf \"{archive}\"")
    {
        WorkingDirectory = models,
        UseShellExecute = false,
    });
    if (tar is null) { Console.Error.WriteLine("could not start tar"); return 1; }
    await tar.WaitForExitAsync();

    if (tar.ExitCode != 0)
    {
        Console.Error.WriteLine($"tar failed with exit code {tar.ExitCode}");
        return 1;
    }

    File.Delete(archive);
    Console.WriteLine("    done");
}

// Kokoro downloads its own model on first run, but into the working directory
// rather than a cache, so report whether it is already there.
var kokoro = Path.Combine(root, "kokoro.onnx");
Console.WriteLine(File.Exists(kokoro)
    ? $"  kokoro.onnx already present ({new FileInfo(kokoro).Length / 1024 / 1024}MB)"
    : "  kokoro.onnx absent - it downloads itself (~310MB) on the first voice run");

Console.WriteLine("\nModels ready.");
return 0;
