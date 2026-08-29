using System.Collections.Generic;
using SpawnDev.RTC;
using SpawnDev.RTC.Audio;

namespace SpawnDev.Reachy;

/// <summary>
/// Live audio link to a Reachy Mini over WebRTC, built on SpawnDev.RTC's cross-platform
/// <see cref="IRTCPeerConnection"/> so the SAME code runs on the desktop (SipSorcery) and in the
/// browser (native WebRTC via SpawnDev.SpawnJS - e.g. inside the Gemineachy extension).
/// </summary>
/// <remarks>
/// Gives access to the robot's 4-mic array, which is the only sane input for a robot that roams the
/// house: it has hardware echo cancellation in the XVF3800, so the robot does not hear and transcribe
/// its own speech. A PC microphone has neither property.
///
/// The mic audio is delivered by the peer connection as a decoded track; the PCM audio bridge
/// (<see cref="IRTCAudioReceiver"/>) surfaces it as 48 kHz PCM, which this class downmixes to mono and
/// decimates to 16 kHz - what Whisper and Silero VAD both expect - on <see cref="OnMicAudio"/>.
/// </remarks>
public sealed class RoseAudioLink : IAsyncDisposable
{
    private readonly string _host;
    private readonly Func<ISignalingSocket>? _signalingSocketFactory;
    private GstSignallingClient? _signalling;
    private IRTCPeerConnection? _pc;
    private IRTCRtpTransceiver? _audioTransceiver;
    private IRTCAudioReceiver? _micReceiver;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private volatile bool _remoteDescriptionSet;
    private Action? _flushIce;

    /// <summary>Sample rate delivered by <see cref="OnMicAudio"/>.</summary>
    public const int OutputSampleRate = 16000;

    /// <summary>
    /// Raised with mono 16 kHz PCM as the microphone streams. Handlers must not block - this fires on
    /// the bridge's receive path.
    /// </summary>
    /// <remarks>
    /// Subscribe BEFORE <see cref="ConnectAsync"/>: the PCM capture path is only started if this event has
    /// a handler when the robot's audio track arrives, so a host that only wants to PLAY the audio (see
    /// <see cref="OnAudioTrack"/>) does not pay to decode it to PCM as well. A late subscriber can call
    /// <see cref="StartPcmCapture"/> to start it after the fact.
    /// </remarks>
    public event Action<short[]>? OnMicAudio;

    /// <summary>
    /// Raised with the robot's decoded audio track as soon as it arrives, before any PCM capture starts.
    /// </summary>
    /// <remarks>
    /// This is the handle a host wants when the audio is to be HEARD rather than analysed: in the browser
    /// the track can go straight to an <c>&lt;audio&gt;</c> element, so the browser decodes and plays it and
    /// no audio ever crosses into .NET. <see cref="OnMicAudio"/> is the other path - decoded PCM for
    /// speech recognition. They are independent; take either, both, or neither.
    /// </remarks>
    public event Action<IRTCMediaStreamTrack>? OnAudioTrack;

    /// <summary>The robot's audio track, once received. Null before that.</summary>
    public IRTCMediaStreamTrack? AudioTrack { get; private set; }

    /// <summary>Raised when the peer connection state changes (W3C connection-state string).</summary>
    public event Action<string>? OnConnectionStateChanged;

    /// <summary>Diagnostic log. ICE/DTLS failures are opaque without it.</summary>
    public event Action<string>? Log;

    /// <param name="hostOrIp">Robot host or IP.</param>
    /// <param name="signalingSocketFactory">
    /// Optional factory for the signalling transport. Desktop leaves it null (a real
    /// <see cref="ClientWebSocketSignalingSocket"/> is used). The browser-extension host passes a factory
    /// that relays <c>ws://</c> frames through the background service worker, since a content script cannot
    /// open <c>ws://</c> from an https page.
    /// </param>
    public RoseAudioLink(string hostOrIp, Func<ISignalingSocket>? signalingSocketFactory = null)
    {
        _host = hostOrIp;
        _signalingSocketFactory = signalingSocketFactory;
    }

    /// <summary>
    /// Brings the WebRTC link up and starts delivering microphone audio to <see cref="OnMicAudio"/>.
    /// </summary>
    /// <remarks>
    /// Runs the whole chain: signalling, SDP, ICE, DTLS, SRTP, then Opus decoded to 16kHz mono PCM.
    /// Two things here were bought the hard way. The robot's BUNDLE tag is the VIDEO m-line, so a
    /// client that offers audio only leaves that transport inactive and the session never comes up -
    /// a recvonly video track is added even though only audio is wanted. And the robot's GStreamer
    /// stack presents an RSA certificate, so the DTLS client has to be told to use RSA too or the
    /// handshake correctly fails with handshake_failure(40).
    /// </remarks>
    /// <param name="ct">Cancels the connection attempt and, once connected, tears the link down.</param>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        _signalling = new GstSignallingClient(_host, socket: _signalingSocketFactory?.Invoke());
        await _signalling.ConnectAsync(token);

