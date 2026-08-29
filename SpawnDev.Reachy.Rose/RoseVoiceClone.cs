using SherpaOnnx;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Zero-shot voice cloning through sherpa-onnx ZipVoice: speaks new text in the
/// voice of a short reference clip.
/// </summary>
/// <remarks>
/// This is the show-voice path. ZipVoice is a flow-matching text-to-speech model
/// that conditions on a reference clip plus its transcript, so given a few clean
/// seconds of a character it will say anything in that voice - no per-voice
/// training, and entirely local. The reference clips come from
/// <see cref="VoiceBuilder"/>, which cuts them out of the show.
///
/// No new engine: sherpa-onnx is already a dependency for the ears, and it runs
/// the ONNX models itself. Output is 24kHz mono (the vocos vocoder's rate).
///
/// Private home use only - the reference audio impersonates real voice actors.
/// </remarks>
public sealed class RoseVoiceClone : IDisposable
{
    private readonly OfflineTts _tts;

    /// <summary>Output sample rate of the synthesiser (24kHz for the vocos vocoder).</summary>
    public int SampleRate => _tts.SampleRate;

    /// <summary>
    /// Flow-matching steps. The distill model is trained to sound right at a
    /// handful of steps; more steps trade time for a little quality.
    /// </summary>
    public int NumSteps { get; set; } = 4;

    /// <summary>The int8 ZipVoice package - the one the official example ships and tests, with a lexicon.</summary>
    private const string Int8Name = "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia";

    /// <summary>The fp32 package - higher quality, but shipped without a lexicon (copy one in).</summary>
    private const string Fp32Name = "sherpa-onnx-zipvoice-distill-zh-en-emilia";

    /// <summary>
    /// The onnxruntime execution provider actually requested ("cpu" or "cuda").
    /// The GPU path runs the ZipVoice ONNX graph on the RTX card, cutting a line
    /// from ~9s to a fraction of a second. sherpa falls back to CPU on its own if
    /// the CUDA provider is not available in the loaded onnxruntime.dll, so asking
    /// for "cuda" is always safe - it just does nothing on a CPU-only build.
    /// </summary>
    public string Provider { get; }

    /// <param name="modelDir">Folder holding the ZipVoice model package(s).</param>
    /// <param name="fp32">Use the full-precision model instead of int8 - less quantization artifact.</param>
    /// <param name="numSteps">Flow-matching steps; more trades time for quality.</param>
    /// <param name="provider">
    /// onnxruntime execution provider: "cpu" (default) or "cuda". "cuda" needs the
    /// CUDA-13 onnxruntime.dll + providers in the output and cuDNN 9 reachable (see
    /// gpu-setup/GPU-SETUP.md); when those are absent sherpa logs the available
    /// providers and quietly runs on CPU, so nothing breaks.
    /// </param>
    public RoseVoiceClone(string modelDir, bool fp32 = false, int numSteps = 4, string provider = "cpu")
    {
        if (provider == "cuda") EnsureCudaLibrariesFindable();
        NumSteps = numSteps;
        Provider = provider;
        var dir = Path.Combine(modelDir, fp32 ? Fp32Name : Int8Name);
        var config = new OfflineTtsConfig();
        config.Model.ZipVoice.Tokens = Path.Combine(dir, "tokens.txt");
        config.Model.ZipVoice.Encoder = Path.Combine(dir, fp32 ? "text_encoder.onnx" : "encoder.int8.onnx");
        config.Model.ZipVoice.Decoder = Path.Combine(dir, fp32 ? "fm_decoder.onnx" : "decoder.int8.onnx");
        // The vocoder is shared across packages; use whichever copy is on disk.
        config.Model.ZipVoice.Vocoder = ResolveVocoder(modelDir, dir);
        config.Model.ZipVoice.DataDir = Path.Combine(dir, "espeak-ng-data");
        config.Model.ZipVoice.Lexicon = Path.Combine(dir, "lexicon.txt");
        // On GPU the flow-matching runs on the card, not the thread pool, so pinning
        // half the cores would only steal them from the rest of the pipeline.
        config.Model.NumThreads = provider == "cuda" ? 2 : Math.Max(1, Environment.ProcessorCount / 2);
        config.Model.Provider = provider;
        // 1 = warnings+errors, so sherpa prints its "Available providers ... " line and
        // we can SEE whether CUDA actually engaged instead of assuming it did.
        config.Model.Debug = provider == "cuda" ? 1 : 0;
        _tts = new OfflineTts(config);
    }

