using Microsoft.Extensions.Logging;
using SpawnDev.Reachy;
using SpawnDev.Reachy.Rose;

// Rose - companion app for Aubs's Reachy Mini.
// Currently a read-only connectivity check while the SDK is built out.

// Resolves the models directory (silero_vad.onnx + the Whisper model dir), which
// sits at the solution root rather than beside the binary.
static string ModelDir()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
    {
        var candidate = Path.Combine(d.FullName, "models");
        if (Directory.Exists(candidate)) return candidate;
    }
    return Path.Combine(Directory.GetCurrentDirectory(), "models");
}

if (args.Contains("--talk"))
{
    var talkIp = args.FirstOrDefault(a => a.Contains('.')) ?? "192.168.1.170";
    var model = args.FirstOrDefault(a => a.StartsWith("--model="))?["--model=".Length..]
                ?? "llama3.1:8b";

    await using var convo = new RoseConversation(talkIp, ModelDir(), model);

    convo.OnLine += (who, what) =>
    {
        var colour = who == "Aubs" ? ConsoleColor.Cyan : ConsoleColor.Yellow;
        Console.ForegroundColor = colour;
        Console.WriteLine($"{who,6}: {what}");
        Console.ResetColor();
    };
    if (args.Contains("--verbose")) convo.Log += m => Console.WriteLine($"  [log] {m}");

    Console.WriteLine($"Waking Rose at {talkIp} (model {model})...");
    await convo.StartAsync();
    Console.WriteLine("\nRose is listening. Just talk to her. Ctrl+C to stop.\n");

    var quit = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
    await quit.Task;

    Console.WriteLine("\nGoodbye.");
    return 0;
}

if (args.Contains("--test-names"))
{
    // Character switching is the feature Aubs will use most, and every character
    // name is either a single letter or a proper noun Whisper has never seen. This
    // measures what recognition ACTUALLY returns for each name in a natural request,
    // so the alias table can be built from evidence instead of imagination.
    //
    // Caveat: a synthesised adult voice is not a ten year old, so this finds the
    // obvious failures, not all of them.
    var synth = new KokoroSharp.Utilities.KokoroWavSynthesizer(
        Path.Combine(Directory.GetCurrentDirectory(), "kokoro.onnx"));

    await using var probe = new RoseEars(ModelDir());
    string? lastHeard = null;
    var done = new SemaphoreSlim(0);
    probe.OnUtterance += t => { lastHeard = t; done.Release(); };

    var voices = new[] { "af_sarah", "af_heart", "am_adam" };
    var misses = new List<string>();

    foreach (var c in CharacterLibrary.All)
    {
        foreach (var voiceName in voices)
        {
            var phrase = $"Can you be {c.Name}?";
            var pcm24 = await synth.SynthesizeAsync(phrase, KokoroSharp.KokoroVoiceManager.GetVoice(voiceName));

            var src = new short[pcm24.Length / 2];
            for (var i = 0; i < src.Length; i++) src[i] = BitConverter.ToInt16(pcm24, i * 2);
            var outLen = src.Length * 2 / 3;
            var pcm16 = new short[outLen];
            for (var i = 0; i < outLen; i++)
            {
                var s = i * 3 / 2;
                pcm16[i] = s + 1 < src.Length ? (short)((src[s] + src[s + 1]) / 2) : src[^1];
            }

            lastHeard = null;
            var silence = new short[8000];
            foreach (var block in new[] { silence, pcm16, silence })
                for (var i = 0; i < block.Length; i += 320)
                {
                    probe.Feed(block[i..Math.Min(i + 320, block.Length)]);
                    Thread.Sleep(20);
                }
            probe.Flush();
            await done.WaitAsync(TimeSpan.FromSeconds(10));

            // Ask AS a different character than the one being requested, so every
            // case must produce a real switch. An earlier version used a fixed
            // "current" and scored the matching character as a pass for not
            // switching, which hid a genuine miss.
            var current = c.Name == "N" ? CharacterLibrary.Uzi : CharacterLibrary.N;
            var resolved = lastHeard is null
                ? null
                : RoseConversation.FindSwitchRequest(lastHeard, current)?.Name;

            var ok = resolved == c.Name;
            if (!ok) misses.Add($"{c.Name}/{voiceName}: heard \"{lastHeard}\"");

            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {c.Name,-5} ({voiceName,-8}) heard \"{lastHeard}\" -> {resolved ?? "(none)"}");
        }
    }

    Console.WriteLine($"\n{CharacterLibrary.All.Count * voices.Length - misses.Count}/{CharacterLibrary.All.Count * voices.Length} resolved");
    foreach (var m in misses) Console.WriteLine($"  MISS {m}");
    return misses.Count == 0 ? 0 : 1;
}

