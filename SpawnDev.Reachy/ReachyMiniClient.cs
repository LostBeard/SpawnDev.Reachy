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

    /// <summary>Connects to a robot by host name or address.</summary>
    /// <param name="hostOrIp">The robot's host name or IP, e.g. "192.168.1.170".</param>
    /// <param name="port">Daemon port. 8000 is the daemon's default.</param>
    public ReachyMiniClient(string hostOrIp, int port = 8000)
        : this(new HttpClient { BaseAddress = new Uri($"http://{hostOrIp}:{port}") }, ownsHttp: true) { }

    /// <summary>Connects using a caller-supplied <see cref="HttpClient"/>.</summary>
    /// <param name="http">Client whose <see cref="HttpClient.BaseAddress"/> points at the daemon.</param>
    /// <param name="ownsHttp">
    /// True to dispose <paramref name="http"/> along with this client. False (the default) when the
    /// caller is pooling it, which is the usual reason to pass one in.
    /// </param>
    /// <exception cref="ArgumentException">The client has no BaseAddress.</exception>
    public ReachyMiniClient(HttpClient http, bool ownsHttp = false)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        if (_http.BaseAddress is null)
            throw new ArgumentException("HttpClient must have a BaseAddress set.", nameof(http));
    }

    // ---- daemon / status ----

    /// <summary>Everything the daemon reports about itself and the robot.</summary>
    public Task<DaemonStatus?> GetStatusAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/daemon/status", ReachyJson.Default.DaemonStatus, ct);

    // ---- motors ----

    /// <summary>The motors' current mode.</summary>
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

    /// <summary>
    /// Current torso rotation, in radians.
    /// </summary>
    /// <remarks>
    /// The body yaw is a real, first-class axis that the stock apps simply never register as a tool -
    /// which is why the robot will tell you it cannot turn its body. It can.
    /// </remarks>
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

    /// <summary>
    /// Current antenna joint positions in radians, as (left, right).
    /// </summary>
    /// <remarks>
    /// Reading these back is the only reliable way to discover the real travel
    /// limits - the daemon silently clamps an out-of-range goto rather than
    /// reporting an error, so a command that "succeeds" may not have moved
    /// anywhere near where it was told to.
    /// </remarks>
    public async Task<(double Left, double Right)?> GetAntennaPositionsAsync(CancellationToken ct = default)
    {
        var a = await _http.GetFromJsonAsync("/api/state/present_antenna_joint_positions",
            ReachyJson.Default.ListDouble, ct);
        return a is { Count: >= 2 } ? (a[0], a[1]) : null;
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

    /// <summary>Plays the daemon's built-in wake-up move.</summary>
    /// <returns>A handle to the QUEUED move - it has not finished when this returns.</returns>
    public async Task<MoveHandle?> WakeUpAsync(CancellationToken ct = default)
    {
        using var r = await _http.PostAsync("/api/move/play/wake_up", null, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync(ReachyJson.Default.MoveHandle, ct);
    }

    /// <summary>
    /// Lowers the robot into its shell, the daemon's own resting move.
    /// </summary>
    /// <remarks>
    /// This is the ONLY pose motor power can be cut from cleanly. A neutral, head-up pose is not
    /// mechanically stable, so disabling the motors there lets gravity drop the head. Wait for the
    /// head to stop moving before cutting power - the returned handle only means the move is queued.
    /// </remarks>
    /// <returns>A handle to the QUEUED move.</returns>
    public async Task<MoveHandle?> GotoSleepAsync(CancellationToken ct = default)
    {
        using var r = await _http.PostAsync("/api/move/play/goto_sleep", null, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync(ReachyJson.Default.MoveHandle, ct);
    }

    /// <summary>Cancels whatever move is currently running.</summary>
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

    /// <summary>
    /// The face the daemon's tracker currently has, or null if it is not reporting one.
    /// </summary>
    /// <remarks>
    /// Gate on <see cref="FaceTarget.Detected"/>. This is the clean tracking signal; the head pose is
    /// not, because it also moves for idle motion and the two cannot be told apart from the value.
    /// </remarks>
    public async Task<FaceTarget?> GetFaceAsync(CancellationToken ct = default)
    {
        var r = await _http.GetFromJsonAsync("/api/media/tracking/face", ReachyJson.Default.FaceTargetResponse, ct);
        return r?.FaceTarget;
    }

    /// <summary>Whether the media subsystem is free to play a sound.</summary>
    public Task<MediaStatus?> GetMediaStatusAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/media/status", ReachyJson.Default.MediaStatus, ct);

    // ---- audio ----

    /// <summary>Current speaker volume. Reads 100 out of the box - see <see cref="SetVolumeAsync"/>.</summary>
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

    /// <summary>Sounds the robot is currently holding, grouped by the daemon's own categories.</summary>
    /// <remarks>
    /// Uploads persist across sessions, so a clip made in an earlier run is still here and still
    /// playable - which is what lets a named, content-addressed clip be reused instead of re-rendered.
    /// </remarks>
    public async Task<Dictionary<string, List<string>>?> ListSoundsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/api/media/sounds", ReachyJson.Default.DictionaryStringListString, ct);

    /// <summary>Uploads a sound to the robot under <paramref name="fileName"/>, overwriting any clip of that name.</summary>
    /// <param name="fileName">Name to store it under, and the name <see cref="PlaySoundAsync"/> takes.</param>
    /// <param name="content">The audio itself. WAV - the daemon sniffs the payload and rejects non-audio.</param>
    /// <param name="ct">Cancels the upload.</param>
    /// <exception cref="HttpRequestException">The daemon rejected it; the message carries what it said.</exception>
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

    /// <summary>
    /// Starts playing an uploaded sound. Returns as soon as playback is QUEUED, not when it ends.
    /// </summary>
    /// <remarks>
    /// This is load-bearing: starting a second clip while one is still playing cuts the first one
    /// off, which sounds like the robot interrupting itself a word or two into every sentence. A
    /// caller playing several clips in a row has to wait out each one's duration itself.
    /// </remarks>
    /// <param name="fileName">Name the clip was uploaded under.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task PlaySoundAsync(string fileName, CancellationToken ct = default)
    {
        using var r = await _http.PostAsJsonAsync("/api/media/play_sound",
            new PlaySoundRequest(fileName), ReachyJson.Default.PlaySoundRequest, ct);
        r.EnsureSuccessStatusCode();
    }

    /// <summary>Stops whatever is playing.</summary>
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

    /// <summary>Disposes the underlying <see cref="HttpClient"/> if this client created it.</summary>
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
