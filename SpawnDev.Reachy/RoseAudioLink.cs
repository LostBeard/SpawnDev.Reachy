using Concentus;
using Concentus.Enums;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace SpawnDev.Reachy;

/// <summary>
/// Live bidirectional audio link to a Reachy Mini over WebRTC.
/// </summary>
/// <remarks>
/// Gives access to the robot's 4-mic array, which is the only sane input for a
/// robot that roams the house: it has hardware echo cancellation in the XVF3800,
/// so the robot does not hear and transcribe its own speech. A PC microphone has
/// neither property.
///
/// Microphone audio arrives as Opus at 48kHz and is decoded and downsampled to
/// 16kHz mono, which is what Whisper and Silero VAD both expect.
/// </remarks>
public sealed class RoseAudioLink : IAsyncDisposable
{
    private readonly string _host;
    private GstSignallingClient? _signalling;
    private RTCPeerConnection? _pc;
    private IOpusDecoder? _decoder;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private volatile bool _remoteDescriptionSet;
    private Action? _flushIce;

    /// <summary>Sample rate delivered by <see cref="OnMicAudio"/>.</summary>
    public const int OutputSampleRate = 16000;

    /// <summary>Opus in WebRTC is always 48kHz regardless of the capture rate.</summary>
    private const int OpusSampleRate = 48000;

    /// <summary>
    /// Raised with mono 16kHz PCM as the microphone streams. Handlers must not
    /// block - this fires on the RTP receive path.
    /// </summary>
    public event Action<short[]>? OnMicAudio;

    /// <summary>Raised when the peer connection state changes.</summary>
    public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;

    /// <summary>Diagnostic log. ICE failures are opaque without it.</summary>
    public event Action<string>? Log;

    public RoseAudioLink(string hostOrIp) => _host = hostOrIp;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        _signalling = new GstSignallingClient(_host);
        await _signalling.ConnectAsync(token);

        var producers = await _signalling.ListProducersAsync(token);
        var robot = producers.FirstOrDefault(p => p.Name == "reachymini");
        if (robot.Id is null)
            throw new InvalidOperationException(
                $"No 'reachymini' producer on {_host}:8443. Found: {producers.Count} producer(s).");

