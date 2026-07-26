using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SherpaOnnx;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Shared plumbing for pulling captioned, single-speaker lines out of the show.
/// </summary>
/// <remarks>
/// Both the automatic reference builder (<see cref="VoiceBuilder"/>) and the
/// audition shortlist (<see cref="VoiceCandidates"/>) need exactly the same three
/// things: the English audio and captions out of an MKV, the captions parsed into
/// timed cues, and those cues walked into runs where only one character is talking.
/// It lives here once so the two modes can never drift apart - an audition that
/// scored a differently-cut clip than the builder would later write would be
/// auditioning the wrong thing.
/// </remarks>
internal static class ShowAudio
{
    public const int Rate = 16000;

    /// <summary>A single caption: when it starts, when it ends, and its raw text.</summary>
    public sealed record Cue(double Start, double End, string Text);

    /// <summary>
    /// A stretch of audio where exactly one person is talking, with its exact transcript.
    /// </summary>
    /// <param name="Name">
    /// The character the captions labelled, or null if the captions did not say.
    /// An unnamed run is still perfectly aligned - it just needs identifying by voice.
    /// </param>
    public sealed record Run(string? Name, double Start, double End, string Text)
    {
        public double Duration => End - Start;
    }

    // ---- caption-anchored reference selection -------------------------------

    private static readonly Regex NameTag = new(@"^\s*\[([A-Z][A-Za-z']*)\]", RegexOptions.Compiled);
    private static readonly Regex AnyBracket = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    // A "-" acting as a speaker-change marker: line-leading, or space-dash-letter.
    // Deliberately does NOT match a hyphen inside a word ("right-size").
    private static readonly Regex SpeakerDash = new(@"(^\s*-|\s-\s?[A-Za-z])", RegexOptions.Compiled);

    /// <summary>
    /// Walks the captions and, from each single-speaker named line, builds the
    /// longest aligned reference run the same speaker continues into.
    /// </summary>
    /// <remarks>
    /// The alignment is the whole point: ZipVoice bleeds any word present in the
    /// reference audio but missing from the reference text into everything it later
    /// says, so audio and transcript must come from the same captions. Captions
    /// re-label only when the speaker changes, which is what makes an unlabelled,
    /// undashed, closely-following caption safe to merge on.
    /// </remarks>
    public static List<Run> BuildRuns(List<Cue> cues, double maxSeconds, double minSeconds = 1.5) =>
        BuildAllRuns(cues, maxSeconds, minSeconds).Where(r => r.Name is not null).ToList();

