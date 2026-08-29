using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Utilities;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Measures whether listening to her own voice actually stops Rose speaking nonsense.
/// </summary>
/// <remarks>
/// The cloner draws fresh noise for every render and some draws come back as a different
/// sentence entirely - it is a property of the model, not of the words it is given, so it
/// cannot be tuned away with a better reference or better phonemes. The answer is to hear
/// the render and draw again, which is what <see cref="RoseVoiceClone.CloneChecked"/> does
/// and what this measures: how often the first draw was already fine, how often a re-roll
/// rescued a bad one, how often nothing did, and what the check costs in time.
///
/// Everything here is desktop compute - render, transcribe, compare, re-roll. Only the
/// speaker is on the robot, so this needs no hardware and can gate the behaviour before
/// Aubs ever hears it.
/// </remarks>
internal static class SpeechVerification
{
    /// <summary>
    /// Lines to render. Real conversational speech in N's register, including the
    /// question forms and the trailing-off ellipses these characters lean on, because
    /// those are what Rose actually says.
    /// </summary>
    private static readonly string[] Fixtures =
    [
        "Oh gosh, hi Aubs! It's N. What do you want to talk about?",
        "Wait, really? That's the coolest thing I've heard all day.",
        "Uzi says I'm useless at this, but I think I'm getting better.",
        "Do you want to hear about the time I fell off the roof?",
        "I don't know, that sounds kind of dangerous, doesn't it?",
        "Sorry! Sorry. I get carried away sometimes.",
        "The murder drones were built in a factory on a different planet.",
        "Hey, can we talk about something else? Please?",
        "That's my favourite one too! We should watch it together.",
        "Um... I think I forgot what I was going to say.",
        "You're really good at building things, way better than me.",
        "Nightmares aren't real, so there's nothing to be scared of.",
    ];

    /// <summary>
    /// A sentence with a known-clean rendering path, used to prove the recogniser works
    /// before any conclusion is drawn from what it hears.
    /// </summary>
    /// <remarks>
    /// Deliberately free of compound words. An earlier control ended "near the river bank"
    /// and a PERFECT render scored 15% word error, because the recogniser writes it as one
    /// word - a floor under the instrument that had nothing to do with the audio. The
    /// control has to measure the instrument's noise, not add its own.
    /// </remarks>
    private const string ControlLine =
        "The quick brown fox jumps over the lazy dog every single morning.";