if (args.Contains("--test-loop"))
{
    // The whole conversation chain end to end, with a synthesised question standing
    // in for a person: recognition -> character switching -> model -> action split
    // -> speech -> the robot's own speaker. Only the acoustic capture is simulated;
    // every stage after it is the identical live code path.
    var loopIp = args.FirstOrDefault(a => a.Contains('.')) ?? "192.168.1.170";

    string[] questions =
    [
        "Hi! Do you like My Little Pony?",
        "Can you be Uzi?",
        "What is your favorite color?",
    ];

    // Say them in a voice that is NOT one of Rose's, so there is no doubt about
    // which side of the conversation any given line came from.
    var asker = new KokoroSharp.Utilities.KokoroWavSynthesizer(
        Path.Combine(Directory.GetCurrentDirectory(), "kokoro.onnx"));
    var askerVoice = KokoroSharp.KokoroVoiceManager.GetVoice("af_sarah");

    await using var loop = new RoseConversation(loopIp, ModelDir(), useMicrophone: false);
    loop.OnLine += (who, what) =>
    {
        Console.ForegroundColor = who == "Aubs" ? ConsoleColor.Cyan : ConsoleColor.Yellow;
        Console.WriteLine($"{who,6}: {what}");
        Console.ResetColor();
    };
    if (args.Contains("--verbose")) loop.Log += m => Console.WriteLine($"  [log] {m}");

    Console.WriteLine($"Waking Rose at {loopIp}...");
    await loop.StartAsync();

    foreach (var q in questions)
    {
        Console.WriteLine($"\n--- injecting: \"{q}\"");

        // Kokoro renders at 24kHz; the mic path is 16kHz, so decimate 3:2 by
        // averaging, matching what the live Opus path delivers.
        var pcm24 = await asker.SynthesizeAsync(q, askerVoice);
        var src = new short[pcm24.Length / 2];
        for (var i = 0; i < src.Length; i++) src[i] = BitConverter.ToInt16(pcm24, i * 2);

        var outLen = src.Length * 2 / 3;
        var pcm16 = new short[outLen];
        for (var i = 0; i < outLen; i++)
        {
            var s = i * 3 / 2;
            pcm16[i] = s + 1 < src.Length ? (short)((src[s] + src[s + 1]) / 2) : src[^1];
        }

        // Lead-in and trail-out silence: the detector needs silence to recognise
        // where an utterance starts and stops.
        var silence = new short[16000 / 2];
        void FeedPaced(short[] data)
        {
            for (var i = 0; i < data.Length; i += 320)
            {
                loop.InjectAudio(data[i..Math.Min(i + 320, data.Length)]);
                Thread.Sleep(20);
            }
        }

        FeedPaced(silence);
        FeedPaced(pcm16);
        FeedPaced(silence);
        loop.FlushAudio();

        // Let recognition, the model and the full spoken reply complete.
        await Task.Delay(3000);
        await loop.WaitForIdleAsync();
    }

    Console.WriteLine("\nLoop test complete.");
    return 0;
}