        var producers = await _signalling.ListProducersAsync(token);
        var robot = producers.FirstOrDefault(p => p.Name == "reachymini");
        if (robot.Id is null)
            throw new InvalidOperationException(
                $"No 'reachymini' producer on {_host}:8443. Found: {producers.Count} producer(s).");

        _pc = RTCPeerConnectionFactory.Create(new RTCPeerConnectionConfig
        {
            // The robot is on the LAN, so host candidates are all that is needed - a STUN server would
            // only add latency and an internet dependency to something that must work fully offline.
            IceServers = System.Array.Empty<RTCIceServerConfig>(),

            // The robot's GStreamer DTLS stack is an RSA-2048 server; the desktop (SipSorcery) client must
            // present an RSA certificate so it offers TLS_ECDHE_RSA_* suites the robot can select - an
            // ECDSA-only offer gets handshake_failure(40). The browser ignores this (native WebRTC
            // negotiates RSA without help). This is the whole original blocker, now a config flag.
            X_UseRsaForDtlsCertificate = true,
        });

        // A recvonly VIDEO track must be declared even though we only want audio. The robot's offer is
        // `a=group:BUNDLE video0 audio1 application2`, which makes video0 the BUNDLE TAG - every bundled
        // stream shares that m-line's ICE/DTLS transport. With no video m-line the bundled transport never
        // comes up and our ICE lands on audio1 where the robot ignores it. recvonly keeps the tag alive;
        // we simply never read the decoded video.
        _pc.AddTransceiver("video", new RTCRtpTransceiverInit { Direction = "recvonly" });
        // Audio is sendrecv on the robot's offer; declare the same so the mic comes up AND the one session
        // carries a send path out to the robot's speaker (see SetSendTrackAsync).
        _audioTransceiver = _pc.AddTransceiver("audio", new RTCRtpTransceiverInit { Direction = "sendrecv" });

        _pc.OnTrack += trackEvent =>
        {
            // The video transceiver exists only to keep the BUNDLE tag alive (see above), so its track is
            // deliberately ignored.
            if (trackEvent.Track?.Kind != "audio" || AudioTrack is not null) return;
            AudioTrack = trackEvent.Track;
            Log?.Invoke("audio track received");
            try { OnAudioTrack?.Invoke(trackEvent.Track); }
            catch (Exception ex) { Log?.Invoke($"OnAudioTrack handler threw: {ex.GetType().Name}: {ex.Message}"); }
            // Only decode to PCM if someone is listening for it. In the browser the PCM path runs a
            // MediaStreamTrackProcessor over this same track, which is pure cost for a host that just
            // wants to play the audio.
            if (OnMicAudio is not null) StartPcmCapture();
        };

        _pc.OnConnectionStateChange += state => OnConnectionStateChanged?.Invoke(state);
        _pc.OnIceConnectionStateChange += s => Log?.Invoke($"ice state: {s}");
        _pc.OnIceGatheringStateChange += s => Log?.Invoke($"ice gathering: {s}");

        var localCands = 0;
        _pc.OnIceCandidate += c =>
        {
            if (c is null || _signalling?.SessionId is null) return;
            var n = Interlocked.Increment(ref localCands);
            Log?.Invoke($"local ICE #{n}: {c.Candidate}");
            _ = _signalling.SendIceAsync(c.Candidate, c.SdpMLineIndex ?? 0, token);
        };

