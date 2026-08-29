using System.Threading.Channels;
using SherpaOnnx;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Turns the robot's microphone stream into finished sentences.
/// </summary>
/// <remarks>
/// Two stages. Silero VAD decides where an utterance starts and stops, then Whisper
/// transcribes the completed segment. Endpointing has to come from a real VAD rather
/// than an energy threshold, because a ten year old talking to a robot pauses mid
/// sentence constantly, and an energy gate either cuts her off or waits forever.
///
/// Everything runs on a private worker thread. The mic callback fires on SIPSorcery's
/// RTP receive path, and blocking that stalls the audio link itself - so the callback
/// does nothing but drop samples into a channel.
/// </remarks>
public sealed class RoseEars : IAsyncDisposable
{
    /// <summary>
    /// A unit of work for the audio thread: either samples, or a request to close
    /// out whatever speech is currently open.
    /// </summary>
    private readonly record struct Work(short[] Pcm, bool Flush);

    private readonly VoiceActivityDetector _vad;
    private readonly SpeechRecognizer _asr;
    private readonly Channel<Work> _incoming;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    /// <summary>Silero's native frame size at 16kHz. The detector expects exactly this many samples per call.</summary>
    private const int VadWindow = 512;

    private readonly float[] _frame = new float[VadWindow];
    private int _framed;

    /// <summary>Raised on the worker thread with each completed, transcribed utterance.</summary>
    public event Action<string>? OnUtterance;

    /// <summary>Diagnostic log.</summary>
    public event Action<string>? Log;

    /// <summary>
    /// While true, incoming audio is discarded and the detector is reset.
    /// </summary>
    /// <remarks>
    /// Set this while Rose is speaking. The XVF3800's hardware echo cancellation
    /// already stops her transcribing herself, but muting also prevents her from
    /// treating a pause in her OWN sentence as Aubs starting to talk.
    /// </remarks>
    public bool Muted { get; set; }