if (args.Contains("--test-ears"))
{
    // Transcribes a wav file through the exact VAD + Whisper path the live loop
    // uses, so recognition can be verified without a robot or a microphone.
    var wavPath = args.FirstOrDefault(a => a.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wavPath is null || !File.Exists(wavPath))
    {
        Console.WriteLine("usage: --test-ears <file.wav>   (16kHz mono)");
        return 1;
    }

    await using var ears = new RoseEars(ModelDir());
    var heard = new List<string>();
    ears.OnUtterance += t => { heard.Add(t); Console.WriteLine($"  UTTERANCE: \"{t}\""); };
    ears.Log += m => Console.WriteLine($"  [log] {m}");

    var bytes = await File.ReadAllBytesAsync(wavPath);

    // Skip the RIFF header and read 16-bit mono PCM.
    var offset = 44;
    var samples = new short[(bytes.Length - offset) / 2];
    for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, offset + i * 2);
    Console.WriteLine($"loaded {samples.Length} samples = {samples.Length / 16000.0:F1}s\n");

    // Feed in 320-sample chunks at real time, exactly as the RTP path delivers
    // them. The pacing is not cosmetic: the intake channel drops the OLDEST frames
    // when it overflows - correct for a live mic, where stale audio is worthless -
    // so blasting a whole file in at once silently loses the START of it.
    var clock = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < samples.Length; i += 320)
    {
        ears.Feed(samples[i..Math.Min(i + 320, samples.Length)]);

        var due = TimeSpan.FromSeconds(i / 16000.0);
        var ahead = due - clock.Elapsed;
        if (ahead > TimeSpan.FromMilliseconds(2)) await Task.Delay(ahead);
    }

    // A recording ends mid-speech, so close the final segment explicitly.
    ears.Flush();

    await Task.Delay(TimeSpan.FromSeconds(10));
    Console.WriteLine($"\n{heard.Count} utterance(s) recognised.");
    return heard.Count > 0 ? 0 : 1;
}