        var offerReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _signalling.OnSdpOffer += sdp =>
        {
            // Fire-and-forget with everything awaited INSIDE, so a failure surfaces on offerReceived
            // instead of vanishing into an unobserved task.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "offer", Sdp = sdp });
                    Log?.Invoke("setRemoteDescription ok");

                    // Safe to apply candidates now, including any that raced ahead.
                    _flushIce?.Invoke();

                    var answer = await _pc.CreateAnswer();
                    var answerSdp = answer.Sdp
                        ?? throw new InvalidOperationException("createAnswer produced no SDP");
                    await _pc.SetLocalDescription(answer);
                    await _signalling.SendAnswerAsync(answerSdp, token);
                    Log?.Invoke("answer sent");
                    offerReceived.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"answer path threw: {ex.GetType().Name}: {ex.Message}");
                    offerReceived.TrySetException(ex);
                }
            }, token);
        };

        // Remote candidates cannot be added before the remote description exists - they are silently
        // discarded. The offer handler is async, so candidates WILL arrive first; queue them and flush
        // once the description is set.
        var remoteCands = 0;
        var pendingIce = new List<(string Cand, int Line)>();
        var iceLock = new object();

        void AddIce(string cand, int line)
        {
            try
            {
                _ = _pc!.AddIceCandidate(new RTCIceCandidateInit { Candidate = cand, SdpMLineIndex = line });
                Log?.Invoke($"remote ICE #{Interlocked.Increment(ref remoteCands)} applied (mline {line})");
            }
            catch (Exception ex) { Log?.Invoke($"remote ICE rejected: {ex.Message}"); }
        }

        void FlushIce()
        {
            List<(string, int)> queued;
            lock (iceLock)
            {
                _remoteDescriptionSet = true;
                queued = [.. pendingIce];
                pendingIce.Clear();
            }
            if (queued.Count > 0) Log?.Invoke($"flushing {queued.Count} queued ICE candidate(s)");
            foreach (var (c, l) in queued) AddIce(c, l);
        }

        _flushIce = FlushIce;

        _signalling.OnIceCandidate += (cand, line) =>
        {
            lock (iceLock)
            {
                if (!_remoteDescriptionSet) { pendingIce.Add((cand, line)); Log?.Invoke($"queued ICE (mline {line})"); return; }
            }
            AddIce(cand, line);
        };

        // Single reader, started BEFORE startSession - see GstSignallingClient.
        _pump = _signalling.ReceiveLoopAsync(token);

        await _signalling.StartSessionAsync(robot.Id, token);
        await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
    }

    /// <summary>
    /// Send audio OUT to the robot's speaker - a microphone track so a person can talk to it, or a
    /// synthesised speech track. Pass <c>null</c> to go silent again without renegotiating.
    /// </summary>
    /// <remarks>
    /// This REPLACES the track on the existing sendrecv audio transceiver; it deliberately does not add a
    /// track to the peer connection. The robot's offer carries exactly one audio m-line
    /// (<c>BUNDLE video0 audio1 application2</c>), and adding a second audio track would create a second
    /// one that has nothing to negotiate against. `replaceTrack` also needs no renegotiation at all, so the
    /// send side can be switched on and off mid-session.
    /// <para>
    /// Echo: whatever is sent here comes out of the robot's speaker and back into its own microphone, which
    /// is the stream <see cref="OnAudioTrack"/>/<see cref="OnMicAudio"/> deliver. The robot's XVF3800 does
    /// hardware AEC on its own output, so it does not hear itself - but a HOST playing the robot's mic on
    /// loudspeakers while sending its own microphone must enable echo cancellation on the captured track,
    /// or it closes the loop through the room at its end.
    /// </para>
    /// </remarks>
    public async Task SetSendTrackAsync(IRTCMediaStreamTrack? track)
    {
        if (_audioTransceiver is null)
            throw new InvalidOperationException("Not connected - call ConnectAsync first.");
        await _audioTransceiver.Sender.ReplaceTrack(track);
        Log?.Invoke(track is null ? "send track cleared" : $"send track set: {track.Id}");
    }

    /// <summary>
    /// Start decoding the robot's audio track to 16 kHz mono PCM on <see cref="OnMicAudio"/>. Called
    /// automatically when the track arrives if <see cref="OnMicAudio"/> already has a handler; call it
    /// explicitly if you subscribed after connecting. Idempotent, and a no-op before the track arrives.
    /// </summary>
    public void StartPcmCapture()
    {
        if (_micReceiver is not null || AudioTrack is null || _pc is null) return;
        try
        {
            _micReceiver = _pc.ReceivePcmAudio(AudioTrack);
            _micReceiver.OnPcmFrame += HandleMicPcm;
            _micReceiver.Start();
            Log?.Invoke("mic receiver started");
        }
        catch (Exception ex) { Log?.Invoke($"mic receiver failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    // Bridge delivers 48 kHz PCM (WebRTC Opus rate). Downmix to mono and decimate to 16 kHz for Whisper
    // / Silero. 48000 -> 16000 is an exact 3:1 decimation; averaging each group is a cheap low-pass that
    // avoids the aliasing plain sample-dropping causes.
    private void HandleMicPcm(AudioPcmFrame frame)
    {
        var pcm = frame.Pcm;
        int channels = frame.Channels;
        int frames = frame.SampleCount;
        if (frames == 0 || channels == 0) return;

        // Downmix to mono first.
        var mono = new short[frames];
        for (int i = 0; i < frames; i++)
        {
            int sum = 0;
            for (int c = 0; c < channels; c++) sum += pcm[i * channels + c];
            mono[i] = (short)(sum / channels);
        }

        int decim = Math.Max(1, frame.SampleRate / OutputSampleRate);
        int outLen = frames / decim;
        if (outLen == 0) return;

        var outPcm = new short[outLen];
        for (int i = 0; i < outLen; i++)
        {
            int sum = 0;
            for (int k = 0; k < decim; k++) sum += mono[i * decim + k];
            outPcm[i] = (short)(sum / decim);
        }

        OnMicAudio?.Invoke(outPcm);
    }

    /// <summary>Tears the link down: stops the pump, closes the peer connection and releases the socket.</summary>
    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_pump is not null) await _pump; } catch { }
        _micReceiver?.Dispose();
        _pc?.Close();
        _pc?.Dispose();
        if (_signalling is not null) await _signalling.DisposeAsync();
        _cts?.Dispose();
    }
}
