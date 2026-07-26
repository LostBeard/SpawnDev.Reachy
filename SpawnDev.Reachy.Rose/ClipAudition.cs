using System.Diagnostics;
using System.Globalization;
using SherpaOnnx;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Turns a compilation of one character's clips - a fan-made "N voice clips" video,
/// for instance - into a ranked set of cloning references.
/// </summary>
/// <remarks>
/// The show audio the caption pipeline (<see cref="VoiceCandidates"/>) draws from is
/// buried under a music score, and a character's LABELLED lines are sparse, so the
/// calmest of them is still not very calm. A hand-collected clip reel does not have
/// either problem: every clip is already the right character, already chosen by a human
/// for being clear, and there is nothing else to identify. All that is missing is the
/// two things ZipVoice needs from a reference - a clean cut and the exact words in it.
///
/// So this does only what is actually missing:
///   1. split the reel into single utterances with the same Silero VAD Rose listens with,
///   2. denoise each (the clips that came from a scored scene still carry the bed),
///   3. transcribe each with Rose's own Whisper, giving the reference text,
///   4. measure and rank them with the SAME calmness rule the caption pipeline uses,
///      so a reel reference and a show reference are judged on one scale.
///
/// The output is identical in shape to <c>--candidates</c>: a shortlist where every
/// clip speaks one fixed line, so the only thing the ear compares is the reference.
/// </remarks>
public static class ClipAudition
{
    public static async Task<int> RunAsync(string[] args)
    {
        var mp3 = ShowAudio.ArgValue(args, "--mp3=");
        if (string.IsNullOrWhiteSpace(mp3) || !File.Exists(mp3))
        {
            Console.WriteLine("need --mp3=<path to a clip compilation> (the file was not found)");
            return 1;
        }

        var name = ShowAudio.ArgValue(args, "--name=") ?? "N";
        var top = Int(args, "--top=", 8);
        var minSec = Dbl(args, "--min=", 2.5);
        var maxSec = Dbl(args, "--max=", 10.0);
        var threshold = (float)Dbl(args, "--vad-threshold=", 0.5);
        var clone = !args.Contains("--no-clone");
        var fp32 = !args.Contains("--int8");
        var steps = Int(args, "--steps=", 16);
        var say = ShowAudio.ArgValue(args, "--say=")
                  ?? "Oh gosh, hi Aubs. Um, I'm N. I was kind of hoping we could just hang out and be friends, if that's okay with you.";

        var solution = ShowAudio.SolutionDir();
        var modelDir = Path.Combine(solution, "models");
        var outDir = Path.Combine(solution, "scratchpad", "candidates", $"{name}_yt");
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"Auditioning {Path.GetFileName(mp3)} for {name} ({minSec:F1}-{maxSec:F1}s clips, top {top}).\n");

        // 1. Decode the reel to the 16 kHz mono float stream everything downstream wants.
        var samples = await DecodeMonoAsync(mp3, ShowAudio.Rate);
        if (samples.Length == 0) { Console.WriteLine("ffmpeg produced no audio - is ffmpeg on PATH?"); return 1; }
        Console.WriteLine($"decoded {samples.Length / (double)ShowAudio.Rate:F1}s of audio");

        // 2. Split into utterances with the detector Rose uses live, so a clip that
        //    segments cleanly here is one she would also hear as a single turn.
        var segments = Segment(samples, modelDir, threshold, maxSec);
        Console.WriteLine($"{segments.Count} utterance(s) found; keeping {minSec:F1}-{maxSec:F1}s\n");

        var denoiser = ShowAudio.MakeDenoiser(modelDir);
        if (denoiser is null) Console.WriteLine("  (no denoiser model - clips will keep any music bed)\n");
        using var recognizer = MakeRecognizer(modelDir);

        // 3+4. For each usable segment: denoise the clip we will actually clone from,
        //      transcribe it for the reference text, and measure the RAW cut (before
        //      denoising) so the noise floor we score on is the real one.
        var cands = new List<VoiceCandidates.Candidate>();
        var clips = new Dictionary<VoiceCandidates.Candidate, float[]>(ReferenceEqualityComparer.Instance);

        foreach (var (startSample, raw) in segments)
        {
            var dur = raw.Length / (double)ShowAudio.Rate;
            if (dur < minSec || dur > maxSec) continue;

            var clip = denoiser is not null ? denoiser.Run(raw, ShowAudio.Rate).Samples : raw;
            var text = Transcribe(recognizer, clip);
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith('[') || text.StartsWith('('))
                continue;   // Whisper's non-speech fallback: not a usable line

            var startSec = startSample / (double)ShowAudio.Rate;
            var run = new ShowAudio.Run(name, startSec, startSec + dur, text.Trim());
            var f = AudioAnalysis.Measure(raw, ShowAudio.Rate);

