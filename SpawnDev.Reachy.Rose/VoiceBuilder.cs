using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SherpaOnnx;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Builds per-character voice reference clips from the show, for voice cloning.
/// </summary>
/// <remarks>
/// ZipVoice clones from a short reference clip PLUS its exact transcript, and it is
/// unforgiving about the two matching: if the audio contains words the transcript
/// does not (or the other way round), those words leak into everything it later
/// says. The reliable source of a perfectly aligned pair is the closed captions
/// themselves - a captioned line gives the exact words AND the exact time window
/// they are spoken in. So a reference is cut straight from a captioned line
/// (<c>[Khan] Guys! My daughter is into doors!</c>): audio at the caption's
/// timestamps, text from the caption. Consecutive unlabelled captions are merged
/// on to it while the speaker has not changed (captions only re-label on a change),
/// which lengthens the reference without breaking the alignment.
///
/// The show audio carries room reverb and a music bed, which ZipVoice would clone
/// as an echoey quality, so each clip is run through a denoiser before it is saved.
///
/// Everything is local: ffmpeg pulls the English audio and the CC track out of the
/// MKVs, sherpa-onnx denoises. Private home use only - the clips impersonate real
/// voice actors and must never be distributed.
/// </remarks>
public static class VoiceBuilder
{
    private const int Rate = 16000;

