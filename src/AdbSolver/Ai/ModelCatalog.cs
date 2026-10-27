using System.Net.Http.Headers;
using System.Text.Json;
using AdbSolver.Core;

namespace AdbSolver.Ai;

/// <summary>
/// 从端点拉取可用模型列表。
///
/// 为什么需要这个：中转站（OneAPI / NewAPI / 各种二开面板）的模型名
/// 跟官方**不一样**——同一家 Claude 在中转站可能叫 `claude-sonnet-4`，
/// 也可能叫 `anthropic/claude-sonnet-4`，还可能是站主自己起的别名。
/// 手动敲十有八九被服务端以 400 / model not found 拒掉，
/// 而且报错信息通常只给你一句 "invalid model"，根本猜不出正确的名字。
///
/// 所以直接问服务端要清单，用下拉框选，比瞎猜强。
/// </summary>
public static class ModelCatalog
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AdbSolver/1.0");
        return c;
    }

    public sealed record Result(List<string> Models, string Error)
    {
        public bool Ok => Models.Count > 0;
    }

    /// <summary>
    /// 按协议类型请求对应的模型清单接口：
    ///   OpenAI 兼容 → GET {base}/models
    ///   Anthropic   → GET {base}/v1/models
    ///   Gemini      → GET {base}/v1beta/models?key=...
    /// </summary>
    public static async Task<Result> FetchAsync(
        AiProviderKind kind,
        string baseUrl,
        string apiKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new Result(new(), "ApiKey 是空的，先填 Key 再拉模型列表。");

        try
        {
            return kind switch
            {
                AiProviderKind.Anthropic => await FetchAnthropicAsync(baseUrl, apiKey, ct),
                AiProviderKind.Gemini => await FetchGeminiAsync(baseUrl, apiKey, ct),
                _ => await FetchOpenAiAsync(baseUrl, apiKey, ct),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new Result(new(), "请求超时（30 秒）。检查 BaseUrl 是否可达。");
        }
        catch (Exception ex)
        {
            return new Result(new(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------- OpenAI 兼容 ----------------

    private static async Task<Result> FetchOpenAiAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var url = BuildModelsUrl(baseUrl, "/v1", "/models");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            return new Result(new(), $"HTTP {(int)resp.StatusCode}：{Truncate(raw, 300)}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        // 标准形态：{ "data": [ { "id": "gpt-4o" }, ... ] }
        // 少数中转站直接返回数组，或者用 "models" 当 key，一并兜住
        var arr = FindArray(root, "data") ?? FindArray(root, "models") ?? (root.ValueKind == JsonValueKind.Array ? root : (JsonElement?)null);
        if (arr is null)
            return new Result(new(), "响应里找不到模型数组：" + Truncate(raw, 300));

        var list = new List<string>();
        foreach (var item in arr.Value.EnumerateArray())
        {
            var id = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : (TryGetString(item, "id") ?? TryGetString(item, "name") ?? TryGetString(item, "model"));
            if (!string.IsNullOrWhiteSpace(id)) list.Add(id!.Trim());
        }

        list = list.Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                   .ToList();

        return list.Count == 0
            ? new Result(new(), "服务端返回了空列表，这个 Key 可能没有列模型的权限。")
            : new Result(list, "");
    }

    // ---------------- Anthropic ----------------

    private static async Task<Result> FetchAnthropicAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var url = BuildModelsUrl(baseUrl, "/v1", "/models");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            return new Result(new(), $"HTTP {(int)resp.StatusCode}：{Truncate(raw, 300)}");

        using var doc = JsonDocument.Parse(raw);
        var arr = FindArray(doc.RootElement, "data") ?? FindArray(doc.RootElement, "models");
        if (arr is null)
            return new Result(new(), "响应里找不到模型数组：" + Truncate(raw, 300));

        var list = new List<string>();
        foreach (var item in arr.Value.EnumerateArray())
        {
            var id = TryGetString(item, "id") ?? TryGetString(item, "name");
            if (!string.IsNullOrWhiteSpace(id)) list.Add(id!.Trim());
        }

        list = list.Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                   .ToList();

        return list.Count == 0
            ? new Result(new(), "服务端返回了空列表。")
            : new Result(list, "");
    }

    // ---------------- Gemini ----------------

    private static async Task<Result> FetchGeminiAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var b = string.IsNullOrWhiteSpace(baseUrl)
            ? "https://generativelanguage.googleapis.com"
            : baseUrl.Trim().TrimEnd('/');

        // 去掉用户可能已经写进去的 /v1beta 或 /v1
        foreach (var suffix in new[] { "/v1beta", "/v1" })
            if (b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                b = b[..^suffix.Length];

        var url = $"{b}/v1beta/models?key={Uri.EscapeDataString(apiKey)}&pageSize=200";

        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            return new Result(new(), $"HTTP {(int)resp.StatusCode}：{Truncate(raw, 300)}");

        using var doc = JsonDocument.Parse(raw);
        var arr = FindArray(doc.RootElement, "models");
        if (arr is null)
            return new Result(new(), "响应里找不到 models 数组：" + Truncate(raw, 300));

        var list = new List<string>();
        foreach (var item in arr.Value.EnumerateArray())
        {
            var name = TryGetString(item, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Gemini 返回的是 "models/gemini-2.0-flash"，调用时要的是短名
            var shortName = name.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? name["models/".Length..]
                : name;

            // 只要支持 generateContent 的，embedding / aqa 这些不能用来答题
            if (item.TryGetProperty("supportedGenerationMethods", out var methods)
                && methods.ValueKind == JsonValueKind.Array)
            {
                var canGenerate = methods.EnumerateArray()
                    .Any(m => m.ValueKind == JsonValueKind.String
                              && string.Equals(m.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase));
                if (!canGenerate) continue;
            }

            list.Add(shortName);
        }

        list = list.Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                   .ToList();

        return list.Count == 0
            ? new Result(new(), "没有找到支持 generateContent 的模型。")
            : new Result(list, "");
    }

    // ---------------- 工具 ----------------

    /// <summary>
    /// 拼 /models 地址。用户可能填了：
    ///   https://x.com            → https://x.com/v1/models
    ///   https://x.com/v1         → https://x.com/v1/models
    ///   https://x.com/v1/        → https://x.com/v1/models
    ///   https://x.com/v1/models  → 原样
    /// </summary>
    private static string BuildModelsUrl(string baseUrl, string versionSeg, string path)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) b = "https://api.openai.com/v1";

        if (b.EndsWith(path, StringComparison.OrdinalIgnoreCase)) return b;

        if (b.EndsWith(versionSeg, StringComparison.OrdinalIgnoreCase)
            || b.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase))
            return b + path;

        return b + versionSeg + path;
    }

    private static JsonElement? FindArray(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Array)
            return el;
        return null;
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
