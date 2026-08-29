using System.Text.Json.Serialization;

namespace SpawnDev.Reachy;

/// <summary>Motor control mode. Maps to the daemon's MotorControlMode enum.</summary>
public enum MotorMode
{
    /// <summary>Motors on and holding position (stiff).</summary>
    Enabled,
    /// <summary>Motors off (limp). Safe parking state when unattended.</summary>
    Disabled,
    /// <summary>Motors on but soft - lets you pose the robot by hand for teach-by-demonstration.</summary>
    GravityCompensation,
}

/// <summary>Interpolation curve for a goto move.</summary>
public enum Interpolation
{
    /// <summary>Constant velocity. Starts and stops abruptly.</summary>
    Linear,
    /// <summary>Daemon default. Smooth minimum-jerk profile.</summary>
    MinJerk,
    /// <summary>Accelerates in and decelerates out, with no overshoot.</summary>
    EaseInOut,
    /// <summary>Exaggerated, snappy easing. Reads as expressive rather than mechanical.</summary>
    Cartoon,
}

/// <summary>A 3D pose: position in metres, orientation in radians.</summary>
public record XyzRpyPose(
    double X = 0, double Y = 0, double Z = 0,
    double Roll = 0, double Pitch = 0, double Yaw = 0);

/// <summary>Direction-of-arrival reading from the 4-mic array.</summary>
/// <remarks>
/// Treat with suspicion. Measured on a real unit: this parks at ~1.57-1.60 rad
/// (90 deg) as an idle default and returns that same value for genuinely different
/// speaker positions when there is background noise (an air conditioner 10 ft away
/// was enough). It also latches on 50-100ms noise blips. Gate on a minimum
/// utterance duration and do not treat a ~90 deg reading as a real bearing.
/// </remarks>
public record DoaInfo(
    [property: JsonPropertyName("angle")] double Angle,
    [property: JsonPropertyName("speech_detected")] bool SpeechDetected);

/// <summary>A face detected by the daemon's tracker. Coordinates are normalised, 0 = centre.</summary>
public record FaceTarget(
    [property: JsonPropertyName("detected")] bool Detected,
    [property: JsonPropertyName("x")] double? X,
    [property: JsonPropertyName("y")] double? Y,
    [property: JsonPropertyName("roll")] double? Roll,
    [property: JsonPropertyName("ts")] double? Ts);

/// <summary>The daemon's reply to a face-target query.</summary>
/// <param name="Status">Daemon status string for the request.</param>
/// <param name="FaceTarget">The tracked face, or null when the tracker sees nobody.</param>
public record FaceTargetResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("face_target")] FaceTarget? FaceTarget);

/// <summary>Health of the daemon's motor control loop.</summary>
/// <param name="MeanFrequency">Average loop rate, in Hz.</param>
/// <param name="MaxInterval">Longest gap between iterations, in seconds. A spike here is a stalled loop.</param>
/// <param name="ErrorCount">Errors the loop has accumulated since it started.</param>
/// <param name="MotorController">Name of the controller driving the motors, when the daemon reports one.</param>
public record ControlLoopStats(
    [property: JsonPropertyName("mean_control_loop_frequency")] double MeanFrequency,
    [property: JsonPropertyName("max_control_loop_interval")] double MaxInterval,
    [property: JsonPropertyName("nb_error")] int ErrorCount,
    [property: JsonPropertyName("motor_controller")] string? MotorController);

/// <summary>State of the daemon's hardware backend - the half that actually drives the robot.</summary>
/// <param name="Ready">True once the backend is up and will accept moves.</param>
/// <param name="MotorControlMode">Current motor mode as the daemon spells it. See <see cref="MotorMode"/>.</param>
/// <param name="ControlLoopStats">Control loop health, when the backend is running.</param>
/// <param name="Error">What went wrong, when the backend is not ready.</param>
public record BackendStatus(
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("motor_control_mode")] string MotorControlMode,
    [property: JsonPropertyName("control_loop_stats")] ControlLoopStats? ControlLoopStats,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>Everything the daemon reports about itself and the robot it is driving.</summary>