    public static async Task<int> RunAsync(string[] args)
    {
        var source = ArgValue(args, "--source=") ?? @"V:\Video\Series\Murder Drones\S01";
        var work = ArgValue(args, "--work=") ?? Path.Combine(SolutionDir(), "scratchpad", "md_audio");
        var outDir = ArgValue(args, "--out=") ?? Path.Combine(SolutionDir(), "models", "voiceprints");
        var modelDir = Path.Combine(SolutionDir(), "models");
        var limit = int.TryParse(ArgValue(args, "--episodes="), out var n) ? n : int.MaxValue;
        var maxRefSeconds = double.TryParse(ArgValue(args, "--seconds="), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? sec : 10.0;
        var denoise = !args.Contains("--no-denoise");

        Directory.CreateDirectory(work);
        Directory.CreateDirectory(outDir);

        var episodes = Directory.Exists(source)
            ? Directory.GetFiles(source, "*.mkv").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray()
            : [];
        if (episodes.Length == 0) { Console.WriteLine($"No .mkv episodes found under {source}"); return 1; }

        using var denoiser = denoise ? MakeDenoiser(modelDir) : null;
        if (denoise && denoiser is null)
            Console.WriteLine("  (denoiser model missing at models/gtcrn_simple.onnx - clips will keep room ambience)");

        Console.WriteLine($"Scanning {episodes.Length} episode(s) for captioned reference lines.\n");

        // The best reference candidate found for each character, across all episodes.
        var best = new Dictionary<string, (float[] Audio, double Dur, string Text, string Ep)>(StringComparer.OrdinalIgnoreCase);

        foreach (var mkv in episodes)
        {
            var epKey = EpisodeKey(Path.GetFileNameWithoutExtension(mkv));
            var wavPath = Path.Combine(work, $"{epKey}.wav");
            var srtPath = Path.Combine(work, $"{epKey}.srt");

            Console.WriteLine($"== {epKey} ==");
            if (!await EnsureExtractedAsync(mkv, wavPath, srtPath)) continue;

            var samples = ReadWavMono16k(wavPath);
            if (samples.Length == 0) { Console.WriteLine("  no audio, skipped"); continue; }

            var cues = ParseCues(srtPath);
            var candidates = BuildCandidates(cues, maxRefSeconds);
            Console.WriteLine($"  {cues.Count} caption(s), {candidates.Count} single-speaker reference run(s)");

            foreach (var (name, start, end, text) in candidates)
            {
                var dur = end - start;
                if (!best.TryGetValue(name, out var cur) || Score(dur, maxRefSeconds) > Score(cur.Dur, maxRefSeconds))
                    best[name] = (Slice(samples, start, end), dur, text, epKey);
            }
        }

        // Write one clean reference clip + transcript per playable character.
        Console.WriteLine("\n== reference clips ==");
        var wrote = 0;
        foreach (var c in CharacterLibrary.All)
        {
            if (!best.TryGetValue(c.Name, out var pick))
            {
                Console.WriteLine($"  {c.Name,-5}  (no captioned line found)");
                continue;
            }

            var audio = pick.Audio;
            if (denoiser is not null) audio = denoiser.Run(audio, Rate).Samples;

            var wav = Path.Combine(outDir, $"{c.Name}.wav");
            WriteWavMono16k(wav, audio);
            File.WriteAllText(Path.Combine(outDir, $"{c.Name}.txt"), pick.Text);
            Console.WriteLine($"  {c.Name,-5}  {pick.Dur,5:F1}s [{pick.Ep}]  \"{Truncate(pick.Text, 50)}\" -> {c.Name}.wav + .txt");
            wrote++;
        }

        Console.WriteLine($"\nWrote {wrote} reference clip(s) to {outDir}");
        return wrote > 0 ? 0 : 1;
    }

    /// <summary>Prefers the longest reference that still fits the target length - long enough to carry the voice, not so long it drifts.</summary>
    private static double Score(double dur, double max) => dur <= max ? dur : max - (dur - max);

    private static OfflineSpeechDenoiser? MakeDenoiser(string modelDir)
    {
        var model = Path.Combine(modelDir, "gtcrn_simple.onnx");
        if (!File.Exists(model)) return null;
        var cfg = new OfflineSpeechDenoiserConfig();
        cfg.Model.Gtcrn.Model = model;
        cfg.Model.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        return new OfflineSpeechDenoiser(cfg);
    }

    // ---- caption-anchored reference selection -------------------------------

    private sealed record Cue(double Start, double End, string Text);

    private static readonly Regex NameTag = new(@"^\s*\[([A-Z][A-Za-z']*)\]", RegexOptions.Compiled);
    private static readonly Regex AnyBracket = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    // A "-" acting as a speaker-change marker: line-leading, or space-dash-letter.
    // Deliberately does NOT match a hyphen inside a word ("right-size").
    private static readonly Regex SpeakerDash = new(@"(^\s*-|\s-\s?[A-Za-z])", RegexOptions.Compiled);

    /// <summary>
    /// Walks the captions and, from each single-speaker named line, builds the
    /// longest aligned reference run the same speaker continues into.
    /// </summary>
    private static List<(string Name, double Start, double End, string Text)> BuildCandidates(List<Cue> cues, double maxSeconds)
    {
        var result = new List<(string, double, double, string)>();

        for (var i = 0; i < cues.Count; i++)
        {
            var name = NameOf(cues[i].Text);
            if (name is null || IsMultiSpeaker(cues[i].Text)) continue;

            var start = cues[i].Start;
            var end = cues[i].End;
            var text = new StringBuilder(CleanText(cues[i].Text));

            // Extend with following captions while the same speaker is still talking:
            // captions re-label only on a change, so an unlabelled, undashed, closely
            // following caption is the same voice.
            var j = i + 1;
            while (j < cues.Count)
            {
                if (cues[j].Start - end > 0.8) break;            // a real pause - stop
                if (NameOf(cues[j].Text) is not null) break;     // a newly labelled speaker
                if (IsMultiSpeaker(cues[j].Text)) break;         // a dash speaker change
                if (cues[j].End - start > maxSeconds) break;     // long enough
                end = cues[j].End;
                var t = CleanText(cues[j].Text);
                if (t.Length > 0) text.Append(' ').Append(t);
                j++;
            }

            var clean = text.ToString().Trim();
            if (clean.Length > 0 && end - start >= 1.5)
                result.Add((name, start, end, clean));

            i = j - 1;   // do not restart inside the run we just consumed
        }

        return result;
    }

    /// <summary>The playable character a caption is labelled with, if any.</summary>
    private static string? NameOf(string cueText)
    {
        var m = NameTag.Match(cueText);
        if (!m.Success) return null;
        return CharacterLibrary.Find(m.Groups[1].Value)?.Name;   // only playable characters
    }

    private static bool IsMultiSpeaker(string cueText)
    {
        // Look past a leading [Name] tag, then for a speaker dash in the remainder.
        var afterName = NameTag.Replace(cueText, "");
        var noBrackets = AnyBracket.Replace(afterName, "");
        return SpeakerDash.IsMatch(noBrackets);
    }

    /// <summary>Caption text reduced to the spoken words: bracket tags and speaker dashes removed.</summary>
    private static string CleanText(string cueText)
    {
        var s = AnyBracket.Replace(cueText, " ");
        s = Regex.Replace(s, @"(^|\s)-\s*", " ");   // drop speaker dashes, keep in-word hyphens
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    // ---- SRT ----------------------------------------------------------------

    private static readonly Regex TimeLine = new(
        @"(\d\d):(\d\d):(\d\d),(\d\d\d)\s*-->\s*(\d\d):(\d\d):(\d\d),(\d\d\d)", RegexOptions.Compiled);

    private static List<Cue> ParseCues(string path)
    {
        var cues = new List<Cue>();
        if (!File.Exists(path)) return cues;

        double start = 0, end = 0;
        var text = new StringBuilder();

        void Flush()
        {
            if (end > start && text.Length > 0) cues.Add(new Cue(start, end, text.ToString().Trim()));
            text.Clear();
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            var tm = TimeLine.Match(line);
            if (tm.Success) { Flush(); start = Ts(tm, 1); end = Ts(tm, 5); continue; }
            if (line.Length == 0 || int.TryParse(line, out _)) continue;   // blank or index line
            if (text.Length > 0) text.Append(' ');
            text.Append(line);
        }
        Flush();
        return cues;
    }

    private static double Ts(Match m, int g) =>
        int.Parse(m.Groups[g].Value) * 3600.0 + int.Parse(m.Groups[g + 1].Value) * 60.0
        + int.Parse(m.Groups[g + 2].Value) + int.Parse(m.Groups[g + 3].Value) / 1000.0;

    // ---- ffmpeg extraction --------------------------------------------------

    private static async Task<bool> EnsureExtractedAsync(string mkv, string wavPath, string srtPath)
    {
        var haveWav = File.Exists(wavPath) && new FileInfo(wavPath).Length > 1024;
        var haveSrt = File.Exists(srtPath) && new FileInfo(srtPath).Length > 16;
        if (haveWav && haveSrt) { Console.WriteLine("  already extracted"); return true; }

        var (audioIdx, subIdx) = await ProbeEnglishStreamsAsync(mkv);
        if (audioIdx < 0) { Console.WriteLine("  no English audio stream, skipped"); return false; }

        if (!haveWav)
        {
            Console.WriteLine($"  extracting audio (stream {audioIdx}) -> {Path.GetFileName(wavPath)}");
            if (!await RunAsync("ffmpeg", $"-y -v error -i \"{mkv}\" -map 0:{audioIdx} -ac 1 -ar {Rate} -c:a pcm_s16le \"{wavPath}\""))
                return false;
        }
        if (!haveSrt && subIdx >= 0)
        {
            Console.WriteLine($"  extracting CC (stream {subIdx}) -> {Path.GetFileName(srtPath)}");
            await RunAsync("ffmpeg", $"-y -v error -i \"{mkv}\" -map 0:{subIdx} \"{srtPath}\"");
        }
        return File.Exists(wavPath);
    }

    private static async Task<(int Audio, int Sub)> ProbeEnglishStreamsAsync(string mkv)
    {
        var (ok, output) = await RunCaptureAsync("ffprobe",
            $"-v error -show_entries stream=index,codec_type:stream_tags=language,title -of csv \"{mkv}\"");
        if (!ok) return (-1, -1);

        int audio = -1, sub = -1;
        var subHadCC = false;
        foreach (var row in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = row.Split(',');
            if (f.Length < 3 || f[0] != "stream" || !int.TryParse(f[1], out var idx)) continue;
            var type = f[2];
            var lang = f.Length > 3 ? f[3] : "";
            var title = f.Length > 4 ? string.Join(",", f[4..]) : "";
            if (!lang.Equals("eng", StringComparison.OrdinalIgnoreCase)) continue;

            if (type == "audio" && audio < 0) audio = idx;
            else if (type == "subtitle")
            {
                var isCC = title.Contains("CC", StringComparison.OrdinalIgnoreCase)
                        || title.Contains("SDH", StringComparison.OrdinalIgnoreCase);
                if (sub < 0 || (isCC && !subHadCC)) { sub = idx; subHadCC = isCC; }
            }
        }
        return (audio, sub);
    }

    private static async Task<bool> RunAsync(string exe, string args) => (await RunCaptureAsync(exe, args)).Ok;

    private static async Task<(bool Ok, string Output)> RunCaptureAsync(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0 && stderr.Length > 0) Console.WriteLine($"    {exe}: {stderr.Trim()}");
            return (p.ExitCode == 0, stdout);
        }
        catch (Exception ex) { Console.WriteLine($"    {exe} failed: {ex.Message}"); return (false, ""); }
    }