    public static async Task<int> RunAsync(string[] args)
    {
        var modelDir = ShowAudio.SolutionDir() is var sln && Directory.Exists(Path.Combine(sln, "models"))
            ? Path.Combine(sln, "models") : Path.Combine(Directory.GetCurrentDirectory(), "models");

        var name = ShowAudio.ArgValue(args, "--name=") ?? "N";
        var steps = int.TryParse(ShowAudio.ArgValue(args, "--steps="), out var st) ? st : 16;
        var trials = int.TryParse(ShowAudio.ArgValue(args, "--trials="), out var tr) ? tr : 2;
        var tolerance = double.TryParse(ShowAudio.ArgValue(args, "--tolerance="), out var to) ? to : 0.2;
        var earsModel = ShowAudio.ArgValue(args, "--ears=") ?? "base.en";
        var provider = args.Contains("--cpu") ? "cpu" : "cuda";
        var fp32 = !args.Contains("--int8");

        var character = CharacterLibrary.All.FirstOrDefault(
                            c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? CharacterLibrary.Default;

        var refWav = Path.Combine(modelDir, "voiceprints", $"{character.Name}.wav");
        var refTxt = Path.Combine(modelDir, "voiceprints", $"{character.Name}.txt");
        if (!File.Exists(refWav) || !File.Exists(refTxt))
        {
            Console.WriteLine($"no voiceprint for {character.Name} under models/voiceprints - run --build-voices or --pick-clip first");
            return 1;
        }

        var (reference, refRate) = ShowAudio.ReadWav(refWav);
        var referenceText = File.ReadAllText(refTxt).Trim();
        if (reference.Length == 0 || referenceText.Length == 0)
        {
            Console.WriteLine($"voiceprint for {character.Name} is empty");
            return 1;
        }

        Console.WriteLine($"character : {character.Name}  (pitch floor {character.PitchFloorHz?.ToString() ?? "-"}, "
                        + $"ceiling {character.PitchCeilingHz?.ToString() ?? "-"}, rate {character.SpeakingRate:F2})");
        Console.WriteLine($"reference : {reference.Length / (double)refRate:F1}s @ {refRate}Hz  \"{referenceText}\"");
        Console.WriteLine($"cloner    : {(fp32 ? "fp32" : "int8")}, {steps} steps, {provider}");
        Console.WriteLine($"recogniser: whisper {earsModel} on cpu (the SELF-CHECK model, not the microphone's)");
        Console.WriteLine();

        // The same class the live path checks renders with, so this harness measures the
        // production instrument rather than a stand-in. NOT RoseEars: the microphone's
        // recogniser is a different model on purpose.
        using var ears = new SpeechRecognizer(modelDir, whisperModel: earsModel, threads: 2);

        // ---- Control 1: can the comparison SEE a wrong sentence at all? -------------------------------
        // A grader that scores everything as fine would report a flawless run no matter how
        // badly the cloner behaved, so it is checked against a known answer first.
        var same = SpokenTextCheck.WordErrorRate(ControlLine, ControlLine);
        var different = SpokenTextCheck.WordErrorRate(ControlLine, "Loner's call, Nanawa, Nenfer, and the rest of them");
        Console.WriteLine($"control 1 : identical text scores {same:P0}, unrelated text scores {different:P0}");
        if (same > 0.001 || different < 0.5)
        {
            Console.WriteLine("            FAILED - the comparison cannot tell a wrong sentence from a right one.");
            return 2;
        }

        // ---- Control 2: does the recogniser understand 24kHz synthesised speech? ----------------------
        // Everything below is a transcription of a 24kHz render. If that path were broken,
        // every render would look garbled and the run would "prove" the cloner is hopeless.
        // Kokoro is deterministic and clean, so a bad score here is the instrument, not the model.
        var kokoroPath = FindKokoro();
        if (kokoroPath is null)
        {
            Console.WriteLine("control 2 : SKIPPED - kokoro.onnx not found, cannot validate the 24kHz recogniser path");
            return 2;
        }

        using (var synth = new KokoroWavSynthesizer(kokoroPath))
        {
            var pcm = await synth.SynthesizeAsync(ControlLine, KokoroVoiceManager.GetVoice(character.Voice));
            var controlSamples = ToFloat(pcm);
            var controlHeard = ears.Transcribe(controlSamples, 24000);
            var controlError = SpokenTextCheck.WordErrorRate(ControlLine, controlHeard);
            Console.WriteLine($"control 2 : 24kHz clean speech transcribes at {controlError:P0} word error");
            Console.WriteLine($"            heard \"{controlHeard}\"");
            if (controlError > tolerance)
            {
                Console.WriteLine("            FAILED - the recogniser cannot read a clean 24kHz render, so nothing");
                Console.WriteLine("            below would measure the cloner. Fix the transcription path first.");
                return 2;
            }
        }

        Console.WriteLine();

        // ---- The measurement --------------------------------------------------------------------------
        using var clone = new RoseVoiceClone(modelDir, fp32, steps, provider)
        {
            StabilizePitch = true,
            PitchCeiling = character.PitchCeilingHz ?? 0,
            PitchFloor = character.PitchFloorHz ?? 0,
        };
        var speed = (float)character.SpeakingRate;
        Console.WriteLine($"policy    : re-roll above {tolerance:P0} word error, up to {clone.MaxRerolls + 1} draws per line");
        Console.WriteLine();

        // Timed separately so the cost of the check is reported as a fact rather than
        // estimated: this is the price Rose pays per spoken line.
        var transcribeMs = 0.0;
        var transcriptions = 0;
        string Listen(float[] samples, int rate)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var heard = ears.Transcribe(samples, rate);
            transcribeMs += sw.Elapsed.TotalMilliseconds;
            transcriptions++;
            return heard;
        }

        // Kept beside the models rather than in the repo: these clips are the show voice
        // cloned, and this project does not distribute audio impersonating real actors.
        // models/ is gitignored and, unlike scratchpad/, is not wiped between sessions.
        var garbleDir = Path.Combine(modelDir, "garbled");

        int alreadyFine = 0, rescued = 0, stillBad = 0, pitchOnlyFailures = 0;
        double plainRenderMs = 0, verifiedRenderMs = 0;
        var plainDraws = 0;
        var verifiedDraws = 0;
        var verifyTranscriptions = 0;

        foreach (var text in Fixtures)
        {
            for (var trial = 1; trial <= trials; trial++)
            {
                // What a caller gets today: one draw, guarded on pitch only.
                clone.Transcribe = null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var plain = clone.CloneChecked(text, reference, refRate, referenceText, speed);
                plainRenderMs += sw.Elapsed.TotalMilliseconds;
                plainDraws += plain.Attempts;

                var plainHeard = Listen(ToFloat(plain.Pcm), clone.SampleRate);
                var plainError = SpokenTextCheck.WordErrorRate(text, plainHeard);

                // The same call with the check wired in, which is the live configuration.
                clone.Transcribe = Listen;
                clone.MaxWordError = tolerance;
                var transcriptionsBefore = transcriptions;
                sw.Restart();
                var verified = clone.CloneChecked(text, reference, refRate, referenceText, speed);
                verifiedRenderMs += sw.Elapsed.TotalMilliseconds;
                verifiedDraws += verified.Attempts;
                verifyTranscriptions += transcriptions - transcriptionsBefore;

                string verdict;
                if (plainError <= tolerance) { alreadyFine++; verdict = "already fine"; }
                else if (verified.Accepted) { rescued++; verdict = "RESCUED"; }
                else { stillBad++; verdict = "still bad"; }

                // A render the pitch guard threw out was never transcribed, so its word
                // error is NOT zero - it is unmeasured, and printing a 0% there would
                // read as a flawless line. Say so instead.
                if (!verified.WordsChecked) { pitchOnlyFailures++; verdict += ", pitch never passed"; }

                var score = verified.WordsChecked ? verified.WordErrorRate.ToString("P0") : "n/a";
                var label = text.Length > 42 ? text[..42] + "..." : text;
                Console.WriteLine($"  {label,-46} draw {plainError,4:P0} -> verified {score,4} "
                                + $"in {verified.Attempts} draw{(verified.Attempts == 1 ? " " : "s")}  {verdict}");

                // Keep the bad ones. A garble happens on a few percent of draws and cannot
                // be summoned on demand, so the only way to ever have a POSITIVE control -
                // a clip a working check MUST flag - is to save each one the moment it
                // occurs. Without them, "no garble happened" and "the check is blind"
                // produce exactly the same clean output.
                if (plainError > tolerance)
                    SaveGarble(garbleDir, text, plain.Pcm, clone.SampleRate, plainHeard, plainError);

                // Also printed for a render that PASSED with a non-zero score, because
                // that residue is the instrument's own floor and the only way to know what
                // it is made of is to read it. Guessing "it is probably the names" would
                // be a story; the transcript is evidence.
                if (plainError > tolerance || !verified.Accepted
                    || (verified.WordsChecked && verified.WordErrorRate > 0))
                {
                    Console.WriteLine($"      wanted : {text}");
                    Console.WriteLine($"      1 draw : {plainHeard}");
                    Console.WriteLine($"      checked: {(verified.WordsChecked ? verified.Transcript : "(never transcribed - every draw failed the pitch guard)")}");
                }
            }
        }

        var total = alreadyFine + rescued + stillBad;
        Console.WriteLine();
        Console.WriteLine($"RESULT    : {alreadyFine} already fine, {rescued} RESCUED by re-rolling, {stillBad} still bad "
                        + $"(of {total} lines)");
        var events = rescued + stillBad;
        Console.WriteLine($"garble    : {(total == 0 ? 0 : events / (double)total):P1} of single draws came back wrong "
                        + $"({events} of {total})");
        if (events < 5)
            Console.WriteLine($"            WARNING: {events} event{(events == 1 ? "" : "s")} is too few to call that a RATE. "
                            + "Raise --trials before quoting it.");
        Console.WriteLine($"pitch     : {pitchOnlyFailures} line{(pitchOnlyFailures == 1 ? "" : "s")} where EVERY draw failed the pitch guard, "
                        + "so the words were never measured");
        // Per LINE, which is what a listener waits for, and per DRAW underneath it, because
        // a line can cost several draws before one is kept.
        var plainPerLine = plainRenderMs / Math.Max(1, total);
        var verifiedPerLine = verifiedRenderMs / Math.Max(1, total);
        Console.WriteLine($"cost      : unverified {plainPerLine:F0}ms/line over {plainDraws / (double)Math.Max(1, total):F2} draws, "
                        + $"{plainRenderMs / Math.Max(1, plainDraws):F0}ms/draw");
        Console.WriteLine($"            verified   {verifiedPerLine:F0}ms/line over {verifiedDraws / (double)Math.Max(1, total):F2} draws");
        var msPerClip = transcribeMs / Math.Max(1, transcriptions);
        var checksPerLine = verifyTranscriptions / (double)Math.Max(1, total);
        Console.WriteLine($"            transcription {msPerClip:F0}ms/clip ({transcriptions} clips)");
        Console.WriteLine();

        // The headline is built from the transcription count, NOT from subtracting the two
        // arms' wall times. Each arm draws its own noise, so their draw counts differ by
        // chance, and that difference swamps the thing being measured - at 12 lines the
        // subtraction once read MINUS 205ms, which is not a saving, it is noise. Counting
        // the checks actually performed and pricing them is the same quantity with almost
        // none of the variance.
        Console.WriteLine($"VERIFICATION COSTS {checksPerLine * msPerClip:F0}ms PER LINE "
                        + $"({checksPerLine:F2} transcriptions x {msPerClip:F0}ms)");
        Console.WriteLine($"  cross-check: the two arms' wall times differ by {verifiedPerLine - plainPerLine:F0}ms/line, "
                        + "which is noisy - they draw independently.");
        // ---- Positive control: does this recogniser still flag garbles it has SEEN? ------------------
        var blind = ScoreGarbles(garbleDir, ears, tolerance, earsModel);

        Console.WriteLine();
        Console.WriteLine("A clean transcript means the WORDS survived. It cannot hear an accent, an odd");
        Console.WriteLine("rhythm or a breath - the pitch guard and a listener still cover those.");

        // A line the re-rolls could not fix is a failure; so is a recogniser that no longer
        // flags a clip already known to be garbled, because that check has gone blind.
        return stillBad == 0 && blind == 0 ? 0 : 1;
    }

