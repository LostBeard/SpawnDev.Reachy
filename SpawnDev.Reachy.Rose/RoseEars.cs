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
    private readonly OfflineRecognizer _asr;
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

    public RoseEars(string modelDir, int threads = 4)
    {
        var vadModel = Path.Combine(modelDir, "silero_vad.onnx");
        var whisperDir = Path.Combine(modelDir, "sherpa-onnx-whisper-base.en");

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

        var asrConfig = new OfflineRecognizerConfig();
        asrConfig.ModelConfig.Whisper.Encoder = Path.Combine(whisperDir, "base.en-encoder.int8.onnx");
        asrConfig.ModelConfig.Whisper.Decoder = Path.Combine(whisperDir, "base.en-decoder.int8.onnx");
        asrConfig.ModelConfig.Tokens = Path.Combine(whisperDir, "base.en-tokens.txt");
        asrConfig.ModelConfig.ModelType = "whisper";
        asrConfig.ModelConfig.NumThreads = threads;
        asrConfig.ModelConfig.Provider = "cpu";
        asrConfig.DecodingMethod = "greedy_search";

        _asr = new OfflineRecognizer(asrConfig);

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

    private void Transcribe(float[] samples)
    {
        var seconds = samples.Length / (double)RoseAudioLink.OutputSampleRate;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var stream = _asr.CreateStream();
        stream.AcceptWaveform(RoseAudioLink.OutputSampleRate, samples);
        _asr.Decode(stream);
        var text = stream.Result.Text?.Trim() ?? "";

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
