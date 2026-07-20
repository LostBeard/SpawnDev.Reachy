namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Turns the stage directions the model writes into actual movement.
/// </summary>
/// <remarks>
/// The personas tell each character it has a head, antennas and a rotating torso
/// and to react physically before speaking, so the model emits things like
/// "*antennas twitch excitedly*" and "*torso rotates to face Aubriella*" on its own.
/// Those are already a description of an intended motion - this maps them onto the
/// servos rather than throwing them away.
///
/// All travel limits below were MEASURED on the robot with --probe-limits, not read
/// from a datasheet. The daemon clamps silently: an out-of-range goto returns
/// success and simply does not go there, so commanding a range that does not exist
/// produces a gesture that looks half-finished rather than an error.
/// </remarks>
public sealed class RoseBody
{
    private readonly ReachyMiniClient _robot;

    /// <summary>One gesture at a time. Overlapping gotos fight each other and jitter.</summary>
    private readonly SemaphoreSlim _moving = new(1, 1);

    public event Action<string>? Log;

    public RoseBody(ReachyMiniClient robot) => _robot = robot;

    // ---- measured envelope (--probe-limits, 2026-07-20) ----
    // Each value is held short of the real limit so a scaled-up gesture still has
    // somewhere to go instead of grinding against a stop.

    /// <summary>Head yaw still tracked cleanly at 1.55; no clamp found.</summary>
    public const double HeadYawMax = 1.2;

    /// <summary>Looking down clamps at about 0.68.</summary>
    public const double HeadPitchDownMax = 0.55;

    /// <summary>Looking up clamps at about 0.51 - noticeably less than looking down.</summary>
    public const double HeadPitchUpMax = -0.40;

    /// <summary>Roll clamps at about 0.70.</summary>
    public const double HeadRollMax = 0.55;

    /// <summary>Head lift clamps hard at 0.0224m. Commanding 0.030 or 0.040 still gives 0.022.</summary>
    public const double HeadLiftMax = 0.0224;

    /// <summary>Antennas clamp at about 3.1 - by far the largest range on the robot.</summary>
    public const double AntennaMax = 2.5;

    /// <summary>
    /// Body yaw clamps at about 0.98 with the head centred. The daemon additionally
    /// constrains |body_yaw - head_yaw| to roughly 65 degrees, so turning the head
    /// the same way first buys more torso travel.
    /// </summary>
    public const double BodyYawMax = 0.70;

    private static double Clamp(double v, double lo, double hi) => Math.Clamp(v, lo, hi);