if (args.Contains("--test-brain"))
{
    // Exercises the LLM path alone - no robot, no audio - so persona quality and
    // latency can be judged without hardware in the way.
    var brainModel = args.FirstOrDefault(a => a.StartsWith("--model="))?["--model=".Length..]
                     ?? "llama3.1:8b";
    var brain = new RoseBrain(brainModel);

    var problem = await brain.CheckAsync();
    if (problem is not null) { Console.WriteLine($"  {problem}"); return 1; }

    string[] prompts =
    [
        "Hi! Do you like My Little Pony?",
        "What's your favourite thing about being a robot?",
        "I had a bad day at school.",
    ];

    foreach (var p in prompts)
    {
        Console.WriteLine($"\n  Aubs: {p}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = TimeSpan.Zero;
        await brain.StreamReplyAsync(p, CharacterLibrary.N, sentence =>
        {
            if (first == TimeSpan.Zero) first = sw.Elapsed;
            Console.WriteLine($"     N: {sentence}");
            return Task.CompletedTask;
        });
        Console.WriteLine($"        (first sentence {first.TotalSeconds:F2}s, total {sw.Elapsed.TotalSeconds:F2}s)");
    }
    return 0;
}

if (args.Contains("--test-udp"))
{
    // Same binary as --test-mic, so the firewall sees the identical program.
    // Running this from a different executable proves nothing, because inbound
    // rules on Windows are per-program.
    var robotIp = args.FirstOrDefault(a => a.Contains('.')) ?? "192.168.1.170";
    const int Port = 51999;

    using var udp = new System.Net.Sockets.UdpClient(Port);
    Console.WriteLine($"listening on 0.0.0.0:{Port} as {Environment.ProcessPath}");

    var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    _ = Task.Run(async () =>
    {
        try
        {
            var r = await udp.ReceiveAsync();
            got.TrySetResult($"{System.Text.Encoding.UTF8.GetString(r.Buffer)} from {r.RemoteEndPoint}");
        }
        catch (Exception ex) { got.TrySetException(ex); }
    });

    // Discover our own LAN address as seen on the route to the robot, rather than
    // assuming which interface is in play.
    using var probe = new System.Net.Sockets.Socket(
        System.Net.Sockets.AddressFamily.InterNetwork,
        System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
    probe.Connect(robotIp, 8000);
    var localIp = ((System.Net.IPEndPoint)probe.LocalEndPoint!).Address.ToString();
    Console.WriteLine($"local address toward robot: {localIp}");

    using var ssh = new Renci.SshNet.SshClient(robotIp, 22, "pollen", "root");
    ssh.Connect();
    using (var cmd = ssh.CreateCommand(
        $"for i in 1 2 3; do echo -n \"HELLO_$i\" > /dev/udp/{localIp}/{Port}; sleep 0.3; done; echo sent"))
        Console.WriteLine($"robot: {cmd.Execute().Trim()}");

    try
    {
        Console.WriteLine($"\nRECEIVED: {await got.Task.WaitAsync(TimeSpan.FromSeconds(6))}");
        Console.WriteLine("=> inbound UDP to THIS binary works. ICE failure is not the firewall.");
        ssh.Disconnect();
        return 0;
    }
    catch (TimeoutException)
    {
        Console.WriteLine("\nNOTHING ARRIVED => inbound UDP genuinely blocked for this binary.");
        ssh.Disconnect();
        return 1;
    }
}

if (args.Contains("--test-mic"))
{
    var ip5 = args.FirstOrDefault(a => a.Contains('.')) ?? "192.168.1.170";
    await using var link = new RoseAudioLink(ip5);

    long totalSamples = 0;
    var packets = 0;
    var levelPeak = 0.0;

    link.OnConnectionStateChanged += s => Console.WriteLine($"  [pc] {s}");
    if (args.Contains("--verbose")) link.Log += m => Console.WriteLine($"  [log] {m}");

    if (args.Contains("--sipdebug"))
    {
        // SIPSorcery closes the peer connection for reasons it only reports
        // internally. Without this the failure is completely opaque.
        SIPSorcery.LogFactory.Set(LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
            .SetMinimumLevel(LogLevel.Debug)));
    }
    link.OnMicAudio += pcm =>
    {
        Interlocked.Increment(ref packets);
        Interlocked.Add(ref totalSamples, pcm.Length);
        double sum = 0;
        foreach (var s in pcm) { var f = s / 32768.0; sum += f * f; }
        var rms = Math.Sqrt(sum / Math.Max(pcm.Length, 1));
        if (rms > levelPeak) levelPeak = rms;
    };

    Console.WriteLine($"connecting audio link to {ip5} ...");
    await link.ConnectAsync();
    Console.WriteLine("connected. TALK TO ROSE - 20 seconds.\n");

    // Live meter, so it is obvious whether the mic is actually live.
    for (var i = 0; i < 20; i++)
    {
        await Task.Delay(1000);
        var db = 20 * Math.Log10(Math.Max(levelPeak, 1e-9));
        var bars = (int)Math.Clamp((db + 60) / 60 * 40, 0, 40);
        Console.WriteLine($"  {i + 1,2}s  {new string('#', bars).PadRight(40)} {db,6:F1} dBFS   pkts={packets} samples={totalSamples}");
        levelPeak = 0;
    }

    Console.WriteLine($"\ntotal: {packets} packets, {totalSamples} samples = {totalSamples / (double)RoseAudioLink.OutputSampleRate:F1}s of 16kHz mono audio");
    return packets > 0 ? 0 : 1;
}

if (args.Contains("--test-signalling"))
{
    var ip4 = args.FirstOrDefault(a => a.Contains('.')) ?? "192.168.1.170";
    await using var sig = new GstSignallingClient(ip4);

    using var cts4 = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    Console.WriteLine($"connecting to ws://{ip4}:8443 ...");
    await sig.ConnectAsync(cts4.Token);
    Console.WriteLine($"  our peerId  : {sig.PeerId}");

    var producers = await sig.ListProducersAsync(cts4.Token);
    Console.WriteLine($"  producers   : {producers.Count}");
    foreach (var (id, name) in producers) Console.WriteLine($"    {id}  meta.name={name}");

    var robotProducer = producers.FirstOrDefault(p => p.Name == "reachymini");
    if (robotProducer.Id is null) { Console.WriteLine("  no 'reachymini' producer found"); return 1; }

    var offerTcs = new TaskCompletionSource<string>();
    var iceCount = 0;
    sig.OnSdpOffer += sdp => offerTcs.TrySetResult(sdp);
    sig.OnIceCandidate += (_, _) => Interlocked.Increment(ref iceCount);

    var pump = sig.ReceiveLoopAsync(cts4.Token);

    Console.WriteLine($"\nstarting session with {robotProducer.Id} ...");
    await sig.StartSessionAsync(robotProducer.Id, cts4.Token);
    Console.WriteLine($"  sessionId   : {sig.SessionId}");

    Console.WriteLine("\nwaiting for SDP offer ...");
    var offer = await offerTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), cts4.Token);

    Console.WriteLine($"  offer bytes : {offer.Length}");
    // What media does the robot actually advertise? This decides what we can pull.
    foreach (var line in offer.Split('\n'))
    {
        var l = line.Trim();
        if (l.StartsWith("m=") || l.Contains("opus", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("a=sendrecv") || l.StartsWith("a=sendonly") || l.StartsWith("a=recvonly")
            || l.Contains("H264", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"    {l}");
    }

    await Task.Delay(2000);
    Console.WriteLine($"\n  ICE candidates received: {iceCount}");
    Console.WriteLine("\nSignalling verified.");
    return 0;
}

if (args.Contains("--test-posture"))
{
    var ip3 = args.FirstOrDefault(a => a.Contains('.')) ?? "192.168.1.170";
    using var robot3 = new ReachyMiniClient(ip3);
    await robot3.SetMotorModeAsync(MotorMode.Enabled);
    using var v3 = new RoseVoice(robot3);

    // Identical audio both times. The ONLY variable is where the head is
    // relative to the upward-firing speaker in the chest.
    foreach (var (label, z, pitch) in new[]
    {
        ("HEAD DOWN (resting on the speaker)", 0.0, 0.45),
        ("HEAD UP (lifted 22mm clear)", RoseVoice.MaxHeadLift, -0.05),
    })
    {
        Console.WriteLine($"\n>>> {label}   z={z:F4} pitch={pitch:F2}");
        v3.LiftHeadToSpeak = false;   // posture is set manually here
        await robot3.GotoAsync(headPose: new XyzRpyPose(Z: z, Pitch: pitch), duration: 1.0);
        await Task.Delay(1500);
        await v3.SpeakAsync("Oh gosh! Can you hear me okay from over there?", CharacterLibrary.N);
        await Task.Delay(5000);
    }

    Console.WriteLine("\nReturning to speaking posture.");
    await robot3.GotoAsync(headPose: new XyzRpyPose(Z: RoseVoice.MaxHeadLift, Pitch: -0.05), duration: 1.0);
    Console.WriteLine("How much louder was head-up?");
    return 0;
}

if (args.Contains("--test-loudness"))
{
    var ip2 = args.FirstOrDefault(a => a.Contains('.')) ?? "192.168.1.170";
    using var robot2 = new ReachyMiniClient(ip2);
    using var v2 = new RoseVoice(robot2);

    const string Line = "Oh gosh! Can you hear me okay from over there?";

    // Objective measurement first. Perceived loudness tracks RMS, not peak - peak
    // is already pinned at the ceiling either way, which is exactly why the stock
    // audio sounds quiet despite every volume control reading 100.
    {
        var synth = new KokoroSharp.Utilities.KokoroWavSynthesizer(
            Path.Combine(Directory.GetCurrentDirectory(), "kokoro.onnx"));
        var before = await synth.SynthesizeAsync(Line, KokoroSharp.KokoroVoiceManager.GetVoice("am_puck"));
        var after = (byte[])before.Clone();
        RoseVoice.Loudify(after);

        static (double Rms, double Peak) Measure(byte[] pcm)
        {
            double sum = 0; double peak = 0;
            var n = pcm.Length / 2;
            for (var i = 0; i < n; i++)
            {
                var s = BitConverter.ToInt16(pcm, i * 2) / 32768.0;
                sum += s * s;
                peak = Math.Max(peak, Math.Abs(s));
            }
            return (Math.Sqrt(sum / Math.Max(n, 1)), peak);
        }

        var b = Measure(before);
        var a = Measure(after);
        static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-9));

        Console.WriteLine($"  raw       : RMS {Db(b.Rms),7:F1} dBFS   peak {Db(b.Peak),6:F1} dBFS");
        Console.WriteLine($"  processed : RMS {Db(a.Rms),7:F1} dBFS   peak {Db(a.Peak),6:F1} dBFS");
        Console.WriteLine($"  GAIN      : {Db(a.Rms) - Db(b.Rms):+0.0;-0.0} dB RMS");
    }

    // Same words, same voice, back to back. The only variable is the compressor.
    foreach (var (label, normalize) in new[] { ("RAW (no processing)", false), ("PROCESSED", true) })
    {
        Console.WriteLine($"\n>>> {label}");
        v2.NormalizeLoudness = normalize;
        await v2.SpeakAsync($"This is version {(normalize ? "two" : "one")}. {Line}", CharacterLibrary.N);
        await Task.Delay(6000);
    }

    Console.WriteLine("\nWhich was louder - version one or version two?");
    return 0;
}