    // ---- WAV helpers --------------------------------------------------------

    private static float[] ReadWavMono16k(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "data")
            {
                var count = Math.Min(size, bytes.Length - pos - 8) / 2;
                var samples = new float[count];
                for (var i = 0; i < count; i++)
                    samples[i] = BitConverter.ToInt16(bytes, pos + 8 + i * 2) / 32768f;
                return samples;
            }
            if (size <= 0) break;
            pos += 8 + size + (size % 2);
        }
        return [];
    }

    private static float[] Slice(float[] samples, double startSec, double endSec)
    {
        var a = Math.Clamp((int)(startSec * Rate), 0, samples.Length);
        var b = Math.Clamp((int)(endSec * Rate), a, samples.Length);
        return samples[a..b];
    }

    private static void WriteWavMono16k(string path, float[] samples)
    {
        using var w = new BinaryWriter(File.Create(path));
        var dataBytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples)
            w.Write((short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue));
    }

    // ---- misc ---------------------------------------------------------------

    private static string EpisodeKey(string fileTag)
    {
        var m = Regex.Match(fileTag, @"S(\d\d)E(\d\d)", RegexOptions.IgnoreCase);
        return m.Success ? $"E{m.Groups[2].Value}" : fileTag;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private static string? ArgValue(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static string SolutionDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "models"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }
}
