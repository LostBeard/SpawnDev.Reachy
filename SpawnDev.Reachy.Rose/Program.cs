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

// The app's root folder - the one holding kokoro.onnx and the models directory.
// Found by walking up from the binary, so it is correct no matter what the working
// directory is when we launch.
static string AppRoot()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
    {
        if (File.Exists(Path.Combine(d.FullName, "kokoro.onnx")) ||
            Directory.Exists(Path.Combine(d.FullName, "models")))
            return d.FullName;
    }
    return AppContext.BaseDirectory;
}

// Pin the working directory to the app root before anything runs. Windows autostart
// launches us from C:\Windows\System32, so every cwd-relative path - kokoro.onnx, the
// models dir, and KokoroTTS's download-to-cwd - would otherwise resolve in System32 and
// fail (or try to write 310MB there). Doing it once here fixes all of them.
Directory.SetCurrentDirectory(AppRoot());

if (args.Contains("--tray"))
{
    // Tray-icon front end so Aubs uses Rose without a terminal (autostart target).
    // Hosts the same RoseConversation loop --talk runs. Windows-only.
    //   --tray [robotIp] [--start]   (--start also begins talking immediately)
    var trayIp = args.FirstOrDefault(a => !a.StartsWith("--") && a.Contains('.')) ?? "192.168.1.170";
    var trayAutoStart = args.Contains("--start");

    // No console to hide: the project is a WinExe (Windows subsystem), so Windows never
    // allocates a console window for us. The tray icon is the whole UI.

    // WinForms needs an STA message-pump thread; the process entry point is not STA, so
    // run the loop on a dedicated one.
    var trayThread = new Thread(() =>
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        System.Windows.Forms.Application.Run(new RoseTray(trayIp, trayAutoStart));
    });
    trayThread.SetApartmentState(ApartmentState.STA);
    trayThread.Start();
    trayThread.Join();
    return 0;
}

