namespace SpawnDev.Reachy.Rose;

/// <summary>
/// The whole loop: Rose listens, thinks, and answers in character.
/// </summary>
/// <remarks>
/// Microphone to speech recognition to language model to speech synthesis to the
/// robot's speaker, all on the local network. This is the piece that turns a robot
/// that can be commanded into one that can be talked to.
/// </remarks>
public sealed class RoseConversation : IAsyncDisposable
{
    private readonly ReachyMiniClient _robot;
    private readonly RoseAudioLink _link;
    private readonly RoseEars _ears;
    private readonly RoseBrain _brain;
    private readonly RoseVoice _voice;

    /// <summary>Serialises replies so two utterances can never talk over each other.</summary>
    private readonly SemaphoreSlim _turn = new(1, 1);

    private readonly CancellationTokenSource _cts = new();

    private Character _character = CharacterLibrary.Default;

    /// <summary>Raised with conversation lines for display. (speaker, text).</summary>
    public event Action<string, string>? OnLine;

    /// <summary>Diagnostic log.</summary>
    public event Action<string>? Log;

    /// <summary>The character Rose is currently playing.</summary>
    public Character Character => _character;

    private readonly bool _useMicrophone;

    /// <param name="useMicrophone">
    /// When false, the robot's microphone is not connected and audio must be supplied
    /// through <see cref="InjectAudio"/>. Everything downstream - recognition, the
    /// model, speech, and the robot's speaker - is the identical live path, which is
    /// what makes the loop testable without a person in the room.
    /// </param>
    public RoseConversation(
        string robotHost,
        string modelDir,
        string ollamaModel = "llama3.1:8b",
        bool useMicrophone = true)
    {
        _robot = new ReachyMiniClient(robotHost);
        _link = new RoseAudioLink(robotHost);
        _ears = new RoseEars(modelDir);
        _brain = new RoseBrain(ollamaModel);
        _voice = new RoseVoice(_robot);
        _useMicrophone = useMicrophone;
    }

    /// <summary>Supplies 16kHz mono audio as if it had come from the robot's microphone.</summary>
    public void InjectAudio(short[] pcm16k) => _ears.Feed(pcm16k);

    /// <summary>Closes out any in-progress utterance in injected audio.</summary>
    public void FlushAudio() => _ears.Flush();

    /// <summary>Completes when Rose is not mid-reply.</summary>
    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        await _turn.WaitAsync(ct);
        _turn.Release();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var problem = await _brain.CheckAsync(ct);
        if (problem is not null)
            throw new InvalidOperationException(
                $"{problem}\nStart it with: ~/AppData/Local/Programs/Ollama/ollama.exe serve");

        _ears.Log += m => Log?.Invoke(m);
        _link.Log += m => Log?.Invoke(m);
        _ears.OnUtterance += text => _ = HandleUtteranceAsync(text);

        // Motors must be live before she can lift her head to speak.
        await _robot.SetMotorModeAsync(MotorMode.Enabled, ct);

        // Load the model now, during the connect, so the first question she asks
        // is answered at warm speed rather than paying the model load.
        var warm = _brain.WarmAsync(ct);

        if (_useMicrophone)
        {
            _link.OnMicAudio += _ears.Feed;
            await _link.ConnectAsync(ct);
        }

        var hello = $"Oh gosh, hi Aubs! It's {_character.Name}. What do you want to talk about?";
        OnLine?.Invoke(_character.Name, hello);
        await SpeakGatedAsync(hello, ct);

