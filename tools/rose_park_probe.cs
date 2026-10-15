#:property PublishAot=false
#:project ../SpawnDev.Reachy/SpawnDev.Reachy.csproj

// Wakes the robot and parks it with ReachyMiniClient.ParkAsync (home, goto_sleep, wait until the sleep move is OVER, motors off), timing each step.
// Not read-only: the robot moves. Check the result against the daemon's own log (journalctl): goto_sleep and set_mode/disabled must be a whole sleep move apart.
//
// Usage: dotnet run rose_park_probe.cs -- [host]
using SpawnDev.Reachy;

var host = args.Length > 0 ? args[0] : "reachy-mini.local";
using var robot = new ReachyMiniClient(host);
var clock = System.Diagnostics.Stopwatch.StartNew();
await robot.SetMotorModeAsync(MotorMode.Enabled);
await robot.WaitForMoveAsync(await robot.WakeUpAsync(), TimeSpan.FromSeconds(10));
Console.WriteLine($"awake at {clock.ElapsedMilliseconds} ms");
await Task.Delay(1500);
var parkStart = clock.ElapsedMilliseconds;
await robot.ParkAsync();
Console.WriteLine($"parked in {clock.ElapsedMilliseconds - parkStart} ms; motors: {(await robot.GetMotorStatusAsync())?.Mode}");
