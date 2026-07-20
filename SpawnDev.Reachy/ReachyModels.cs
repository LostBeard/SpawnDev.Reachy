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
    Linear,
    /// <summary>Daemon default. Smooth minimum-jerk profile.</summary>
    MinJerk,
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

public record FaceTargetResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("face_target")] FaceTarget? FaceTarget);

public record ControlLoopStats(
    [property: JsonPropertyName("mean_control_loop_frequency")] double MeanFrequency,
    [property: JsonPropertyName("max_control_loop_interval")] double MaxInterval,
    [property: JsonPropertyName("nb_error")] int ErrorCount,
    [property: JsonPropertyName("motor_controller")] string? MotorController);

public record BackendStatus(
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("motor_control_mode")] string MotorControlMode,
    [property: JsonPropertyName("control_loop_stats")] ControlLoopStats? ControlLoopStats,
    [property: JsonPropertyName("error")] string? Error);

public record DaemonStatus(
    [property: JsonPropertyName("robot_name")] string RobotName,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("wireless_version")] bool WirelessVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("wlan_ip")] string? WlanIp,
    [property: JsonPropertyName("hardware_id")] string? HardwareId,
    [property: JsonPropertyName("backend_status")] BackendStatus? BackendStatus,
    [property: JsonPropertyName("face_target")] FaceTarget? FaceTarget);

public record MotorStatus([property: JsonPropertyName("mode")] string Mode);

public record VolumeInfo(
    [property: JsonPropertyName("volume")] int Volume,
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("device")] string? Device);

public record MediaStatus(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("released")] bool Released,
    [property: JsonPropertyName("no_media")] bool NoMedia);

public record AudioParameter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("values")] List<double> Values);

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
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
internal partial class ReachyJson : JsonSerializerContext;
