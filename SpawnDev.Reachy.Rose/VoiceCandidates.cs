using System.Globalization;
using System.Text;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Finds, ranks and auditions candidate voice-cloning reference lines from the show.
/// </summary>
/// <remarks>
/// ZipVoice transfers the reference clip's DELIVERY, not just its timbre. A reference
/// cut from a shouted line produces a clone that shouts every sentence it is ever
/// given - which is exactly how N, who is soft-spoken and apologetic almost all the
/// time, ended up sounding nothing like himself: the original picker optimised for
/// the LONGEST single-speaker run, and the longest uninterrupted run a character gets
/// is usually the one where they are yelling.
///
/// Two things had to change. The pool had to get bigger - the captions name a speaker
/// only when it is ambiguous, so N had FIVE labelled lines in the whole season and no
/// ranking can find a calm line among five loud ones. <see cref="SpeakerMatcher"/>
/// identifies the unlabelled ones by voice, without ever altering how a clip is cut.
///
/// And the objective had to change from longest to calmest, measured rather than
/// guessed:
///
///   calm  - median pitch, loudness, spectral brightness and level variation, each
///           scored against that character's OWN distribution. Shouting raises pitch,
///           raises level, and shifts energy upward in the spectrum; all three move
///           together and none of them need a threshold picked by hand.
///   clean - the noise floor under the line (a music bed or an effect sitting beneath
///           the dialogue) and how much of the window is actually speech.
///
/// The caption text votes too: exclamation marks and ALL-CAPS words are the caption
/// author telling us, in writing, that this line is shouted.
///
/// Ranking narrows it; it does not decide. The last step is a human ear, so every
/// shortlisted candidate is also cloned saying the same calm test sentence, and the
/// winner is locked in with --pick.
/// </remarks>
internal static class VoiceCandidates
{
    /// <summary>
    /// A calm, N-typical audition line. Deliberately soft: no exclamations, no
    /// hard plosive runs, and the hesitant phrasing the character actually uses.
    /// A test sentence full of sharp loud sounds makes every reference sound sharp
    /// and loud, which tells you nothing about the reference.
    /// </summary>
    public const string DefaultSay =
        "Oh gosh, hi Aubs. Um, I'm N. I was kind of hoping we could just hang out and be friends, if that's okay with you.";

    // ---- shortlist ----------------------------------------------------------

