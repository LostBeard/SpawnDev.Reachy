#:property PublishAot=false
#:project ../SpawnDev.Reachy/SpawnDev.Reachy.csproj

// How fast the robot's WebRTC data channel (the audio upload path) moves bytes, on its own: uploads junk of a few sizes and times it until the robot answers the play command
// (with an error, since it is not audio, which is the proof that every byte arrived). Compare with tools/rose_link_test.cs, the same Wi-Fi link measured with plain TCP.
//
// Usage: dotnet run rose_channel_speed.cs -- [host] [--burst N] [--period ms]
using SpawnDev.Reachy;

var host = args.Length > 0 ? args[0] : "reachy-mini.local";
var burst = Array.IndexOf(args, "--burst") is var bi and >= 0 && bi + 1 < args.Length ? int.Parse(args[bi + 1]) : 32;
var period = Array.IndexOf(args, "--period") is var pi and >= 0 && pi + 1 < args.Length ? int.Parse(args[pi + 1]) : 10;
await using var link = new RoseAudioLink(host) { SctpMaxBurst = burst, SctpBurstPeriodMilliseconds = period };
Console.WriteLine($"SCTP MaxBurst {burst}, burst period {(period == 0 ? "default" : period + " ms")}");
await link.ConnectAsync();
for (var i = 0; i < 50 && link.Data is not { IsOpen: true }; i++) await Task.Delay(100);
var data = link.Data ?? throw new Exception("the data channel did not open");
Console.WriteLine("data channel open\n");
foreach (var kb in new[] { 50, 150, 400, 1000, 1000 })
{
    var junk = new byte[kb * 1024];
    new Random(kb).NextBytes(junk);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var id = await data.UploadAudioAsync(junk);
    var queued = clock.ElapsedMilliseconds;
    try { await data.PlayUploadedAudioAsync(id, TimeSpan.FromSeconds(60)); }
    catch (InvalidOperationException) { /* expected: not audio; the answer means the robot has all of it */ }
    var total = clock.ElapsedMilliseconds;
    Console.WriteLine($"{kb,5} KB: handed to the channel in {queued,5} ms, robot confirmed after {total,5} ms = {kb / (total / 1000.0),7:0} KB/s");
    await Task.Delay(500);
}
