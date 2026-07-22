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
/// The closed captions label who is speaking - but only sometimes, and only when
/// it would otherwise be ambiguous, so the labels are sparse (a handful of lines
/// per character per episode). That is still enough. The captions seed the
/// answer: diarization splits every episode into speaker clusters on its own, and
/// the sparse <c>[Name]</c> labels are used only to put a NAME on each cluster.
/// Once a cluster is named, ALL of its audio - not just the labelled lines -
/// becomes reference material for that character.
///
/// Capitalised brackets are names (<c>[Uzi]</c>); lowercase brackets are sound
/// cues (<c>[grunts]</c>, <c>[dramatic music playing]</c>) and mark regions to
/// EXCLUDE, since reference audio for cloning must be clean single-speaker speech,
/// not dialogue buried under a music sting.
///
/// Everything is local: ffmpeg pulls the English audio and the CC track out of
/// the MKVs, sherpa-onnx does the diarization. Private home use only - the clips
/// impersonate real voice actors and must never be distributed.
/// </remarks>
public static class VoiceBuilder
{
    private const int Rate = 16000;

    public static async Task<int> RunAsync(string[] args)
    {
        var source = ArgValue(args, "--source=")
            ?? @"V:\Video\Series\Murder Drones\S01";
        var work = ArgValue(args, "--work=") ?? Path.Combine(SolutionDir(), "scratchpad", "md_audio");
        var outDir = ArgValue(args, "--out=") ?? Path.Combine(SolutionDir(), "models", "voiceprints");
        var modelDir = Path.Combine(SolutionDir(), "models");
        var limit = int.TryParse(ArgValue(args, "--episodes="), out var n) ? n : int.MaxValue;
        var threshold = float.TryParse(ArgValue(args, "--threshold="), NumberStyles.Float, CultureInfo.InvariantCulture, out var th) ? th : 0.5f;
        var maxRefSeconds = double.TryParse(ArgValue(args, "--seconds="), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? sec : 15.0;

        var seg = Path.Combine(modelDir, "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
        var emb = Path.Combine(modelDir, "wespeaker_en_voxceleb_CAM++.onnx");
        if (!File.Exists(seg) || !File.Exists(emb))
        {
            Console.WriteLine($"Missing diarization models. Expected:\n  {seg}\n  {emb}");
            return 1;
        }

        Directory.CreateDirectory(work);
        Directory.CreateDirectory(outDir);

        var episodes = Directory.Exists(source)
            ? Directory.GetFiles(source, "*.mkv").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray()
            : [];
        if (episodes.Length == 0) { Console.WriteLine($"No .mkv episodes found under {source}"); return 1; }

        Console.WriteLine($"Diarizing {episodes.Length} episode(s), clustering threshold {threshold}.\n");

        using var diar = MakeDiarizer(seg, emb, threshold);

        // Per-character candidate reference segments (audio, duration, transcript),
        // across all episodes. The best single one becomes the cloning reference.
        var pooled = new Dictionary<string, List<(float[] Audio, double Dur, string Text)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mkv in episodes)
        {
            var tag = Path.GetFileNameWithoutExtension(mkv);
            var epKey = EpisodeKey(tag);
            var wavPath = Path.Combine(work, $"{epKey}.wav");
            var srtPath = Path.Combine(work, $"{epKey}.srt");

            Console.WriteLine($"== {epKey} ==");
            if (!await EnsureExtractedAsync(mkv, wavPath, srtPath)) continue;

            var samples = ReadWavMono16k(wavPath);
            if (samples.Length == 0) { Console.WriteLine("  no audio, skipped"); continue; }

            var labels = ParseSrt(srtPath, out var excludeRanges, out var speechCues);
            Console.WriteLine($"  audio {samples.Length / (double)Rate / 60.0:F1} min, " +
                              $"{labels.Count} name-labelled cue(s), {excludeRanges.Count} sound-cue region(s)");

            var sw = Stopwatch.StartNew();
            var segments = diar.Process(samples);
            Console.WriteLine($"  diarized into {segments.Select(s => s.Speaker).Distinct().Count()} speaker(s) " +
                              $"across {segments.Length} segment(s) in {sw.Elapsed.TotalSeconds:F0}s");

            // Name each cluster by majority vote from the labelled cues that overlap it.
            var clusterName = NameClusters(segments, labels);
            foreach (var (cluster, name) in clusterName.OrderBy(kv => kv.Key))
                Console.WriteLine($"    cluster {cluster} -> {name}");

            // Collect each named cluster's clean segments as reference audio.
            foreach (var s in segments)
            {
                if (!clusterName.TryGetValue(s.Speaker, out var name)) continue;
                if (OverlapsExcluded(s.Start, s.End, excludeRanges)) continue;
                var dur = s.End - s.Start;
                if (dur < 0.5) continue;                 // ignore sub-half-second scraps
                var clip = Slice(samples, s.Start, s.End);
                var txt = TextForSegment(speechCues, s.Start, s.End);
                if (!pooled.TryGetValue(name, out var list)) pooled[name] = list = [];
                list.Add((clip, dur, txt));
            }
        }

        // Write ONE clean reference clip + transcript per playable character.
        Console.WriteLine("\n== reference clips ==");
        var wrote = 0;
        foreach (var c in CharacterLibrary.All)
        {
            if (!pooled.TryGetValue(c.Name, out var segs) || segs.Count == 0)
            {
                Console.WriteLine($"  {c.Name,-5}  (no audio found)");
                continue;
            }

            // ZipVoice wants ONE clean contiguous reference with its exact transcript,
            // not a pile of fragments. Prefer the longest named segment that has words
            // and sits in a good reference length - a very long one is usually two
            // speakers the diarizer merged, and a wordless one has no transcript.
            var best = segs
                .Where(x => x.Text.Length > 0 && x.Dur >= 3.0 && x.Dur <= maxRefSeconds)
                .OrderByDescending(x => x.Dur)
                .FirstOrDefault();
            if (best.Audio is null)
                best = segs.Where(x => x.Text.Length > 0).OrderByDescending(x => x.Dur).FirstOrDefault();
            if (best.Audio is null)
            {
                Console.WriteLine($"  {c.Name,-5}  (no segment with a transcript)");
                continue;
            }

            var wav = Path.Combine(outDir, $"{c.Name}.wav");
            WriteWavMono16k(wav, best.Audio);
            File.WriteAllText(Path.Combine(outDir, $"{c.Name}.txt"), best.Text);
            Console.WriteLine($"  {c.Name,-5}  {best.Dur,5:F1}s  \"{Truncate(best.Text, 55)}\" -> {c.Name}.wav + .txt");
            wrote++;
        }

        Console.WriteLine($"\nWrote {wrote} reference clip(s) to {outDir}");
        return wrote > 0 ? 0 : 1;
    }

    private static OfflineSpeakerDiarization MakeDiarizer(string seg, string emb, float threshold)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = seg;
        config.Segmentation.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.Embedding.Model = emb;
        config.Embedding.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        // Auto speaker count: the cast size varies per episode, so cluster by
        // similarity threshold rather than a fixed count we would have to guess.
        config.Clustering.NumClusters = -1;
        config.Clustering.Threshold = threshold;
        config.MinDurationOn = 0.3f;
        config.MinDurationOff = 0.5f;
        return new OfflineSpeakerDiarization(config);
    }

