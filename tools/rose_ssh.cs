#:package SSH.NET@2024.*

// Run a command on Rose over SSH. General purpose - the daemon's /logs endpoint
// is a deprecated HTML page, so real diagnostics have to come from journalctl.
//
// Usage: dotnet run rose_ssh.cs "<command>" [host]

using Renci.SshNet;

var command = args.Length > 0 ? args[0] : "uptime";
var host = args.Length > 1 ? args[1] : "192.168.1.170";

using var ssh = new SshClient(host, 22, "pollen", "root");
ssh.Connect();

using var cmd = ssh.CreateCommand(command);
cmd.CommandTimeout = TimeSpan.FromSeconds(60);
var output = cmd.Execute();

Console.Write(output);
if (!string.IsNullOrWhiteSpace(cmd.Error)) Console.Error.Write(cmd.Error);

ssh.Disconnect();
return cmd.ExitStatus ?? 0;