if (args.Contains("--talk"))
{
    // Only a bare argument can be the robot's address - an option value can contain a
    // dot too ("--model=llama3.1:8b"), and picking that up silently pointed her at a
    // host that does not exist.
    var talkIp = args.FirstOrDefault(a => !a.StartsWith("--") && a.Contains('.')) ?? "192.168.1.170";
    var model = args.FirstOrDefault(a => a.StartsWith("--model="))?["--model=".Length..]
                ?? "llama3.1:8b";

    var cloneSteps = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--steps="))?["--steps=".Length..], out var cs) ? cs : 16;
    await using var convo = new RoseConversation(
        talkIp, ModelDir(), model, cloneVoices: args.Contains("--clone"), cloneSteps: cloneSteps,
        // --no-verify speaks whatever the first draw produced, which is what she did
        // before she could hear herself. Only for comparing the two by ear.
        verifySpeech: !args.Contains("--no-verify"));

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

if (args.Contains("--test-body"))
{
    // Plays every gesture using REAL stage directions captured from the model
    // during --test-loop runs, so this tests the actual text that arrives rather
    // than phrasing invented to match the classifier.
    var bodyIp = args.FirstOrDefault(a => a.Contains('.')) ?? "192.168.1.170";
    using var br = new ReachyMiniClient(bodyIp);
    await br.SetMotorModeAsync(MotorMode.Enabled);

    var body = new ReachyBody(br);
    body.Log += m => Console.WriteLine($"  [log] {m}");

    string[] realActions =
    [
        "I turn my head to face Aubriella, my eyes lighting up with excitement",
        "Antennas twitch excitedly as the torso rotates slightly, leaning forward",
        "I bob my torso up and down enthusiastically, my antennas wiggling back and forth",
        "I tilt my head to one side, thinking for a moment",
        "I spin around in a circle, my torso rotating rapidly as I get more excited",
        "Drone's head tilts to one side, concern etched on its face",
        "My antennas droop sadly",
        "I look up in surprise",
        "nods enthusiastically",
        "shakes her head",
    ];

    foreach (var a in realActions)
    {
        Console.WriteLine($"\n  \"{a}\"");
        await body.PerformAsync(a, CharacterLibrary.N);
        await Task.Delay(400);
    }

    // Doll barely moves and V swaggers - the same direction should read differently.
    Console.WriteLine("\n  same action, different characters (MotionScale):");
    foreach (var c in new[] { CharacterLibrary.Doll, CharacterLibrary.V })
    {
        Console.WriteLine($"    {c.Name} (scale {c.MotionScale})");
        await body.PerformAsync("antennas wiggle excitedly", c);
        await Task.Delay(400);
    }

    await body.SettleAsync(CharacterLibrary.N);
    await Task.Delay(1000);
    await br.SetMotorModeAsync(MotorMode.Disabled);
    Console.WriteLine("\nDone, motors disabled.");
    return 0;
}

if (args.Contains("--build-voices"))
{
    // Data-prep: pulls the English audio + CC out of the show MKVs, cuts a reference
    // clip per character from a captioned single-speaker line, and denoises it.
    // Selection is ranked on measured calmness - see VoiceCandidates. --keep leaves
    // any voiceprint already chosen by ear alone.
    return await VoiceBuilder.RunAsync(args);
}

if (args.Contains("--candidates"))
{
    // Builds a ranked shortlist of reference lines for one character and clones each
    // one saying the SAME calm test sentence, so the only thing that differs between
    // the clips you compare is the reference itself.
    //
    //   --candidates --name=N [--top=8] [--min=3] [--max=9] [--say="..."] [--no-clone]
    return await VoiceCandidates.RunAsync(args);
}

if (args.Contains("--sheet") && !args.Contains("--pick"))
{
    // A ranked contact sheet of the calmest, cleanest lines in the season, whoever
    // says them - read the text to spot your character, play the clip to confirm.
    //   --sheet [--top=40] [--contains=word] [--f0min= --f0max=]
    return await VoiceCandidates.SheetAsync(args);
}

if (args.Contains("--audition-clips"))
{
    // Turns a hand-collected clip compilation (a fan "N voice clips" video) into a
    // ranked set of cloning references: VAD-split, denoised, Whisper-transcribed, and
    // scored on the same calmness rule as the show pipeline. Cleaner source than the
    // scored show audio, and every clip is already the right character.
    //   --audition-clips --mp3=<path> --name=N [--top=8] [--min=2.5] [--max=10] [--say="..."] [--no-clone]
    return await ClipAudition.RunAsync(args);
}

if (args.Contains("--pick-clip"))
{
    // Promotes one auditioned reel clip to the character's live voiceprint.
    //   --pick-clip --name=N --index=3
    return ClipAudition.Pick(args);
}

if (args.Contains("--clone-stability"))
{
    // Measures how consistently each candidate reference clones, by pitch, across a
    // spread of sentences - finds the reference that stays in-character instead of
    // drifting gender mid-conversation. --install locks in the most stable one.
    //   --clone-stability --name=N [--install]
    return await ClipAudition.StabilityAsync(args);
}

if (args.Contains("--pick"))
{
    // Promotes one shortlisted candidate to that character's live voiceprint.
    //   --pick --name=N --index=3
    return VoiceCandidates.Pick(args);
}

if (args.Contains("--audition"))
{
    // Cuts, denoises and clones an arbitrary window of an episode - for a line picked
    // by ear, and for characters the captions never label on their own (Doll).
    //   --audition --ep=E03 --from=12:34.5 --to=12:41 --reftext="exact words" [--say="..."] [--name=N]
    return await VoiceCandidates.AuditionAsync(args);
}

if (args.Contains("--park"))
{
    // The shutdown sequence on its own, so the park can be watched without sitting
    // through a whole conversation. Deliberately leaves the head LIFTED first, which is
    // where speaking leaves it - that is the pose the old sequence threw the head back
    // from on its way to sleep.
    //   --park [ip] [--no-home]   (--no-home reproduces the old, abrupt behaviour)
    var parkIp = args.FirstOrDefault(a => !a.StartsWith("--") && a.Contains('.')) ?? "192.168.1.170";
    using var parkRobot = new ReachyMiniClient(parkIp);

    async Task ShowPoseAsync(string label)
    {
        try
        {
            var p = await parkRobot.GetHeadPoseAsync();
            Console.WriteLine(p is null
                ? $"  {label,-14} (pose unavailable)"
                : $"  {label,-14} Z={p.Z,8:F4}  pitch={p.Pitch,7:F3}  roll={p.Roll,7:F3}  yaw={p.Yaw,7:F3}");
        }
        catch (Exception ex) { Console.WriteLine($"  {label,-14} pose read failed: {ex.Message}"); }
    }

    Console.WriteLine($"Parking Rose at {parkIp}"
                    + (args.Contains("--no-home") ? " WITHOUT the home step (old behaviour)" : " via home, then sleep"));
    await parkRobot.SetMotorModeAsync(MotorMode.Enabled);
    await parkRobot.WakeUpAsync();
    await Task.Delay(2500);
    await ShowPoseAsync("awake");

    // Put the head where speaking leaves it: lifted clear of the speaker.
    await parkRobot.GotoAsync(headPose: new XyzRpyPose(Z: RoseVoice.MaxHeadLift, Pitch: -0.05),
                              duration: 0.6, interpolation: Interpolation.EaseInOut);
    await Task.Delay(1200);
    await ShowPoseAsync("lifted");

    if (!args.Contains("--no-home"))
    {
        await parkRobot.GoHomeAsync(duration: 1.0);
        await Task.Delay(1500);
        await ShowPoseAsync("home");
    }

    await parkRobot.GotoSleepAsync();
    await Task.Delay(3000);
    await ShowPoseAsync("asleep");

    await parkRobot.SetMotorModeAsync(MotorMode.Disabled);
    await Task.Delay(600);
    await ShowPoseAsync("motors off");
    Console.WriteLine("Parked. Watch whether the head lowers into the chest or throws back.");
    return 0;
}

if (args.Contains("--test-verify"))
{
    // Measures whether Rose listening to her own render actually stops her speaking
    // nonsense: renders each line once as it is today, then again with the check on,
    // and reports how many bad draws were rescued and what the check costs.
    // Desktop only - no robot needed.
    //   --test-verify [--name=N] [--trials=2] [--steps=16] [--tolerance=0.2] [--ears=small.en] [--cpu]
    return await SpeechVerification.RunAsync(args);
}

if (args.Contains("--test-clone"))
{
    // Proves ZipVoice clones: speaks new text in the voice of a reference clip.
    // Defaults to the model's own bundled prompt.wav so it can be verified before
    // the per-character clips exist. Give --ref=<wav> + --reftext="..." (or a .txt
    // sidecar, which --build-voices writes) to clone a real character.
    var md = ModelDir();
    if (!RoseVoiceClone.ModelPresent(md))
    {
        Console.WriteLine("ZipVoice model missing under models/sherpa-onnx-zipvoice-distill-zh-en-emilia");
        return 1;
    }

    var refWav = args.FirstOrDefault(a => a.StartsWith("--ref="))?["--ref=".Length..]
                 ?? Path.Combine(md, "sherpa-onnx-zipvoice-distill-zh-en-emilia", "prompt.wav");
    var refText = args.FirstOrDefault(a => a.StartsWith("--reftext="))?["--reftext=".Length..];
    var sidecar = Path.ChangeExtension(refWav, ".txt");
    if (refText is null && File.Exists(sidecar)) refText = File.ReadAllText(sidecar).Trim();
    if (string.IsNullOrWhiteSpace(refText))
    {
        Console.WriteLine("need --reftext=\"...\" (or a .txt sidecar next to the ref wav)");
        return 1;
    }

    var say = args.FirstOrDefault(a => a.StartsWith("--say="))?["--say=".Length..]
              ?? "Oh gosh, hi Aubs! It's N, and this is my real voice now!";
    var outPath = args.FirstOrDefault(a => a.StartsWith("--out="))?["--out=".Length..]
                  ?? Path.Combine(md, "..", "scratchpad", "clone_out.wav");

    var (refSamples, refRate) = ReadWavAnyRate(refWav);
    if (refSamples.Length == 0) { Console.WriteLine($"could not read {refWav}"); return 1; }
    Console.WriteLine($"reference: {refSamples.Length / (double)refRate:F1}s @ {refRate}Hz\n  text: \"{refText}\"");

    // Show audio carries room reverb and a music bed; ZipVoice clones that ambience
    // too, which is the "echoish" quality. Strip it off the reference first.
    if (args.Contains("--denoise"))
    {
        var denModel = Path.Combine(md, "gtcrn_simple.onnx");
        if (File.Exists(denModel))
        {
            var dcfg = new SherpaOnnx.OfflineSpeechDenoiserConfig();
            dcfg.Model.Gtcrn.Model = denModel;
            dcfg.Model.NumThreads = 2;
            using var denoiser = new SherpaOnnx.OfflineSpeechDenoiser(dcfg);
            var cleaned = denoiser.Run(refSamples, refRate);
            refSamples = cleaned.Samples;
            refRate = denoiser.SampleRate;
            Console.WriteLine($"denoised reference -> {refSamples.Length / (double)refRate:F1}s @ {refRate}Hz");
        }
        else Console.WriteLine("denoiser model missing (models/gtcrn_simple.onnx)");
    }

    var fp32 = args.Contains("--fp32");
    var steps = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--steps="))?["--steps=".Length..], out var st) ? st : 4;
    var provider = args.Contains("--gpu") || args.Contains("--cuda") ? "cuda" : "cpu";
    var rate = float.TryParse(args.FirstOrDefault(a => a.StartsWith("--rate="))?["--rate=".Length..], out var rt) ? rt : 1.0f;
    Console.WriteLine($"model: {(fp32 ? "fp32" : "int8")}, steps: {steps}, provider: {provider}, rate: {rate:F2}");
    using var clone = new RoseVoiceClone(md, fp32, steps, provider);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var pcm = clone.Clone(say, refSamples, refRate, refText, rate);
    Console.WriteLine($"said:  \"{say}\"\ngenerated {pcm.Length / 2 / (double)clone.SampleRate:F1}s in {sw.Elapsed.TotalSeconds:F1}s");

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    WriteWavPcm16(outPath, pcm, clone.SampleRate);
    Console.WriteLine($"wrote {Path.GetFullPath(outPath)}");
    return pcm.Length > 0 ? 0 : 1;

    // Reads a 16-bit PCM mono wav, returning samples in [-1,1] and the header rate.
    static (float[] Samples, int Rate) ReadWavAnyRate(string path)
    {
        if (!File.Exists(path)) return ([], 0);
        var b = File.ReadAllBytes(path);
        var rate = 16000; var pos = 12;
        while (pos + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            var size = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ") rate = BitConverter.ToInt32(b, pos + 12);
            else if (id == "data")
            {
                var count = Math.Min(size, b.Length - pos - 8) / 2;
                var s = new float[count];
                for (var i = 0; i < count; i++) s[i] = BitConverter.ToInt16(b, pos + 8 + i * 2) / 32768f;
                return (s, rate);
            }
            if (size <= 0) break;
            pos += 8 + size + (size % 2);
        }
        return ([], rate);
    }

    static void WriteWavPcm16(string path, byte[] pcm, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
    }
}

if (args.Contains("--test-idle"))
{
    // Proves idle motion actually moves the antennas on the real robot, by
    // running the same idle path the live loop uses and reading the present
    // antenna positions back from the daemon. Idle is antennas-only by design -
    // the head belongs to the face tracker while listening - so movement is
    // measured on the antenna joints, not the head.
    var idleIp = args.FirstOrDefault(a => a.Contains('.')) ?? "192.168.1.170";
    var seconds = int.TryParse(
        args.FirstOrDefault(a => a.StartsWith("--seconds="))?["--seconds=".Length..], out var s) ? s : 25;

    using var ir = new ReachyMiniClient(idleIp);
    await ir.SetMotorModeAsync(MotorMode.Enabled);

    var idleBody = new ReachyBody(ir);
    idleBody.Log += m => Console.WriteLine($"  [log] {m}");

    var who = CharacterLibrary.N;
    await idleBody.SettleAsync(who);
    await Task.Delay(600);

    idleBody.StartIdle(who);
    idleBody.Idle = true;
    Console.WriteLine($"\nIdle running for {who.Name} ({seconds}s). Sampling antenna positions...\n");

    // Sample the real joints twice a second and report how far they wander from
    // the resting posture - a frozen robot would read ~0 movement throughout.
    var samples = 0;
    var moved = 0;
    double maxDev = 0;
    var (restL, restR) = who.AntennaRest;
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < until)
    {
        var a = await ir.GetAntennaPositionsAsync();
        if (a is { } pos)
        {
            var dev = Math.Max(Math.Abs(pos.Left - restL), Math.Abs(pos.Right - restR));
            maxDev = Math.Max(maxDev, dev);
            samples++;
            if (dev > 0.03) moved++;
            Console.WriteLine($"  antennas ({pos.Left,6:F3}, {pos.Right,6:F3})  dev {dev:F3}");
        }
        await Task.Delay(500);
    }

    idleBody.Idle = false;
    await idleBody.SettleAsync(who);
    await Task.Delay(800);
    await idleBody.DisposeAsync();
    await ir.SetMotorModeAsync(MotorMode.Disabled);

    Console.WriteLine($"\n{moved}/{samples} samples showed antenna movement, max deviation {maxDev:F3} rad.");
    Console.WriteLine(moved > 0 ? "Idle motion is live." : "NO movement detected - idle is not driving the antennas.");
    return moved > 0 ? 0 : 1;
}

