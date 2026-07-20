#:package SSH.NET@2024.*

// Rose - ALSA volume fix over SSH.
//
// The XVF3800 audio board has a known Linux defect where the PCM,1 mixer control
// sits well below full scale, so playback is quiet even with everything else at
// 100. The daemon's /api/volume/set already reads 100 and the XVF3800 DSP exposes
// no speaker output gain at all (verified against its parameter table), so this
// mixer control is the last software lever before the amplifier itself.
//
// Read-only by default. Pass --apply to actually change anything.
//
// Usage: dotnet run rose_volume_fix.cs [--apply] [host] [user] [password]

using Renci.SshNet;

var apply = args.Contains("--apply");
var rest = args.Where(a => a != "--apply").ToArray();
var host = rest.Length > 0 ? rest[0] : "192.168.1.170";
var user = rest.Length > 1 ? rest[1] : "pollen";
var pass = rest.Length > 2 ? rest[2] : "root";

using var ssh = new SshClient(host, 22, user, pass);

try
{
    ssh.Connect();
}
catch (Exception ex)
{
    Console.WriteLine($"SSH connect to {user}@{host} failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

Console.WriteLine($"Connected to {user}@{host}\n");

string Run(string cmd)
{
    using var c = ssh.CreateCommand(cmd);
    var outp = c.Execute();
    var err = c.Error;
    return string.IsNullOrWhiteSpace(err) ? outp.TrimEnd() : $"{outp.TrimEnd()}\n[stderr] {err.TrimEnd()}";
}

Console.WriteLine("=== sound cards ===");
Console.WriteLine(Run("cat /proc/asound/cards"));

Console.WriteLine("\n=== playback devices ===");
Console.WriteLine(Run("aplay -l"));

// Resolve the ReSpeaker card index rather than assuming 0. Extra USB audio
// devices shift the numbering, which is a documented cause of volume control
// silently failing.
var card = Run("aplay -l | grep -i respeaker | head -n1 | sed -n 's/^card \\([0-9]*\\):.*/\\1/p'").Trim();
if (string.IsNullOrWhiteSpace(card))
{
    Console.WriteLine("\nCould not find a reSpeaker card. Falling back to card 0 for inspection only.");
    card = "0";
}
Console.WriteLine($"\n=== using card {card} ===");

Console.WriteLine("\n=== ALL mixer controls (before) ===");
Console.WriteLine(Run($"amixer -c {card} scontents"));

if (!apply)
{
    Console.WriteLine("\n--- DRY RUN. Re-run with --apply to set PCM,1 to 100% and persist. ---");
    ssh.Disconnect();
    return 0;
}

Console.WriteLine("\n=== applying: PCM,1 -> 100% ===");
Console.WriteLine(Run($"amixer -c {card} set PCM,1 100%"));

Console.WriteLine("\n=== persisting with alsactl store ===");
Console.WriteLine(Run($"sudo alsactl store {card}"));

Console.WriteLine("\n=== mixer controls (after) ===");
Console.WriteLine(Run($"amixer -c {card} scontents"));

ssh.Disconnect();
Console.WriteLine("\nDone. Play something through Rose and compare.");
return 0;
