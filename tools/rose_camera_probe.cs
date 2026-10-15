#:property PublishAot=false
#:property BuiltInComInteropSupport=true
#:project ../SpawnDev.Reachy/SpawnDev.Reachy.csproj

// Connects to the robot over WebRTC and reports what its camera delivers: picture size, frames per second, brightness, and (with --save) one picture as a BMP.
// READ-ONLY: it does not move the robot. Asleep, the head is down and the picture is nearly black; wake it first to see the room.
//
// With --wake it wakes the robot first (head up, motors on) and parks it afterwards by the confirmed recipe: home, sleep, wait for the head to stop, motors off.
// Usage: dotnet run rose_camera_probe.cs -- [host] [--seconds N] [--fps N] [--save path.bmp] [--wake]
using SpawnDev.Reachy;

var host = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "reachy-mini.local";
double Num(string name, double fallback) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture) : fallback;
var seconds = Num("--seconds", 12);
var fps = Num("--fps", 5);
var savePath = Array.IndexOf(args, "--save") is var s and >= 0 && s + 1 < args.Length ? args[s + 1] : null;

var wake = args.Contains("--wake");
using var robot = new ReachyMiniClient(host);
if (wake)
{
    await robot.SetMotorModeAsync(MotorMode.Enabled);
    await robot.WakeUpAsync();
    await Task.Delay(6000);   // the move is only queued when the call returns
    Console.WriteLine("robot awake");
}
await using var link = new RoseAudioLink(host) { VideoMaxFramesPerSecond = fps };
var frames = 0; var first = DateTime.MinValue; var last = DateTime.MinValue;
SpawnDev.RTC.Video.RTCVideoFrame? keep = null;
double brightness = 0;
link.OnVideoFrame += f =>
{
    if (frames++ == 0) { first = DateTime.UtcNow; Console.WriteLine($"first picture: {f.Width}x{f.Height}"); }
    last = DateTime.UtcNow;
    long sum = 0; for (var i = 0; i < f.Bgra.Length; i += 4 * 97) sum += (f.Bgra[i] + f.Bgra[i + 1] * 6 + f.Bgra[i + 2] * 3) / 10;
    brightness = sum / (double)(f.Bgra.Length / (4 * 97) + 1);
    keep = f;
};
link.Log += m => { if (m.Contains("video") || m.Contains("track") || m.Contains("state")) Console.WriteLine("  [link] " + m); };
await link.ConnectAsync();
Console.WriteLine($"connected; listening for {seconds:0} s at up to {fps:0} pictures/s");
await Task.Delay(TimeSpan.FromSeconds(seconds));

Console.WriteLine($"{frames} pictures" + (frames > 1 ? $" over {(last - first).TotalSeconds:0.0} s = {frames / Math.Max((last - first).TotalSeconds, 0.001):0.0}/s" : "") + $", mean brightness {brightness:0}/255");
if (savePath != null && keep is { } k)
{
    // 24-bit BMP, bottom-up rows.
    var rowBytes = (k.Width * 3 + 3) & ~3;
    var pixels = new byte[rowBytes * k.Height];
    for (var y = 0; y < k.Height; y++)
        for (var x = 0; x < k.Width; x++)
        {
            var src = (y * k.Width + x) * 4; var dst = (k.Height - 1 - y) * rowBytes + x * 3;
            pixels[dst] = k.Bgra[src]; pixels[dst + 1] = k.Bgra[src + 1]; pixels[dst + 2] = k.Bgra[src + 2];
        }
    using var fs = File.Create(savePath);
    using var w = new BinaryWriter(fs);
    w.Write((byte)'B'); w.Write((byte)'M'); w.Write(54 + pixels.Length); w.Write(0); w.Write(54);
    w.Write(40); w.Write(k.Width); w.Write(k.Height); w.Write((short)1); w.Write((short)24); w.Write(0); w.Write(pixels.Length); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
    w.Write(pixels);
    Console.WriteLine($"saved {savePath}");
}

if (wake)
{
    await robot.GoHomeAsync(1.5);
    await Task.Delay(2500);
    await robot.GotoSleepAsync();
    await Task.Delay(7000);
    await robot.SetMotorModeAsync(MotorMode.Disabled);
    Console.WriteLine("robot parked");
}
