namespace SpawnDev.Reachy;

/// <summary>A physical gesture the robot can perform.</summary>
public enum Gesture
{
    /// <summary>No gesture - the stage direction did not describe one, or described nothing physical.</summary>
    None,

    /// <summary>Head dips and returns. Agreement, or listening.</summary>
    Nod,

    /// <summary>Head turns side to side. Disagreement.</summary>
    Shake,

    /// <summary>Head rolls to one side. Curiosity or confusion.</summary>
    Tilt,

    /// <summary>Antennas snap upright. Sudden interest.</summary>
    Perk,

    /// <summary>Antennas waggle. Excitement or playfulness.</summary>
    Wiggle,

    /// <summary>Antennas fall. Disappointment or sadness.</summary>
    Droop,

    /// <summary>Head drops to look at the floor. Shyness, or looking at something below.</summary>
    LookDown,

    /// <summary>Head rises to look up. Wonder, or thinking.</summary>
    LookUp,

    /// <summary>Torso leans toward whoever is being spoken to. Attention, conspiracy.</summary>
    LeanIn,

    /// <summary>
    /// Torso rotates. Turning the head first BUYS travel here - body yaw is additionally
    /// constrained relative to head yaw.
    /// </summary>
    TurnBody,

    /// <summary>Whole body bobs. Delight.</summary>
    Bounce,

    /// <summary>Head sweeps all the way round. The biggest gesture in the set.</summary>
    Spin,
}

/// <summary>
/// Turns a free-text stage direction into a <see cref="Gesture"/>.
/// </summary>
/// <remarks>
/// A conversational model narrates physical action inline, in asterisks:
/// "*Antennas twitch excitedly* Wait, really?!". <see cref="SpokenText"/> separates those from the words
/// to speak; this decides what the servos do about them.
/// <para>
/// Pure and robot-free, so it is unit testable - and shared, so the desktop companion and the browser
/// extension classify identically instead of each grinding out its own vocabulary and drifting.
/// </para>
/// </remarks>
public static class GestureClassifier
{
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
}