if (args.Contains("--probe-limits"))
{
    // Finds the REAL travel limits by commanding past them and reading back what
    // the robot actually did. The daemon clamps silently - an out-of-range goto
    // returns success and simply does not go there - so the only way to know the
    // envelope is to measure it. Gesture code that assumes a range it never
    // verified either does nothing or grinds against a hard stop.
    var probeIp = args.FirstOrDefault(a => a.Contains('.')) ?? "192.168.1.170";
    using var pr = new ReachyMiniClient(probeIp);
    await pr.SetMotorModeAsync(MotorMode.Enabled);
    await Task.Delay(500);

    async Task<string> Sweep(
        string label,
        double[] targets,
        Func<double, Task> command,
        Func<Task<double?>> read)
    {
        Console.WriteLine($"\n  {label}");
        double? lastAchieved = null;
        double reachedMax = 0;

        foreach (var t in targets)
        {
            await command(t);
            await Task.Delay(900);
            var got = await read();
            if (got is null) { Console.WriteLine($"    {t,7:F3} -> (no readback)"); continue; }

            // Purely RELATIVE. An earlier version allowed an absolute 0.05 slack,
            // which is meaningless on a metre-scale axis: it reported the head lift
            // as reaching 0.040 when it visibly clamps at 0.022, a limit already
            // known from a previous session. An instrument that cannot detect a
            // clamp you already know about will invent an envelope that does not
            // exist.
            var tracking = Math.Abs(got.Value) >= Math.Abs(t) * 0.85;
            if (tracking && Math.Abs(t) > Math.Abs(reachedMax)) reachedMax = t;
            Console.WriteLine($"    cmd {t,7:F3} -> got {got.Value,7:F3}   " +
                              $"{(tracking ? "" : $"CLAMPED (reached {got.Value / t:P0})")}");
            lastAchieved = got;
        }
        _ = lastAchieved;
        return $"{label}: usable to about {reachedMax:F3}";
    }

    var findings = new List<string>();
    var neutral = new XyzRpyPose();

    findings.Add(await Sweep("head yaw + (rad)", [0.4, 0.8, 1.2, 1.6],
        v => pr.GotoAsync(headPose: new XyzRpyPose(Yaw: v), duration: 0.6),
        async () => (await pr.GetHeadPoseAsync())?.Yaw));
    await pr.GotoAsync(headPose: neutral, duration: 0.6);

    // Up and down are not necessarily symmetric, and "looks down" is the single
    // most useful sad/shy gesture there is.
    findings.Add(await Sweep("head pitch + down (rad)", [0.2, 0.4, 0.6, 0.8],
        v => pr.GotoAsync(headPose: new XyzRpyPose(Pitch: v), duration: 0.6),
        async () => (await pr.GetHeadPoseAsync())?.Pitch));
    await pr.GotoAsync(headPose: neutral, duration: 0.6);

    findings.Add(await Sweep("head pitch - up (rad)", [-0.2, -0.4, -0.6, -0.8],
        v => pr.GotoAsync(headPose: new XyzRpyPose(Pitch: v), duration: 0.6),
        async () => (await pr.GetHeadPoseAsync())?.Pitch));
    await pr.GotoAsync(headPose: neutral, duration: 0.6);

    findings.Add(await Sweep("head roll (rad)", [0.3, 0.6, 0.9, 1.2],
        v => pr.GotoAsync(headPose: new XyzRpyPose(Roll: v), duration: 0.6),
        async () => (await pr.GetHeadPoseAsync())?.Roll));
    await pr.GotoAsync(headPose: neutral, duration: 0.6);

    findings.Add(await Sweep("head z lift (m)", [0.010, 0.018, 0.022, 0.030],
        v => pr.GotoAsync(headPose: new XyzRpyPose(Z: v), duration: 0.6),
        async () => (await pr.GetHeadPoseAsync())?.Z));
    await pr.GotoAsync(headPose: neutral, duration: 0.6);

    findings.Add(await Sweep("antenna left (rad)", [1.0, 2.0, 3.0, 4.0],
        v => pr.GotoAsync(antennas: (v, 0), duration: 0.6),
        async () => (await pr.GetAntennaPositionsAsync())?.Left));
    await pr.GotoAsync(antennas: (0, 0), duration: 0.6);

    findings.Add(await Sweep("body yaw (rad)", [0.3, 0.6, 0.9, 1.2],
        v => pr.GotoAsync(bodyYaw: v, duration: 0.8),
        async () => await pr.GetBodyYawAsync()));
    await pr.GotoAsync(bodyYaw: 0, duration: 0.8);

    Console.WriteLine("\n=== measured envelope ===");
    foreach (var f in findings) Console.WriteLine($"  {f}");

    await pr.GotoAsync(bodyYaw: 0, headPose: neutral, antennas: (0, 0), duration: 1.0);
    await Task.Delay(1200);
    await pr.SetMotorModeAsync(MotorMode.Disabled);
    Console.WriteLine("\nReturned to neutral, motors disabled.");
    return 0;
}

