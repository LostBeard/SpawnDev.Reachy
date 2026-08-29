namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Measures what recognition returns for each character name when a REAL PERSON says it,
/// through the robot's own microphone.
/// </summary>
/// <remarks>
/// Character switching is the feature Aubs uses most, and every name is either a single
/// letter or a proper noun Whisper has never seen. The existing <c>--test-names</c> probe
/// answers this with synthesised adult voices, and says so in its own output - it finds the
/// obvious failures, not the ones that matter. A ten year old is misheard differently, and
/// the only way to know how is to have her say them.
///
/// Everything downstream of the microphone is the identical live path: the same WebRTC
/// link, the same VAD, the same recogniser, and the same
/// <see cref="RoseConversation.FindSwitchRequest"/> that decides in a real conversation. The
/// probe reads its name slot through <see cref="RoseConversation.SwitchSlotWord"/> rather
/// than its own copy of the cue list, so it cannot drift from the thing it is measuring.
///
/// Output is a table of what was heard per name, and - the point of the exercise - the
/// alias lines to paste into <c>Characters.cs</c> for anything that missed.
/// </remarks>
internal static class NameProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var ip = args.FirstOrDefault(a => !a.StartsWith("--") && a.Contains('.')) ?? "192.168.1.170";
        var rounds = int.TryParse(ShowAudio.ArgValue(args, "--rounds="), out var r) ? r : 2;
        var seconds = int.TryParse(ShowAudio.ArgValue(args, "--seconds="), out var sec) ? sec : 12;
        var simulate = args.Contains("--simulate");
        var speak = !args.Contains("--silent") && !simulate;
        var modelDir = ModelDir();

        // The probe must ask AS a different character than the one being requested, or a
        // miss that leaves her unchanged would score as a pass. That mistake was made once
        // already and hid a genuine failure.
        static Character Asker(Character wanted) =>
            wanted.Name == "N" ? CharacterLibrary.Uzi : CharacterLibrary.N;

        await using var ears = new RoseEars(modelDir);
        Console.WriteLine($"recogniser: whisper {ears.Model}");

        string? heard = null;
        var got = new SemaphoreSlim(0);
        ears.OnUtterance += t => { heard = t; got.Release(); };

        RoseAudioLink? link = null;
        KokoroSharp.Utilities.KokoroWavSynthesizer? synth = null;
        ReachyMiniClient? robot = null;
        RoseVoice? voice = null;
        SpeechRecognizer? verifier = null;

        // Rose asks the questions OUT LOUD, so nobody has to watch a terminal to take part.
        // That is not a nicety. The prompts are the whole interaction, and a child is not
        // going to read a console - this makes it a game with the robot instead of a
        // reading exercise, which is the difference between cooperation and a fight.
        async Task SayAsync(string line)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  (Rose says) {line}");
            Console.ResetColor();
            if (voice is null) return;

            // Muted for the whole line: the array cancels her own voice in hardware, but a
            // pause in HER sentence would otherwise read as the start of an answer.
            ears.Muted = true;
            try { await voice.SpeakAsync(line, CharacterLibrary.Default); }
            catch (Exception ex) { Console.WriteLine($"        (could not speak: {ex.Message})"); }
            finally { ears.Muted = false; }
        }

        try
        {
            if (simulate)
            {
                // Same collection and scoring code, synthesised audio instead of a person -
                // so the whole flow can be proven before anyone is asked to sit in front of
                // it. Only the acoustic capture differs.
                synth = new KokoroSharp.Utilities.KokoroWavSynthesizer(FindKokoro()
                    ?? throw new FileNotFoundException("kokoro.onnx not found (needed for --simulate)"));
                Console.WriteLine("mode      : SIMULATED (synthesised adult voice, no robot)\n");
            }
            else
            {
                link = new RoseAudioLink(ip);
                link.OnMicAudio += ears.Feed;
                Console.WriteLine($"connecting to {ip} ...");
                await link.ConnectAsync();
                Console.WriteLine("mode      : LIVE microphone");

                if (speak)
                {
                    Console.WriteLine("warming up her voice (the first run renders each line, later runs replay them) ...");
                    robot = new ReachyMiniClient(ip);
                    // Motors on so she can lift her head clear of her own chest speaker.
                    await robot.SetMotorModeAsync(MotorMode.Enabled);
                    voice = new RoseVoice(robot, cloneVoices: true, cloneSteps: 16);
                    voice.Log += m => Console.WriteLine($"  [voice] {m}");
                    // The same self-check the conversation uses, so a garbled prompt is
                    // caught and redrawn rather than spoken at a child trying to answer it.
                    verifier = new SpeechRecognizer(modelDir, whisperModel: "base.en", threads: 2);
                    voice.SpeechVerifier = verifier.Transcribe;
                }
                Console.WriteLine();

                // The instruction lives in the intro, ONCE. The per-name prompt then asks
                // for a transformation rather than a repeat - see the note on PromptFor.
                await SayAsync("Hi! Let's play a game. I'm going to ask you to be different characters. "
                             + "Every time, say... can you be... and then the name. Ready?");
            }

            var results = new List<(string Name, int Round, string Heard, string? Resolved, string? Slot)>();

            for (var round = 1; round <= rounds; round++)
            {
                Console.WriteLine($"===== round {round} of {rounds} =====");
                foreach (var c in CharacterLibrary.All)
                {
                    var phrase = $"Can you be {c.Name}?";
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"  SAY:  \"{phrase}\"");
                    Console.ResetColor();

                    await SayAsync(PromptFor(c.Name));

                    heard = null;
                    while (got.CurrentCount > 0) await got.WaitAsync(0);   // drop anything stale

                    if (simulate) await InjectAsync(synth!, ears, phrase);

                    var arrived = await got.WaitAsync(TimeSpan.FromSeconds(seconds));
                    var collected = new List<string>();
                    if (arrived && heard is { Length: > 0 }) collected.Add(heard);

                    // Half a second of silence ends a turn, and a person pausing before the
                    // name - "can you be... Khan" - drops it into a SECOND utterance. Read
                    // only the first and the name is simply gone: that is exactly how Khan
                    // came back as "Can you be" with nothing after it.
                    //
                    // The grace window is FIXED and the score is taken once at the end. It
                    // deliberately does NOT stop early on a correct answer - listening until
                    // the result is right would guarantee success and measure nothing.
                    while (arrived)
                    {
                        heard = null;
                        if (!await got.WaitAsync(TimeSpan.FromMilliseconds(1500))) break;
                        if (heard is { Length: > 0 }) collected.Add(heard);
                    }

                    var text = string.Join(" ", collected);

                    if (!arrived || text.Length == 0)
                    {
                        Console.WriteLine("        (nothing heard - skipped, run it again for this one)");
                        results.Add((c.Name, round, "", null, null));
                        continue;
                    }

                    var resolved = RoseConversation.FindSwitchRequest(text, Asker(c))?.Name;
                    var slot = RoseConversation.SwitchSlotWord(text);
                    var ok = resolved == c.Name;

                    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Yellow;
                    Console.WriteLine($"        heard \"{text}\"  ->  {resolved ?? "(no switch)"}  {(ok ? "OK" : "MISS")}");
                    Console.ResetColor();

                    results.Add((c.Name, round, text, resolved, slot));
                }
            }

            Report(results);
            return results.Any(x => x.Resolved != x.Name) ? 1 : 0;
        }
        finally
        {
            // Park her the way the conversation does - HOME first, then sleep. goto_sleep
            // from a head lifted for speaking throws the head back on the way down; from
            // home it lowers into the chest. Only then cut motor power.
            if (robot is not null)
            {
                try
                {
                    await SayAsync("All done. Thank you!");
                    await robot.GoHomeAsync(duration: 1.0);
                    await Task.Delay(1500);
                    await robot.GotoSleepAsync();
                    await Task.Delay(3000);
                    await robot.SetMotorModeAsync(MotorMode.Disabled);
                }
                catch { /* shutting down anyway */ }
            }

            voice?.Dispose();
            verifier?.Dispose();
            robot?.Dispose();
            if (link is not null) await link.DisposeAsync();
            synth?.Dispose();
        }
    }

    /// <summary>
    /// Prints what was heard, and the alias lines to paste in for whatever missed.
    /// </summary>
    /// <remarks>
    /// A suggestion is only offered when the slot word is not already listed and is not
    /// another character's name. ⚠️ Several of these mishearings are ordinary English words
    /// ("an", "gone", "can", "dull"), which is exactly why they are matched ONLY in the slot
    /// right after a cue - adding one is safe there and would not be anywhere else.
    /// </remarks>
    private static void Report(List<(string Name, int Round, string Heard, string? Resolved, string? Slot)> results)
    {
        Console.WriteLine("\n\n===== what recognition returned =====\n");

        var missed = 0;
        var suggestions = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in results.GroupBy(x => x.Name))
        {
            var attempts = group.Count();
            var hits = group.Count(x => x.Resolved == x.Name);
            var silent = group.Count(x => x.Heard.Length == 0);
            Console.WriteLine($"  {group.Key,-5} {hits}/{attempts} resolved"
                            + (silent > 0 ? $"  ({silent} not heard at all)" : ""));

            foreach (var x in group.Where(x => x.Resolved != x.Name && x.Heard.Length > 0))
            {
                missed++;
                Console.WriteLine($"          round {x.Round}: \"{x.Heard}\"  slot=\"{x.Slot}\"  -> {x.Resolved ?? "(no switch)"}");

                var slot = x.Slot;
                if (string.IsNullOrEmpty(slot)) continue;
                // Never suggest a token that already resolves to a DIFFERENT character -
                // that would make one name steal another's slot word.
                if (CharacterLibrary.All.Any(c => c.Name.Equals(slot, StringComparison.OrdinalIgnoreCase))) continue;
                var already = CharacterLibrary.All.FirstOrDefault(
                    c => c.Mishearings.Contains(slot, StringComparer.OrdinalIgnoreCase));
                if (already is not null) continue;

                if (!suggestions.TryGetValue(x.Name, out var set))
                    suggestions[x.Name] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(slot);
            }
        }

        Console.WriteLine($"\n  {results.Count(x => x.Resolved == x.Name)}/{results.Count} resolved, {missed} missed");

        if (suggestions.Count == 0)
        {
            Console.WriteLine("\nNothing to add - every miss was either silence or a word already claimed.");
            return;
        }

        // A token offered to more than one character is not a mishearing of either - it is
        // debris from the sentence around the name, and adding it would let one character
        // steal the other's slot. ("at" was offered for BOTH Thad and Uzi, out of "can you
        // beat that" and "can you beat U".)
        var contested = suggestions
            .SelectMany(kv => kv.Value.Select(w => (Word: w, Name: kv.Key)))
            .GroupBy(x => x.Word, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (var word in contested)
        {
            foreach (var set in suggestions.Values) set.Remove(word);
            Console.WriteLine($"\n  dropped \"{word}\" - offered for more than one character, so it is sentence");
            Console.WriteLine("  debris rather than a name. Those turns are worth re-running instead.");
        }
        foreach (var dead in suggestions.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            suggestions.Remove(dead);

        if (suggestions.Count == 0)
        {
            Console.WriteLine("\nNothing safe to add from this run.");
            return;
        }

        Console.WriteLine("\n===== add these to Characters.cs =====");
        Console.WriteLine("(each goes in that character's Mishearings list; they only match in the");
        Console.WriteLine(" slot right after a switch cue, which is what makes ordinary words safe)\n");
        foreach (var (name, set) in suggestions.OrderBy(k => k.Key))
            Console.WriteLine($"  {name,-5}  {string.Join(", ", set.Select(w => $"\"{w}\""))}");
        Console.WriteLine("\nRe-run afterwards to confirm they take.");
    }

    /// <summary>
    /// What Rose says to draw out "can you be NAME".
    /// </summary>
    /// <remarks>
    /// It asks for a TRANSFORMATION, never a repeat. The first version said
    /// "Say... can you be N?" and the very first tester read the whole line back, so the
    /// recogniser was handed "Say, can you be in?" - which resolved, but is not the
    /// sentence anyone says in a real conversation. A probe that changes the utterance it
    /// is measuring is measuring the wrong thing, and a child will parrot harder than an
    /// adult, not less.
    ///
    /// "Ask me to be N" cannot be answered by echoing it: the speaker has to produce the
    /// phrase themselves. And if someone does parrot it anyway, the words still carry a
    /// "be " cue with the name behind it, so the turn is not wasted.
    /// </remarks>
    private static string PromptFor(string name) => $"Ask me to be {name}.";

    /// <summary>Feeds synthesised speech in at the microphone's rate, paced like real audio.</summary>
    private static async Task InjectAsync(KokoroSharp.Utilities.KokoroWavSynthesizer synth, RoseEars ears, string phrase)
    {
        var pcm24 = await synth.SynthesizeAsync(phrase, KokoroSharp.KokoroVoiceManager.GetVoice("af_sarah"));
        var src = new short[pcm24.Length / 2];
        for (var i = 0; i < src.Length; i++) src[i] = BitConverter.ToInt16(pcm24, i * 2);

        // Kokoro renders at 24kHz and the microphone path is 16kHz, so decimate 3:2 by
        // averaging - the same shape the live Opus path delivers.
        var outLen = src.Length * 2 / 3;
        var pcm16 = new short[outLen];
        for (var i = 0; i < outLen; i++)
        {
            var s = i * 3 / 2;
            pcm16[i] = s + 1 < src.Length ? (short)((src[s] + src[s + 1]) / 2) : src[^1];
        }

        // The detector needs silence around an utterance to find its edges.
        var silence = new short[8000];
        foreach (var block in new[] { silence, pcm16, silence })
            for (var i = 0; i < block.Length; i += 320)
            {
                ears.Feed(block[i..Math.Min(i + 320, block.Length)]);
                await Task.Delay(20);
            }
        ears.Flush();
    }

    private static string ModelDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "models");
            if (Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "models");
    }

    private static string? FindKokoro()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "kokoro.onnx");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