    /// <summary>
    /// Every single-speaker run, including the ones the captions never named.
    /// </summary>
    /// <remarks>
    /// Captions only write a speaker label when the speaker would otherwise be
    /// ambiguous, so most of what a character says is in an UNLABELLED caption. Those
    /// runs are cut identically to labelled ones and are just as well aligned - all
    /// they lack is a name, which <see cref="SpeakerMatcher"/> supplies from the voice
    /// itself. Without them a character has a handful of candidate lines instead of
    /// hundreds, and no amount of ranking can find a calm line in a pool that has none.
    /// </remarks>
    public static List<Run> BuildAllRuns(List<Cue> cues, double maxSeconds, double minSeconds = 1.5)
    {
        var result = new List<Run>();

        for (var i = 0; i < cues.Count; i++)
        {
            if (IsMultiSpeaker(cues[i].Text)) continue;
            var name = NameOf(cues[i].Text);

            // A bracket tag that is not a playable character - [Tessa], or a sound cue
            // like [sighs] - means this window is not a clean line of cast dialogue.
            if (name is null && HasBracketTag(cues[i].Text)) continue;

            var start = cues[i].Start;
            var end = cues[i].End;
            var text = new StringBuilder(CleanText(cues[i].Text));

            var j = i + 1;
            while (j < cues.Count)
            {
                if (cues[j].Start - end > 0.8) break;            // a real pause - stop
                if (HasBracketTag(cues[j].Text)) break;          // a new speaker label, or a sound cue
                if (IsMultiSpeaker(cues[j].Text)) break;         // a dash speaker change
                if (cues[j].End - start > maxSeconds) break;     // long enough
                end = cues[j].End;
                var t = CleanText(cues[j].Text);
                if (t.Length > 0) text.Append(' ').Append(t);
                j++;
            }

            var clean = text.ToString().Trim();
            if (clean.Length > 0 && end - start >= minSeconds)
                result.Add(new Run(name, start, end, clean));

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

    /// <summary>
    /// Whether the caption carries any bracket tag at all - a speaker name we do not
    /// play, or a sound cue like <c>[sighs]</c>. Either way the window is not clean
    /// single-character dialogue, so it cannot anchor a reference.
    /// </summary>
    private static bool HasBracketTag(string cueText) => AnyBracket.IsMatch(cueText);

    private static bool IsMultiSpeaker(string cueText)
    {
        // Look past a leading [Name] tag, then for a speaker dash in the remainder.
        var afterName = NameTag.Replace(cueText, "");
        var noBrackets = AnyBracket.Replace(afterName, "");
        return SpeakerDash.IsMatch(noBrackets);
    }

    /// <summary>Caption text reduced to the spoken words: bracket tags and speaker dashes removed.</summary>
    public static string CleanText(string cueText)
    {
        var s = AnyBracket.Replace(cueText, " ");
        s = Regex.Replace(s, @"(^|\s)-\s*", " ");   // drop speaker dashes, keep in-word hyphens
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    // ---- SRT ----------------------------------------------------------------

    private static readonly Regex TimeLine = new(
        @"(\d\d):(\d\d):(\d\d),(\d\d\d)\s*-->\s*(\d\d):(\d\d):(\d\d),(\d\d\d)", RegexOptions.Compiled);

    public static List<Cue> ParseCues(string path)
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

    // ---- denoiser -----------------------------------------------------------

    /// <summary>
    /// The GTCRN denoiser, or null if the model is not on disk.
    /// </summary>
    /// <remarks>
    /// The show mix carries room reverb and a music bed under the dialogue, and
    /// ZipVoice clones that ambience as an echoey quality in everything it says.
    /// Stripping it off the reference is what made the clones clean.
    /// </remarks>
    public static OfflineSpeechDenoiser? MakeDenoiser(string modelDir)
    {
        var model = Path.Combine(modelDir, "gtcrn_simple.onnx");
        if (!File.Exists(model)) return null;
        var cfg = new OfflineSpeechDenoiserConfig();
        cfg.Model.Gtcrn.Model = model;
        cfg.Model.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        return new OfflineSpeechDenoiser(cfg);
    }

    // ---- ffmpeg extraction --------------------------------------------------

    /// <summary>Pulls the English audio and the English CC track out of an MKV, if not already done.</summary>
    public static async Task<bool> EnsureExtractedAsync(string mkv, string wavPath, string srtPath, bool quiet = false)
    {
        var haveWav = File.Exists(wavPath) && new FileInfo(wavPath).Length > 1024;
        var haveSrt = File.Exists(srtPath) && new FileInfo(srtPath).Length > 16;
        if (haveWav && haveSrt) { if (!quiet) Console.WriteLine("  already extracted"); return true; }

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

    /// <summary>Reads a 16-bit PCM mono wav as float samples in [-1,1], with its header sample rate.</summary>
    public static (float[] Samples, int Rate) ReadWav(string path)
    {
        if (!File.Exists(path)) return ([], 0);
        var bytes = File.ReadAllBytes(path);
        var rate = Rate;
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt ") rate = BitConverter.ToInt32(bytes, pos + 12);
            else if (id == "data")
            {
                var count = Math.Min(size, bytes.Length - pos - 8) / 2;
                var samples = new float[count];
                for (var i = 0; i < count; i++)
                    samples[i] = BitConverter.ToInt16(bytes, pos + 8 + i * 2) / 32768f;
                return (samples, rate);
            }
            if (size <= 0) break;
            pos += 8 + size + (size % 2);
        }
        return ([], rate);
    }

    public static float[] ReadWavMono16k(string path) => ReadWav(path).Samples;

    public static float[] Slice(float[] samples, double startSec, double endSec, int rate = Rate)
    {
        var a = Math.Clamp((int)(startSec * rate), 0, samples.Length);
        var b = Math.Clamp((int)(endSec * rate), a, samples.Length);
        return samples[a..b];
    }

    public static void WriteWav(string path, float[] samples, int rate = Rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        var dataBytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples)
            w.Write((short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue));
    }

    /// <summary>Writes already-packed 16-bit PCM bytes with a RIFF header.</summary>
    public static void WriteWavPcm(string path, byte[] pcm, int rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
    }

    // ---- misc ---------------------------------------------------------------

    public static string EpisodeKey(string fileTag)
    {
        var m = Regex.Match(fileTag, @"S(\d\d)E(\d\d)", RegexOptions.IgnoreCase);
        return m.Success ? $"E{m.Groups[2].Value}" : fileTag;
    }

    public static string? ArgValue(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    public static string SolutionDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "models"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }

    /// <summary>Formats seconds as the h:mm:ss.mmm an SRT / a video player shows.</summary>
    public static string Timecode(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss\.fff");
}