        _decoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, 1);

        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            // The robot is on the LAN, so host candidates are all that is needed.
            // A STUN server would only add latency and an external dependency to
            // something that must work with no internet at all.
            iceServers = [],

            // The robot's GStreamer DTLS stack generates an RSA-2048 certificate
            // (verified live: sha256WithRSAEncryption / rsaEncryption 2048-bit), and
            // it is the DTLS server here.
            //
            // SIPSorcery's DtlsClient derives its ClientHello cipher suite list from
            // the key type of ITS OWN certificate, which defaults to ECDSA - so it
            // offers only the five TLS_ECDHE_ECDSA_* suites. An RSA server cannot
            // select any of them and has no choice but to answer
            // handshake_failure(40). That was the whole blocker.
            //
            // Presenting an RSA certificate switches the offered suites to
            // TLS_ECDHE_RSA_*, which the robot can select. The flag drives both the
            // self-signed cert generation and the suite list, so the SDP fingerprint
            // stays consistent.
            X_UseRsaForDtlsCertificate = true,
        });

        // A VIDEO track must be declared even though we only want audio.
        //
        // The robot's offer is `a=group:BUNDLE video0 audio1 application2`, which
        // makes video0 the BUNDLE TAG - every bundled stream shares that m-line's
        // ICE/DTLS transport. With no video track, SIPSorcery answers video0 with
        // `a=inactive`, the bundled transport never comes up, and our ICE candidate
        // lands on audio1 where the robot ignores it. The robot then sits at
        // `signaling_state: have-local-offer` and tears the session down.
        //
        // Declaring it recvonly keeps the tag m-line alive. We simply never read
        // the decoded video.
        var videoFormat = new VideoFormat(VideoCodecsEnum.H264, 97, 90000);
        _pc.addTrack(new MediaStreamTrack(
            new List<VideoFormat> { videoFormat }, MediaStreamStatusEnum.RecvOnly));

        // Audio is sendrecv on the robot's offer, so declare the same and we get
        // the microphone up and a path for speech back down in one session.
        var audioFormat = new AudioFormat(AudioCodecsEnum.OPUS, 100, OpusSampleRate, 2);
        _pc.addTrack(new MediaStreamTrack(
            new List<AudioFormat> { audioFormat }, MediaStreamStatusEnum.SendRecv));

        _pc.OnRtpPacketReceived += (_, media, pkt) =>
        {
            if (media != SDPMediaTypesEnum.audio) return;
            try { DecodeAndEmit(pkt.Payload); }
            catch { /* a lost or malformed packet must not kill the stream */ }
        };

        _pc.onconnectionstatechange += state => OnConnectionStateChanged?.Invoke(state);
        _pc.oniceconnectionstatechange += s => Log?.Invoke($"ice state: {s}");
        _pc.onicegatheringstatechange += s => Log?.Invoke($"ice gathering: {s}");

        var localCands = 0;
        _pc.onicecandidate += c =>
        {
            if (c is null || _signalling?.SessionId is null) return;
            var n = Interlocked.Increment(ref localCands);
            Log?.Invoke($"local ICE #{n}: {c.candidate}");
            _ = _signalling.SendIceAsync(c.candidate, (int)c.sdpMLineIndex, token);
        };

        var offerReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _signalling.OnSdpOffer += sdp =>
        {
            // Fire-and-forget with everything awaited INSIDE, so a failure surfaces
            // on offerReceived instead of vanishing into an unobserved task.
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.offer,
                        sdp = sdp,
                    });
                    Log?.Invoke($"setRemoteDescription -> {result}");
                    if (result != SetDescriptionResultEnum.OK)
                    {
                        offerReceived.TrySetException(
                            new InvalidOperationException($"setRemoteDescription failed: {result}"));
                        return;
                    }

                    // Safe to apply candidates now, including any that raced ahead.
                    _flushIce?.Invoke();

                    var answer = _pc.createAnswer(null);
                    Log?.Invoke($"answer created, {answer.sdp?.Length ?? 0} bytes");
                    foreach (var l in (answer.sdp ?? "").Split('\n'))
                    {
                        var s = l.Trim();
                        if (s.StartsWith("m=") || s.StartsWith("a=mid:") || s.StartsWith("a=group:"))
                            Log?.Invoke($"  answer| {s}");
                    }
                    await _pc.setLocalDescription(answer);

                    await _signalling.SendAnswerAsync(answer.sdp, token);
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

        // Remote candidates cannot be added before the remote description exists -
        // they are silently discarded. The offer handler is async, so candidates
        // WILL arrive first; queue them and flush once the description is set.
        var remoteCands = 0;
        var pendingIce = new List<(string Cand, int Line)>();
        var iceLock = new object();

        void AddIce(string cand, int line)
        {
            try
            {
                _pc.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = cand,
                    sdpMLineIndex = (ushort)line,
                });
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

    private void DecodeAndEmit(byte[] opusPayload)
    {
        if (_decoder is null || opusPayload.Length == 0) return;

        // 60ms at 48kHz is the largest frame Opus will hand back.
        var pcm48 = new short[OpusSampleRate / 1000 * 60];
        var decoded = _decoder.Decode(opusPayload, pcm48, pcm48.Length, false);
        if (decoded <= 0) return;

        // 48000 -> 16000 is an exact 3:1 decimation. Averaging each triple is a
        // cheap low-pass that avoids the aliasing plain sample-dropping causes.
        var outLen = decoded / 3;
        if (outLen == 0) return;

        var pcm16 = new short[outLen];
        for (var i = 0; i < outLen; i++)
        {
            var sum = pcm48[i * 3] + pcm48[i * 3 + 1] + pcm48[i * 3 + 2];
            pcm16[i] = (short)(sum / 3);
        }

        OnMicAudio?.Invoke(pcm16);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_pump is not null) await _pump; } catch { }
        _pc?.Close("disposed");
        _decoder?.Dispose();
        if (_signalling is not null) await _signalling.DisposeAsync();
        _cts?.Dispose();
    }
}
