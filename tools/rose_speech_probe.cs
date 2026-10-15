#:property PublishAot=false
#:property BuiltInComInteropSupport=true
#:project ../SpawnDev.Reachy/SpawnDev.Reachy.csproj

// Connects to the robot with live speech streaming on and plays a short quiet tone through its speaker, to check the connection still comes up with a PCM sender behind the audio track,
// that the microphone still works alongside it, and how long the tone takes to start. Not read-only: it makes a sound.
//
// Usage: dotnet run rose_speech_probe.cs -- [host] [--seconds N] [--amplitude 0..1]
using SpawnDev.Reachy;

var host = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "reachy-mini.local";
double Num(string name, double fallback) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture) : fallback;
var seconds = Num("--seconds", 1.5);
var amplitude = Num("--amplitude", 0.25);

await using var link = new RoseAudioLink(host) { StreamSpeech = true };
var micFrames = 0;
link.OnMicAudio += _ => Interlocked.Increment(ref micFrames);
link.Log += m => { if (m.Contains("track") || m.Contains("state") || m.Contains("send")) Console.WriteLine("  [link] " + m); };
var connect = System.Diagnostics.Stopwatch.StartNew();
await link.ConnectAsync();
Console.WriteLine($"connected in {connect.ElapsedMilliseconds} ms; speech stream: {(link.Speech != null ? "ready" : "NOT created")}");
if (link.Speech == null) return 1;

await Task.Delay(1500);                                     // let the audio path settle
Console.WriteLine($"microphone frames so far: {micFrames}");
var pcm = new short[(int)(RobotSpeechStreamer.SampleRate * seconds)];
for (var i = 0; i < pcm.Length; i++)
{
    var fade = Math.Min(1.0, Math.Min(i, pcm.Length - i) / 2400.0);      // 50 ms fades: no click
    pcm[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / RobotSpeechStreamer.SampleRate) * 32767 * amplitude * fade);
}
var clock = System.Diagnostics.Stopwatch.StartNew();
await link.Speech.SpeakAsync(pcm);
Console.WriteLine($"sent {seconds:0.0} s of tone in {clock.ElapsedMilliseconds} ms ({link.Speech.FramesSent} frames out in total)");
await Task.Delay(1000);
Console.WriteLine($"microphone frames at the end: {micFrames} (the mic must still be flowing)");
return micFrames > 0 ? 0 : 2;