    public static async Task<int> RunAsync(string[] args)
    {
        var solution = ShowAudio.SolutionDir();
        var modelDir = Path.Combine(solution, "models");
        var work = ShowAudio.ArgValue(args, "--work=") ?? Path.Combine(solution, "scratchpad", "md_audio");
        var source = ShowAudio.ArgValue(args, "--source=") ?? @"V:\Video\Series\Murder Drones\S01";
        var outRoot = ShowAudio.ArgValue(args, "--out=") ?? Path.Combine(solution, "scratchpad", "candidates");

        var name = ShowAudio.ArgValue(args, "--name=") ?? "N";
        var character = CharacterLibrary.Find(name);
        if (character is null)
        {
            Console.WriteLine($"unknown character '{name}'. Known: {string.Join(", ", CharacterLibrary.All.Select(c => c.Name))}");
            return 1;
        }
        name = character.Name;

        var top = Int(args, "--top=", 8);
        var minSec = Dbl(args, "--min=", 3.0);
        var maxSec = Dbl(args, "--max=", 9.0);
        var say = ShowAudio.ArgValue(args, "--say=") ?? DefaultSay;
        var clone = !args.Contains("--no-clone");
        var fp32 = !args.Contains("--int8");
        var steps = Int(args, "--steps=", 16);
        var captionsOnly = args.Contains("--captions-only");

        Console.WriteLine($"Ranking reference candidates for {name} ({minSec:F1}-{maxSec:F1}s, top {top}).\n");

        var ranked = await RankAllAsync(source, work, minSec, maxSec, captionsOnly, args.Contains("--voice-id"));
        if (!ranked.TryGetValue(name, out var scored) || scored.Count == 0)
        {
            Console.WriteLine($"No lines found for {name}.");
            Console.WriteLine("(Audition one by hand instead: --audition --ep= --from= --to= --reftext=)");
            return 1;
        }

        var shortlist = scored.Take(top).ToList();
        Console.WriteLine($"\n== {name}: top {shortlist.Count} of {scored.Count} candidate line(s) ==");

        var outDir = Path.Combine(outRoot, name);
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        using var denoiser = ShowAudio.MakeDenoiser(modelDir);
        if (denoiser is null) Console.WriteLine("  (no denoiser model - clips will keep room ambience)\n");

        // Write every shortlisted reference exactly as --build-voices would write it:
        // cut at the caption, then denoised. Auditioning anything else would audition
        // a clip we are not going to use.
        var report = new StringBuilder();
        report.AppendLine($"Reference candidates for {name}  (ranked calmest + cleanest first)");
        report.AppendLine($"test line: \"{say}\"");
        report.AppendLine();

        var audio = new EpisodeAudio(work);
        for (var i = 0; i < shortlist.Count; i++)
        {
            var c = shortlist[i];
            var tag = $"{name}_{i + 1:00}";
            var clip = audio.Cut(c);
            if (denoiser is not null) clip = denoiser.Run(clip, ShowAudio.Rate).Samples;

            ShowAudio.WriteWav(Path.Combine(outDir, $"{tag}_ref.wav"), clip);
            File.WriteAllText(Path.Combine(outDir, $"{tag}_ref.txt"), c.Run.Text);

            var line = $"{i + 1,2}. {c.Run.Duration,4:F1}s  {c.Episode} @ {ShowAudio.Timecode(c.Run.Start)}  {c.Source,-8}"
                     + $"  calm {c.Calm,5:F2}  clean {c.Clean,5:F2}  |  f0 {c.F.MedianF0,3:F0}Hz"
                     + $"  {c.F.RmsDb,5:F1}dB  hf {c.F.HfRatio,4:F2}  floor {c.F.NoiseFloorDb,5:F1}dB";
            Console.WriteLine(line);
            Console.WriteLine($"     \"{Truncate(c.Run.Text, 96)}\"");
            report.AppendLine(line);
            report.AppendLine($"     \"{c.Run.Text}\"");
            report.AppendLine();
        }

        if (clone)
        {
            Console.WriteLine($"\nCloning {shortlist.Count} candidate(s) saying the test line "
                            + $"({(fp32 ? "fp32" : "int8")}, {steps} steps). This is the part you listen to.\n");
            using var voice = new RoseVoiceClone(modelDir, fp32, steps);
            for (var i = 0; i < shortlist.Count; i++)
            {
                var tag = $"{name}_{i + 1:00}";
                var (refSamples, refRate) = ShowAudio.ReadWav(Path.Combine(outDir, $"{tag}_ref.wav"));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pcm = voice.Clone(say, refSamples, refRate, shortlist[i].Run.Text);
                ShowAudio.WriteWavPcm(Path.Combine(outDir, $"{tag}_say.wav"), pcm, voice.SampleRate);
                Console.WriteLine($"  {tag}_say.wav  ({pcm.Length / 2.0 / voice.SampleRate:F1}s audio in {sw.Elapsed.TotalSeconds:F1}s)");
            }
        }

        report.AppendLine();
        report.AppendLine("Listen to the *_say.wav files - they all speak the same line, so the only");
        report.AppendLine("difference you hear is the reference. Lock the winner in with:");
        report.AppendLine($"  dotnet run --project SpawnDev.Reachy.Rose -- --pick --name={name} --index=<n>");
        File.WriteAllText(Path.Combine(outDir, "_shortlist.txt"), report.ToString());

        Console.WriteLine($"\nWrote {shortlist.Count} candidate(s) to {outDir}");
        Console.WriteLine($"Listen to {name}_NN_say.wav (same line every time - the only variable is the reference),");
        Console.WriteLine($"then lock the winner:  --pick --name={name} --index=<n>");
        return 0;
    }

    // ---- contact sheet ------------------------------------------------------