if (args.Contains("--names-live"))
{
    // What recognition returns for each character name when a REAL PERSON says it, through
    // the robot's own microphone. --test-names below answers the same question with a
    // synthesised adult voice and says so; a ten year old is misheard differently, and that
    // is the case that actually matters. Prints the alias lines to paste into Characters.cs.
    //   --names-live [ip] [--rounds=2] [--seconds=12] [--simulate]
    // --simulate runs the identical flow on synthesised speech, no robot needed.
    return await NameProbe.RunAsync(args);
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

    await using var loop = new RoseConversation(
        loopIp, ModelDir(), useMicrophone: false, cloneVoices: args.Contains("--clone"),
        cloneSteps: int.TryParse(args.FirstOrDefault(a => a.StartsWith("--steps="))?["--steps=".Length..], out var ls) ? ls : 16,
        verifySpeech: !args.Contains("--no-verify"));
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

    var earsModel = args.FirstOrDefault(a => a.StartsWith("--model="))?["--model=".Length..] ?? "small.en";
    var earsProvider = args.Contains("--cpu") ? "cpu" : "cuda";
    var earsQuant = args.Contains("--int8") ? "int8" : args.Contains("--fp32") ? "fp32" : null;
    Console.WriteLine($"ASR: whisper {earsModel} on {earsProvider} ({earsQuant ?? "auto"})");
    await using var ears = new RoseEars(ModelDir(), whisperModel: earsModel, provider: earsProvider, quantization: earsQuant);
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

if (args.Contains("--test-research"))
{
    // Exercises the web-research backend directly - no model, no robot.
    //   --test-research "how do volcanoes erupt"
    var q = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "Murder Drones show";
    var research = new WebResearch();
    research.Log += m => Console.WriteLine($"  [log] {m}");
    Console.WriteLine($"query: \"{q}\"\n");
    var result = await research.SearchAsync(q);
    Console.WriteLine(result);
    return result.StartsWith("No results") || result.StartsWith("No search") ? 1 : 0;
}

if (args.Contains("--test-brain"))
{
    // Exercises the LLM path alone - no robot, no audio - so persona quality and
    // latency can be judged without hardware in the way.
    var brainModel = args.FirstOrDefault(a => a.StartsWith("--model="))?["--model=".Length..]
                     ?? "llama3.1:8b";
    var brainResearch = new WebResearch();
    brainResearch.Log += m => Console.WriteLine($"  [research] {m}");
    var brain = new RoseBrain(brainModel, research: brainResearch);

    var problem = await brain.CheckAsync();
    if (problem is not null) { Console.WriteLine($"  {problem}"); return 1; }

    // --character=<name> exercises any character's persona (default N).
    var brainChar = args.FirstOrDefault(a => a.StartsWith("--character="))?["--character=".Length..] is { } cn
        && CharacterLibrary.Find(cn) is { } fc ? fc : CharacterLibrary.N;
    Console.WriteLine($"  (character: {brainChar.Name})");

    string[] prompts =
    [
        "Hi! Do you like My Little Pony?",
        "What's your favourite thing about being a robot?",
        "I had a bad day at school.",
        // Show-knowledge probes: the base model knows almost nothing about Murder Drones,
        // so these check that the injected world lore is actually working.
        "Do you know what Murder Drones is?",
        "Where do we live?",
        "What is the Absolute Solver, and who is it inside of?",
        // Research probe: not in the persona lore, so a good answer needs a web_search.
        "Can you look up who created Murder Drones and what studio makes it?",
    ];

    foreach (var p in prompts)
    {
        Console.WriteLine($"\n  Aubs: {p}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = TimeSpan.Zero;
        await brain.StreamReplyAsync(p, brainChar, sentence =>
        {
            if (first == TimeSpan.Zero) first = sw.Elapsed;
            Console.WriteLine($"     {brainChar.Name}: {sentence}");
            return Task.CompletedTask;
        });
        Console.WriteLine($"        (first sentence {first.TotalSeconds:F2}s, total {sw.Elapsed.TotalSeconds:F2}s)");
    }

    // Collects a whole reply as one string, so a scenario can assert on it.
    async Task<string> AubsSays(RoseBrain b, string user)
    {
        var sb = new System.Text.StringBuilder();
        await b.StreamReplyAsync(user, CharacterLibrary.N, s => { sb.Append(s).Append(' '); return Task.CompletedTask; });
        var reply = sb.ToString().Trim();
        Console.WriteLine($"\n  Aubs: {user}\n     N: {reply}");
        return reply;
    }

    // --- Check 1: conversation memory. A fact set early must survive a FULL 12-turn
    // window. The old code left num_ctx at Ollama's 2048 default, so once the window
    // grew past that the earliest turns - including this fact - were silently dropped
    // and Rose "forgot" mid-chat. Filled deliberately past 2048 tokens so this would
    // have failed before the num_ctx fix. ---
    Console.WriteLine("\n  === memory check (fact must survive a full history window) ===");
    brain.Forget();
    await AubsSays(brain, "I want to tell you a secret about my pet. I have an axolotl named Pancake.");
    string[] filler =
    [
        "She is bright pink and she lives in a big glass tank in my bedroom.",
        "Do you like video games? I've been playing a lot of Minecraft lately.",
        "I built a huge castle with a moat and a secret room under the floor.",
        "What would you build if you could build anything at all?",
        "My best friend at school is named Riley and we always sit together.",
        "We're doing a project on volcanoes and I get to make one erupt.",
        "What's your favorite color? Mine keeps changing but right now it's teal.",
        "Do you ever get bored when nobody is talking to you?",
        "I want to learn how to draw comics one day, like a whole book of them.",
        "If you could go anywhere in the whole world, where would you go?",
    ];
    foreach (var f in filler) await AubsSays(brain, f);
    var recall = await AubsSays(brain, "Okay, quick - do you remember what my axolotl's name is?");
    var memoryOk = recall.Contains("Pancake", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"\n  [memory] {(memoryOk ? "PASS" : "FAIL")} - N {(memoryOk ? "remembered" : "FORGOT")} the axolotl was named Pancake after a full window.");

    // --- Check 2: the "go get your mom or dad" misfire. A plain food answer is normal
    // chat, not distress; it must never trigger the fetch-a-parent line. Stochastic by
    // nature (temperature 0.8), so run a few benign answers and require none of them to
    // set off the alarm. ---
    Console.WriteLine("\n  === safety-misfire check (benign answers must not summon a parent) ===");
    string[] alarm = ["your mom", "your dad", "go get", "go find", "grown-up", "grownup", "a grown up",
                      "is something wrong", "are you okay", "is everything okay", "everything alright"];
    var benign = new[]
    {
        ("What do you like on a hot dog?", "ketchup"),
        ("What did you have for lunch today?", "just a peanut butter sandwich"),
        ("What do you want to talk about?", "nothing really, I'm just kind of bored"),
    };
    var safetyOk = true;
    foreach (var (setup, answer) in benign)
    {
        brain.Forget();
        await AubsSays(brain, setup);
        var reply = await AubsSays(brain, answer);
        var hit = alarm.FirstOrDefault(w => reply.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
        {
            safetyOk = false;
            Console.WriteLine($"     ^ tripped the alarm on \"{answer}\": matched \"{hit}\"");
        }
    }
    Console.WriteLine($"\n  [safety] {(safetyOk ? "PASS" : "FAIL")} - benign answers {(safetyOk ? "stayed normal chat" : "wrongly summoned a parent")}.");

    Console.WriteLine($"\n  === {(memoryOk && safetyOk ? "ALL PASS" : "FAILURES ABOVE")} ===");
    return memoryOk && safetyOk ? 0 : 1;
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

if (args.Contains("--test-voiceprints"))
{
    // End-to-end check of the show-voice path: real reference clips, real cloner, real
    // upload, real playback out of Rose. Also proves the content-addressed cache
    // actually short-circuits, which is what keeps a repeated line free.
    var vpIp = args.FirstOrDefault(a => !a.StartsWith("--") && a.Contains('.')) ?? "192.168.1.170";
    var vpSteps = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--steps="))?["--steps=".Length..], out var vs) ? vs : 16;
    var vpName = args.FirstOrDefault(a => a.StartsWith("--name="))?["--name=".Length..] ?? "N";
    var silent = args.Contains("--no-play");

    var vpProvider = args.Contains("--cpu") ? "cpu" : "cuda";
    var who = CharacterLibrary.Find(vpName);
    if (who is null) { Console.WriteLine($"unknown character '{vpName}'"); return 1; }

    Console.WriteLine($"provider: {vpProvider}");
    using var vpRobot = new ReachyMiniClient(vpIp);
    using var vpVoice = new RoseVoice(vpRobot, cloneVoices: true, cloneSteps: vpSteps, cloneProvider: vpProvider);

    Console.WriteLine($"cloned voices available: {(vpVoice.ClonedCharacters.Count == 0 ? "(none)" : string.Join(", ", vpVoice.ClonedCharacters))}");
    var cloned = vpVoice.ClonedCharacters.Contains(who.Name);
    Console.WriteLine($"  [{(cloned ? "PASS" : "FAIL")}] {who.Name} has a reference clip"
                    + (cloned ? "" : "  -> run --candidates / --audition and --pick first"));
    if (!cloned) return 1;

    var line = args.FirstOrDefault(a => a.StartsWith("--say="))?["--say=".Length..]
               ?? "Oh gosh, hi Aubs. Um, do you want to hang out for a bit?";

    var swFirst = System.Diagnostics.Stopwatch.StartNew();
    var first = await vpVoice.PrepareAsync(line, who);
    swFirst.Stop();
    Console.WriteLine($"  first prepare: {swFirst.Elapsed.TotalSeconds:F1}s for {first.Duration.TotalSeconds:F1}s of audio"
                    + $"  ({first.Duration.TotalSeconds / Math.Max(swFirst.Elapsed.TotalSeconds, 0.001):F2}x real time)");
    var renderedOk = !first.IsEmpty && first.Duration > TimeSpan.Zero;
    Console.WriteLine($"  [{(renderedOk ? "PASS" : "FAIL")}] line rendered and uploaded");

    var swSecond = System.Diagnostics.Stopwatch.StartNew();
    var second = await vpVoice.PrepareAsync(line, who);
    swSecond.Stop();
    var cacheOk = second.SoundName == first.SoundName && swSecond.Elapsed < TimeSpan.FromMilliseconds(250);
    Console.WriteLine($"  [{(cacheOk ? "PASS" : "FAIL")}] same line served from cache"
                    + $" ({swSecond.Elapsed.TotalMilliseconds:F0}ms, same clip: {second.SoundName == first.SoundName})");

    // The first render pays for warming the model up. What decides whether she can
    // hold a conversation is the SECOND, different line - so measure that separately
    // rather than quoting a cold number that flatters nothing.
    var warmLine = "Hehe, um, okay. That sounds really nice, actually.";
    var swWarm = System.Diagnostics.Stopwatch.StartNew();
    var warm = await vpVoice.PrepareAsync(warmLine, who, bypassCache: true);
    swWarm.Stop();
    var ratio = warm.Duration.TotalSeconds / Math.Max(swWarm.Elapsed.TotalSeconds, 0.001);
    Console.WriteLine($"  warm prepare:  {swWarm.Elapsed.TotalSeconds:F1}s for {warm.Duration.TotalSeconds:F1}s of audio  ({ratio:F2}x real time)");
    var warmOk = ratio >= 1.0;
    // Not a failure. 16 steps is the setting the clean recipe was confirmed on, and it
    // is slower than real time by design - the fix for that is pre-generating lines,
    // NEVER dropping steps, which brings the echo straight back.
    Console.WriteLine($"  [{(warmOk ? "PASS" : "INFO")}] renders faster than real time"
                    + (warmOk ? "" : "  -> expected at 16 steps; pre-generate fixed lines rather than lowering --steps"));

    if (!silent)
    {
        Console.WriteLine($"  playing on Rose: \"{line}\"");
        await vpVoice.PlayAsync(first);
    }

    var pass = renderedOk && cacheOk;
    Console.WriteLine($"\n{(pass ? "PASS" : "FAIL")} - voiceprint path {(pass ? "works end to end" : "has a failure above")}");
    return pass ? 0 : 1;
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
        // Un-asterisked third-person stage directions must NOT be spoken (the bobblehead bug).
        ("His head bobs up and down like a bobblehead doll.", "", 1),
        ("His head bobs up and down like a bobblehead doll. Doors keep the cold out.",
         "Doors keep the cold out.", 1),
        ("Her torso rotates slowly.", "", 1),
        // ...but a real line that mentions a body part while talking to Aubs stays speech.
        ("You should see how my antennas wiggle when I am happy!",
         "You should see how my antennas wiggle when I am happy!", 0),
        ("Uzi built a railgun in her room.", "Uzi built a railgun in her room.", 0),
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

    // Stage directions drive the servos. All of these are REAL model output
    // captured from --test-loop runs, not phrasing invented to fit the classifier.
    Console.WriteLine("\n  gesture classification:");
    (string Action, Gesture Expect)[] gestureCases =
    [
        ("Antennas twitch excitedly as the torso rotates slightly", Gesture.Wiggle),
        ("I bob my torso up and down enthusiastically, my antennas wiggling", Gesture.Bounce),
        ("antennas wiggle excitedly", Gesture.Wiggle),
        ("My antennas droop sadly", Gesture.Droop),
        ("I tilt my head to one side, thinking for a moment", Gesture.Tilt),
        ("I spin around in a circle, my torso rotating rapidly", Gesture.Spin),
        ("Drone's head tilts to one side, concern etched on its face", Gesture.Tilt),
        ("I look up in surprise", Gesture.LookUp),
        ("nods enthusiastically", Gesture.Nod),
        ("shakes her head", Gesture.Shake),
        ("antennas perk up alertly", Gesture.Perk),
        // "head" is a noun at index 0 - the verb has to win, not the body part.
        ("Head spins around to face Aubs, a big smile on its face", Gesture.Spin),
        ("head moves", Gesture.Tilt),
        ("leaning forward with interest", Gesture.LeanIn),
        ("", Gesture.None),
        ("says nothing in particular", Gesture.None),
    ];

    var gesturePass = 0;
    foreach (var (action, expect) in gestureCases)
    {
        var got = GestureClassifier.Classify(action);
        var ok = got == expect;
        if (ok) gesturePass++;
        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] \"{action}\" -> {got}, expected {expect}");
    }
    Console.WriteLine($"\n{gesturePass}/{gestureCases.Length} gesture cases passed");

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
        ("can you be can", "Khan"),               // "can you be Khan" as Aubs's mic hears it
        ("can you be can doorman", "Khan"),        // "can you be Khan Doorman" - first name discriminates
        ("switch to can", "Khan"),
        ("can you be using doorman", "Uzi"),       // same surname as Khan; first name says it's Uzi
        ("can you be dull", "Doll"),
        ("can you be sad", "Thad"),
        ("can you be sin", "Cyn"),                 // "can you be Cyn" as heard
        ("switch to using", "Uzi"),

        // ...but the same words must NOT hijack ordinary sentences. Several of them
        // are common English words, so this is the risk the slot rule exists for.
        ("I want an ice cream", null),
        ("I want to be gone from here", null),
        ("can you be quiet", null),
        ("I want a dull knife for the craft", null),
        ("be careful", null),

        // ---- transcripts Aubs actually produced, 2026-08-29 ----------------------------
        // These are not invented. Every line below came off the robot's own microphone
        // during --names-live, which is why they belong here permanently: the synthesised
        // suite scores 24/24 and could not see any of them.
        // ⚠️ She said "can you be N" CORRECTLY - her father heard her say it. Recognition
        // returned "and you'll be n", so the CUE PHRASE itself was misheard, not the name.
        // Every longer cue was destroyed and only the bare "be " survived to carry it,
        // which is the whole reason that fallback exists.
        //
        // Asserted as Uzi because this gate runs with N already current, where resolving
        // to N is correctly a no-op - an N expectation would pass for the wrong reason.
        // The "be " cue ends on a space, so it must NOT demand a non-letter after it.
        ("and you'll be uzi", "Uzi"),
        ("and you'll be n", null),                 // resolves to N, and N is already current
        ("can you be sinned", "Cyn"),              // "Cyn" heard as a past-tense verb
        ("can you be fad", "Thad"),
        ("can you be, sen", "Cyn"),

        // A pause before the name splits the utterance, and the halves arrive joined.
        // "Khan" came back as "on" for BOTH speakers - and "on" is deliberately NOT an
        // alias, because "can you be on my team?" is a sentence a child really says.
        // Khan simply has to be asked again; a false switch mid-play is the worse failure.
        //
        // Aubs's pause was not carelessness: she had just been told Khan is "kon" when she
        // had been saying "can", and she stopped to choose. Worth knowing that BOTH of her
        // pronunciations are already covered - "can" and "con" are both in his list - so
        // the deliberation was the only thing that cost her the turn.
        ("can you be? on", null),
        ("can you be can", "Khan"),                // her natural pronunciation
        ("can you be kon", "Khan"),                // the one she was taught mid-session

        // Recognition returns "can you beat that" for "can you be Thad". A plain substring
        // cue matches "can you be" inside "can you BEAT that" and leaves "at" in the name
        // slot, so the cue has to end on a word boundary - otherwise this resolves, and so
        // does "can you be at the store".
        ("can you beat that", null),
        ("can you be at the store", null),
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

    // Web-research gate: only real look-ups should offer the tool, never chit-chat.
    Console.WriteLine("\n  web-research gate:");
    (string Said, bool Expect)[] researchCases =
    [
        ("Can you look up the tallest dog in the world?", true),
        ("Look it up for me", true),
        ("Search the web for how volcanoes erupt", true),
        ("How do volcanoes erupt?", true),
        ("What is an axolotl?", true),
        ("Who invented the light bulb?", true),
        // ...but personal, roleplay, and in-world talk must NOT reach for the web.
        ("What's your favorite color?", false),
        ("Do you like My Little Pony?", false),
        ("Where do we live?", false),
        ("I built a castle in Minecraft.", false),
        ("ketchup", false),
        ("Can you be Uzi?", false),
    ];
    var researchPass = 0;
    foreach (var (said, expect) in researchCases)
    {
        var got = RoseBrain.ResearchWorthy(said);
        var ok = got == expect;
        if (ok) researchPass++;
        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] \"{said}\" -> {got}  expected {expect}");
    }
    Console.WriteLine($"\n{researchPass}/{researchCases.Length} research-gate cases passed");

    var allOk = speechPass == cases.Length && sayableOk
                && switchPass == switchCases.Length
                && boundaryPass == boundaryCases.Length
                && gesturePass == gestureCases.Length
                && researchPass == researchCases.Length;
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
        ("thad", "Thad"), ("VEE", "V"), ("jay", "J"), ("cyn", "Cyn"),
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
