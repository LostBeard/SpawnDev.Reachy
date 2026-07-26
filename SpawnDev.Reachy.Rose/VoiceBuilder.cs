using System.Globalization;

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
/// on to it while the speaker has not changed, which lengthens the reference
/// without breaking the alignment. (<see cref="ShowAudio"/> does all of that.)
///
/// WHICH captioned line gets picked is the other half of the problem, and the half
/// that was originally wrong here. This used to keep the LONGEST run per character,
/// which reliably picked their loudest scene - the longest uninterrupted thing a
/// character says is usually a rant. ZipVoice transfers delivery, so a shouted
/// reference makes a clone that shouts everything. Selection now goes through
/// <see cref="VoiceCandidates"/>, which ranks on measured calmness and cleanliness.
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
    public static async Task<int> RunAsync(string[] args)
    {
        var solution = ShowAudio.SolutionDir();
        var source = ShowAudio.ArgValue(args, "--source=") ?? @"V:\Video\Series\Murder Drones\S01";
        var work = ShowAudio.ArgValue(args, "--work=") ?? Path.Combine(solution, "scratchpad", "md_audio");
        var outDir = ShowAudio.ArgValue(args, "--out=") ?? Path.Combine(solution, "models", "voiceprints");
        var modelDir = Path.Combine(solution, "models");
        var minSec = Dbl(args, "--min=", 3.0);
        var maxSec = Dbl(args, "--max=", 9.0);
        var denoise = !args.Contains("--no-denoise");
        var keep = args.Contains("--keep");

        Directory.CreateDirectory(work);
        Directory.CreateDirectory(outDir);

        using var denoiser = denoise ? ShowAudio.MakeDenoiser(modelDir) : null;
        if (denoise && denoiser is null)
            Console.WriteLine("  (denoiser model missing at models/gtcrn_simple.onnx - clips will keep room ambience)");

        var ranked = await VoiceCandidates.RankAllAsync(source, work, minSec, maxSec);
        if (ranked.Count == 0)
        {
            Console.WriteLine($"No captioned reference lines found. Checked {work} and {source}.");
            return 1;
        }

        Console.WriteLine("\n== reference clips ==");
        var wrote = 0;
        foreach (var c in CharacterLibrary.All)
        {
            var wav = Path.Combine(outDir, $"{c.Name}.wav");
            var txt = Path.Combine(outDir, $"{c.Name}.txt");

            // A hand-picked reference beats anything the ranking can find, so --keep
            // protects the ones already chosen by ear while the rest are rebuilt.
            if (keep && File.Exists(wav) && File.Exists(txt))
            {
                Console.WriteLine($"  {c.Name,-5}  kept (already chosen by ear)");
                continue;
            }

            if (!ranked.TryGetValue(c.Name, out var list) || list.Count == 0)
            {
                Console.WriteLine($"  {c.Name,-5}  (no captioned line found - audition one by hand)");
                continue;
            }

            var pick = list[0];
            var audio = VoiceCandidates.CutAudio(work, pick);
            if (denoiser is not null) audio = denoiser.Run(audio, ShowAudio.Rate).Samples;

            ShowAudio.WriteWav(wav, audio);
            File.WriteAllText(txt, pick.Run.Text);
            Console.WriteLine(
                $"  {c.Name,-5}  {pick.Run.Duration,4:F1}s [{pick.Episode} @ {ShowAudio.Timecode(pick.Run.Start)}] {pick.Source,-8}"
                + $"  calm {pick.Calm,5:F2}  f0 {pick.F.MedianF0,3:F0}Hz"
                + $"  of {list.Count,4} candidate(s)");
            Console.WriteLine($"         \"{Truncate(pick.Run.Text, 78)}\"");
            wrote++;
        }

        Console.WriteLine($"\nWrote {wrote} reference clip(s) to {outDir}");
        Console.WriteLine("Ranking narrows it; your ear decides. To compare the runners-up for one character:");
        Console.WriteLine("  --candidates --name=N     then     --pick --name=N --index=<n>");
        return wrote > 0 ? 0 : 1;
    }

    private static double Dbl(string[] args, string prefix, double fallback) =>
        double.TryParse(ShowAudio.ArgValue(args, prefix), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
