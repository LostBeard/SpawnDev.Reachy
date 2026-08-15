using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
    /// Context window, in tokens. Ollama defaults to a mere 2048 when the request does
    /// not ask for more, and once the persona (a few hundred tokens of system prompt)
    /// plus a growing history crosses that line Ollama SILENTLY drops the oldest
    /// messages - which is Rose losing the thread of the conversation mid-chat. 8192
    /// holds the whole <see cref="MaxTurns"/> window plus the persona with room to spare
    /// and costs little KV-cache on an 8B model.
    /// </summary>
    /// <remarks>
    /// This MUST match between <see cref="WarmAsync"/> and <see cref="StreamReplyAsync"/>:
    /// Ollama reloads the model whenever num_ctx changes between calls, so warming at one
    /// size and chatting at another would pay the full load on the first real question -
    /// exactly the wait the warm-up exists to avoid.
    /// </remarks>
    private const int NumCtx = 8192;

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

    /// <summary>
    /// The web-research tool, or null to run without it. When set, the model may call
    /// <c>web_search</c> to look things up mid-reply and answer from the results.
    /// </summary>
    private readonly WebResearch? _research;

    /// <summary>Most rounds of "model calls a tool, we search, model continues" per turn.</summary>
    private const int MaxToolRounds = 3;

    public RoseBrain(string model = "llama3.1:8b", string endpoint = "http://localhost:11434", WebResearch? research = null)
    {
        _model = model;
        _research = research;
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
        public string Role = "";
        public string Content = "";
        /// <summary>Set on an assistant message that asked to call tools.</summary>
        public List<ToolCall>? ToolCalls;
        /// <summary>Set on a tool-result message: the tool it answers.</summary>
        public string? ToolName;
    }

    private sealed record ToolCall(string Name, string Query);

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
            // num_ctx must match the chat call's, or Ollama reloads the model on the
            // first real question and the warm-up buys nothing.
            options = new { num_predict = 1, num_ctx = NumCtx },
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

        // The model may want to look something up. That is a loop: it asks for a
        // web_search, we run it, feed the result back, and it either answers or searches
        // again. The tool-call and result messages are TRANSIENT - kept only for this
        // turn's follow-up calls, never persisted - so history stays a clean
        // user/assistant thread (no context bloat, no dangling tool message after a trim).
        var working = new List<Msg>();
        var reply = "";

        // Only OFFER the tool when the utterance actually looks like a research request.
        // An 8B model, handed a tool every turn, calls it constantly - it will "look up"
        // ketchup and My Little Pony - which is slow and absurd. Gating the offer keeps
        // ordinary chat instant and offline, and still lets her look things up on request.
        var mayResearch = _research is not null && ResearchWorthy(userText);

        for (var round = 0; round < MaxToolRounds; round++)
        {
            // Offer the tool except on the final allowed round, where we force an answer.
            var offerTools = mayResearch && round < MaxToolRounds - 1;
            var (content, toolCalls) = await StreamOnceAsync(character, working, offerTools, onSentence, ct);

            if (toolCalls.Count == 0) { reply = content; break; }

            // Record what it asked for, then answer each search back to it.
            working.Add(new Msg { Role = "assistant", Content = content, ToolCalls = toolCalls });
            foreach (var tc in toolCalls)
            {
                var result = tc.Name == "web_search"
                    ? await _research!.SearchAsync(tc.Query, ct)
                    : $"Unknown tool '{tc.Name}'.";
                working.Add(new Msg { Role = "tool", Content = result, ToolName = tc.Name });
            }
        }

        _history.Add(new Msg { Role = "assistant", Content = reply });
        return reply.Trim();
    }

    /// <summary>
    /// One streaming request. Emits spoken sentences from any content as it arrives, and
    /// returns the full content plus any tool calls the model asked for.
    /// </summary>
    private async Task<(string Content, List<ToolCall> ToolCalls)> StreamOnceAsync(
        Character character, List<Msg> working, bool offerTools, Func<string, Task> onSentence, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = BuildMessages(character, working),
            ["stream"] = true,
            ["keep_alive"] = KeepAlive,
            ["options"] = new JsonObject
            {
                ["temperature"] = 0.8,
                ["top_p"] = 0.9,
                ["num_predict"] = 200,
                ["num_ctx"] = NumCtx,
            },
        };
        if (offerTools) body["tools"] = BuildTools();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var full = new StringBuilder();
        var pending = new StringBuilder();
        var toolCalls = new List<ToolCall>();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("message", out var m)) continue;

                if (m.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    foreach (var tc in tcs.EnumerateArray())
                        if (ParseToolCall(tc) is { } call) toolCalls.Add(call);

                if (!m.TryGetProperty("content", out var c) || c.GetString() is not { Length: > 0 } chunk) continue;

                full.Append(chunk);
                pending.Append(chunk);

                var cut = LastSentenceEnd(pending.ToString());
                if (cut <= 0) continue;

                var sentence = pending.ToString()[..cut].Trim();
                pending.Remove(0, cut);
                if (sentence.Length > 0) await onSentence(sentence);
            }
            catch (JsonException) { continue; }
        }

        var tail = pending.ToString().Trim();
        if (tail.Length > 0) await onSentence(tail);

        return (full.ToString().Trim(), toolCalls);
    }

    /// <summary>
    /// True when the utterance is actually a request to look something up, so the tool is
    /// worth offering. An explicit "look it up / search / research" always qualifies. A
    /// factual question about the WORLD qualifies too - but a personal or roleplay question
    /// ("what's YOUR favorite color", "where do WE live") does not, since those are answered
    /// from character and the injected show lore, not the web.
    /// </summary>
    internal static bool ResearchWorthy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.ToLowerInvariant();

        // An explicit ask wins even if it also says "you" ("can YOU look it up").
        if (Regex.IsMatch(t, @"\b(look (it |that |this )?up|looks? up|search|google|research|find out|look into)\b"))
            return true;

        // Personal / roleplay / in-world questions are not web research.
        if (Regex.IsMatch(t, @"\b(you|your|yours|you're|we|we're|our|us|i|i'm|i've|my|me|myself|let's)\b"))
            return false;

        // A factual question about the outside world.
        if (Regex.IsMatch(t, @"^\s*(what|whats|what's|who|whos|who's|where|when|how|why|which)\b"))
            return true;
        return Regex.IsMatch(t, @"\b(tell me about|what is|what are|who is|who are|how many|how much|how do|how does)\b");
    }

    private static ToolCall? ParseToolCall(JsonElement tc)
    {
        if (!tc.TryGetProperty("function", out var fn)) return null;
        var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (name.Length == 0) return null;

        var query = "";
        if (fn.TryGetProperty("arguments", out var args))
        {
            // Ollama sends arguments as an object; some models emit a JSON string.
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("query", out var q))
                query = q.GetString() ?? "";
            else if (args.ValueKind == JsonValueKind.String)
                try
                {
                    using var ad = JsonDocument.Parse(args.GetString() ?? "");
                    if (ad.RootElement.TryGetProperty("query", out var q2)) query = q2.GetString() ?? "";
                }
                catch { /* leave query empty; the tool returns a "no query" note */ }
        }
        return new ToolCall(name, query);
    }

    /// <summary>Builds the messages array: system persona, persisted history, this turn's tool messages.</summary>
    private JsonArray BuildMessages(Character character, List<Msg> working)
    {
        var arr = new JsonArray { Message("system", character.Persona) };
        foreach (var m in _history) arr.Add(ToMessage(m));
        foreach (var m in working) arr.Add(ToMessage(m));
        return arr;

        static JsonObject ToMessage(Msg m)
        {
            var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
            if (m.ToolCalls is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var tc in m.ToolCalls)
                    calls.Add(new JsonObject
                    {
                        ["function"] = new JsonObject
                        {
                            ["name"] = tc.Name,
                            ["arguments"] = new JsonObject { ["query"] = tc.Query },
                        },
                    });
                o["tool_calls"] = calls;
            }
            if (m.ToolName is not null) o["tool_name"] = m.ToolName;
            return o;
        }
    }

    private static JsonObject Message(string role, string content) =>
        new() { ["role"] = role, ["content"] = content };

    /// <summary>The one tool the model is offered: look something up on the web.</summary>
    private static JsonArray BuildTools() =>
    [
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "web_search",
                ["description"] =
                    "Look up real, current, or factual information on the web (Wikipedia and DuckDuckGo). "
                    + "Use it when Aubs asks about something you do not know, asks you to look something up, "
                    + "or asks to research a topic. Do NOT use it for ordinary chat, feelings, or roleplay - "
                    + "only when you genuinely need facts you do not already have.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The search terms to look up.",
                        },
                    },
                    ["required"] = new JsonArray { "query" },
                },
            },
        },
    ];

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