if (args.Contains("--test-voice"))
{
    var ip = args.FirstOrDefault(a => a.Contains('.')) ?? "192.168.1.170";
    using var robot = new ReachyMiniClient(ip);

    Console.WriteLine("Loading Kokoro...");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var voice = new RoseVoice(robot);
    Console.WriteLine($"  ready in {sw.Elapsed.TotalSeconds:F1}s\n");

    if (args.Contains("--inspect"))
    {
        var synth = new KokoroSharp.Utilities.KokoroWavSynthesizer(
            Path.Combine(Directory.GetCurrentDirectory(), "kokoro.onnx"));
        var raw = await synth.SynthesizeAsync("Oh gosh, hello there!",
            KokoroSharp.KokoroVoiceManager.GetVoice("am_puck"));

        Console.WriteLine($"  bytes returned : {raw.Length}");
        Console.WriteLine($"  first 16 hex   : {Convert.ToHexString(raw.AsSpan(0, Math.Min(16, raw.Length)))}");
        Console.WriteLine($"  first 4 ascii  : '{System.Text.Encoding.ASCII.GetString(raw, 0, Math.Min(4, raw.Length))}'");

        var viaSave = Path.Combine(Path.GetTempPath(), "kokoro_savefile.wav");
        synth.SaveAudioToFile(raw, viaSave);
        var saved = await File.ReadAllBytesAsync(viaSave);
        Console.WriteLine($"  SaveAudioToFile: {saved.Length} bytes, first 4 = '{System.Text.Encoding.ASCII.GetString(saved, 0, 4)}'");
        Console.WriteLine($"  header delta   : {saved.Length - raw.Length} bytes added");
        Console.WriteLine($"  saved to       : {viaSave}");
        return 0;
    }

    // One line per character, so their voices can be compared back to back.
    (Character C, string Line)[] lines =
    [
        (CharacterLibrary.N,    "Oh gosh, hi Aubs! I'm N, and I'm so happy you're here!"),
        (CharacterLibrary.Uzi,  "Ugh, finally. Took you long enough."),
        (CharacterLibrary.V,    "Well well well. Look who decided to show up."),
        (CharacterLibrary.J,    "You are four minutes behind schedule. Noted."),
        (CharacterLibrary.Doll, "...hello."),
    ];

    foreach (var (c, line) in lines)
    {
        Console.WriteLine($"  [{c.Name,-5}] ({c.Voice}) \"{line}\"");
        var t = System.Diagnostics.Stopwatch.StartNew();
        await voice.SpeakAsync(line, c);
        Console.WriteLine($"          synth+upload+play issued in {t.Elapsed.TotalSeconds:F2}s");
        // Playback is fire-and-forget on the daemon, so pace the lines by hand.
        await Task.Delay(4000);
    }

    Console.WriteLine("\nDone.");
    return 0;
}