        try { await warm; } catch (Exception ex) { Log?.Invoke($"warmup failed: {ex.Message}"); }
    }

    private async Task HandleUtteranceAsync(string text)
    {
        // Drop anything that arrives while a reply is still being spoken rather than
        // queueing it. Queued turns make her answer a question from ten seconds ago,
        // which reads as her being confused rather than busy.
        if (!await _turn.WaitAsync(0)) { Log?.Invoke($"busy, dropped: \"{text}\""); return; }

        try
        {
            OnLine?.Invoke("Aubs", text);

            if (TrySwitchCharacter(text, out var switched))
            {
                _character = switched;
                _brain.Forget();
                var line = SwitchGreeting(switched);
                OnLine?.Invoke(switched.Name, line);
                await SpeakGatedAsync(line, _cts.Token);
                return;
            }

            // Mute for the whole reply, not per sentence: the gaps between sentences
            // are short enough that toggling would re-open the mic into her own tail.
            _ears.Muted = true;
            try
            {
                // Sentences are synthesised as the model produces them, but played
                // strictly one after another. Overlapping playback cuts the previous
                // line off a word or two in, which sounds like Rose interrupting
                // herself; waiting to synthesise until the previous line finished
                // would instead leave a dead gap between every sentence. Rendering
                // ahead while playing in order avoids both.
                var playback = Task.CompletedTask;
                var turnClock = System.Diagnostics.Stopwatch.StartNew();

                await _brain.StreamReplyAsync(text, _character, async sentence =>
                {
                    var (say, actions) = SpokenText.Split(sentence);
                    foreach (var a in actions) Log?.Invoke($"action: {a}");

                    // A sentence that was nothing but a stage direction has no
                    // speech left in it - skip it rather than synthesise silence.
                    if (!SpokenText.IsSayable(say)) return;

                    var prepared = await _voice.PrepareAsync(say, _character, _cts.Token);

                    var previous = playback;
                    playback = Task.Run(async () =>
                    {
                        await previous;
                        OnLine?.Invoke(_character.Name, say);

                        // Logged so overlap can be checked from the timeline rather
                        // than only by ear: each start must be at or after the
                        // previous end.
                        var start = turnClock.Elapsed;
                        await _voice.PlayAsync(prepared, _cts.Token);
                        Log?.Invoke(
                            $"play [{start.TotalSeconds,5:F2}s -> {turnClock.Elapsed.TotalSeconds,5:F2}s] " +
                            $"{prepared.Duration.TotalSeconds:F2}s audio");
                    }, _cts.Token);
                }, _cts.Token);

                await playback;
            }
            finally { _ears.Muted = false; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke($"turn failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { _turn.Release(); }
    }

    private async Task SpeakGatedAsync(string text, CancellationToken ct)
    {
        _ears.Muted = true;
        try { await _voice.SpeakAsync(text, _character, ct); }
        finally { _ears.Muted = false; }
    }

    /// <summary>
    /// Phrases that mean Aubs is asking for a different character. Longest first, so
    /// "i want to talk to" wins over "i want" and the name slot lands correctly.
    /// </summary>
    private static readonly string[] SwitchCues =
    [
        "i want to talk to", "i want to speak to", "pretend to be", "can you be",
        "i want you to be", "switch to", "talk like", "turn into", "play as",
        "change to", "i want", "become", "be ",
    ];

    /// <summary>
    /// Decides whether an utterance is a request to change character.
    /// </summary>
    /// <remarks>
    /// A bare name is NOT enough. Three characters are single letters, and simply
    /// mentioning one - "V is so funny" - would otherwise silently swap who she is
    /// mid-conversation. Requiring an explicit cue means talking ABOUT a character
    /// and asking her to BE one are different things, which is how Aubs will
    /// naturally use it.
    /// </remarks>
    public static Character? FindSwitchRequest(string text, Character current)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lower = text.ToLowerInvariant();

        var cue = SwitchCues.FirstOrDefault(c => lower.Contains(c, StringComparison.Ordinal));
        if (cue is null) return null;

        var found = CharacterLibrary.Find(text);

        // Nothing matched by name, so fall back to what recognition ACTUALLY returns
        // for these names. Restricted to the word right after the cue, because
        // several mishearings are ordinary words and matching them anywhere would
        // fire on sentences that are not requests at all.
        if (found is null)
        {
            var after = lower[(lower.IndexOf(cue, StringComparison.Ordinal) + cue.Length)..];
            var slot = after.Split(
                [' ', '\t', ',', '.', '!', '?', ';', ':', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(slot))
                found = CharacterLibrary.All.FirstOrDefault(
                    c => c.Mishearings.Contains(slot, StringComparer.OrdinalIgnoreCase));
        }

        return found is null || found.Name == current.Name ? null : found;
    }

    private bool TrySwitchCharacter(string text, out Character character)
    {
        var found = FindSwitchRequest(text, _character);
        character = found ?? _character;
        return found is not null;
    }

    private static string SwitchGreeting(Character c) => c.Name switch
    {
        "N" => "Oh gosh, hi! It's me, N! What are we doing?",
        "Uzi" => "Ugh. Fine. What do you want?",
        "V" => "Well, hello! Excellent choice, obviously.",
        "J" => "Serial Designation J. Let's keep this efficient.",
        "Doll" => "...hello.",
        "Khan" => "Oh! Hello there. Did you want to talk about doors?",
        "Thad" => "Heyyy! This is gonna be great!",
        _ => $"Hi! It's {c.Name}.",
    };

    public async ValueTask DisposeAsync()
    {
        // Hand the VRAM back before tearing down, while the token is still live.
        await _brain.ReleaseAsync();

        _cts.Cancel();
        await _ears.DisposeAsync();
        await _link.DisposeAsync();
        _voice.Dispose();
        _robot.Dispose();
        _cts.Dispose();
        _turn.Dispose();
    }
}