    /// <summary>
    /// Performs the gesture a stage direction describes, if it maps to one.
    /// </summary>
    /// <remarks>
    /// Never blocks the caller for long and never throws - a gesture is decoration.
    /// If Rose is already moving, the new one is dropped rather than queued: a
    /// backlog of gestures played after the sentence they belonged to looks worse
    /// than no gesture at all.
    /// </remarks>
    public async Task PerformAsync(string action, Character character, CancellationToken ct = default)
    {
        var gesture = Classify(action);
        if (gesture == Gesture.None) return;

        if (!await _moving.WaitAsync(0, ct)) { Log?.Invoke($"busy, skipped gesture {gesture}"); return; }

        try
        {
            Log?.Invoke($"gesture {gesture} <- \"{action}\"");
            await RunAsync(gesture, character, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke($"gesture failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { _moving.Release(); }
    }

    public enum Gesture
    {
        None, Nod, Shake, Tilt, Perk, Wiggle, Droop,
        LookDown, LookUp, LeanIn, TurnBody, Bounce, Spin,
    }

    /// <summary>Marker for "the antennas are the subject", refined by the verb near them.</summary>
    private const Gesture Antennas = (Gesture)(-1);

    private static readonly (Gesture Gesture, string[] Words)[] Cues =
    [
        (Antennas,          ["antenna"]),
        (Gesture.Nod,       ["nod", "agrees", "agreeing"]),
        (Gesture.Shake,     ["shake", "shakes"]),
        (Gesture.Tilt,      ["tilt", "curious", "confused", "puzzl", "quizzical", "think", "ponder", "considers"]),
        (Gesture.Spin,      ["spin", "circle", "twirl", "whirl"]),
        (Gesture.Bounce,    ["bounce", "bob", "jump", "hop", "excited", "giggl", "laugh", "chuckl", "wiggl"]),
        (Gesture.Droop,     ["sad", "sigh", "droop", "dejected", "disappoint", "downcast", "slump"]),
        (Gesture.LeanIn,    ["lean", "closer", "peer"]),
        (Gesture.LookDown,  ["look down", "looks down", "glance down", "floor", "ground"]),
        (Gesture.LookUp,    ["look up", "looks up", "gasp", "surprise", "shock"]),
        (Gesture.TurnBody,  ["torso", "body", "rotate", "turn", "swivel", "face"]),
    ];

    /// <summary>
    /// Checked only when nothing above matched.
    /// </summary>
    /// <remarks>
    /// "head" is a bare noun rather than an action, so it must not compete on
    /// position - it is almost always the first word of the sentence. Letting it
    /// win turned "Head spins around to face Aubs" into a head tilt, because
    /// "head" sits at index 0 and "spins" does not.
    /// </remarks>
    private static readonly (Gesture Gesture, string[] Words)[] Fallbacks =
    [
        (Gesture.Tilt, ["head"]),
    ];

    /// <summary>
    /// Picks a gesture from the words in a stage direction.
    /// </summary>
    /// <remarks>
    /// Whichever cue appears EARLIEST wins, because the model writes the primary
    /// action first and qualifies it afterwards. Fixed-priority ordering got this
    /// wrong in both directions on real output: "antennas twitch excitedly as the
    /// torso rotates" is an antenna twitch that happens to be excited, while "I bob
    /// my torso up and down enthusiastically, my antennas wiggling" is a bob that
    /// happens to involve antennas. Position separates them; keyword priority
    /// cannot.
    /// </remarks>
    public static Gesture Classify(string action)
    {
        if (string.IsNullOrWhiteSpace(action)) return Gesture.None;
        var a = action.ToLowerInvariant();

        var best = Gesture.None;
        var bestAt = int.MaxValue;

        foreach (var (gesture, words) in Cues)
            foreach (var w in words)
            {
                var at = a.IndexOf(w, StringComparison.Ordinal);
                if (at >= 0 && at < bestAt) { bestAt = at; best = gesture; }
            }

        bool Has(params string[] words) => words.Any(w => a.Contains(w, StringComparison.Ordinal));

        if (best == Gesture.None)
        {
            foreach (var (gesture, words) in Fallbacks)
                if (Has(words)) return gesture;
            return Gesture.None;
        }

        if (best != Antennas) return best;

        // The antennas are the subject - the verb decides what they do.
        if (Has("droop", "lower", "fall", "sag", "sad", "flatten")) return Gesture.Droop;
        if (Has("perk", "straight", "alert", "raise", "shoot up", "stand")) return Gesture.Perk;
        return Gesture.Wiggle;
    }

    /// <summary>Where Rose sits between gestures: head up and attentive.</summary>
    private async Task RestAsync(Character c, CancellationToken ct, double duration = 0.5) =>
        await _robot.GotoAsync(
            bodyYaw: 0,
            headPose: new XyzRpyPose(Z: HeadLiftMax, Pitch: -0.05),
            antennas: (Clamp(c.AntennaRest.Left, -AntennaMax, AntennaMax),
                       Clamp(c.AntennaRest.Right, -AntennaMax, AntennaMax)),
            duration: duration,
            interpolation: Interpolation.EaseInOut,
            ct: ct);

    private async Task RunAsync(Gesture g, Character c, CancellationToken ct)
    {
        // Bigger characters move bigger. V swaggers, Doll barely moves at all.
        var s = Math.Clamp(c.MotionScale, 0.3, 1.6);
        var lift = HeadLiftMax;

        // Cartoon interpolation overshoots slightly and settles, which reads as
        // alive where minjerk reads as a machine executing a trajectory.
        const Interpolation Snappy = Interpolation.Cartoon;

        switch (g)
        {
            case Gesture.Nod:
                for (var i = 0; i < 2; i++)
                {
                    await _robot.GotoAsync(headPose: new XyzRpyPose(Z: lift, Pitch: Clamp(0.30 * s, 0, HeadPitchDownMax)),
                        duration: 0.22, interpolation: Snappy, ct: ct);
                    await Task.Delay(220, ct);
                    await _robot.GotoAsync(headPose: new XyzRpyPose(Z: lift, Pitch: Clamp(-0.12 * s, HeadPitchUpMax, 0)),
                        duration: 0.22, interpolation: Snappy, ct: ct);
                    await Task.Delay(220, ct);
                }
                break;

            case Gesture.Shake:
                for (var i = 0; i < 2; i++)
                {
                    await _robot.GotoAsync(headPose: new XyzRpyPose(Z: lift, Yaw: Clamp(0.35 * s, -HeadYawMax, HeadYawMax)),
                        duration: 0.20, interpolation: Snappy, ct: ct);
                    await Task.Delay(200, ct);
                    await _robot.GotoAsync(headPose: new XyzRpyPose(Z: lift, Yaw: Clamp(-0.35 * s, -HeadYawMax, HeadYawMax)),
                        duration: 0.20, interpolation: Snappy, ct: ct);
                    await Task.Delay(200, ct);
                }
                break;

            case Gesture.Tilt:
                await _robot.GotoAsync(
                    headPose: new XyzRpyPose(Z: lift, Roll: Clamp(0.40 * s, -HeadRollMax, HeadRollMax)),
                    antennas: (Clamp(c.AntennaRest.Left + 0.3, -AntennaMax, AntennaMax), c.AntennaRest.Right),
                    duration: 0.45, interpolation: Snappy, ct: ct);
                await Task.Delay(900, ct);
                break;

            case Gesture.Perk:
                await _robot.GotoAsync(
                    headPose: new XyzRpyPose(Z: lift, Pitch: Clamp(-0.18 * s, HeadPitchUpMax, 0)),
                    antennas: (Clamp(1.2 * s, -AntennaMax, AntennaMax), Clamp(1.2 * s, -AntennaMax, AntennaMax)),
                    duration: 0.30, interpolation: Snappy, ct: ct);
                await Task.Delay(700, ct);
                break;

            case Gesture.Wiggle:
                for (var i = 0; i < 3; i++)
                {
                    var w = 0.9 * s;
                    await _robot.GotoAsync(antennas: (Clamp(w, -AntennaMax, AntennaMax), Clamp(-w, -AntennaMax, AntennaMax)),
                        duration: 0.16, interpolation: Snappy, ct: ct);
                    await Task.Delay(160, ct);
                    await _robot.GotoAsync(antennas: (Clamp(-w, -AntennaMax, AntennaMax), Clamp(w, -AntennaMax, AntennaMax)),
                        duration: 0.16, interpolation: Snappy, ct: ct);
                    await Task.Delay(160, ct);
                }
                break;

            case Gesture.Droop:
                await _robot.GotoAsync(
                    headPose: new XyzRpyPose(Z: lift * 0.4, Pitch: Clamp(0.35 * s, 0, HeadPitchDownMax)),
                    antennas: (Clamp(-1.1 * s, -AntennaMax, AntennaMax), Clamp(-1.1 * s, -AntennaMax, AntennaMax)),
                    duration: 0.8, interpolation: Interpolation.EaseInOut, ct: ct);
                await Task.Delay(1100, ct);
                break;

            case Gesture.LookDown:
                await _robot.GotoAsync(headPose: new XyzRpyPose(Z: lift, Pitch: Clamp(0.45 * s, 0, HeadPitchDownMax)),
                    duration: 0.5, interpolation: Interpolation.EaseInOut, ct: ct);
                await Task.Delay(800, ct);
                break;

            case Gesture.LookUp:
                await _robot.GotoAsync(
                    headPose: new XyzRpyPose(Z: lift, Pitch: Clamp(-0.35 * s, HeadPitchUpMax, 0)),
                    antennas: (Clamp(1.6 * s, -AntennaMax, AntennaMax), Clamp(1.6 * s, -AntennaMax, AntennaMax)),
                    duration: 0.25, interpolation: Snappy, ct: ct);
                await Task.Delay(800, ct);
                break;

            case Gesture.LeanIn:
                // X is forward. Small values only - this axis has far less travel
                // than the rotational ones.
                await _robot.GotoAsync(headPose: new XyzRpyPose(X: 0.015 * s, Z: lift, Pitch: 0.12 * s),
                    duration: 0.5, interpolation: Interpolation.EaseInOut, ct: ct);
                await Task.Delay(900, ct);
                break;

            case Gesture.TurnBody:
                {
                    // Turning the head the same way first buys torso travel, since
                    // the daemon constrains the two to within about 65 degrees.
                    var yaw = Clamp(0.5 * s, -BodyYawMax, BodyYawMax);
                    await _robot.GotoAsync(
                        bodyYaw: yaw,
                        headPose: new XyzRpyPose(Z: lift, Yaw: Clamp(yaw * 0.5, -HeadYawMax, HeadYawMax)),
                        duration: 0.7, interpolation: Snappy, ct: ct);
                    await Task.Delay(1000, ct);
                }
                break;

            case Gesture.Bounce:
                for (var i = 0; i < 3; i++)
                {
                    await _robot.GotoAsync(headPose: new XyzRpyPose(Z: lift),
                        antennas: (Clamp(1.3 * s, -AntennaMax, AntennaMax), Clamp(1.3 * s, -AntennaMax, AntennaMax)),
                        duration: 0.14, interpolation: Snappy, ct: ct);
                    await Task.Delay(150, ct);
                    await _robot.GotoAsync(headPose: new XyzRpyPose(Z: lift * 0.25),
                        antennas: (Clamp(0.4 * s, -AntennaMax, AntennaMax), Clamp(0.4 * s, -AntennaMax, AntennaMax)),
                        duration: 0.14, interpolation: Snappy, ct: ct);
                    await Task.Delay(150, ct);
                }
                break;

            case Gesture.Spin:
                {
                    // Not a full rotation - the torso cannot do one. Swing to each
                    // side and back, which reads as the same excitement.
                    var yaw = Clamp(0.65 * s, -BodyYawMax, BodyYawMax);
                    await _robot.GotoAsync(bodyYaw: yaw, headPose: new XyzRpyPose(Z: lift, Yaw: yaw * 0.6),
                        duration: 0.45, interpolation: Snappy, ct: ct);
                    await Task.Delay(480, ct);
                    await _robot.GotoAsync(bodyYaw: -yaw, headPose: new XyzRpyPose(Z: lift, Yaw: -yaw * 0.6),
                        duration: 0.6, interpolation: Snappy, ct: ct);
                    await Task.Delay(620, ct);
                }
                break;
        }

        await RestAsync(c, ct);
    }

    /// <summary>Returns to a neutral resting pose.</summary>
    public async Task SettleAsync(Character c, CancellationToken ct = default)
    {
        try { await RestAsync(c, ct, duration: 0.8); } catch { }
    }
}