    /// <param name="whisperModel">
    /// Which Whisper model to transcribe with, e.g. "small.en" or "base.en". small.en
    /// understands a child's voice noticeably better than base.en; base.en is the
    /// fallback the fetch step always installs. The folder must be
    /// <c>models/sherpa-onnx-whisper-&lt;model&gt;</c>.
    /// </param>
    /// <param name="provider">
    /// onnxruntime execution provider, "cpu" (default) or "cuda". CPU is the default on
    /// purpose: the GPU on this box already holds the language model (~6GB) and the voice
    /// cloner, and on a 12GB card that leaves no room for Whisper too - adding it pushed
    /// VRAM to ~96% and a starved CUDA op wedged the whole conversation ("stuck thinking,
    /// no motion"). small.en on CPU is only ~1s per short utterance and keeps the whole
    /// GPU for the model and the clone. Pass "cuda" only on a machine with VRAM to spare.
    /// </param>
    /// <param name="quantization">
    /// "int8" (default), "fp32", or null for the default. int8 is used because the fp32
    /// Whisper decoder faults the current onnxruntime CUDA provider; see the note by the
    /// quant selection below.
    /// </param>
    public RoseEars(string modelDir, int threads = 4, string whisperModel = "small.en", string provider = "cpu", string? quantization = null)
    {
        var vadModel = Path.Combine(modelDir, "silero_vad.onnx");

        var whisperName = whisperModel;
        var whisperDir = Path.Combine(modelDir, $"sherpa-onnx-whisper-{whisperName}");
        if (!Directory.Exists(whisperDir))
        {
            // Not downloaded yet - fall back to base.en, which fetch_models always installs.
            whisperName = "base.en";
            whisperDir = Path.Combine(modelDir, "sherpa-onnx-whisper-base.en");
        }

        if (!File.Exists(vadModel))
            throw new FileNotFoundException($"Silero VAD model not found: {vadModel}");
        if (!Directory.Exists(whisperDir))
            throw new DirectoryNotFoundException($"Whisper model dir not found: {whisperDir}");

        var vadConfig = new VadModelConfig
        {
            SampleRate = RoseAudioLink.OutputSampleRate,
            NumThreads = 1,
            Provider = "cpu",
        };
        vadConfig.SileroVad.Model = vadModel;

        // Tuned for a child mid-thought rather than a dictation app. Half a second
        // of silence ends the turn: shorter clips her off between clauses, longer
        // and the conversation feels like it is buffering.
        vadConfig.SileroVad.Threshold = 0.5f;
        vadConfig.SileroVad.MinSilenceDuration = 0.5f;
        vadConfig.SileroVad.MinSpeechDuration = 0.25f;
        vadConfig.SileroVad.MaxSpeechDuration = 20.0f;
        vadConfig.SileroVad.WindowSize = VadWindow;

        _vad = new VoiceActivityDetector(vadConfig, bufferSizeInSeconds: 30.0f);

        _asr = new SpeechRecognizer(modelDir, whisperModel, threads, provider, quantization);

        // Dropping the oldest frame under pressure is correct here: stale microphone
        // audio has no value, and unbounded growth would turn a slow transcription
        // into an ever-growing backlog.
        _incoming = Channel.CreateBounded<Work>(
            new BoundedChannelOptions(capacity: 256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        _worker = Task.Factory.StartNew(
            () => WorkerAsync(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Accepts microphone PCM. Safe to call from the RTP receive path - never blocks.
    /// </summary>
    public void Feed(short[] pcm16k)
    {
        if (Muted) return;
        _incoming.Writer.TryWrite(new Work(pcm16k, Flush: false));
    }

    /// <summary>
    /// Closes out any speech still in progress and transcribes it.
    /// </summary>
    /// <remarks>
    /// The detector only emits a segment once it has seen enough trailing silence,
    /// so audio that ends while someone is still talking - the end of a recording,
    /// or the end of a session - would otherwise never be transcribed at all.
    /// </remarks>
    public void Flush() => _incoming.Writer.TryWrite(new Work([], Flush: true));

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var work in _incoming.Reader.ReadAllAsync(ct))
            {
                if (work.Flush)
                {
                    // Pad out the partial frame so the tail of the last word is not
                    // silently discarded along with it.
                    if (_framed > 0)
                    {
                        Array.Clear(_frame, _framed, VadWindow - _framed);
                        _vad.AcceptWaveform(_frame);
                        _framed = 0;
                    }
                    _vad.Flush();
                    Drain(ct);
                    // Nothing more is coming, so a turn still held open is as finished as
                    // it will ever be.
                    CommitPending();
                    continue;
                }

                if (Muted)
                {
                    // Drop anything buffered from before the mute so Rose's own
                    // speech can never surface as a stale utterance afterwards.
                    _framed = 0;
                    _vad.Clear();
                    // Commit rather than discard: the words were really said, and Rose is
                    // about to speak, so nothing further can arrive to continue them.
                    CommitPending();
                    continue;
                }

                // A held turn is waiting on a clock, and audio is the only thing that wakes
                // this loop - so the window is checked as samples flow past.
                CommitPendingIfDue();

                // The detector wants exact 512-sample frames; RTP hands us 320.
                var pcm = work.Pcm;
                for (var i = 0; i < pcm.Length; i++)
                {
                    _frame[_framed++] = pcm[i] / 32768f;
                    if (_framed < VadWindow) continue;

                    _vad.AcceptWaveform(_frame);
                    _framed = 0;
                    Drain(ct);
                    if (ct.IsCancellationRequested) return;
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { Log?.Invoke($"ears worker died: {ex.GetType().Name}: {ex.Message}"); }
    }

    // ---- soft-ended turns ------------------------------------------------------------
    //
    // Half a second of silence used to COMMIT a turn irrevocably. People do not talk like
    // that: they pause to think, to choose a word, to decide how to pronounce a name. Aubs
    // paused before "Khan" - she had just been told it is "kon" after saying "can" - and
    // the turn closed on "can you be", dropping the name into a second utterance nobody
    // read. She simply did not get the character she asked for.
    //
    // So a turn that looks UNFINISHED is now soft-ended rather than committed: it waits
    // briefly, and speech arriving in that window REOPENS it and is appended. This is the
    // shape huggingface/speech-to-speech uses (a soft-ended, reopenable turn with a grace
    // for incomplete turns and a hard ceiling); the timings below are theirs rather than
    // numbers I made up.
    //
    // ⚠️ The grace applies ONLY to turns that look unfinished. A normal sentence commits
    // the instant it always did, so the conversation does not get slower to fix a case
    // that is not happening.

    /// <summary>
    /// How long a soft-ended turn waits for the rest of the sentence.
    /// </summary>
    /// <remarks>
    /// Longer than the 600ms huggingface/speech-to-speech uses, and deliberately so: the
    /// grace does not start until the DETECTOR has closed the segment, and ours needs
    /// <see cref="VadModelConfig"/>'s 500ms of silence to do that. So the pause a speaker
    /// can actually take is 500ms + this, where theirs is 64ms + theirs. 1200 gives about
    /// 1.7s of real thinking time, which covers "can you be... Khan" from someone deciding
    /// how to pronounce it. Only turns that already look unfinished ever wait.
    /// </remarks>
    private static readonly TimeSpan ReopenGrace = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Hard ceiling on holding a turn, however much it keeps looking unfinished.
    /// </summary>
    /// <remarks>
    /// Without this a speaker who keeps trailing off could hold a turn open forever, and
    /// Rose would simply stop answering. Better to reply to a half-sentence than to go
    /// silent.
    /// </remarks>
    private static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(4);

    private string? _pending;
    private DateTime _pendingSince;
    private DateTime _pendingDue;

    /// <summary>
    /// Takes a finished segment and either commits it or holds it open for the rest.
    /// </summary>
    private void Offer(string text)
    {
        var now = DateTime.UtcNow;

        if (_pending is null)
        {
            if (!LooksUnfinished(text)) { Emit(text); return; }
            _pending = text;
            _pendingSince = now;
            _pendingDue = now + ReopenGrace;
            Log?.Invoke($"turn held open (looks unfinished): \"{text}\"");
            return;
        }

        // Speech arrived inside the window, so the turn reopens and this continues it.
        _pending = $"{_pending} {text}";
        Log?.Invoke($"turn reopened -> \"{_pending}\"");

        if (!LooksUnfinished(_pending) || now - _pendingSince >= MaxHold) CommitPending();
        else _pendingDue = now + ReopenGrace;
    }

    /// <summary>Commits a held turn once its window has passed. Called as audio flows.</summary>
    /// <remarks>
    /// ⚠️ Speech happening RIGHT NOW pushes the window back, and that is load-bearing rather
    /// than a refinement. The continuation does not become readable the moment it is
    /// spoken: the detector still needs its 500ms of silence to close that segment, and
    /// then it has to be transcribed. Timing the grace purely on the clock therefore
    /// expired it while the speaker was mid-word - measured, the turn committed before
    /// "Khan" ever arrived, which is the exact bug this exists to fix. Resumed speech IS
    /// the reopen signal; silence is the only thing that should run the clock down.
    /// </remarks>
    private void CommitPendingIfDue()
    {
        if (_pending is null) return;

        var now = DateTime.UtcNow;

        if (_vad.IsSpeechDetected())
        {
            // They are still talking. Hold, but never past the ceiling.
            if (now - _pendingSince < MaxHold) _pendingDue = now + ReopenGrace;
            else CommitPending();
            return;
        }

        if (now >= _pendingDue || now - _pendingSince >= MaxHold) CommitPending();
    }

    private void CommitPending()
    {
        var text = _pending;
        _pending = null;
        if (text is not null) Emit(text);
    }

    private void Emit(string text) => OnUtterance?.Invoke(text);

    /// <summary>
    /// Whether an utterance reads as cut off mid-sentence.
    /// </summary>
    /// <remarks>
    /// Deliberately lexical rather than a model. Semantic end-of-turn detection is the
    /// general answer to this problem and it needs a classifier reading partial
    /// transcripts; the cheap 95% is that English sentences do not END on a function word.
    /// "can you be", "what is", "I want to" are all obviously unfinished, and a real
    /// sentence almost never closes on one of these.
    ///
    /// ⚠️ Punctuation is NOT a usable signal here, which is worth recording because it is
    /// the obvious idea: recognition returned "Can you be?" - complete with a question
    /// mark - for a sentence that was missing its last word entirely.
    /// </remarks>
    internal static bool LooksUnfinished(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var words = text.ToLowerInvariant().Split(
            [' ', '\t', '\n', ',', '.', '!', '?', ';', ':', '"', '\'', '(', ')', '-'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return false;

        return DanglingWords.Contains(words[^1]);
    }

    /// <summary>
    /// Words an English sentence does not end on, so hearing one last means the rest is
    /// still coming.
    /// </summary>
    /// <remarks>
    /// Kept to words that are genuinely never terminal. "be" earns its place from the
    /// live failure ("can you be" + "Khan"); the articles, prepositions and auxiliaries
    /// around it are the same class. Words that CAN legitimately end a sentence stay out,
    /// however common - "it", "me", "here", "now", "too" - because holding those would
    /// delay ordinary replies for nothing.
    /// </remarks>
    private static readonly HashSet<string> DanglingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "my", "your", "our", "their", "his", "her", "its",
        "be", "is", "am", "are", "was", "were", "been", "being",
        "do", "does", "did", "have", "has", "had",
        "can", "could", "will", "would", "shall", "should", "may", "might", "must",
        "to", "of", "for", "with", "from", "into", "onto", "about", "than", "as",
        "and", "or", "but", "so", "if", "because", "that", "which", "who", "whose",
        "very", "really", "more", "most", "some", "any", "every", "each", "another",
        "what", "wanna", "gonna", "like",
    };

    /// <summary>Transcribes every segment the detector has finished with.</summary>
    private void Drain(CancellationToken ct)
    {
        while (!_vad.IsEmpty())
        {
            var segment = _vad.Front();
            _vad.Pop();
            Transcribe(segment.Samples);
            if (ct.IsCancellationRequested) return;
        }
    }

    /// <summary>
    /// Transcribes one clip directly, without going through the microphone path.
    /// </summary>
    /// <remarks>
    /// The microphone's OWN recogniser, exposed for callers that already have audio in
    /// hand rather than a live stream - reading a wav, or scoring a captured clip.
    ///
    /// This is NOT the right instrument for checking Rose's own renders, even though it
    /// looks like it: this model is chosen to understand a ten year old, and a model that
    /// good repairs the very defect a render check is looking for. Handed a render that
    /// had collapsed into "Can Can Can We Can We Can", small.en wrote back the sentence
    /// that was meant and scored it perfect. Self-checking gets its own recogniser, see
    /// <see cref="SpeechRecognizer"/>.
    /// </remarks>
    /// <param name="samples">Mono float samples in [-1, 1].</param>
    /// <param name="sampleRate">Sample rate of <paramref name="samples"/>.</param>
    /// <returns>What was heard, or an empty string once the ears have been disposed.</returns>
    public string TranscribeClip(float[] samples, int sampleRate) => _asr.Transcribe(samples, sampleRate);

    /// <summary>The Whisper model the microphone is listening through.</summary>
    public string Model => _asr.Model;

    private void Transcribe(float[] samples)
    {
        var seconds = samples.Length / (double)RoseAudioLink.OutputSampleRate;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var text = _asr.Transcribe(samples, RoseAudioLink.OutputSampleRate);

        Log?.Invoke($"heard {seconds:F1}s -> \"{text}\" ({sw.ElapsedMilliseconds}ms)");

        if (IsNoise(text)) return;
        Offer(text);
    }

    /// <summary>
    /// Filters out what Whisper emits for non-speech audio.
    /// </summary>
    /// <remarks>
    /// Fed a cough, a chair scrape or a fan, Whisper does not return an empty string -
    /// it hallucinates a bracketed sound tag, a lone punctuation mark, or one of a
    /// small set of stock phrases it falls back on. Passing those to the LLM makes
    /// Rose answer things nobody said, which reads as her being broken.
    /// </remarks>
    private static bool IsNoise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        // Whisper's sound-event tags: [BLANK_AUDIO], (coughing), *music*
        var t = text.Trim();
        if (t.StartsWith('[') || t.StartsWith('(') || t.StartsWith('*')) return true;

        // Nothing with a letter in it is not a sentence.
        if (!t.Any(char.IsLetter)) return true;

        // Whisper's stock hallucinations on silence.
        string[] stock =
        [
            "you", "thank you", "thanks for watching", "thank you for watching",
            "bye", "okay", "so", "yeah", "the", "oh",
        ];
        var bare = new string(t.Where(c => char.IsLetter(c) || c == ' ').ToArray())
            .Trim().ToLowerInvariant();
        return stock.Contains(bare);
    }

    public async ValueTask DisposeAsync()
    {
        _incoming.Writer.TryComplete();
        _cts.Cancel();
        try { await _worker; } catch { }
        _vad.Dispose();
        _asr.Dispose();
        _cts.Dispose();
    }
}
