using System.Runtime.InteropServices;
using SpawnDev.RTC.Audio;

namespace SpawnDev.Reachy;

/// <summary>
/// Speech to the robot's speaker as a LIVE audio stream over the WebRTC session, instead of a file uploaded to the daemon and then played.
/// </summary>
/// <remarks>
/// <para>
/// The upload path costs the whole clip's transfer before a single sample plays: measured on a robot over Wi-Fi, 70 to 100 KB/s, so a 6 s sentence (290 KB) waited 3 to 4 s.
/// A stream starts at once and the next sentence follows without a gap. The price is Opus (the WebRTC speech codec) instead of the WAV, which is why this is a choice and not the only path.
/// </para>
/// <para>
/// Real-time pacing is this class's job. The receiving end plays by RTP timestamps through a small jitter buffer, so audio pushed faster than it plays piles up and is dropped.
/// A pacing thread therefore sends one 20 ms frame every 20 ms - and keeps sending SILENCE between clips, so the stream is continuous: the encoder and the robot's receive chain stay warm
/// and the first word of the next sentence is not swallowed (the daemon's own client keeps a continuous stream for the same reason). Frames go out on a timer that is catch-up
/// corrected, so a late tick sends two frames rather than losing time.
/// </para>
/// </remarks>
public sealed class RobotSpeechStreamer : IDisposable
{
    /// <summary>The rate audio is pushed at: the WebRTC Opus rate.</summary>
    public const int SampleRate = 48000;

    /// <summary>Samples in one 20 ms frame.</summary>
    public const int FrameSamples = SampleRate / 50;

    private readonly IRTCAudioSender _sender;
    private readonly object _lock = new();
    private readonly Queue<Segment> _queue = new();
    private readonly Thread _thread;
    private volatile bool _stop;

    private sealed class Segment
    {
        public required short[] Pcm;
        public int Offset;
        public required TaskCompletionSource Done;
        public CancellationTokenRegistration Cancel;
    }

    /// <summary>Frames sent so far (silence included): for tests and diagnostics.</summary>
    public long FramesSent { get; private set; }

    /// <summary>How long after its last frame is sent a clip counts as heard: the robot's jitter buffer and speaker chain (the upload path adds 250 ms for the same reason).</summary>
    public TimeSpan PlayoutLatency { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <param name="sender">Where frames go: the peer connection's audio sender (48 kHz mono).</param>
    public RobotSpeechStreamer(IRTCAudioSender sender)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _thread = new Thread(Run) { IsBackground = true, Name = "robot speech pacing", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Plays 48 kHz mono PCM16 through the robot's speaker and returns once it has been heard (its last frame sent, plus <see cref="PlayoutLatency"/>). Cancelling drops what has not yet been
    /// sent. Clips queue: the next starts the moment the one before ends.
    /// </summary>
    public async Task SpeakAsync(short[] pcm48kMono, CancellationToken ct = default)
    {
        if (pcm48kMono.Length == 0) return;
        var segment = new Segment { Pcm = pcm48kMono, Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        segment.Cancel = ct.Register(() =>
        {
            lock (_lock)
            {
                var kept = _queue.Where(s => !ReferenceEquals(s, segment)).ToList();
                _queue.Clear();
                foreach (var s in kept) _queue.Enqueue(s);
            }
            segment.Done.TrySetCanceled(ct);
        });
        lock (_lock) _queue.Enqueue(segment);
        try { await segment.Done.Task.ConfigureAwait(false); }
        finally { segment.Cancel.Dispose(); }
        await Task.Delay(PlayoutLatency, ct).ConfigureAwait(false);
    }

    /// <summary>Drops everything not yet sent (an interruption). What the robot has already buffered is cleared separately (<see cref="ReachyMiniClient.ClearIncomingAudioAsync"/>).</summary>
    public void Flush()
    {
        List<Segment> dropped;
        lock (_lock) { dropped = _queue.ToList(); _queue.Clear(); }
        foreach (var s in dropped) s.Done.TrySetCanceled();
    }

    private void Run()
    {
        var silence = new short[FrameSamples];
        // Windows' default timer tick is 15.6 ms: a 20 ms frame clock needs 1 ms.
        var raised = false;
        if (OperatingSystem.IsWindows()) { try { timeBeginPeriod(1); raised = true; } catch { } }
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long due = 0;                                        // frames that should have been sent by now
            while (!_stop)
            {
                var shouldHaveSent = clock.ElapsedMilliseconds / 20;
                if (due >= shouldHaveSent + 1) { Thread.Sleep(1); continue; }   // at most one frame ahead
                due++;
                var frame = new short[FrameSamples];
                Segment? finished = null;
                lock (_lock)
                {
                    if (_queue.Count > 0)
                    {
                        var s = _queue.Peek();
                        var n = Math.Min(FrameSamples, s.Pcm.Length - s.Offset);
                        Array.Copy(s.Pcm, s.Offset, frame, 0, n);
                        s.Offset += n;
                        if (s.Offset >= s.Pcm.Length) { finished = s; _queue.Dequeue(); }
                    }
                    else frame = silence;
                }
                try { _sender.PushPcm(new AudioPcmFrame(frame, SampleRate, 1)); }
                catch { /* a frame the transport refuses is lost; the next one goes out */ }
                FramesSent++;
                finished?.Done.TrySetResult();
            }
        }
        finally
        {
            if (raised) { try { timeEndPeriod(1); } catch { } }
        }
    }

    public void Dispose()
    {
        _stop = true;
        Flush();
        _thread.Join(500);
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
}