    private static bool _cudaPathReady;

    /// <summary>
    /// Puts the app's native-library folder on the process DLL search path so the
    /// CUDA execution provider can find cuDNN.
    /// </summary>
    /// <remarks>
    /// onnxruntime 1.27 loads cuDNN dynamically by bare name ("cudnn64_9.dll"), and
    /// the OS default search covers the exe folder and PATH but NOT the
    /// runtimes/win-x64/native subfolder where the GPU onnxruntime + its cuDNN live.
    /// cuBLAS is found because the CUDA toolkit's bin is on PATH; cuDNN is not, so
    /// without this the provider fails with "Cannot load symbol cudnnCreate" and
    /// silently falls back to CPU. Prepending the native folder to PATH fixes it
    /// without disturbing how anything else resolves. cuDNN 9 also delay-loads
    /// zlibwapi.dll, which lives in the same folder. See gpu-setup/GPU-SETUP.md.
    /// </remarks>
    internal static void EnsureCudaLibrariesFindable()
    {
        if (_cudaPathReady) return;
        _cudaPathReady = true;
        var nativeDir = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        if (!Directory.Exists(nativeDir)) return;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Split(Path.PathSeparator).Any(p => string.Equals(p.TrimEnd('\\'), nativeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            Environment.SetEnvironmentVariable("PATH", nativeDir + Path.PathSeparator + path);
    }

    private static string ResolveVocoder(string modelDir, string dir)
    {
        var inPackage = Path.Combine(dir, "vocos_24khz.onnx");
        if (File.Exists(inPackage)) return inPackage;
        var alt = Path.Combine(modelDir, Fp32Name, "vocos_24khz.onnx");
        return File.Exists(alt) ? alt : inPackage;
    }

    /// <summary>True once the model files are present and the engine has loaded.</summary>
    public static bool ModelPresent(string modelDir) =>
        File.Exists(Path.Combine(modelDir, Int8Name, "decoder.int8.onnx"))
        && File.Exists(Path.Combine(modelDir, Int8Name, "lexicon.txt"));

    /// <summary>
    /// Speaks <paramref name="text"/> in the voice of <paramref name="reference"/>,
    /// returning 16-bit PCM at <see cref="SampleRate"/>.
    /// </summary>
    /// <param name="reference">Reference audio, mono float samples in [-1,1].</param>
    /// <param name="referenceSampleRate">Sample rate of the reference audio.</param>
    /// <param name="referenceText">Exact transcript of the reference audio.</param>
    /// <summary>
    /// Re-roll a render whose pitch drifted out of the reference speaker's range.
    /// </summary>
    /// <remarks>
    /// Zero-shot ZipVoice re-samples random noise for every call, so a reference whose
    /// speaker sits near the male/female line - N is soft and fairly high - occasionally
    /// renders a sentence in the wrong gender and the next one back in the right one.
    /// A single reference cannot eliminate this (measured: every candidate drifts at
    /// least once in eight), so the fix is here: after a render, measure its pitch, and
    /// if it fell outside the reference's own f0 band, render again. A drift is
    /// uncommon and a re-roll almost always lands in range, so the average cost is small
    /// and N stops changing gender mid-conversation. Off by default so the diagnostics
    /// can still see the raw drift.
    /// </remarks>
    public bool StabilizePitch { get; set; }

    /// <summary>How many times to re-roll a drifted render before keeping the closest.</summary>
    public int MaxRerolls { get; set; } = 4;

    /// <summary>
    /// Absolute upper pitch bound (Hz), or 0 to bound only by the reference's own pitch.
    /// For a character whose real voice sits near the male/female line - a pre-teen boy -
    /// the self-calibrated band runs too high, so this caps it into clearly-in-character
    /// territory. Left 0 for characters the reference pitch bounds cleanly on its own.
    /// </summary>
    public double PitchCeiling { get; set; }

    /// <summary>
    /// Absolute lower pitch bound (Hz), or 0 to bound only by the reference's own pitch.
    /// The self-calibrated floor (refF0 - 70) is generous so normal male dips are not
    /// rejected, but a young voice can render surprisingly deep and still land inside
    /// it; this holds the low end up so a pre-teen boy stays a pre-teen boy.
    /// </summary>
    public double PitchFloor { get; set; }

    /// <summary>
    /// Hands a finished render back to a recogniser, so a garbled one can be heard and
    /// re-rolled. Null (the default) leaves renders unchecked.
    /// </summary>
    /// <remarks>
    /// The cloner draws fresh noise on every call, and some draws come back as words
    /// nobody asked for - one sentence rendered at four seeds gave three clean results
    /// and one that transcribed as "Loner's call, Nanawa, Nenfer". It is a property of
    /// the model, not of the text it was given, so no amount of reference or phoneme
    /// tuning removes it. The only reliable detector is to LISTEN to the render, which
    /// is what this is: give it the recogniser that is already in the stack and a bad
    /// draw is thrown away instead of spoken.
    ///
    /// Takes the samples and their rate, and returns what was heard. Called on whatever
    /// thread is rendering, so the implementation must be safe to call from there -
    /// <see cref="RoseEars.TranscribeClip"/> serialises the shared recogniser for exactly
    /// this.
    /// </remarks>
    public Func<float[], int, string>? Transcribe { get; set; }

    /// <summary>
    /// How much of the line may come back wrong before a render is re-rolled, 0 to 1.
    /// </summary>
    /// <remarks>
    /// Not zero. The recogniser is a Whisper model listening to synthesised speech, so a
    /// perfectly good render routinely comes back with a dropped "the" or a homophone,
    /// and demanding a perfect transcript would re-roll every line for nothing. A fifth
    /// of the words wrong is well clear of that noise and well under a garbled render,
    /// which comes back as a different sentence entirely.
    /// </remarks>
    public double MaxWordError { get; set; } = 0.2;

    /// <summary>
    /// What a render attempt produced, and whether it survived the guards.
    /// </summary>
    /// <param name="Pcm">16-bit PCM at <see cref="SampleRate"/> - the best attempt, even if none passed.</param>
    /// <param name="Attempts">How many draws this line cost, including ones thrown away.</param>
    /// <param name="PitchInRange">The kept render's pitch sat in the reference speaker's band (true when unchecked).</param>
    /// <param name="WordsChecked">A recogniser listened to the kept render.</param>
    /// <param name="WordsVerified">What came back was the line that was asked for.</param>
    /// <param name="WordErrorRate">How much of the kept render came back wrong, when checked.</param>
    /// <param name="Transcript">What the recogniser heard in the kept render, when checked.</param>
    public readonly record struct CloneResult(
        byte[] Pcm, int Attempts, bool PitchInRange,
        bool WordsChecked, bool WordsVerified, double WordErrorRate, string Transcript)
    {
        /// <summary>True when every guard that ran was satisfied by the render being returned.</summary>
        public bool Accepted => PitchInRange && (!WordsChecked || WordsVerified);
    }

    /// <summary>
    /// Speaks <paramref name="text"/> in the reference voice, returning 16-bit PCM at
    /// <see cref="SampleRate"/>.
    /// </summary>
    public byte[] Clone(string text, float[] reference, int referenceSampleRate, string referenceText, float speed = 1.0f)
        => CloneChecked(text, reference, referenceSampleRate, referenceText, speed).Pcm;

    /// <summary>
    /// Speaks <paramref name="text"/> in the reference voice and reports whether the
    /// render passed the guards, so a caller can decide not to keep a bad one.
    /// </summary>
    /// <remarks>
    /// Both guards share ONE re-roll loop rather than nesting. They are the same
    /// operation - draw again and keep the better one - and running them as two loops
    /// would multiply the worst case and leave two places that decide what "good enough"
    /// means.
    ///
    /// Order inside an attempt matters: pitch is measured from the samples in
    /// milliseconds, transcription costs a Whisper decode, and a render that already
    /// drifted out of the speaker's range is being thrown away regardless. So the cheap
    /// check runs first and only a render that survives it is worth listening to.
    /// </remarks>
    public CloneResult CloneChecked(string text, float[] reference, int referenceSampleRate, string referenceText, float speed = 1.0f)
    {
        // A quarter second of trailing silence so the model does not carry the last
        // reference word into the start of the generated line. Done once, not per roll.
        var padded = PadTail(reference, referenceSampleRate / 4);

        var checkWords = Transcribe is not null && MaxWordError < 1.0;
        var checkPitch = StabilizePitch;
        double lo = 0, hi = 0;

        if (checkPitch)
        {
            // Anchor to the reference speaker's own pitch rather than a hand-set gender
            // threshold, so this self-calibrates: a male reference rejects a female render
            // and a female reference rejects a male one, with no per-character tuning.
            var refF0 = AudioAnalysis.Measure(reference, referenceSampleRate).MedianF0;

            // Nothing voiced to calibrate against, so there is no band to judge against
            // and the pitch guard has to stand down. The word check is unaffected.
            if (refF0 <= 0) checkPitch = false;
            else
            {
                // Asymmetric: generous below (normal male variation dips low) and tighter
                // above, because the drift that matters is upward into the female range.
                // Relative to the reference's own pitch, so it self-calibrates and never
                // rejects a female character's legitimately high render.
                lo = PitchFloor > 0 ? Math.Max(refF0 - 70, PitchFloor) : refF0 - 70;
                hi = PitchCeiling > 0 ? Math.Min(refF0 + 35, PitchCeiling) : refF0 + 35;
            }
        }

        if (!checkPitch && !checkWords)
            return new CloneResult(
                GenerateOnce(text, padded, referenceSampleRate, referenceText, speed).Pcm,
                Attempts: 1, PitchInRange: true,
                WordsChecked: false, WordsVerified: false, WordErrorRate: 0, Transcript: "");

        // Nothing passed, so keep the least-bad. Pitch distance leads because a render
        // that drifted out of the speaker's range was never transcribed at all - it was
        // rejected before the check was worth paying for - and word error separates the
        // renders that DID get listened to. An unmeasured render loses that tie-break to
        // a measured one rather than sneaking in on a default of zero.
        CloneResult best = default;
        var bestRank = (PitchDistance: double.MaxValue, WordError: double.MaxValue);

        var draws = MaxRerolls + 1;
        for (var attempt = 1; attempt <= draws; attempt++)
        {
            var (pcm, samples) = GenerateOnce(text, padded, referenceSampleRate, referenceText, speed);

            var pitchDistance = 0.0;
            if (checkPitch)
            {
                var f0 = AudioAnalysis.Measure(samples, SampleRate).MedianF0;
                pitchDistance = f0 < lo ? lo - f0 : f0 > hi ? f0 - hi : 0;
            }

            // Only listen to a render that is otherwise keepable - a drifted one is
            // already rejected, and the transcription would be paid for nothing.
            var wordsChecked = false;
            var wordsOk = false;
            var wordError = 0.0;
            var transcript = "";
            if (checkWords && pitchDistance <= 0)
            {
                transcript = Transcribe!(samples, SampleRate) ?? "";
                wordError = SpokenTextCheck.WordErrorRate(text, transcript);
                wordsChecked = true;
                wordsOk = wordError <= MaxWordError;
            }

            var candidate = new CloneResult(
                pcm, attempt, PitchInRange: pitchDistance <= 0,
                wordsChecked, wordsOk, wordError, transcript);

            if (candidate.Accepted) return candidate;

            var rank = (PitchDistance: pitchDistance, WordError: wordsChecked ? wordError : double.MaxValue);
            if (rank.PitchDistance < bestRank.PitchDistance
                || (rank.PitchDistance == bestRank.PitchDistance && rank.WordError < bestRank.WordError))
            {
                bestRank = rank;
                best = candidate;
            }
        }

        // Every draw failed something. Keep the closest one - Rose still has to answer -
        // and let the caller see that it was never verified.
        //
        // Attempts reports how many draws this LINE cost, not which draw is being
        // returned: a caller measuring the price of the guards needs the work done, and
        // reporting the winning index would quietly under-count every failed line.
        return best with { Attempts = draws };
    }

    private (byte[] Pcm, float[] Samples) GenerateOnce(string text, float[] paddedReference, int referenceSampleRate, string referenceText, float speed)
    {
        var gen = new OfflineTtsGenerationConfig
        {
            ReferenceAudio = paddedReference,
            ReferenceSampleRate = referenceSampleRate,
            ReferenceText = referenceText,
            NumSteps = NumSteps,
            Speed = speed,
            Sid = 0,
        };

        var audio = _tts.GenerateWithConfig(text, gen, null!);
        var samples = audio.Samples;
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var v = (short)Math.Clamp((int)MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)v;
            pcm[i * 2 + 1] = (byte)(v >> 8);
        }
        return (pcm, samples);
    }

    private static float[] PadTail(float[] samples, int silenceSamples)
    {
        var padded = new float[samples.Length + silenceSamples];
        Array.Copy(samples, padded, samples.Length);
        return padded;
    }

    public void Dispose() => _tts.Dispose();
}
