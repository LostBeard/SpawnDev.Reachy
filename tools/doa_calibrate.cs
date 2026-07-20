// Rose - DOA calibration logger.
// Polls the Reachy Mini daemon's direction-of-arrival endpoint and reports the
// angle reported by the 4-mic array whenever speech is detected.
//
// Usage: dotnet run doa_calibrate.cs [robot-ip] [seconds]
// Speak from a known direction, note what angle Rose reports, repeat.
//
// File-based apps run with reflection-based JSON disabled, so everything goes
// through the source-generated context at the bottom of this file.

using System.Net.Http.Json;
using System.Text.Json.Serialization;

var ip = args.Length > 0 ? args[0] : "192.168.1.170";
var seconds = args.Length > 1 ? int.Parse(args[1]) : 60;

var http = new HttpClient { BaseAddress = new Uri($"http://{ip}:8000"), Timeout = TimeSpan.FromSeconds(3) };

Console.WriteLine($"Polling DOA on {ip} for {seconds}s. Speak from a known direction.");
Console.WriteLine("Convention check: face Rose head-on and speak, then from her left, then her right.\n");
Console.WriteLine($"{"time",-10}{"rad",-10}{"deg",-10}");
Console.WriteLine(new string('-', 30));

var start = DateTime.UtcNow;
var wasSpeech = false;
var samples = new List<double>();

while ((DateTime.UtcNow - start).TotalSeconds < seconds)
{
    try
    {
        var doa = await http.GetFromJsonAsync("/api/state/doa", RoseJson.Default.Doa);
        if (doa is null) continue;

        if (doa.speech_detected)
        {
            samples.Add(doa.angle);
            // Print on the rising edge, then periodically, so a long utterance
            // does not flood the console.
            if (!wasSpeech || samples.Count % 10 == 0)
            {
                var deg = doa.angle * 180.0 / Math.PI;
                Console.WriteLine($"{(DateTime.UtcNow - start).TotalSeconds,-10:F1}{doa.angle,-10:F3}{deg,-10:F1}");
            }
        }
        else if (wasSpeech && samples.Count > 0)
        {
            // Utterance ended - report the median, which rejects the noisy
            // leading and trailing frames better than a mean.
            samples.Sort();
            var median = samples[samples.Count / 2];
            Console.WriteLine($"  -> utterance end: median {median:F3} rad / {median * 180.0 / Math.PI:F1} deg  ({samples.Count} samples)\n");
            samples.Clear();
        }

        wasSpeech = doa.speech_detected;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  poll error: {ex.Message}");
        await Task.Delay(500);
    }

    await Task.Delay(50);
}

Console.WriteLine("Done.");

record Doa(double angle, bool speech_detected);

[JsonSerializable(typeof(Doa))]
partial class RoseJson : JsonSerializerContext;