    /// <summary>
    /// Ranks every calm, clean single-speaker line in the season - whoever says it -
    /// and writes them out with their text, for a human to pick from by reading.
    /// </summary>
    /// <remarks>
    /// The captions name a speaker only when it would otherwise be ambiguous: across
    /// all eight episodes there are EIGHT [N] cues, of which five survive as usable
    /// reference runs. No ranking can fix a pool that small, and the speaker-embedding
    /// classifier that would have widened it does not work on this material (its
    /// leave-one-out accuracy is measured on every run and it fails the gate).
    ///
    /// What DOES work is that a human reads "Uzi, there, um... might be some stuff
    /// down here that you don't want to see" and knows instantly that it is N. So the
    /// machine does what it is good at - finding the calmest, cleanest, best-aligned
    /// windows in seven hours of audio - and the human does the part that needs one
    /// second of judgment per line, on forty lines instead of eight episodes.
    /// </remarks>
    public static async Task<int> SheetAsync(string[] args)
    {
        var solution = ShowAudio.SolutionDir();
        var modelDir = Path.Combine(solution, "models");
        var work = ShowAudio.ArgValue(args, "--work=") ?? Path.Combine(solution, "scratchpad", "md_audio");
        var source = ShowAudio.ArgValue(args, "--source=") ?? @"V:\Video\Series\Murder Drones\S01";
        var outDir = ShowAudio.ArgValue(args, "--out=") ?? Path.Combine(solution, "scratchpad", "candidates", "_sheet");

        var top = Int(args, "--top=", 40);
        var minSec = Dbl(args, "--min=", 3.0);
        var maxSec = Dbl(args, "--max=", 9.0);
        var f0Min = Dbl(args, "--f0min=", 0);
        var f0Max = Dbl(args, "--f0max=", double.MaxValue);
        var contains = ShowAudio.ArgValue(args, "--contains=");

        var pool = await RankPoolAsync(source, work, minSec, maxSec);
        var filtered = pool
            .Where(c => c.F.MedianF0 >= f0Min && c.F.MedianF0 <= f0Max)
            .Where(c => contains is null || c.Run.Text.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .Take(top).ToList();

        if (filtered.Count == 0) { Console.WriteLine("nothing matched"); return 1; }

        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        using var denoiser = ShowAudio.MakeDenoiser(modelDir);
        var audio = new EpisodeAudio(work);
        var report = new StringBuilder();
        report.AppendLine($"Calmest, cleanest single-speaker lines in the season ({filtered.Count} of {pool.Count}).");
        report.AppendLine("Read the text to spot your character, listen to the clip to confirm, then:");
        report.AppendLine("  --pick --name=<character> --sheet --index=<n>");
        report.AppendLine();

        Console.WriteLine($"\n== contact sheet: {filtered.Count} line(s) ==");
        for (var i = 0; i < filtered.Count; i++)
        {
            var c = filtered[i];
            var tag = $"SHEET_{i + 1:00}";
            var clip = audio.Cut(c);
            if (denoiser is not null) clip = denoiser.Run(clip, ShowAudio.Rate).Samples;
            ShowAudio.WriteWav(Path.Combine(outDir, $"{tag}_ref.wav"), clip);
            File.WriteAllText(Path.Combine(outDir, $"{tag}_ref.txt"), c.Run.Text);

            var head = $"{i + 1,3}. {c.Run.Duration,4:F1}s  {c.Episode} @ {ShowAudio.Timecode(c.Run.Start)}"
                     + $"  calm {c.Calm,5:F2}  f0 {c.F.MedianF0,3:F0}Hz  floor {c.F.NoiseFloorDb,5:F1}dB"
                     + (c.FromCaption ? $"  [captioned {c.Run.Name}]" : "");
            Console.WriteLine(head);
            Console.WriteLine($"      \"{Truncate(c.Run.Text, 100)}\"");
            report.AppendLine(head);
            report.AppendLine($"      \"{c.Run.Text}\"");
            report.AppendLine();
        }

        File.WriteAllText(Path.Combine(outDir, "_sheet.txt"), report.ToString());
        Console.WriteLine($"\nWrote {filtered.Count} clip(s) + _sheet.txt to {outDir}");
        Console.WriteLine("Read the text to spot N, play the clip to confirm, then:");
        Console.WriteLine("  --pick --name=N --sheet --index=<n>");
        return 0;
    }

    /// <summary>Every candidate line in the season, ranked together regardless of speaker.</summary>
    private static async Task<List<Candidate>> RankPoolAsync(string source, string work, double minSec, double maxSec)
    {
        var all = await CollectRunsAsync(source, work, maxSec, minSec);
        return Rank(all);
    }

    // ---- locking a winner ---------------------------------------------------

    /// <summary>Promotes a shortlisted candidate to the character's live voiceprint.</summary>
    public static int Pick(string[] args)
    {
        var solution = ShowAudio.SolutionDir();
        var name = ShowAudio.ArgValue(args, "--name=") ?? "N";
        var character = CharacterLibrary.Find(name);
        if (character is null) { Console.WriteLine($"unknown character '{name}'"); return 1; }
        name = character.Name;

        var index = Int(args, "--index=", 0);
        if (index < 1) { Console.WriteLine("need --index=<n> (the number shown in the shortlist)"); return 1; }

        var fromSheet = args.Contains("--sheet");
        var candidateDir = Path.Combine(solution, "scratchpad", "candidates", fromSheet ? "_sheet" : name);
        var tag = fromSheet ? $"SHEET_{index:00}" : $"{name}_{index:00}";
        var srcWav = Path.Combine(candidateDir, $"{tag}_ref.wav");
        var srcTxt = Path.Combine(candidateDir, $"{tag}_ref.txt");
        if (!File.Exists(srcWav) || !File.Exists(srcTxt))
        {
            Console.WriteLine($"no candidate {index} for {name} (looked for {srcWav})");
            Console.WriteLine($"run:  --candidates --name={name}");
            return 1;
        }

        var outDir = Path.Combine(solution, "models", "voiceprints");
        Directory.CreateDirectory(outDir);
        File.Copy(srcWav, Path.Combine(outDir, $"{name}.wav"), overwrite: true);
        File.Copy(srcTxt, Path.Combine(outDir, $"{name}.txt"), overwrite: true);

        var text = File.ReadAllText(srcTxt).Trim();
        var (s, r) = ShowAudio.ReadWav(srcWav);
        Console.WriteLine($"{name} voiceprint <- candidate {index}  ({s.Length / (double)r:F1}s)");
        Console.WriteLine($"  \"{text}\"");
        Console.WriteLine($"  -> models/voiceprints/{name}.wav + .txt");
        return 0;
    }

    // ---- auditioning a hand-picked line -------------------------------------

    /// <summary>
    /// Cuts, denoises and clones an arbitrary window of an episode, for a line
    /// picked by ear rather than by the caption ranking.
    /// </summary>
    /// <remarks>
    /// Needed for characters the captions never label on their own (Doll), and for
    /// any line a human hears as more representative than the ranking's pick. The
    /// transcript must match the audio word for word or ZipVoice leaks the missing
    /// words into everything it says, so --reftext is required and not guessed.
    /// </remarks>
    public static async Task<int> AuditionAsync(string[] args)
    {
        var solution = ShowAudio.SolutionDir();
        var modelDir = Path.Combine(solution, "models");
        var work = ShowAudio.ArgValue(args, "--work=") ?? Path.Combine(solution, "scratchpad", "md_audio");
        var source = ShowAudio.ArgValue(args, "--source=") ?? @"V:\Video\Series\Murder Drones\S01";

        var ep = (ShowAudio.ArgValue(args, "--ep=") ?? "E01").ToUpperInvariant();
        if (!ep.StartsWith('E')) ep = "E" + ep.PadLeft(2, '0');
        var from = Time(ShowAudio.ArgValue(args, "--from="));
        var to = Time(ShowAudio.ArgValue(args, "--to="));
        var refText = ShowAudio.ArgValue(args, "--reftext=");
        var say = ShowAudio.ArgValue(args, "--say=") ?? DefaultSay;
        var name = ShowAudio.ArgValue(args, "--name=");
        var outPath = ShowAudio.ArgValue(args, "--out=") ?? Path.Combine(solution, "scratchpad", "audition.wav");
        var fp32 = !args.Contains("--int8");
        var steps = Int(args, "--steps=", 16);
        var denoise = !args.Contains("--no-denoise");

        var wavPath = Path.Combine(work, $"{ep}.wav");
        if (!File.Exists(wavPath))
        {
            var mkv = Directory.Exists(source)
                ? Directory.GetFiles(source, "*.mkv").FirstOrDefault(f => ShowAudio.EpisodeKey(Path.GetFileNameWithoutExtension(f)) == ep)
                : null;
            if (mkv is null) { Console.WriteLine($"no audio for {ep} at {wavPath}, and no matching MKV under {source}"); return 1; }
            if (!await ShowAudio.EnsureExtractedAsync(mkv, wavPath, Path.Combine(work, $"{ep}.srt"))) return 1;
        }

        // --at= snaps to the captioned line playing at that moment and takes its exact
        // window AND its exact words. That is the whole point: pausing the show and
        // reading a timecode off the player is free, while transcribing a line by hand
        // is both tedious and the one mistake that breaks cloning - a single word in
        // the audio that is missing from the transcript leaks into every generated line.
        var at = Time(ShowAudio.ArgValue(args, "--at="));
        if (!double.IsNaN(at))
        {
            var cues = ShowAudio.ParseCues(Path.Combine(work, $"{ep}.srt"));
            if (cues.Count == 0) { Console.WriteLine($"no captions at {Path.Combine(work, $"{ep}.srt")}"); return 1; }

            var runs = ShowAudio.BuildAllRuns(cues, maxSeconds: Dbl(args, "--max=", 12.0), minSeconds: 0.5);
            var hit = runs.FirstOrDefault(r => at >= r.Start - 0.5 && at <= r.End + 0.5)
                   ?? runs.OrderBy(r => Math.Min(Math.Abs(r.Start - at), Math.Abs(r.End - at))).FirstOrDefault();
            if (hit is null) { Console.WriteLine($"no single-speaker captioned line near {ShowAudio.Timecode(at)} in {ep}"); return 1; }

            from = hit.Start;
            to = hit.End;
            refText ??= hit.Text;
            Console.WriteLine($"--at {ShowAudio.Timecode(at)} snapped to the captioned line "
                            + $"{ShowAudio.Timecode(from)} -> {ShowAudio.Timecode(to)}"
                            + (hit.Name is not null ? $"  [captioned {hit.Name}]" : "  [speaker not captioned]"));
            Console.WriteLine($"  \"{hit.Text}\"");
            Console.WriteLine($"  (if that is not the line you meant, nudge --at= or give --from=/--to=/--reftext= yourself)");
        }

        if (double.IsNaN(from) || double.IsNaN(to) || to <= from)
        {
            Console.WriteLine("need --at=<mm:ss> (snaps to the caption), or --from=<mm:ss.mmm> --to=<mm:ss.mmm>");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(refText))
        {
            Console.WriteLine("need --reftext=\"the exact words spoken in that window\"");
            Console.WriteLine("(exact: any word in the audio but not the text leaks into every line the clone speaks)");
            return 1;
        }

        var samples = ShowAudio.ReadWavMono16k(wavPath);
        var clip = ShowAudio.Slice(samples, from, to);
        if (clip.Length == 0) { Console.WriteLine($"empty window {from}-{to} in {ep}"); return 1; }

        var raw = AudioAnalysis.Measure(clip, ShowAudio.Rate);
        Console.WriteLine($"{ep} {ShowAudio.Timecode(from)} -> {ShowAudio.Timecode(to)}  ({clip.Length / (double)ShowAudio.Rate:F1}s)");
        Console.WriteLine($"  f0 {raw.MedianF0:F0}Hz  {raw.RmsDb:F1}dB  hf {raw.HfRatio:F2}  floor {raw.NoiseFloorDb:F1}dB  speech {raw.VoicedFraction:P0}");
        Console.WriteLine($"  ref text: \"{refText}\"");

        if (denoise)
        {
            using var denoiser = ShowAudio.MakeDenoiser(modelDir);
            if (denoiser is not null) clip = denoiser.Run(clip, ShowAudio.Rate).Samples;
            else Console.WriteLine("  (no denoiser model on disk - keeping room ambience)");
        }

        var refWav = Path.ChangeExtension(outPath, null) + "_ref.wav";
        ShowAudio.WriteWav(refWav, clip);
        File.WriteAllText(Path.ChangeExtension(refWav, ".txt"), refText.Trim());

        using var voice = new RoseVoiceClone(modelDir, fp32, steps);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pcm = voice.Clone(say, clip, ShowAudio.Rate, refText.Trim());
        ShowAudio.WriteWavPcm(outPath, pcm, voice.SampleRate);
        Console.WriteLine($"  said: \"{say}\"");
        Console.WriteLine($"  wrote {outPath}  ({pcm.Length / 2.0 / voice.SampleRate:F1}s audio in {sw.Elapsed.TotalSeconds:F1}s)");

        if (name is not null && CharacterLibrary.Find(name) is { } ch)
        {
            var outDir = Path.Combine(solution, "models", "voiceprints");
            Directory.CreateDirectory(outDir);
            File.Copy(refWav, Path.Combine(outDir, $"{ch.Name}.wav"), overwrite: true);
            File.WriteAllText(Path.Combine(outDir, $"{ch.Name}.txt"), refText.Trim());
            Console.WriteLine($"  locked in as {ch.Name}'s voiceprint (--name={ch.Name} was given)");
        }
        else
        {
            Console.WriteLine($"  (add --name=<character> to lock this in as their voiceprint)");
        }
        return 0;
    }

    // ---- candidates ---------------------------------------------------------

    /// <param name="Run">The captioned line: when, and the exact words.</param>
    /// <param name="Episode">Episode key, e.g. "E03".</param>
    /// <param name="F">Measurements taken on the raw cut, before denoising.</param>
    /// <param name="Embedding">Speaker embedding of the raw cut, or null if unavailable.</param>
    public sealed record Candidate(ShowAudio.Run Run, string Episode, AudioAnalysis.Features F, float[]? Embedding)
    {
        /// <summary>Who this line belongs to, whether the captions said so or the voice did.</summary>
        public string? Name { get; set; } = Run.Name;

        /// <summary>True when the captions named the speaker outright.</summary>
        public bool FromCaption => Run.Name is not null;

        /// <summary>Cosine similarity to the assigned character's voice, when identified by voice.</summary>
        public double Similarity { get; set; }

        /// <summary>Short provenance tag for the shortlist: how we know who is speaking.</summary>
        public string Source => FromCaption ? "[CC]" : $"~{Similarity:F2}";

        public double Calm { get; set; }
        public double Clean { get; set; }
        public double Total { get; set; }
    }

    /// <summary>
    /// Every character's candidate lines, each character's list ranked best first.
    /// </summary>
    /// <remarks>
    /// One pass over the episode audio for all characters at once, and one shared
    /// ranking rule. Both the audition shortlist and --build-voices go through here,
    /// so what you audition is exactly what gets written - the two cannot drift.
    /// </remarks>
    public static async Task<Dictionary<string, List<Candidate>>> RankAllAsync(
        string source, string work, double minSec, double maxSec, bool captionsOnly = false, bool forceVoiceId = false)
    {
        var all = await CollectRunsAsync(source, work, maxSec, minSec);
        if (!captionsOnly) Identify(all, forceVoiceId);

        // Ranked per character: the scores are z-scores against that character's own
        // range, because "calm" only means anything relative to how they usually sound.
        return all.Where(c => c.Name is not null)
                  .GroupBy(c => c.Name!, StringComparer.OrdinalIgnoreCase)
                  .ToDictionary(g => g.Key, g => Rank(g.ToList()), StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<List<Candidate>> CollectRunsAsync(string source, string work, double maxSec, double minSec)
    {
        Directory.CreateDirectory(work);
        var modelDir = Path.Combine(ShowAudio.SolutionDir(), "models");
        var result = new List<Candidate>();

        // Prefer already-extracted episode audio; only touch the MKVs if it is missing.
        var episodes = Directory.Exists(work)
            ? Directory.GetFiles(work, "E*.wav").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        if (episodes.Count == 0 && Directory.Exists(source))
        {
            foreach (var mkv in Directory.GetFiles(source, "*.mkv").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var key = ShowAudio.EpisodeKey(Path.GetFileNameWithoutExtension(mkv));
                var wav = Path.Combine(work, $"{key}.wav");
                Console.WriteLine($"== {key} ==");
                if (await ShowAudio.EnsureExtractedAsync(mkv, wav, Path.Combine(work, $"{key}.srt")))
                    episodes.Add(wav);
            }
        }

        using var matcher = SpeakerMatcher.Create(modelDir);
        if (matcher is null)
            Console.WriteLine("  (no speaker model at models/wespeaker_en_voxceleb_CAM++.onnx - captioned lines only)");

        // The embedding is taken on the DENOISED clip. The show mixes music and
        // effects under the dialogue, and a speaker embedding of dialogue-plus-score
        // partly describes the score - which makes two characters sharing a cue look
        // more alike than either does to themselves in a different scene. Measured:
        // leave-one-out accuracy is reported on every run, so this is checkable.
        using var embedDenoiser = ShowAudio.MakeDenoiser(modelDir);

        foreach (var wavPath in episodes)
        {
            var key = Path.GetFileNameWithoutExtension(wavPath);
            var srtPath = Path.Combine(work, $"{key}.srt");
            var cues = ShowAudio.ParseCues(srtPath);
            if (cues.Count == 0) continue;

            var samples = ShowAudio.ReadWavMono16k(wavPath);
            if (samples.Length == 0) continue;

            var kept = 0;
            foreach (var run in ShowAudio.BuildAllRuns(cues, maxSec, minSec))
            {
                if (run.Duration < minSec || run.Duration > maxSec) continue;

                var clip = ShowAudio.Slice(samples, run.Start, run.End);
                if (clip.Length < ShowAudio.Rate) continue;

                // Measured on the RAW cut, before denoising: the noise floor under the
                // dialogue is exactly what we want to know about, and the denoiser
                // would erase the evidence.
                var forEmbedding = embedDenoiser is not null ? embedDenoiser.Run(clip, ShowAudio.Rate).Samples : clip;
                result.Add(new Candidate(run, key, AudioAnalysis.Measure(clip, ShowAudio.Rate),
                                         matcher?.Embed(forEmbedding, ShowAudio.Rate)));
                kept++;
            }
            Console.WriteLine($"  {key}  {cues.Count,4} caption(s) -> {kept,4} single-speaker line(s) in range");
        }

        return result;
    }

    // ---- identifying the unlabelled lines -----------------------------------

    /// <summary>
    /// Names the runs the captions left unnamed, by comparing their voice to the
    /// voice of the runs the captions DID name.
    /// </summary>
    /// <remarks>
    /// Validated before it is trusted, by leave-one-out over the labelled lines: each
    /// captioned line is scored against centroids rebuilt without it, and we report
    /// how often the right character wins. That number is printed rather than assumed
    /// - if the embeddings could not tell this cast apart, it would say so.
    ///
    /// Assignment is deliberately conservative (a similarity floor AND a margin over
    /// the runner-up) because an unclaimed line costs nothing while a wrong one wastes
    /// a slot in a shortlist a human has to listen through.
    /// </remarks>
    private static void Identify(List<Candidate> all, bool force)
    {
        var labelled = all.Where(c => c.FromCaption && c.Embedding is not null).ToList();
        var unlabelled = all.Where(c => !c.FromCaption && c.Embedding is not null).ToList();
        if (labelled.Count == 0 || unlabelled.Count == 0) return;

        var byName = labelled.GroupBy(c => c.Name!, StringComparer.OrdinalIgnoreCase)
                             .Where(g => g.Count() >= 2)   // one example is not a voice, it is an anecdote
                             .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        if (byName.Count < 2) return;

        var centroids = byName.ToDictionary(
            kv => kv.Key,
            kv => SpeakerMatcher.Centroid(kv.Value.Select(c => c.Embedding!).ToList()),
            StringComparer.OrdinalIgnoreCase);

        // --- leave-one-out check on the lines we already know the answer for ---
        var correct = 0;
        var total = 0;
        var sameSims = new List<double>();
        var perName = new List<string>();
        foreach (var (who, members) in byName)
        {
            if (members.Count < 3) continue;   // a centroid of one is not a fair test
            var hit = 0;
            foreach (var held in members)
            {
                var others = members.Where(m => !ReferenceEquals(m, held)).Select(m => m.Embedding!).ToList();
                var own = SpeakerMatcher.Centroid(others);

                var ownSim = SpeakerMatcher.Similarity(held.Embedding!, own);
                sameSims.Add(ownSim);

                var bestOther = centroids.Where(kv => !kv.Key.Equals(who, StringComparison.OrdinalIgnoreCase))
                                         .Select(kv => SpeakerMatcher.Similarity(held.Embedding!, kv.Value))
                                         .DefaultIfEmpty(-1).Max();
                if (ownSim > bestOther) { correct++; hit++; }
                total++;
            }
            perName.Add($"{who} {hit}/{members.Count}");
        }

        // A floor taken from how similar a character actually is to himself, rather
        // than a number picked by hand that would need re-tuning per show.
        var minSim = 0.45;
        if (sameSims.Count >= 4)
        {
            sameSims.Sort();
            minSim = Math.Clamp(sameSims[sameSims.Count / 4], 0.35, 0.70);
        }
        const double minMargin = 0.05;

        Console.WriteLine($"\n  voice ID: {centroids.Count} character voiceprint(s) from {labelled.Count} captioned line(s)");
        if (total > 0)
            Console.WriteLine($"  leave-one-out on captioned lines: {correct}/{total} correct"
                            + $" [{string.Join(", ", perName)}]"
                            + $"  (accept >= {minSim:F2} similarity, +{minMargin:F2} margin)");
        else
            Console.WriteLine($"  (too few captioned lines per character to cross-validate)");

        // The classifier has to earn its keep on lines whose answer we already know,
        // every run, before any of its guesses are allowed into a shortlist. Measured
        // on this cast it does not: wespeaker CAM++ is trained on real human speech,
        // and these are heavily-processed cartoon performances mixed under a score, so
        // its accuracy sits at or below chance and everything collapses onto whichever
        // centroid has the most members. A wrong name here does not produce a broken
        // reference (the cut is still caption-aligned) but it does fill a human's
        // shortlist with the wrong character, which is worse than a shorter shortlist.
        //
        // Left in and self-checking rather than deleted: swap in an embedding model
        // that handles this material and it switches itself back on.
        const double requiredAccuracy = 0.80;
        var accuracy = total > 0 ? correct / (double)total : 0;
        if (!force && (total < 8 || accuracy < requiredAccuracy))
        {
            Console.WriteLine($"  -> REJECTED (needs >= {requiredAccuracy:P0} over >= 8 checks). Using captioned lines only.");
            Console.WriteLine($"     Override with --voice-id if you want to see its guesses anyway.");
            return;
        }

        // --- name the unlabelled runs ---
        var assigned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in unlabelled)
        {
            var best = ""; var bestSim = double.MinValue; var second = double.MinValue;
            foreach (var (who, centroid) in centroids)
            {
                var sim = SpeakerMatcher.Similarity(c.Embedding!, centroid);
                if (sim > bestSim) { second = bestSim; bestSim = sim; best = who; }
                else if (sim > second) second = sim;
            }

            if (bestSim < minSim || bestSim - second < minMargin) continue;
            c.Name = best;
            c.Similarity = bestSim;
            assigned[best] = assigned.GetValueOrDefault(best) + 1;
        }

        var summary = string.Join(", ", assigned.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} +{kv.Value}"));
        Console.WriteLine($"  matched {assigned.Values.Sum()} of {unlabelled.Count} unlabelled line(s):  {summary}");
    }

    // ---- ranking ------------------------------------------------------------

    /// <summary>
    /// Scores every candidate against the character's own distribution and sorts best first.
    /// </summary>
    /// <remarks>
    /// Scoring is relative, not absolute. "Calm" only means anything compared to how
    /// this particular character usually sounds - N's quiet is not V's quiet - and a
    /// hand-picked pitch or loudness threshold would need re-tuning per character and
    /// would silently rot. Z-scores need no thresholds and adapt on their own.
    /// </remarks>
    internal static List<Candidate> Rank(List<Candidate> all)
    {
        var f0 = Z(all.Select(c => c.F.MedianF0));
        var rms = Z(all.Select(c => c.F.RmsDb));
        var hf = Z(all.Select(c => c.F.HfRatio));
        var dyn = Z(all.Select(c => c.F.FrameDbStd));
        var floor = Z(all.Select(c => c.F.NoiseFloorDb));

        for (var i = 0; i < all.Count; i++)
        {
            var c = all[i];

            // Shouting raises pitch, level and spectral brightness together, and makes
            // the level swing more. All four point the same way, so the sum is far
            // more robust than any one of them.
            var textHeat = TextHeat(c.Run.Text);
            c.Calm = -(1.2 * f0[i] + 0.9 * rms[i] + 0.8 * hf[i] + 0.4 * dyn[i]) - 1.6 * textHeat;

            // A clean reference matters as much as a calm one: whatever is under the
            // dialogue gets cloned along with the voice.
            c.Clean = -1.0 * floor[i] + 1.5 * (c.F.VoicedFraction - 0.5);

            // Longer references carry the voice better, up to a point - but this is a
            // gentle tiebreak, never the objective. Optimising for it is what produced
            // the shouted reference in the first place.
            var lengthBonus = 0.15 * Math.Min(c.Run.Duration, 7.0) / 7.0;

            // A line the captions named outright is certain; a voice match is very
            // likely. Break ties toward certainty, but never let it outweigh delivery.
            var certainty = c.FromCaption ? 0.10 : 0.0;

            c.Total = 0.65 * c.Calm + 0.35 * c.Clean + lengthBonus + certainty;
        }

        return all.OrderByDescending(c => c.Total).ToList();
    }

    /// <summary>
    /// How loud the caption itself says the line is, in [0,1].
    /// </summary>
    /// <remarks>
    /// The caption author already did this work: they wrote the exclamation marks and
    /// the ALL-CAPS. It is the cheapest and most reliable shouting detector available,
    /// and it is completely independent of the acoustic measurements, so when the two
    /// agree the candidate is very likely right.
    /// </remarks>
    private static double TextHeat(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 1.0;

        var bangs = text.Count(ch => ch == '!');
        var caps = words.Count(w => w.Length >= 2 && w.All(ch => !char.IsLetter(ch) || char.IsUpper(ch)) && w.Any(char.IsLetter));

        // Per-word so a long calm line is not punished for one exclamation, and a
        // three-word line of nothing but exclamations is punished hard.
        var heat = 3.0 * bangs / words.Length + 2.0 * caps / words.Length;
        return Math.Clamp(heat, 0, 1);
    }

    private static double[] Z(IEnumerable<double> values)
    {
        var v = values.ToArray();
        if (v.Length == 0) return [];
        var mean = v.Average();
        var sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Length);
        if (sd < 1e-6) return new double[v.Length];
        return v.Select(x => (x - mean) / sd).ToArray();
    }

    // ---- audio on demand ----------------------------------------------------

    /// <summary>
    /// Re-cuts a candidate's audio from its episode, caching the last episode read.
    /// </summary>
    /// <remarks>
    /// Candidates carry measurements and an embedding, not samples: a whole season of
    /// them held as audio would be gigabytes, while only the shortlisted handful ever
    /// needs its waveform back.
    /// </remarks>
    private sealed class EpisodeAudio(string work)
    {
        private string? _key;
        private float[] _samples = [];

        public float[] Cut(Candidate c)
        {
            if (_key != c.Episode)
            {
                _samples = ShowAudio.ReadWavMono16k(Path.Combine(work, $"{c.Episode}.wav"));
                _key = c.Episode;
            }
            return ShowAudio.Slice(_samples, c.Run.Start, c.Run.End);
        }
    }

    /// <summary>Re-cuts one candidate's audio. For callers that only need a single clip.</summary>
    public static float[] CutAudio(string work, Candidate c) => new EpisodeAudio(work).Cut(c);

    // ---- arg helpers --------------------------------------------------------

    private static int Int(string[] args, string prefix, int fallback) =>
        int.TryParse(ShowAudio.ArgValue(args, prefix), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double Dbl(string[] args, string prefix, double fallback) =>
        double.TryParse(ShowAudio.ArgValue(args, prefix), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Parses "1:23.456", "83.4" or "0:01:23.456" into seconds. NaN if unparseable.</summary>
    private static double Time(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return double.NaN;
        s = s.Trim().Replace(',', '.');
        var parts = s.Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return double.NaN;
            total = total * 60 + v;
        }
        return total;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
