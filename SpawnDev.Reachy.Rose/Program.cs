using Microsoft.Extensions.Logging;
using SpawnDev.Reachy;
using SpawnDev.Reachy.Rose;

// Rose - companion app for Aubs's Reachy Mini.
// Currently a read-only connectivity check while the SDK is built out.

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
