using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// The conversational model behind Rose, running locally through Ollama.
/// </summary>
/// <remarks>
/// Local by requirement, not by preference: this is a ten year old's toy in a
/// family home, so nothing she says leaves the house and there is no account, no
/// subscription and no per-message cost sitting between her and the robot.
///
/// llama3.1:8b was chosen by measurement over qwen3.5:9b and gemma4:12b. Warm TTFT
/// is 0.16s at 68 tok/s, and it is the only one of the three that answered in
/// character AND knew the actual source material - the larger two were offloading
/// to CPU on this box and took 6-26 seconds, which is a different product.
/// </remarks>
public sealed class RoseBrain
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly List<Msg> _history = [];

    /// <summary>How many prior turns to carry. Enough to hold a thread, short enough to stay fast.</summary>
    private const int MaxTurns = 12;

    /// <summary>
    /// How long Ollama keeps the model in VRAM after a request.
    /// </summary>
    /// <remarks>
    /// Ollama's default is five minutes, after which the next question pays the
    /// full model load - measured at 6.4s versus 1.3s warm. A child wanders off and
    /// comes back constantly, so the default turns most "first questions after a
    /// break" into a wait long enough to look broken.
    ///
    /// This pins several GB of VRAM, so <see cref="ReleaseAsync"/> hands it straight
    /// back when the session ends rather than letting it idle out.
    /// </remarks>
    private const string KeepAlive = "30m";

    public RoseBrain(string model = "llama3.1:8b", string endpoint = "http://localhost:11434")
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri(endpoint),
            // First call after a cold start includes the model load. Generation
            // itself is streamed, so this only bounds the wait for the first token.
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    private sealed class Msg
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    /// <summary>Drops the conversation history, keeping the model loaded.</summary>
    public void Forget() => _history.Clear();

    /// <summary>
    /// Loads the model into VRAM so the first real question does not pay for it.
    /// </summary>
    public async Task WarmAsync(CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            prompt = "hi",
            stream = false,
            keep_alive = KeepAlive,
            options = new { num_predict = 1 },
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/api/generate", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Releases the model from VRAM immediately instead of waiting for it to idle out.
    /// </summary>
    /// <remarks>
    /// This is a shared workstation GPU. Holding several GB for half an hour after
    /// the robot has been switched off is not ours to do.
    /// </remarks>
    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { model = _model, keep_alive = 0 });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("/api/generate", content, ct);
        }
        catch { /* best effort - the model idles out on its own regardless */ }
    }

    /// <summary>Verifies Ollama is up and the model is present.</summary>
    public async Task<string?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/api/tags", ct);
            if (!resp.IsSuccessStatusCode) return $"Ollama returned {(int)resp.StatusCode}";

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var names = doc.RootElement.GetProperty("models")
                .EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? "")
                .ToList();

            return names.Any(n => n == _model || n.StartsWith(_model.Split(':')[0]))
                ? null
                : $"model '{_model}' not installed. Available: {string.Join(", ", names)}";
        }
        catch (Exception ex)
        {
            return $"cannot reach Ollama at {_http.BaseAddress}: {ex.Message}";
        }
    }

    /// <summary>
    /// Streams a reply, invoking <paramref name="onSentence"/> as each sentence completes.
    /// </summary>
    /// <remarks>
    /// Sentence-at-a-time is what makes the robot feel responsive. Waiting for the
    /// full reply before synthesising adds the model's entire generation time to the
    /// silence before she speaks; emitting the first sentence as soon as it lands
    /// means she starts talking while the rest is still being written.
    /// </remarks>
    public async Task<string> StreamReplyAsync(
        string userText,
        Character character,
        Func<string, Task> onSentence,
        CancellationToken ct = default)
    {
        _history.Add(new Msg { Role = "user", Content = userText });
        TrimHistory();

        var messages = new List<Msg> { new() { Role = "system", Content = character.Persona } };
        messages.AddRange(_history);

        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            messages,
            stream = true,
            keep_alive = KeepAlive,
            options = new
            {
                // Warm and playful rather than deterministic; she is roleplaying,
                // not looking up facts.
                temperature = 0.8,
                top_p = 0.9,
                num_predict = 200,
            },
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var full = new StringBuilder();
        var pending = new StringBuilder();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            string? chunk;
            try
            {
                using var doc = JsonDocument.Parse(line);
                chunk = doc.RootElement.TryGetProperty("message", out var m)
                    && m.TryGetProperty("content", out var c) ? c.GetString() : null;
            }
            catch (JsonException) { continue; }

            if (string.IsNullOrEmpty(chunk)) continue;

            full.Append(chunk);
            pending.Append(chunk);

            var text = pending.ToString();
            var cut = LastSentenceEnd(text);
            if (cut <= 0) continue;

            var sentence = text[..cut].Trim();
            pending.Remove(0, cut);
            if (sentence.Length > 0) await onSentence(sentence);
        }

        var tail = pending.ToString().Trim();
        if (tail.Length > 0) await onSentence(tail);

        var reply = full.ToString().Trim();
        _history.Add(new Msg { Role = "assistant", Content = reply });
        return reply;
    }

    /// <summary>
    /// Index just past the last sentence-ending punctuation, or 0 if there is none.
    /// </summary>
    /// <remarks>
    /// Deliberately ignores a trailing "." that is still being written - a decimal
    /// point or an abbreviation would otherwise split a sentence mid-word and the
    /// synthesiser would read the fragment with falling intonation.
    /// </remarks>
    internal static int LastSentenceEnd(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] is not ('.' or '!' or '?' or '\n')) continue;

            // Require whitespace after the mark, so "3.5" and "Mr." do not split.
            if (i == text.Length - 1) continue;
            if (!char.IsWhiteSpace(text[i + 1])) continue;

            // An ellipsis is a PAUSE, not an end. These characters lean on it
            // heavily - "It's so... sparkly!" - and splitting there breaks one
            // phrase into two clips with a gap between them, which is audible and
            // makes her sound like she is buffering.
            if (text[i] == '.' && (i > 0 && text[i - 1] == '.')) continue;
            if (text[i] == '.' && i >= 2 && text[i - 1] == ' ' && text[i - 2] == '.') continue;

            return i + 1;
        }
        return 0;
    }

    private void TrimHistory()
    {
        var max = MaxTurns * 2;
        if (_history.Count <= max) return;
        _history.RemoveRange(0, _history.Count - max);
    }
}
