namespace SpawnDev.Reachy;

/// <summary>What the robot is currently doing, from the point of view of someone standing in front of it.</summary>
public enum ReachyMood
{
    /// <summary>Not in a conversation. Still alive, just not attending to anyone.</summary>
    Idle,

    /// <summary>The microphone is open and it is hearing the room.</summary>
    Listening,

    /// <summary>A reply is being worked on. Nothing to say yet.</summary>
    Thinking,

    /// <summary>Talking. The reply's own stage directions own the body while this lasts.</summary>
    Speaking,
}

/// <summary>
/// Makes the robot show what it is doing, for someone who cannot see the screen.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 THE ROBOT IS THE WHOLE INTERFACE WHEN IT IS IN ANOTHER ROOM. Captain: <i>"imagine the user is using
/// the Reachy in a different room away from the PC... the Reachy's movement and input/output systems are
/// what they are interacting with."</i> On a screen, "listening", "thinking" and "idle" are three obvious
/// states - a level meter, a spinner, a cursor. On the robot they were one state: perfectly still. Someone
/// who has just spoken to a motionless robot cannot tell whether it heard them, is working, has finished,
/// or is broken, and the natural response to all four is to say it again.
/// </para>
/// <para>
/// ⭐ The vocabulary is deliberately the SAME <see cref="Gesture"/> set a character performs from its
/// written stage directions. A second, private set of "system" motions would read as a different machine
/// reacting, and the amplitudes here are the ones measured on a real unit.
/// </para>
/// <para>
/// ⚠️ IT NEVER FIGHTS A REPLY. <see cref="ReachyBody"/> serialises gestures and drops rather than queues,
/// so a state animation that arrives mid-gesture is skipped - which is the correct outcome: what the
/// character is acting out matters more than what the app thinks its status is. <see cref="ReachyMood.Speaking"/>
/// goes further and stops the idle loop entirely, so nothing stirs the antennae underneath a performance.
/// </para>
/// </remarks>
public sealed class ReachyPresence : IAsyncDisposable
{
    private readonly ReachyBody _body;
    private readonly GestureStyle _style;
    private readonly Random _rng = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _disposed;

    /// <summary>Drive <paramref name="body"/>'s posture from the app's state.</summary>
    /// <param name="body">The body to move. Not disposed with this instance.</param>
    /// <param name="style">Resting antenna posture and motion scale, as for any gesture.</param>
    public ReachyPresence(ReachyBody body, GestureStyle? style = null)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _style = style ?? GestureStyle.Default;
    }

    /// <summary>What the robot is currently showing.</summary>
    public ReachyMood Mood { get; private set; } = ReachyMood.Idle;

    /// <summary>Diagnostic log, matching the rest of the SDK's conventions.</summary>
    public event Action<string>? Log;

    /// <summary>
    /// Show <paramref name="mood"/>. Returns as soon as the opening gesture is away.
    /// </summary>
    /// <remarks>
    /// ⚠️ Setting the mood it is already in does nothing. Re-triggering on every token of a streamed reply
    /// would be a gesture per token, which the drop-don't-queue rule would turn into a stutter of skipped
    /// motions rather than movement.
    /// </remarks>
    public async Task SetAsync(ReachyMood mood, CancellationToken ct = default)
    {
        if (_disposed || mood == Mood) return;
        Mood = mood;
        Log?.Invoke($"[reachy-mood] {mood}");

        await StopLoopAsync().ConfigureAwait(false);

        switch (mood)
        {
            case ReachyMood.Listening:
                // Antennae up and held: the one posture that unmistakably means "go on, I am hearing you".
                // Idle stays ON underneath, so it keeps breathing while it waits rather than freezing in
                // an alert pose, which reads as a photograph of attention rather than attention.
                _body.Idle = true;
                await _body.PerformAsync(Gesture.Perk, _style, ct).ConfigureAwait(false);
                break;

            case ReachyMood.Thinking:
                // 🔴 THE LONGEST SILENCE IN THE WHOLE INTERACTION, and the one with nothing to show for
                // it: the model is generating and there is no audio, no text on any screen the person can
                // see, and nothing to perform. A single gesture would be over long before the reply is,
                // so this is the one mood that keeps moving on its own.
                _body.Idle = false;   // the loop below owns the body; two sources would collide
                StartLoop(ct);
                break;

            case ReachyMood.Speaking:
                // Hand the body to the reply. Its stage directions are a better performance than anything
                // a status animation could invent, and idle antennae under them look like a twitch.
                _body.Idle = false;
                break;

            default:
                // Back to alive-but-unoccupied.
                _body.Idle = true;
                await _body.SettleAsync(_style, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>The thinking loop: small, slow, and never the same twice in a row.</summary>
    private void StartLoop(CancellationToken ct)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _loopCts.Token;
        _loop = Task.Run(async () =>
        {
            // ⚠️ Nothing may escape. An unhandled exception on a background task exits the .NET WASM
            // runtime and takes the page with it, and this is decoration.
            try
            {
                // Tilt first, immediately: the gap between "you stopped talking" and "it did something"
                // is exactly the moment a person decides they were not heard.
                var moves = new[] { Gesture.Tilt, Gesture.LookUp, Gesture.Wiggle, Gesture.LookDown };
                var i = _rng.Next(moves.Length);
                while (!token.IsCancellationRequested)
                {
                    await _body.PerformAsync(moves[i % moves.Length], _style, token).ConfigureAwait(false);
                    i++;
                    // Irregular, so it never becomes a mechanical tick - and long enough that the robot
                    // looks like it is considering something rather than fidgeting.
                    await Task.Delay(TimeSpan.FromMilliseconds(_rng.Next(2200, 4200)), token)
                              .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log?.Invoke($"[reachy-mood] thinking loop failed: {ex.Message}"); }
        }, token);
    }

    private async Task StopLoopAsync()
    {
        if (_loopCts is not { } cts) return;
        _loopCts = null;
        try { cts.Cancel(); } catch { }
        // Wait it out: a gesture still in flight would land after the next mood's opening move and read
        // as the robot changing its mind.
        if (_loop is { } loop) { try { await loop.ConfigureAwait(false); } catch { } }
        _loop = null;
        cts.Dispose();
    }

    /// <summary>Stop animating. Does not park the robot - that belongs to whoever owns the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopLoopAsync().ConfigureAwait(false);
    }
}