if (args.Contains("--reflect-cert"))
{
    foreach (var c in typeof(SIPSorcery.Net.RTCCertificate2).GetConstructors())
        Console.WriteLine("  ctor(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
    foreach (var p in typeof(SIPSorcery.Net.RTCCertificate2).GetProperties())
        Console.WriteLine("  prop " + p.PropertyType.Name + " " + p.Name);
    foreach (var f in typeof(SIPSorcery.Net.RTCCertificate2).GetFields())
        Console.WriteLine("  field " + f.FieldType.Name + " " + f.Name);
    return 0;
}

if (args.Contains("--reflect-sherpa"))
{
    var sherpa = typeof(SherpaOnnx.OfflineRecognizer).Assembly;
    var filter = args.FirstOrDefault(a => a.StartsWith("--type="))?["--type=".Length..];
    foreach (var t in sherpa.GetExportedTypes().OrderBy(t => t.Name))
    {
        if (filter is not null && !t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
        Console.WriteLine($"\n=== {t.FullName} ===");
        foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            Console.WriteLine($"  field {f.FieldType.Name} {f.Name}");
        foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
        foreach (var c in t.GetConstructors())
            Console.WriteLine($"  ctor({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                                     | System.Reflection.BindingFlags.DeclaredOnly))
        {
            if (m.IsSpecialName) continue;
            Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
        }
    }
    return 0;
}

if (args.Contains("--reflect-tts"))
{
    var asm = typeof(KokoroSharp.KokoroTTS).Assembly;
    foreach (var t in asm.GetExportedTypes().OrderBy(t => t.Name))
    {
        var members = new List<string>();
        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                                     | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
        {
            if (m.IsSpecialName) continue;
            var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            members.Add($"  {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({ps})");
        }
        foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                                        | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            members.Add($"  prop {p.PropertyType.Name} {p.Name}");
        foreach (var e in t.GetEvents(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                                    | System.Reflection.BindingFlags.DeclaredOnly))
            members.Add($"  event {e.EventHandlerType?.Name} {e.Name}");

        if (members.Count == 0) continue;
        Console.WriteLine($"\n=== {t.FullName} ===");
        foreach (var m in members) Console.WriteLine(m);
    }
    return 0;
}

if (args.Contains("--test-speech"))
{
    // Real model output. Roleplay models narrate action inline and the synthesiser
    // must never read those markers aloud.
    (string Raw, string ExpectSpoken, int ExpectActions)[] cases =
    [
        ("*Head swivels to face Aubriella with a big smile* Oh gosh, yes!",
         "Oh gosh, yes!", 1),
        ("I just love all the colorful ponies!", "I just love all the colorful ponies!", 0),
        ("*Antennas wave slightly as if excitedly swishing back and forth*", "", 1),
        ("Ooh, *leaning forward* I love that!", "Ooh, I love that!", 1),
        ("_torso rotates_ Hello there.", "Hello there.", 1),
        ("*unclosed action that runs on", "", 1),
        ("", "", 0),
        ("Wait, really?! *gasps*", "Wait, really?!", 1),
    ];

    var speechPass = 0;
    foreach (var (raw, expectSpoken, expectActions) in cases)
    {
        var (say, actions) = SpokenText.Split(raw);
        var ok = say == expectSpoken && actions.Length == expectActions;
        if (ok) speechPass++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] \"{raw}\"");
        if (!ok)
            Console.WriteLine($"           got \"{say}\" +{actions.Length} actions, expected \"{expectSpoken}\" +{expectActions}");
    }

    // Nothing sayable must ever reach the synthesiser.
    var sayableOk = !SpokenText.IsSayable("") && !SpokenText.IsSayable("  ,  ")
                    && SpokenText.IsSayable("Hi");
    Console.WriteLine($"  [{(sayableOk ? "PASS" : "FAIL")}] IsSayable gate");

    Console.WriteLine($"\n{speechPass}/{cases.Length} split cases passed");

    // Sentence boundaries decide where speech is cut into clips. An ellipsis is a
    // pause inside a phrase, and splitting on it puts an audible gap mid-sentence.
    Console.WriteLine("\n  sentence-boundary detection:");
    (string Text, bool ShouldSplit)[] boundaryCases =
    [
        ("Hello there. ", true),
        ("It's so... ", false),
        ("It's so... sparkly! ", true),
        ("Wait, really?! ", true),
        ("That costs 3.5 dollars", false),
        ("Hmm... ", false),
        ("No split yet", false),
    ];

    var boundaryPass = 0;
    foreach (var (text, shouldSplit) in boundaryCases)
    {
        var cut = RoseBrain.LastSentenceEnd(text);
        var ok = (cut > 0) == shouldSplit;
        if (ok) boundaryPass++;
        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] \"{text}\" -> cut at {cut}, expected {(shouldSplit ? "a split" : "no split")}");
    }
    Console.WriteLine($"\n{boundaryPass}/{boundaryCases.Length} boundary cases passed");

    // Switching character must need an explicit request. Merely TALKING about a
    // character must not silently change who Rose is mid-conversation.
    Console.WriteLine("\n  switch-intent gate (current = N):");
    (string Said, string? Expect)[] switchCases =
    [
        ("can you be J", "J"),
        ("switch to Uzi", "Uzi"),
        ("I want to talk to V", "V"),
        ("talk like Doll", "Doll"),
        ("pretend to be Khan", "Khan"),
        ("V is so funny", null),          // talking ABOUT V
        ("J was mean to Uzi", null),      // recounting the show
        ("I like N the best", null),      // N is already current anyway
        ("can you be N", null),           // already N - no pointless re-greet
        ("what do you think", null),

        // Recognition mishearings, measured from --test-names. These must resolve.
        ("can you be using", "Uzi"),
        ("can you be gone", "Khan"),
        ("can you be dull", "Doll"),
        ("can you be sad", "Thad"),
        ("switch to using", "Uzi"),

        // ...but the same words must NOT hijack ordinary sentences. Several of them
        // are common English words, so this is the risk the slot rule exists for.
        ("I want an ice cream", null),
        ("I want to be gone from here", null),
        ("can you be quiet", null),
        ("I want a dull knife for the craft", null),
        ("be careful", null),
    ];

    var switchPass = 0;
    foreach (var (said, expect) in switchCases)
    {
        var got = RoseConversation.FindSwitchRequest(said, CharacterLibrary.N)?.Name;
        var ok = got == expect;
        if (ok) switchPass++;
        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] \"{said}\" -> {got ?? "(stay)"}  expected {expect ?? "(stay)"}");
    }
    Console.WriteLine($"\n{switchPass}/{switchCases.Length} switch cases passed");

    var allOk = speechPass == cases.Length && sayableOk
                && switchPass == switchCases.Length
                && boundaryPass == boundaryCases.Length;
    return allOk ? 0 : 1;
}

