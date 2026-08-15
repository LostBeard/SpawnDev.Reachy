using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// Looks things up on the open web so Rose can research and explain, instead of drawing
/// a blank on anything the local model was not trained on.
/// </summary>
/// <remarks>
/// This is the ONE part of Rose that reaches off the machine: the query text goes to a
/// search source. Everything else - the model, the voice, the microphone - stays local.
/// Two sources, no API key and no account: Wikipedia (clean, reliable, covers most of a
/// kid's "what is / who is / how does" questions) and DuckDuckGo (general web, and DDG
/// does not profile the user). Results are short snippets the model reads and then
/// explains in character and age-appropriately - the model is the filter and the context,
/// which is the whole point of letting her look things up.
/// </remarks>
public sealed class WebResearch
{
    private readonly HttpClient _http;

    /// <summary>Total characters of research context handed back to the model per query.</summary>
    private const int MaxResultChars = 1200;

    public WebResearch()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        // Wikipedia's API requires a descriptive User-Agent or it returns 403.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RoseCompanion/1.0 (local family robot; contact lostit1278@gmail.com)");
    }

    /// <summary>Diagnostic log.</summary>
    public event Action<string>? Log;

    /// <summary>
    /// Researches <paramref name="query"/> and returns a compact block of source snippets
    /// for the model to read, or a short "nothing found" note. Never throws - a failed
    /// lookup returns a note rather than breaking the conversation.
    /// </summary>
    public async Task<string> SearchAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length == 0) return "No search query was given.";

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Both sources run at once, each on its own short budget, so a slow or blocked
        // source cannot drag the whole lookup. Wikipedia is the cleanest and answers most
        // factual questions; DuckDuckGo adds general-web coverage but is the flakier of the
        // two, so it gets the tighter deadline.
        var wikiTask = Safe(c => WikipediaAsync(query, c), TimeSpan.FromSeconds(5), ct);
        var ddgTask = Safe(c => DuckDuckGoAsync(query, c), TimeSpan.FromSeconds(3), ct);
        await Task.WhenAll(wikiTask, ddgTask);

        var parts = new List<string>();
        if (wikiTask.Result is { } wiki) parts.Add(wiki);
        if (ddgTask.Result is { } ddg) parts.Add(ddg);

        Log?.Invoke($"research \"{query}\" -> {parts.Count} source(s) in {sw.ElapsedMilliseconds}ms");

        if (parts.Count == 0) return $"No results were found for \"{query}\".";

        var joined = string.Join("\n\n", parts);
        return joined.Length <= MaxResultChars ? joined : joined[..MaxResultChars] + "...";
    }

    // ---- Wikipedia ----------------------------------------------------------

    private async Task<string?> WikipediaAsync(string query, CancellationToken ct)
    {
        var searchUrl = "https://en.wikipedia.org/w/api.php?action=query&list=search&srlimit=1&format=json&srsearch="
                        + Uri.EscapeDataString(query);
        using var sresp = await _http.GetAsync(searchUrl, ct);
        if (!sresp.IsSuccessStatusCode) return null;

        using var sdoc = JsonDocument.Parse(await sresp.Content.ReadAsStringAsync(ct));
        var hits = sdoc.RootElement.GetProperty("query").GetProperty("search");
        if (hits.GetArrayLength() == 0) return null;

        var title = hits[0].GetProperty("title").GetString();
        if (string.IsNullOrEmpty(title)) return null;

        // The REST summary endpoint returns a clean plain-text intro extract.
        var sumUrl = "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(title.Replace(' ', '_'));
        using var rresp = await _http.GetAsync(sumUrl, ct);
        if (!rresp.IsSuccessStatusCode) return null;

        using var rdoc = JsonDocument.Parse(await rresp.Content.ReadAsStringAsync(ct));
        var extract = rdoc.RootElement.TryGetProperty("extract", out var e) ? e.GetString() : null;
        return string.IsNullOrWhiteSpace(extract) ? null : $"Wikipedia ({title}): {extract.Trim()}";
    }

    // ---- DuckDuckGo ---------------------------------------------------------

    private async Task<string?> DuckDuckGoAsync(string query, CancellationToken ct)
    {
        // Instant Answer API: no key, returns a clean abstract for many topics.
        var url = "https://api.duckduckgo.com/?format=json&no_html=1&t=rose&q=" + Uri.EscapeDataString(query);
        using var resp = await _http.GetAsync(url, ct);
        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var abstractText = doc.RootElement.TryGetProperty("AbstractText", out var a) ? a.GetString() : null;
            if (!string.IsNullOrWhiteSpace(abstractText))
                return $"DuckDuckGo: {abstractText.Trim()}";

            if (doc.RootElement.TryGetProperty("RelatedTopics", out var rt) && rt.ValueKind == JsonValueKind.Array)
            {
                var snippets = new List<string>();
                foreach (var t in rt.EnumerateArray())
                {
                    if (snippets.Count >= 3) break;
                    if (t.TryGetProperty("Text", out var txt) && txt.GetString() is { Length: > 0 } s)
                        snippets.Add(s.Trim());
                }
                if (snippets.Count > 0) return "DuckDuckGo: " + string.Join(" | ", snippets);
            }
        }

        // Fall back to the lightweight HTML results page for general queries the Instant
        // Answer API has nothing for. Best-effort scrape - guarded, never required.
        return await DuckDuckGoHtmlAsync(query, ct);
    }

    private async Task<string?> DuckDuckGoHtmlAsync(string query, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://lite.duckduckgo.com/lite/?q=" + Uri.EscapeDataString(query));
        req.Headers.UserAgent.Clear();
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RoseCompanion/1.0");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var html = await resp.Content.ReadAsStringAsync(ct);
        // The lite page puts each result body in <td class="result-snippet">...</td>.
        var matches = Regex.Matches(html, "<td[^>]*class=\"result-snippet\"[^>]*>(.*?)</td>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var snippets = new List<string>();
        foreach (Match m in matches)
        {
            if (snippets.Count >= 3) break;
            var text = WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<.*?>", " ")).Trim();
            text = Regex.Replace(text, @"\s+", " ");
            if (text.Length > 0) snippets.Add(text);
        }
        return snippets.Count > 0 ? "Web results: " + string.Join(" | ", snippets) : null;
    }

    private async Task<string?> Safe(Func<CancellationToken, Task<string?>> op, TimeSpan budget, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);
        try { return await op(cts.Token); }
        catch (OperationCanceledException) { Log?.Invoke("research source timed out"); return null; }
        catch (Exception ex) { Log?.Invoke($"research source failed: {ex.GetType().Name}: {ex.Message}"); return null; }
    }
}
