namespace SpawnDev.Reachy;

/// <summary>
/// How a particular personality moves - the two knobs that make the same gesture read as a different
/// character performing it.
/// </summary>
/// <param name="AntennaRest">Resting antenna angles (radians). A big part of read-at-a-glance mood.</param>
/// <param name="MotionScale">Multiplier on gesture size. Bigger = more animated. Clamped when applied.</param>
/// <remarks>
/// This exists so the gesture choreography can live in the library while the desktop companion keeps its
/// richer <c>Character</c> notion (voice, persona, prompt) to itself: a Character supplies a GestureStyle
/// and everything below this line is the same code for every host.
/// </remarks>
public record GestureStyle((double Left, double Right) AntennaRest, double MotionScale)
{
    /// <summary>Neutral movement - what a host with no personality model should use.</summary>
    public static readonly GestureStyle Default = new((0.0, 0.0), 1.0);
}