    /// <summary>
    /// Saves a render that came back as the wrong words, so it can serve as a positive
    /// control from now on.
    /// </summary>
    /// <remarks>
    /// A garble cannot be reproduced on demand - the noise draw that caused it is gone the
    /// moment it is redrawn - so a library of them can only be built by keeping each one as
    /// it happens. The intended text and what was heard are written beside the audio,
    /// because without the text the clip cannot be scored again.
    /// </remarks>
    private static void SaveGarble(string dir, string text, byte[] pcm, int rate, string heard, double error)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var tag = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(text + heard)))[..8].ToLowerInvariant();
            // The tag is the failure's fingerprint (the line, and what it came back as), so
            // the same failure recurring adds nothing to the control set but bulk. Keep the
            // first one and let a genuinely different failure make a new entry.
            if (Directory.EnumerateFiles(dir, $"*-{tag}.wav").Any())
            {
                Console.WriteLine($"      already have this failure as a control ({tag})");
                return;
            }

            var stem = Path.Combine(dir, $"{stamp}-{tag}");
            ShowAudio.WriteWavPcm(stem + ".wav", pcm, rate);
            File.WriteAllText(stem + ".txt", text);
            File.WriteAllText(stem + ".heard.txt", $"{error:P0}\n{heard}\n");
            Console.WriteLine($"      kept as a positive control: {Path.GetFileName(stem)}.wav");
        }
        catch (Exception ex)
        {
            // A control that could not be saved must be visible, not swallowed - the whole
            // point of the library is that it is not silently empty.
            Console.WriteLine($"      COULD NOT SAVE the garbled render: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-scores every garbled render captured so far, and reports how many this recogniser
    /// no longer flags.
    /// </summary>
    /// <remarks>
    /// This is the check on the check. Every other number here can come back perfect either
    /// because the cloner behaved or because the recogniser stopped noticing, and those two
    /// look identical. Replaying clips already KNOWN to be wrong separates them: a recogniser
    /// or a tolerance that scores a known garble as fine is blind, and says so.
    /// </remarks>
    private static int ScoreGarbles(string dir, SpeechRecognizer ears, double tolerance, string earsModel)
    {
        Console.WriteLine();
        if (!Directory.Exists(dir))
        {
            Console.WriteLine("controls  : NO garbled renders captured yet, so nothing proves this check can");
            Console.WriteLine("            see one. Every clean run above is unfalsified, not verified.");
            return 0;
        }

        var clips = Directory.GetFiles(dir, "*.wav").OrderBy(f => f).ToArray();
        if (clips.Length == 0)
        {
            Console.WriteLine("controls  : NO garbled renders captured yet - nothing proves this check can see one.");
            return 0;
        }

        Console.WriteLine($"controls  : replaying {clips.Length} captured garble{(clips.Length == 1 ? "" : "s")} through whisper {earsModel}");
        var missed = 0;
        var missedStrong = 0;
        var strongCount = 0;
        foreach (var wav in clips)
        {
            var textPath = Path.ChangeExtension(wav, ".txt");
            if (!File.Exists(textPath)) continue;

            var wanted = File.ReadAllText(textPath).Trim();
            var (samples, rate) = ShowAudio.ReadWav(wav);
            if (samples.Length == 0) continue;

            var heard = ears.Transcribe(samples, rate);
            var error = SpokenTextCheck.WordErrorRate(wanted, heard);
            var flagged = error > tolerance;
            if (!flagged) missed++;

            // The score this clip earned when it was captured, which is what says whether
            // it is real evidence. Not every capture is a garble, and the measured scores
            // fall into two clearly separated groups:
            //
            //   good renders             0 - 15%   (the instrument's own floor)
            //   marginal captures       23 - 27%   ("gonna" for "going to", a dropped "Um")
            //   real garbles            62 - 75%   ("Can Can Can We Can We Can")
            //
            // Over the captures so far nothing has landed between 27% and 62%, so STRONG is
            // drawn at 50%, in the middle of that gap: a
            // clip caught there is unarguably not the sentence that was asked for. A
            // marginal capture is a tolerance false positive sitting just above the noise,
            // and failing a run because one of those re-scores at 15% would be a false
            // gate - it is reported, not enforced.
            var originalPath = Path.ChangeExtension(wav, ".heard.txt");
            var originalText = File.Exists(originalPath)
                ? File.ReadLines(originalPath).FirstOrDefault()?.Trim() ?? ""
                : "";
            var strong = double.TryParse(originalText.TrimEnd('%'), out var pct) && pct >= 50;
            if (strong) strongCount++;

            if (!flagged && strong) missedStrong++;

            Console.WriteLine($"            {Path.GetFileNameWithoutExtension(wav)}  "
                            + $"captured at {(originalText.Length > 0 ? originalText : "?"),4} "
                            + $"{(strong ? "STRONG  " : "marginal"),-8}  now {error,4:P0}  "
                            + (flagged ? "flagged"
                                       : strong ? "MISSED - this check would have spoken it"
                                                : "not flagged (marginal, does not fail the run)"));
            if (!flagged)
            {
                Console.WriteLine($"              wanted: {wanted}");
                Console.WriteLine($"              heard : {heard}");
            }
        }

        if (strongCount == 0)
        {
            Console.WriteLine($"            {clips.Length} captured, but NONE is strong evidence (all below 50% when caught).");
            Console.WriteLine("            Nothing here proves the check can see a real garble yet.");
        }
        else if (missedStrong == 0)
        {
            Console.WriteLine($"            all {strongCount} STRONG control{(strongCount == 1 ? "" : "s")} flagged - "
                            + "the check demonstrably sees a garbled render.");
        }
        else
        {
            Console.WriteLine($"            {missedStrong} STRONG control{(missedStrong == 1 ? "" : "s")} MISSED - "
                            + "this recogniser and tolerance cannot be trusted to catch a garble.");
        }
        if (missed > missedStrong)
            Console.WriteLine($"            ({missed - missedStrong} marginal capture{(missed - missedStrong == 1 ? "" : "s")} "
                            + "not flagged, which is expected - they sit in the instrument's noise.)");

        // Only a missed STRONG control means the check has gone blind.
        return missedStrong;
    }

    /// <summary>Whole 16-bit PCM buffer as float samples in [-1, 1].</summary>
    private static float[] ToFloat(byte[] pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        return samples;
    }

    /// <summary>
    /// kokoro.onnx, wherever it sits above the binary. Walked rather than assumed for the
    /// same reason the app does it: the working directory is not reliably the app root.
    /// </summary>
    private static string? FindKokoro()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "kokoro.onnx");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
