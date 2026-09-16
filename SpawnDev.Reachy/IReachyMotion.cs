namespace SpawnDev.Reachy;

/// <summary>
/// The motion surface a choreographer needs from a Reachy Mini, independent of how it is reached.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 WHY THIS EXISTS. <see cref="ReachyBody"/> is the valuable part of this SDK - the measured motion
/// envelope, the gesture classifier and the idle life - and it was bound to <see cref="ReachyMiniClient"/>,
/// which speaks plain HTTP to the daemon on the LAN. A page served over HTTPS cannot reach that at all
/// (the browser blocks it as mixed content), so a hosted demo could never drive a robot, and the obvious
/// workaround - reimplementing the choreography against a second transport - would give two classifiers
/// that eventually disagree about what a character just did.
/// </para>
/// <para>
/// The surface turned out to be ONE method. <c>ReachyBody</c> calls <see cref="GotoAsync"/> and nothing
/// else, so a WebRTC transport (the browser SDK, signalled through a Hugging Face Space) reuses every
/// gesture unchanged.
/// </para>
/// <para>
/// ⚠️ Keep this to what a CHOREOGRAPHER needs. Connection lifecycle - wake, park, motor mode - belongs to
/// whatever owns the connection, not to the thing that moves the head; see <see cref="IReachyLifecycle"/>.
/// </para>
/// </remarks>
public interface IReachyMotion
{
    /// <summary>
    /// Move toward a target pose over <paramref name="duration"/> seconds. Any argument left null is
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// ⚠️ The daemon CLAMPS SILENTLY: an out-of-range goto returns success and simply does not go there,
    /// so a transport must not add validation that turns that into an error - a gesture that looks
    /// half-finished is the documented behaviour the envelope in <see cref="ReachyBody"/> is tuned around.
    /// </remarks>
    Task<MoveHandle?> GotoAsync(
        double? bodyYaw = null,
        XyzRpyPose? headPose = null,
        (double Left, double Right)? antennas = null,
        double duration = 1.0,
        Interpolation interpolation = Interpolation.MinJerk,
        CancellationToken ct = default);
}

/// <summary>
/// Connection lifecycle for a Reachy Mini: bring it up safely and park it safely.
/// </summary>
/// <remarks>
/// 🔴 THE PARKING ORDER IS A CONFIRMED RECIPE AND IS NOT NEGOTIABLE: go home FIRST, then sleep, wait for
/// the head to settle, and only then motors off. <c>goto_sleep</c> starts from wherever the robot IS, and
/// speaking leaves the head lifted - sleeping from there throws the head back, while from home it lowers
/// into the chest. Any implementation of this interface owes callers that ordering.
/// </remarks>
public interface IReachyLifecycle
{
    // Signatures match ReachyMiniClient's existing members exactly, so the HTTP transport satisfies this
    // by declaration alone - no shim, and no chance of the two drifting.

    /// <summary>Enable, disable or float the motors.</summary>
    Task SetMotorModeAsync(MotorMode mode, CancellationToken ct = default);

    /// <summary>Play the daemon's own wake-up move, out of the parked pose.</summary>
    Task<MoveHandle?> WakeUpAsync(CancellationToken ct = default);

    /// <summary>Return to the neutral home pose.</summary>
    Task<MoveHandle?> GoHomeAsync(double duration = 1.0, CancellationToken ct = default);

    /// <summary>Play the daemon's own sleep move, lowering the head into the chest.</summary>
    Task<MoveHandle?> GotoSleepAsync(CancellationToken ct = default);
}