/// <param name="RobotName">The robot's own name, as configured on the unit.</param>
/// <param name="State">Daemon lifecycle state.</param>
/// <param name="WirelessVersion">True on a Reachy Mini Wireless rather than the tethered model.</param>
/// <param name="Version">Daemon software version.</param>
/// <param name="WlanIp">The robot's address on the wireless network, when it has one.</param>
/// <param name="HardwareId">Unique identifier for this unit.</param>
/// <param name="BackendStatus">State of the hardware backend, when it has started.</param>
/// <param name="FaceTarget">The face the tracker currently has, when it has one.</param>
public record DaemonStatus(
    [property: JsonPropertyName("robot_name")] string RobotName,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("wireless_version")] bool WirelessVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("wlan_ip")] string? WlanIp,
    [property: JsonPropertyName("hardware_id")] string? HardwareId,
    [property: JsonPropertyName("backend_status")] BackendStatus? BackendStatus,
    [property: JsonPropertyName("face_target")] FaceTarget? FaceTarget);

/// <summary>The motors' current mode.</summary>
/// <param name="Mode">Mode as the daemon spells it. See <see cref="MotorMode"/>.</param>
public record MotorStatus([property: JsonPropertyName("mode")] string Mode);

/// <summary>Speaker volume as the daemon reports it.</summary>
/// <param name="Volume">
/// Level, 0 to 100. Worth knowing that 100 is where this already sits and there is no headroom
/// left in hardware - loudness has to be won in the audio itself. See RoseVoice.Loudify.
/// </param>
/// <param name="Platform">Audio platform backing the output, when the daemon names one.</param>
/// <param name="Device">Output device, when the daemon names one.</param>
public record VolumeInfo(
    [property: JsonPropertyName("volume")] int Volume,
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("device")] string? Device);

/// <summary>Whether the daemon's media subsystem is free to play a sound.</summary>
/// <param name="Available">The media device is present and usable.</param>
/// <param name="Released">The device has been handed back and is not held by anything.</param>
/// <param name="NoMedia">Nothing is currently loaded to play.</param>
public record MediaStatus(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("released")] bool Released,
    [property: JsonPropertyName("no_media")] bool NoMedia);

/// <summary>One tunable parameter on the XVF3800 audio front end.</summary>
/// <param name="Name">Parameter name as the daemon exposes it.</param>
/// <param name="Values">Its current value or values.</param>
public record AudioParameter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("values")] List<double> Values);

/// <summary>
/// A handle to a move the daemon has QUEUED.
/// </summary>
/// <remarks>
/// Receiving this means the move was accepted, not that it has finished - the POST returns as soon
/// as the move is queued. Code that needs the robot to have actually arrived has to watch the pose
/// settle, which is why parking waits for the head to stop moving before cutting motor power.
/// </remarks>
/// <param name="Uuid">The daemon's identifier for the queued move.</param>
public record MoveHandle([property: JsonPropertyName("uuid")] string Uuid);

// ---- request bodies ----

internal record GotoRequest(
    [property: JsonPropertyName("head_pose")] XyzRpyPoseDto? HeadPose,
    [property: JsonPropertyName("antennas")] double[]? Antennas,
    [property: JsonPropertyName("body_yaw")] double? BodyYaw,
    [property: JsonPropertyName("duration")] double Duration,
    [property: JsonPropertyName("interpolation")] string Interpolation);

internal record XyzRpyPoseDto(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("roll")] double Roll,
    [property: JsonPropertyName("pitch")] double Pitch,
    [property: JsonPropertyName("yaw")] double Yaw);

internal record VolumeRequest([property: JsonPropertyName("volume")] int Volume);

internal record PlaySoundRequest([property: JsonPropertyName("file")] string File);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DaemonStatus))]
[JsonSerializable(typeof(MotorStatus))]
[JsonSerializable(typeof(DoaInfo))]
[JsonSerializable(typeof(FaceTarget))]
[JsonSerializable(typeof(FaceTargetResponse))]
[JsonSerializable(typeof(VolumeInfo))]
[JsonSerializable(typeof(MediaStatus))]
[JsonSerializable(typeof(AudioParameter))]
[JsonSerializable(typeof(MoveHandle))]
[JsonSerializable(typeof(GotoRequest))]
[JsonSerializable(typeof(VolumeRequest))]
[JsonSerializable(typeof(PlaySoundRequest))]
[JsonSerializable(typeof(XyzRpyPoseDto))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<double>))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
internal partial class ReachyJson : JsonSerializerContext;