if (args.Contains("--test-characters"))
{
    // Phrases Aubs would actually say, including how speech-to-text tends to
    // mangle them. Expected null means "no switch requested".
    (string Said, string? Expect)[] cases =
    [
        ("N", "N"), ("uzi", "Uzi"), ("Uzi Doorman", "Uzi"),
        ("switch to V", "V"), ("can you be J", "J"), ("talk like Doll", "Doll"),
        ("serial designation n", "N"), ("khan", "Khan"), ("uzi's dad", "Khan"),
        ("thad", "Thad"), ("VEE", "V"), ("jay", "J"),
        ("hello there", null), ("", null), ("   ", null),
    ];

    var pass = 0;
    foreach (var (said, expect) in cases)
    {
        var got = CharacterLibrary.Find(said)?.Name;
        var ok = got == expect;
        if (ok) pass++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] \"{said}\" -> {got ?? "(none)"}  expected {expect ?? "(none)"}");
    }
    Console.WriteLine($"\n{pass}/{cases.Length} passed");
    return pass == cases.Length ? 0 : 1;
}

var host = args.Length > 0 ? args[0] : "192.168.1.170";

using var rose = new ReachyMiniClient(host);
Console.WriteLine($"Connecting to {rose.BaseAddress}\n");

var status = await rose.GetStatusAsync();
if (status is null) { Console.WriteLine("No response from daemon."); return 1; }

