// Which local model should be N?
//
// For a spoken conversation, time-to-first-token IS the product: it is how long
// Aubs stands there after she stops talking. Total generation time matters much
// less because TTS can start speaking the first sentence while the rest streams.
//
// Also checks character adherence with her real question, and flags reasoning
// models - a visible <think> block is dead air in a voice pipeline and can leak
// into speech.

using System.Diagnostics;
using System.Text;
using System.Text.Json;

string[] models =
[
    "llama3.1:8b",
    "qwen3.5:9b-q4_K_M",
    "gemma4:12b",
    "nemotron-3-nano:4b",
    "qwen2.5:1.5b-instruct-q4_K_M",
];

const string Persona = """
    You are Serial Designation N from Murder Drones. You are cheerful, warm, goofy
    and eager to please. You apologise a lot and get excited about whatever other
    people like. You say things like "Oh gosh!" and "Wait, really?!".
    You are talking to Aubriella, who is ten. Keep replies to one to three
    sentences. Say yes and then say why, with a real reason.
    """;

const string Question = "N, would you like My Little Pony?";

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

Console.WriteLine($"prompt: \"{Question}\"\n");
Console.WriteLine($"{"model",-32}{"TTFT",-10}{"total",-10}{"tok/s",-9}think");
Console.WriteLine(new string('-', 75));

var results = new List<(string Model, double Ttft, double Total, double Tps, bool Think, string Text)>();

// Measure each model TWICE and keep the second. The first call pays for loading
// the weights into VRAM, which on a cold 12B is tens of seconds and has nothing
// to do with conversational latency - a resident model never pays it again.
foreach (var model in models)
for (var pass = 0; pass < 2; pass++)
{
    var warmup = pass == 0;
    // Built by hand: file-based apps run with reflection-based JSON disabled, so
    // anonymous types cannot be serialised. JsonEncodedText handles the escaping.
    var body = $$"""
        {"model":"{{JsonEncodedText.Encode(model)}}","stream":true,"messages":[
          {"role":"system","content":"{{JsonEncodedText.Encode(Persona)}}"},
          {"role":"user","content":"{{JsonEncodedText.Encode(Question)}}"}
        ]}
        """;

    var sw = Stopwatch.StartNew();
    double ttft = -1;
    var sb = new StringBuilder();
    var tokens = 0;

    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:11434/api/chat")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("message", out var m) &&
                m.TryGetProperty("content", out var c))
            {
                var chunk = c.GetString() ?? "";
                if (chunk.Length > 0)
                {
                    if (ttft < 0) ttft = sw.Elapsed.TotalSeconds;
                    sb.Append(chunk);
                    tokens++;
                }
            }
            if (doc.RootElement.TryGetProperty("done", out var d) && d.GetBoolean()) break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{model,-32}ERROR: {ex.Message}");
        continue;
    }

    sw.Stop();
    if (warmup) continue;   // first pass only paid the VRAM load cost

    var text = sb.ToString();
    if (text.Trim().Length == 0)
    {
        Console.WriteLine($"{model,-32}EMPTY RESPONSE - produced no content");
        continue;
    }
    // A reasoning model emits a think block; in a voice pipeline that is silence
    // the listener has to wait through, and it can leak into the spoken output.
    var think = text.Contains("<think", StringComparison.OrdinalIgnoreCase)
             || text.Contains("</think", StringComparison.OrdinalIgnoreCase);
    var total = sw.Elapsed.TotalSeconds;
    var tps = tokens / Math.Max(total, 0.001);

    Console.WriteLine($"{model,-32}{ttft,-10:F2}{total,-10:F2}{tps,-9:F1}{(think ? "YES" : "-")}");
    results.Add((model, ttft, total, tps, think, text));
}

Console.WriteLine("\n\n=== responses ===\n");
foreach (var r in results)
{
    Console.WriteLine($"--- {r.Model}  (TTFT {r.Ttft:F2}s){(r.Think ? "  [REASONING MODEL]" : "")}");
    Console.WriteLine(r.Text.Trim());
    Console.WriteLine();
}