    /// <summary>Assigns each diarization cluster the character name that its overlapping labelled cues most agree on.</summary>
    private static Dictionary<int, string> NameClusters(
        OfflineSpeakerDiarizationSegment[] segments, List<(double Start, double End, string Name)> labels)
    {
        // votes[cluster][name] = overlapping labelled seconds.
        var votes = new Dictionary<int, Dictionary<string, double>>();

        foreach (var (ls, le, name) in labels)
            foreach (var s in segments)
            {
                var ov = Math.Min(le, s.End) - Math.Max(ls, s.Start);
                if (ov <= 0) continue;
                if (!votes.TryGetValue(s.Speaker, out var byName)) votes[s.Speaker] = byName = new(StringComparer.OrdinalIgnoreCase);
                byName[name] = byName.GetValueOrDefault(name) + ov;
            }

        var result = new Dictionary<int, string>();
        foreach (var (cluster, byName) in votes)
        {
            var best = byName.OrderByDescending(kv => kv.Value).First();
            result[cluster] = best.Key;
        }
        return result;
    }

    // ---- SRT ----------------------------------------------------------------

    private static readonly Regex TimeLine = new(
        @"(\d\d):(\d\d):(\d\d),(\d\d\d)\s*-->\s*(\d\d):(\d\d):(\d\d),(\d\d\d)", RegexOptions.Compiled);
    private static readonly Regex NameTag = new(@"^\s*\[([A-Z][A-Za-z']*)\]", RegexOptions.Compiled);
    private static readonly Regex AnyBracket = new(@"\[([^\]]+)\]", RegexOptions.Compiled);

    /// <summary>
    /// Parses an SRT into name-labelled cues and the time ranges covered by
    /// lowercase sound cues (to exclude from reference audio).
    /// </summary>
    private static List<(double Start, double End, string Name)> ParseSrt(
        string path, out List<(double, double)> exclude, out List<(double Start, double End, string Text)> speech)
    {
        var labels = new List<(double, double, string)>();
        var excl = new List<(double, double)>();
        var spk = new List<(double, double, string)>();
        exclude = excl;
        speech = spk;
        if (!File.Exists(path)) return labels;

        var lines = File.ReadAllLines(path);
        double start = 0, end = 0;
        var text = new StringBuilder();

        void Flush()
        {
            if (end > start && text.Length > 0)
            {
                var t = text.ToString();
                var m = NameTag.Match(t);
                var name = m.Success ? Canonical(m.Groups[1].Value) : null;
                if (name is not null) labels.Add((start, end, name));

                // Brackets removed leaves the spoken words - the transcript ZipVoice
                // pairs with the reference audio. A cue that reduces to nothing but a
                // lowercase sound cue ("[grunts]", "[music playing]") has no clean
                // speech, so it marks a region to exclude instead.
                var stripped = AnyBracket.Replace(t, "").Trim();
                if (stripped.Length > 0) spk.Add((start, end, stripped));
                else if (AnyBracket.Matches(t).Any(b => b.Groups[1].Value.Length > 0 && char.IsLower(b.Groups[1].Value[0])))
                    excl.Add((start, end));
            }
            text.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var tm = TimeLine.Match(line);
            if (tm.Success)
            {
                Flush();
                start = Ts(tm, 1);
                end = Ts(tm, 5);
                continue;
            }
            if (line.Length == 0 || int.TryParse(line, out _)) continue;  // blank or index line
            if (text.Length > 0) text.Append(' ');
            text.Append(line);
        }
        Flush();
        return labels;
    }