Console.WriteLine($"  robot      : {status.RobotName}  (v{status.Version})");
Console.WriteLine($"  state      : {status.State}   wireless: {status.WirelessVersion}");
Console.WriteLine($"  ip / hwid  : {status.WlanIp}  {status.HardwareId}");

if (status.BackendStatus?.ControlLoopStats is { } cls)
    Console.WriteLine($"  loop       : {cls.MeanFrequency:F1} Hz, {cls.ErrorCount} errors");

var motors = await rose.GetMotorStatusAsync();
var bodyYaw = await rose.GetBodyYawAsync();
var head = await rose.GetHeadPoseAsync();
var vol = await rose.GetVolumeAsync();
var media = await rose.GetMediaStatusAsync();
var doa = await rose.GetDoaAsync();
var face = await rose.GetFaceAsync();

Console.WriteLine($"  motors     : {motors?.Mode}");
Console.WriteLine($"  body_yaw   : {bodyYaw:F3} rad");
Console.WriteLine($"  head yaw   : {head?.Yaw:F3} rad  (pitch {head?.Pitch:F3}, roll {head?.Roll:F3})");
Console.WriteLine($"  volume     : {vol?.Volume} on {vol?.Device}");
Console.WriteLine($"  media      : available={media?.Available} released={media?.Released}");
Console.WriteLine($"  doa        : {doa?.Angle:F3} rad, speech={doa?.SpeechDetected}");
Console.WriteLine($"  face       : detected={face?.Detected} x={face?.X:F3} y={face?.Y:F3}");

var sounds = await rose.ListSoundsAsync();
var files = sounds?.GetValueOrDefault("files") ?? [];
Console.WriteLine($"  sounds     : {files.Count} uploaded");

Console.WriteLine("\nSDK read path verified.");
return 0;
