#:package SSH.NET@2026.*

// Measures the Wi-Fi link to the robot with plain TCP (none of the SDK in the way): PC -> robot for a few seconds, robot -> PC for a few seconds, and what the robot's Wi-Fi
// says about the link (signal, the rates each side is using, retries, power save). Re-run it after each router or robot change and compare.
//
// Found 2026-10-15: PC -> robot 90 KB/s, robot -> PC 5.4 MB/s, the robot receiving at 1.0 Mbit/s with the signal at -16 dBm.
// Needs SSH to the robot (pollen / root, like rose_ssh.cs). Leaves nothing behind on it.
//
// Usage: dotnet run rose_link_test.cs -- [robot-ip] [seconds]
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Renci.SshNet;

var host = args.Length > 0 ? args[0] : "192.168.1.170";
var seconds = args.Length > 1 ? double.Parse(args[1]) : 8;

string Local()
{
    using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    s.Connect(host, 8000);
    return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
}
var me = Local();

using var ssh = new SshClient(host, 22, "pollen", "root");
ssh.Connect();
string Run(string cmd, int timeoutSeconds = 30)
{
    using var c = ssh.CreateCommand(cmd);
    c.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
    return c.Execute();
}

const string RobotSink = """
import socket, time, sys
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("0.0.0.0", 5001)); s.listen(1); s.settimeout(30)
c, a = s.accept(); t0 = time.time(); n = 0
while True:
    d = c.recv(65536)
    if not d: break
    n += len(d)
open("/tmp/linktest.out", "w").write("%d %.3f" % (n, time.time() - t0))
""";
const string RobotSend = """
import socket, time, sys
s = socket.create_connection((sys.argv[1], 5002), timeout=10)
buf = b"x" * 16384; n = 0; t0 = time.time()
while time.time() - t0 < float(sys.argv[2]):
    s.sendall(buf); n += len(buf)
s.close()
print("%d %.3f" % (n, time.time() - t0))
""";

string Link() => Run("sudo /sbin/iw dev wlan0 station dump | grep -E 'signal:|tx bitrate|rx bitrate|tx retries|tx failed'; sudo /sbin/iw dev wlan0 get power_save; nmcli -t -f ACTIVE,SSID,CHAN,FREQ dev wifi | grep '^yes'")
    .Replace("\t", " ").Trim();

Console.WriteLine("robot's view of the link before:\n" + Link() + "\n");

// PC -> robot
Run("sudo pkill -f "[l]inktest_sink.py"; rm -f /tmp/linktest.out /tmp/linktest_sink.py");
Run($"cat > /tmp/linktest_sink.py <<'PY'\n{RobotSink}\nPY\nnohup python3 /tmp/linktest_sink.py > /dev/null 2>&1 &");
await Task.Delay(1000);
long sent = 0;
var clock = Stopwatch.StartNew();
using (var tcp = new TcpClient())
{
    await tcp.ConnectAsync(host, 5001);
    var stream = tcp.GetStream();
    var buf = new byte[16384];
    while (clock.Elapsed.TotalSeconds < seconds) { await stream.WriteAsync(buf); sent += buf.Length; }
}
var pcSeconds = clock.Elapsed.TotalSeconds;
await Task.Delay(1500);
var during = Run("sudo /sbin/iw dev wlan0 station dump | grep -E 'rx bitrate|signal:'").Replace("\t", " ").Trim();
var got = Run("cat /tmp/linktest.out 2>/dev/null").Split(' ');
Run("sudo pkill -f "[l]inktest_sink.py"; rm -f /tmp/linktest.out /tmp/linktest_sink.py");
if (got.Length == 2)
{
    var n = long.Parse(got[0]); var dt = double.Parse(got[1], System.Globalization.CultureInfo.InvariantCulture);
    Console.WriteLine($"PC -> robot: {n / 1024 / dt:0} KB/s ({n * 8 / 1e6 / dt:0.0} Mbit/s), {n / 1024} KB in {dt:0.0} s   [the robot received this much; the PC handed over {sent / 1024} KB in {pcSeconds:0.0} s]");
}
else Console.WriteLine($"PC -> robot: no result from the robot (PC handed over {sent / 1024} KB in {pcSeconds:0.0} s = {sent / 1024 / pcSeconds:0} KB/s)");

// robot -> PC
var listener = new TcpListener(IPAddress.Any, 5002);
listener.Start();
Run($"cat > /tmp/linktest_send.py <<'PY'\n{RobotSend}\nPY");
var sender = Task.Run(() => Run($"python3 /tmp/linktest_send.py {me} {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}", (int)seconds + 20));
using var acceptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
TcpClient? incoming = null;
try { incoming = await listener.AcceptTcpClientAsync(acceptTimeout.Token); }
catch (OperationCanceledException) { Console.WriteLine("robot -> PC: the robot could not connect back to this PC (a firewall rule for dotnet.exe on port 5002?); skipped"); }
if (incoming != null)
{
    using var client = incoming;
    var rx = client.GetStream();
    var buf = new byte[65536];
    long total = 0;
    var rxClock = Stopwatch.StartNew();
    int r;
    while ((r = await rx.ReadAsync(buf)) > 0) total += r;
    var dt = rxClock.Elapsed.TotalSeconds;
    Console.WriteLine($"robot -> PC: {total / 1024 / dt:0} KB/s ({total * 8 / 1e6 / dt:0.0} Mbit/s), {total / 1024} KB in {dt:0.0} s");
}
listener.Stop();
try { await sender.WaitAsync(TimeSpan.FromSeconds(20)); } catch (Exception) { }
Run("rm -f /tmp/linktest_send.py");

Console.WriteLine("\nrobot's view of the link while receiving:\n" + during);
Console.WriteLine("\nafter:\n" + Link());