            var cand = new VoiceCandidates.Candidate(run, "YT", f, Embedding: null) { Name = name };
            cands.Add(cand);
            clips[cand] = clip;
        }

        denoiser?.Dispose();

        if (cands.Count == 0)
        {
            Console.WriteLine("No usable single-speaker clips in that length range.");
            Console.WriteLine("Try a wider --min/--max, or --vad-threshold=0.3 if clips ran together.");
            return 1;
        }

        // Same ranking rule as the caption pipeline: calmest and cleanest first.
        var ranked = VoiceCandidates.Rank(cands);
        var shortlist = ranked.Take(top).ToList();

        // Write every reference exactly as it will be used: the denoised cut plus the
        // transcript, so what you audition is what would be installed.
        for (var i = 0; i < shortlist.Count; i++)
        {
            var c = shortlist[i];
            var tag = $"{name}_yt_{i + 1:00}";
            ShowAudio.WriteWav(Path.Combine(outDir, $"{tag}_ref.wav"), clips[c]);
            File.WriteAllText(Path.Combine(outDir, $"{tag}_ref.txt"), c.Run.Text);
        }

        Console.WriteLine($"== {name}: top {shortlist.Count} of {cands.Count} clip(s), calmest + cleanest first ==\n");
        for (var i = 0; i < shortlist.Count; i++)
        {
            var c = shortlist[i];
            Console.WriteLine(
                $"{i + 1,2}. {c.Run.Duration,4:F1}s @ {ShowAudio.Timecode(c.Run.Start)}"
                + $"  calm {c.Calm,5:F2}  clean {c.Clean,5:F2}  |  f0 {c.F.MedianF0,3:F0}Hz"
                + $"  {c.F.RmsDb,5:F1}dB  floor {c.F.NoiseFloorDb,5:F1}dB");
            Console.WriteLine($"    \"{Truncate(c.Run.Text, 90)}\"");
        }

        if (clone)
        {
            Console.WriteLine($"\ncloning each saying the same line ({(fp32 ? "fp32" : "int8")}, {steps} steps) - this is the part you listen to.\n");
            using var voice = new RoseVoiceClone(modelDir, fp32, steps);
            for (var i = 0; i < shortlist.Count; i++)
            {
                var c = shortlist[i];
                var tag = $"{name}_yt_{i + 1:00}";
                var sw = Stopwatch.StartNew();
                var pcm = voice.Clone(say, clips[c], ShowAudio.Rate, c.Run.Text);
                ShowAudio.WriteWavPcm(Path.Combine(outDir, $"{tag}_say.wav"), pcm, voice.SampleRate);
                Console.WriteLine($"  {tag}_say.wav  ({pcm.Length / 2.0 / voice.SampleRate:F1}s in {sw.Elapsed.TotalSeconds:F1}s)");
            }
        }

        Console.WriteLine($"\nWrote {shortlist.Count} clip(s) to {outDir}");
        Console.WriteLine($"Listen to {name}_yt_NN_say.wav (same line every time - only the reference differs),");
        Console.WriteLine($"then lock the winner in:  --pick-clip --name={name} --index=<n>");
        return 0;
    }

    /// <summary>
    /// Promotes one auditioned reel clip to the character's live voiceprint, by copying
    /// its denoised reference wav and transcript over <c>models/voiceprints/&lt;Name&gt;.*</c>.
    /// </summary>
    public static int Pick(string[] args)
    {
        var name = ShowAudio.ArgValue(args, "--name=") ?? "N";
        if (!int.TryParse(ShowAudio.ArgValue(args, "--index="), out var index) || index < 1)
        {
            Console.WriteLine("need --index=<n> from the audition shortlist");
            return 1;
        }

        var solution = ShowAudio.SolutionDir();
        var tag = $"{name}_yt_{index:00}";
        var srcWav = Path.Combine(solution, "scratchpad", "candidates", $"{name}_yt", $"{tag}_ref.wav");
        var srcTxt = Path.ChangeExtension(srcWav, ".txt");
        if (!File.Exists(srcWav) || !File.Exists(srcTxt))
        {
            Console.WriteLine($"no such clip: {srcWav}");
            Console.WriteLine($"run  --audition-clips --mp3=... --name={name}  first");
            return 1;
        }

        var outDir = Path.Combine(solution, "models", "voiceprints");
        Directory.CreateDirectory(outDir);
        File.Copy(srcWav, Path.Combine(outDir, $"{name}.wav"), overwrite: true);
        File.Copy(srcTxt, Path.Combine(outDir, $"{name}.txt"), overwrite: true);

        Console.WriteLine($"{name}'s live voiceprint is now clip #{index}:");
        Console.WriteLine($"  \"{File.ReadAllText(srcTxt).Trim()}\"");
        return 0;
    }

    // ---- segmentation -------------------------------------------------------

    /// <summary>
    /// Splits a 16 kHz stream into speech segments with Silero VAD, returning each
    /// segment's start sample and its samples. Segments are drained as the stream is
    /// fed so the detector's buffer cannot overflow on a long reel.
    /// </summary>
    private static List<(int Start, float[] Samples)> Segment(float[] samples, string modelDir, float threshold, double maxSec)
    {
        const int window = 512;   // Silero requires exactly 512-sample frames at 16 kHz
        var vadModel = Path.Combine(modelDir, "silero_vad.onnx");
        if (!File.Exists(vadModel))
            throw new FileNotFoundException($"Silero VAD model not found: {vadModel}");

        var cfg = new VadModelConfig
        {
            SampleRate = ShowAudio.Rate,
            NumThreads = 1,
            Provider = "cpu",
        };
        cfg.SileroVad.Model = vadModel;
        cfg.SileroVad.Threshold = threshold;
        cfg.SileroVad.MinSilenceDuration = 0.4f;
        cfg.SileroVad.MinSpeechDuration = 0.25f;
        cfg.SileroVad.MaxSpeechDuration = (float)maxSec;   // long montages get chopped, not kept whole
        cfg.SileroVad.WindowSize = window;

        using var vad = new VoiceActivityDetector(cfg, bufferSizeInSeconds: 60.0f);

        var result = new List<(int, float[])>();
        var frame = new float[window];

        void DrainInto()
        {
            while (!vad.IsEmpty())
            {
                var seg = vad.Front();
                vad.Pop();
                result.Add((seg.Start, seg.Samples));
            }
        }

        for (var i = 0; i + window <= samples.Length; i += window)
        {
            Array.Copy(samples, i, frame, 0, window);
            vad.AcceptWaveform(frame);
            DrainInto();
        }
        vad.Flush();
        DrainInto();

        return result;
    }

    // ---- transcription ------------------------------------------------------

    private static OfflineRecognizer MakeRecognizer(string modelDir)
    {
        var whisperDir = Path.Combine(modelDir, "sherpa-onnx-whisper-base.en");
        var cfg = new OfflineRecognizerConfig();
        cfg.ModelConfig.Whisper.Encoder = Path.Combine(whisperDir, "base.en-encoder.int8.onnx");
        cfg.ModelConfig.Whisper.Decoder = Path.Combine(whisperDir, "base.en-decoder.int8.onnx");
        cfg.ModelConfig.Tokens = Path.Combine(whisperDir, "base.en-tokens.txt");
        cfg.ModelConfig.ModelType = "whisper";
        cfg.ModelConfig.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        cfg.ModelConfig.Provider = "cpu";
        cfg.DecodingMethod = "greedy_search";
        return new OfflineRecognizer(cfg);
    }

    private static string Transcribe(OfflineRecognizer asr, float[] samples16k)
    {
        using var stream = asr.CreateStream();
        stream.AcceptWaveform(ShowAudio.Rate, samples16k);
        asr.Decode(stream);
        return stream.Result.Text?.Trim() ?? "";
    }

    // ---- ffmpeg decode ------------------------------------------------------

    private static async Task<float[]> DecodeMonoAsync(string src, int rate)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"reel_{Guid.NewGuid():N}.wav");
        try
        {
            var ok = await RunAsync("ffmpeg",
                $"-y -v error -i \"{src}\" -ac 1 -ar {rate} -c:a pcm_s16le \"{tmp}\"");
            if (!ok || !File.Exists(tmp)) return [];
            return ShowAudio.ReadWav(tmp).Samples;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static async Task<bool> RunAsync(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0 && stderr.Length > 0) Console.WriteLine($"  ffmpeg: {stderr.Trim()}");
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Console.WriteLine($"  {exe} failed: {ex.Message}"); return false; }
    }

    // ---- misc ---------------------------------------------------------------

    private static int Int(string[] args, string prefix, int fallback) =>
        int.TryParse(ShowAudio.ArgValue(args, prefix), out var v) ? v : fallback;

    private static double Dbl(string[] args, string prefix, double fallback) =>
        double.TryParse(ShowAudio.ArgValue(args, prefix), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private sealed class ReferenceEqualityComparer : IEqualityComparer<VoiceCandidates.Candidate>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public bool Equals(VoiceCandidates.Candidate? a, VoiceCandidates.Candidate? b) => ReferenceEquals(a, b);
        public int GetHashCode(VoiceCandidates.Candidate c) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c);
    }
}
