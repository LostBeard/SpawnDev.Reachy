using System.Net.Http.Json;
using System.Text;

namespace SpawnDev.Reachy;

/// <summary>
/// C# client for the Reachy Mini daemon REST API.
/// </summary>
/// <remarks>
/// The daemon exposes a FastAPI service on port 8000 with a published OpenAPI
/// schema and no authentication on the LAN, so no Python SDK is required to drive
/// the robot. Verified against a Reachy Mini Wireless running daemon v1.9.0.
/// </remarks>
public class ReachyMiniClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>Daemon base address, e.g. http://192.168.1.170:8000.</summary>
    public Uri BaseAddress => _http.BaseAddress!;

    public ReachyMiniClient(string hostOrIp, int port = 8000)
        : this(new HttpClient { BaseAddress = new Uri($"http://{hostOrIp}:{port}") }, ownsHttp: true) { }

    public ReachyMiniClient(HttpClient http, bool ownsHttp = false)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        if (_http.BaseAddress is null)
            throw new ArgumentException("HttpClient must have a BaseAddress set.", nameof(http));
    }

    // ---- daemon / status ----

    public Task<DaemonStatus?> GetStatusAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/daemon/status", ReachyJson.Default.DaemonStatus, ct);

    // ---- motors ----

    public Task<MotorStatus?> GetMotorStatusAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/motors/status", ReachyJson.Default.MotorStatus, ct);

    /// <summary>
    /// Sets the motor control mode. Prefer <see cref="MotorMode.Disabled"/> when
    /// leaving the robot unattended - the motors can otherwise enter thermal
    /// protection after holding position under load.
    /// </summary>
    public async Task SetMotorModeAsync(MotorMode mode, CancellationToken ct = default)
    {
        var s = mode switch
        {
            MotorMode.Enabled => "enabled",
            MotorMode.Disabled => "disabled",
            MotorMode.GravityCompensation => "gravity_compensation",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        using var r = await _http.PostAsync($"/api/motors/set_mode/{s}", null, ct);
        r.EnsureSuccessStatusCode();
    }

    // ---- state ----

    public Task<double> GetBodyYawAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/state/present_body_yaw", ReachyJson.Default.Double, ct);

    /// <summary>
    /// Current head pose. NOTE: the yaw of this pose is NOT a clean face-tracking
    /// error signal - it also moves for idle/ambient motion when nothing is being
    /// tracked, and the two are indistinguishable from the value alone. Use
    /// <see cref="GetFaceAsync"/> gated on <see cref="FaceTarget.Detected"/> instead.
    /// </summary>
    public async Task<XyzRpyPose?> GetHeadPoseAsync(CancellationToken ct = default)
    {
        var d = await _http.GetFromJsonAsync("/api/state/present_head_pose", ReachyJson.Default.XyzRpyPoseDto, ct);
        return d is null ? null : new XyzRpyPose(d.X, d.Y, d.Z, d.Roll, d.Pitch, d.Yaw);
    }

    /// <summary>Direction of arrival from the mic array. See <see cref="DoaInfo"/> for caveats.</summary>
    public Task<DoaInfo?> GetDoaAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/state/doa", ReachyJson.Default.DoaInfo, ct);

    // ---- movement ----

    /// <summary>
    /// Smoothly moves to a target over <paramref name="duration"/> seconds. Any
    /// argument left null is not commanded and keeps its current value.
    /// </summary>
    /// <remarks>
    /// body_yaw is clamped by the daemon to [-pi, pi], and |body_yaw - head_yaw|
    /// is additionally constrained to about 65 degrees.
    /// </remarks>
    public async Task<MoveHandle?> GotoAsync(
        double? bodyYaw = null,
        XyzRpyPose? headPose = null,
        (double Left, double Right)? antennas = null,
        double duration = 1.0,
        Interpolation interpolation = Interpolation.MinJerk,
        CancellationToken ct = default)
    {
        var req = new GotoRequest(
            headPose is null ? null : new XyzRpyPoseDto(headPose.X, headPose.Y, headPose.Z, headPose.Roll, headPose.Pitch, headPose.Yaw),
            antennas is null ? null : [antennas.Value.Left, antennas.Value.Right],
            bodyYaw,
            duration,
            interpolation switch
            {
                Interpolation.Linear => "linear",
                Interpolation.MinJerk => "minjerk",
                Interpolation.EaseInOut => "ease_in_out",
                Interpolation.Cartoon => "cartoon",
                _ => "minjerk",
            });

        using var resp = await _http.PostAsJsonAsync("/api/move/goto", req, ReachyJson.Default.GotoRequest, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(ReachyJson.Default.MoveHandle, ct);
    }

    public async Task<MoveHandle?> WakeUpAsync(CancellationToken ct = default)
    {
        using var r = await _http.PostAsync("/api/move/play/wake_up", null, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync(ReachyJson.Default.MoveHandle, ct);
    }

    public async Task<MoveHandle?> GotoSleepAsync(CancellationToken ct = default)
    {
        using var r = await _http.PostAsync("/api/move/play/goto_sleep", null, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync(ReachyJson.Default.MoveHandle, ct);
    }

    public async Task StopMoveAsync(CancellationToken ct = default)
    {
        using var r = await _http.PostAsync("/api/move/stop", null, ct);
        r.EnsureSuccessStatusCode();
    }

    // ---- vision / tracking ----

    /// <summary>
    /// Enables the daemon's face tracker. NOTE: this switch controls face
    /// DETECTION as well as head motion - disabling it makes
    /// <see cref="GetFaceAsync"/> report Detected=false permanently, so there is
    /// no way to observe a face while the head is held still.
    /// </summary>
    public async Task SetFaceTrackingAsync(bool enabled, CancellationToken ct = default)
    {
        var path = enabled ? "/api/media/tracking/enable" : "/api/media/tracking/disable";
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var r = await _http.PostAsync(path, enabled ? content : null, ct);
        r.EnsureSuccessStatusCode();
    }

    public async Task<FaceTarget?> GetFaceAsync(CancellationToken ct = default)
    {
        var r = await _http.GetFromJsonAsync("/api/media/tracking/face", ReachyJson.Default.FaceTargetResponse, ct);
        return r?.FaceTarget;
    }

    public Task<MediaStatus?> GetMediaStatusAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/media/status", ReachyJson.Default.MediaStatus, ct);

    // ---- audio ----

    public Task<VolumeInfo?> GetVolumeAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/volume/current", ReachyJson.Default.VolumeInfo, ct);

    /// <summary>
    /// Sets output volume (0-100) and plays a test sound.
    /// </summary>
    /// <remarks>
    /// On a real unit this reads 100 out of the box, and both ALSA PCM controls
    /// already sit at 0.00 dB, so there is typically no headroom to gain here.
    /// If Rose is too quiet, normalise the audio you send rather than expecting
    /// this to help.
    /// </remarks>
    public async Task SetVolumeAsync(int volume, CancellationToken ct = default)
    {
        using var r = await _http.PostAsJsonAsync("/api/volume/set",
            new VolumeRequest(Math.Clamp(volume, 0, 100)), ReachyJson.Default.VolumeRequest, ct);
        r.EnsureSuccessStatusCode();
    }

    public async Task<Dictionary<string, List<string>>?> ListSoundsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/api/media/sounds", ReachyJson.Default.DictionaryStringListString, ct);

    public async Task UploadSoundAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var sc = new StreamContent(content);
        // The daemon sniffs the payload and rejects anything that is not really
        // audio, so the part needs a correct content type, not just a .wav name.
        sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(sc, "file", fileName);

        using var r = await _http.PostAsync("/api/media/sounds/upload", form, ct);
        if (!r.IsSuccessStatusCode)
        {
            var detail = await r.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Upload of '{fileName}' failed: {(int)r.StatusCode} {r.StatusCode}. Daemon said: {detail}");
        }
    }

    public async Task PlaySoundAsync(string fileName, CancellationToken ct = default)
    {
        using var r = await _http.PostAsJsonAsync("/api/media/play_sound",
            new PlaySoundRequest(fileName), ReachyJson.Default.PlaySoundRequest, ct);
        r.EnsureSuccessStatusCode();
    }

    public async Task StopSoundAsync(CancellationToken ct = default)
    {
        using var r = await _http.PostAsync("/api/media/stop_sound", null, ct);
        r.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Reads a raw XVF3800 DSP parameter by name (see the daemon's
    /// media/audio_control_utils.py for the full table). Returns null if the
    /// parameter is not readable on this board.
    /// </summary>
    /// <remarks>
    /// The table contains no speaker output gain. Only capture-side controls
    /// (AUDIO_MGR_MIC_GAIN, PP_AGC*) and AUDIO_MGR_REF_GAIN, which is the AEC
    /// loopback reference rather than the speaker level.
    /// </remarks>
    public async Task<AudioParameter?> ReadAudioParameterAsync(string name, CancellationToken ct = default)
    {
        using var r = await _http.GetAsync($"/api/audio/config/parameter/{Uri.EscapeDataString(name)}", ct);
        if (!r.IsSuccessStatusCode) return null;
        return await r.Content.ReadFromJsonAsync(ReachyJson.Default.AudioParameter, ct);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
