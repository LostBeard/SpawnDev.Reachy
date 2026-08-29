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
                    continue;
                }

                if (Muted)
                {
                    // Drop anything buffered from before the mute so Rose's own
                    // speech can never surface as a stale utterance afterwards.
                    _framed = 0;
                    _vad.Clear();
                    continue;
                }

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
        OnUtterance?.Invoke(text);
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
