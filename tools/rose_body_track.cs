// Rose - body-yaw face tracker.
//
// The daemon's built-in face tracking moves the HEAD only, which is why Rose
// seems unable to turn to face you - she can only glance. This adds torso
// rotation underneath it, so she keeps people centred over a much wider arc.
//
// WHY THERE IS NO AUTO-CALIBRATION HERE (measured, not assumed):
//   - Probing body_yaw and measuring the resulting face-x shift does not work
//     while head tracking is on: the head re-centres the face mid-probe and
//     cancels most of the displacement. An identical 0.20 rad probe returned
//     dx=0.119 on one run and dx=0.047 on the next, swinging the derived gain
//     from -1.67 to -4.28 and driving a visible oscillation.
//   - Turning head tracking off to measure clean geometry does not work either:
//     /api/media/tracking/disable also disables face DETECTION, so there is no
//     face to measure.
//   The sign was consistent across every run (positive body yaw moves the face
//   positive in x), so the mapping is a fixed constant and the runaway guard
//   below is the safety net if a different unit ever disagrees.
//
// Usage: dotnet run rose_body_track.cs [robot-ip] [seconds]

using System.Net.Http.Json;
using System.Text.Json.Serialization;

var ip = args.Length > 0 ? args[0] : "192.168.1.170";
var seconds = args.Length > 1 ? int.Parse(args[1]) : 120;

var http = new HttpClient { BaseAddress = new Uri($"http://{ip}:8000"), Timeout = TimeSpan.FromSeconds(5) };

// Negative: drive the error to zero rather than amplify it.
const double RadPerX = -1.5;
// Kept low because the head tracker is nulling this same error at the same time.
// The body is meant to lag; at higher gain the two overshoot into a limit cycle.
const double Gain = 0.35;
const double Deadband = 0.08;
const int Persist = 3;
const double MaxStep = 0.35;
const double MaxBodyYaw = Math.PI;

async Task<Face?> GetFace()
{
    var r = await http.GetFromJsonAsync("/api/media/tracking/face", RoseJson.Default.FaceResponse);
    return r?.face_target;
}

async Task<double> GetYaw() =>
    await http.GetFromJsonAsync("/api/state/present_body_yaw", RoseJson.Default.Double);

async Task Goto(double bodyYaw, double duration, string interp = "ease_in_out")
{
    bodyYaw = Math.Clamp(bodyYaw, -MaxBodyYaw, MaxBodyYaw);
    using var resp = await http.PostAsJsonAsync("/api/move/goto",
        new GotoRequest(bodyYaw, duration, interp), RoseJson.Default.GotoRequest);
    resp.EnsureSuccessStatusCode();
}

Console.WriteLine("Enabling motors and face tracking...");
using (await http.PostAsync("/api/motors/set_mode/enabled", null)) { }
using (await http.PostAsync("/api/media/tracking/enable",
    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))) { }
await Task.Delay(1500);

Console.WriteLine($"Tracking for {seconds}s. Walk around - Rose should turn her body to follow.\n");

var start = DateTime.UtcNow;
var lastMove = DateTime.MinValue;
var streak = 0;
var prevErr = double.NaN;
var worseStreak = 0;

while ((DateTime.UtcNow - start).TotalSeconds < seconds)
{
    var f = await GetFace();

    if (f is { detected: true, x: not null })
    {
        var err = f.x.Value;

        // A single frame over the deadband is as likely to be detector noise as a
        // real move, so require the offset to persist before committing.
        if (Math.Abs(err) > Deadband) streak++; else streak = 0;

        if (streak >= Persist && (DateTime.UtcNow - lastMove).TotalSeconds > 1.0)
        {
            streak = 0;

            // Runaway guard: if corrections keep making the error worse, the sign
            // is wrong for this unit and continuing would swing her away until she
            // loses the face. Stop rather than spin.
            if (!double.IsNaN(prevErr) && Math.Abs(err) > Math.Abs(prevErr) + 0.02)
            {
                if (++worseStreak >= 3)
                {
                    Console.WriteLine("\nRUNAWAY GUARD: error grew 3 corrections running. Stopping.");
                    break;
                }
            }
            else worseStreak = 0;
            prevErr = err;

            var yaw = await GetYaw();
            var correction = Math.Clamp(err * RadPerX * Gain, -MaxStep, MaxStep);
            var target = yaw + correction;

            Console.WriteLine($"x={err,6:F3}  yaw {yaw,6:F2} -> {target,6:F2}  (d={correction,+6:F2})");
            await Goto(target, 1.0);
            lastMove = DateTime.UtcNow;
        }
    }
    else streak = 0;

    await Task.Delay(150);
}

Console.WriteLine("\nDone. Returning to centre.");
await Goto(0, 1.5);

record Face(bool detected, double? x, double? y, double? roll, double? ts);
record FaceResponse(string status, Face? face_target);
record GotoRequest(double body_yaw, double duration, string interpolation);

[JsonSerializable(typeof(Face))]
[JsonSerializable(typeof(FaceResponse))]
[JsonSerializable(typeof(GotoRequest))]
[JsonSerializable(typeof(double))]
partial class RoseJson : JsonSerializerContext;