    private static double Ts(Match m, int g) =>
        int.Parse(m.Groups[g].Value) * 3600.0 + int.Parse(m.Groups[g + 1].Value) * 60.0
        + int.Parse(m.Groups[g + 2].Value) + int.Parse(m.Groups[g + 3].Value) / 1000.0;

    /// <summary>Maps a CC name to our character name, or keeps it as-is for non-playable speakers (e.g. Tessa).</summary>
    private static string Canonical(string ccName)
    {
        var c = CharacterLibrary.Find(ccName);
        return c?.Name ?? ccName;
    }

    private static bool OverlapsExcluded(double s, double e, List<(double, double)> exclude) =>
        exclude.Any(r => Math.Min(e, r.Item2) - Math.Max(s, r.Item1) > 0);

    /// <summary>The transcript of a diarization segment: the speech cues that overlap its time window, in order.</summary>
    private static string TextForSegment(List<(double Start, double End, string Text)> cues, double s, double e) =>
        string.Join(" ", cues.Where(c => Math.Min(e, c.End) - Math.Max(s, c.Start) > 0.1)
                             .OrderBy(c => c.Start).Select(c => c.Text)).Trim();

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

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
            var ok = await RunAsync("ffmpeg",
                $"-y -v error -i \"{mkv}\" -map 0:{audioIdx} -ac 1 -ar {Rate} -c:a pcm_s16le \"{wavPath}\"");
            if (!ok) return false;
        }
        if (!haveSrt && subIdx >= 0)
        {
            Console.WriteLine($"  extracting CC (stream {subIdx}) -> {Path.GetFileName(srtPath)}");
            await RunAsync("ffmpeg", $"-y -v error -i \"{mkv}\" -map 0:{subIdx} \"{srtPath}\"");
        }
        return File.Exists(wavPath);
    }

    /// <summary>Finds the English audio stream index and the English CC subtitle stream index.</summary>
    private static async Task<(int Audio, int Sub)> ProbeEnglishStreamsAsync(string mkv)
    {
        var (ok, output) = await RunCaptureAsync("ffprobe",
            $"-v error -show_entries stream=index,codec_type:stream_tags=language,title -of csv \"{mkv}\"");
        if (!ok) return (-1, -1);

        int audio = -1, sub = -1;
        var subTitleHadCC = false;
        foreach (var row in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "stream,<index>,<codec_type>,<language?>,<title?>"
            var f = row.Split(',');
            if (f.Length < 3 || f[0] != "stream") continue;
            if (!int.TryParse(f[1], out var idx)) continue;
            var type = f[2];
            var lang = f.Length > 3 ? f[3] : "";
            var title = f.Length > 4 ? string.Join(",", f[4..]) : "";
            if (!lang.Equals("eng", StringComparison.OrdinalIgnoreCase)) continue;

            if (type == "audio" && audio < 0) audio = idx;
            else if (type == "subtitle")
            {
                var isCC = title.Contains("CC", StringComparison.OrdinalIgnoreCase)
                        || title.Contains("SDH", StringComparison.OrdinalIgnoreCase);
                // Prefer a CC/SDH track (it carries the speaker labels); otherwise take the first.
                if (sub < 0 || (isCC && !subTitleHadCC)) { sub = idx; subTitleHadCC = isCC; }
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

    // ---- WAV + audio helpers ------------------------------------------------

    private static float[] ReadWavMono16k(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // Walk chunks to find "data"; do not assume it begins at offset 44.
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
        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        var dataBytes = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                 // PCM fmt chunk size
        w.Write((short)1);           // PCM
        w.Write((short)1);           // mono
        w.Write(Rate);
        w.Write(Rate * 2);           // byte rate
        w.Write((short)2);           // block align
        w.Write((short)16);          // bits
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples)
            w.Write((short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue));
    }

    // ---- misc ---------------------------------------------------------------

    private static string EpisodeKey(string fileTag)
    {
        var m = Regex.Match(fileTag, @"S(\d\d)E(\d\d)", RegexOptions.IgnoreCase);
        return m.Success ? $"E{m.Groups[2].Value}" : fileTag;
    }

    private static string? ArgValue(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static string SolutionDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "models"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }
}
