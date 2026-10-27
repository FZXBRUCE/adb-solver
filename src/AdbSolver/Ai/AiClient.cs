using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdbSolver.Core;

namespace AdbSolver.Ai;

public sealed record AiMessage(string Role, string Content);

public sealed record AiResult(bool Ok, string Text, string Error, string ProviderName, string Model)
{
    public static AiResult Fail(string err, string provider = "", string model = "") =>
        new(false, "", err, provider, model);
}

/// <summary>
/// 多厂商 AI 客户端。支持三种协议族：
///   - OpenAI 兼容（/v1/chat/completions）：DeepSeek / Kimi / 通义 / 智谱 / OneAPI / NewAPI / 各类中转站
///   - Anthropic（/v1/messages）：Claude 官方
///   - Gemini（/v1beta/models/{model}:generateContent）：Google 官方
///
/// 统一支持纯文本与图片（base64）两种输入，这样同一个"识图"接口可以喂给所有厂商。
/// 内置故障转移：按配置顺序尝试，第一个成功的胜出。
/// </summary>
public sealed class AiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly List<AiEndpointConfig> _endpoints;
    private readonly object _rrLock = new();
    private int _rrCursor;

    public AiClient(IEnumerable<AiEndpointConfig> endpoints)
    {
        _endpoints = endpoints.Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.ApiKey)).ToList();

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(180),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AdbSolver/1.0");
    }

    public int AvailableCount => _endpoints.Count;

    public IReadOnlyList<AiEndpointConfig> Endpoints => _endpoints;

    private List<AiEndpointConfig> CandidatesFor(string role)
    {
        var list = _endpoints.Where(e => e.Supports(role)).ToList();
        if (list.Count == 0) list = _endpoints.ToList();

        // 轮转起点，避免每次都压第一个端点
        if (list.Count <= 1) return list;
        int start;
        lock (_rrLock) { start = _rrCursor++ % list.Count; }
        return list.Skip(start).Concat(list.Take(start)).ToList();
    }

    /// <summary>
    /// 发起一次对话。imageBase64 不为空时按多模态图片输入处理（用于 OCR）。
    /// </summary>
    public async Task<AiResult> ChatAsync(
        string role,
        string systemPrompt,
        string userPrompt,
        string? imageBase64 = null,
        string? imageMime = null,
        double temperature = 0.2,
        int maxTokens = 2048,
        CancellationToken ct = default)
    {
        var candidates = CandidatesFor(role);
        if (candidates.Count == 0)
            return AiResult.Fail("没有可用的 AI 端点。请先在「设置 → AI 端点」里填写 BaseUrl / ApiKey / Model。");

        var errors = new List<string>();

        foreach (var ep in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var text = ep.Kind switch
                {
                    AiProviderKind.Anthropic => await CallAnthropicAsync(
                        ep, systemPrompt, userPrompt, imageBase64, imageMime, temperature, maxTokens, ct),
                    AiProviderKind.Gemini => await CallGeminiAsync(
                        ep, systemPrompt, userPrompt, imageBase64, imageMime, temperature, maxTokens, ct),
                    _ => await CallOpenAiAsync(
                        ep, systemPrompt, userPrompt, imageBase64, imageMime, temperature, maxTokens, ct),
                };

                if (!string.IsNullOrWhiteSpace(text))
                {
                    Log.Info($"[{role}] {ep.Name}/{ep.Model} 返回 {text.Length} 字符");
                    return new AiResult(true, text.Trim(), "", ep.Name, ep.Model);
                }

                errors.Add($"{ep.Name}: 返回空内容");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var msg = $"{ep.Name}: {ex.Message}";
                errors.Add(msg);
                Log.Warn($"[{role}] 端点失败 → {msg}");
            }
        }

        return AiResult.Fail("所有端点均失败：\n" + string.Join("\n", errors));
    }

    // ---------------- OpenAI 兼容 ----------------

    private async Task<string> CallOpenAiAsync(
        AiEndpointConfig ep, string system, string user,
        string? imgB64, string? imgMime, double temp, int maxTokens, CancellationToken ct)
    {
        var url = CombineUrl(ep.BaseUrl, "/chat/completions");

        var userContent = new JsonArray();
        if (!string.IsNullOrEmpty(imgB64))
        {
            userContent.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = $"data:{imgMime ?? "image/png"};base64,{imgB64}",
                },
            });
        }
        userContent.Add(new JsonObject { ["type"] = "text", ["text"] = user });

        var body = new JsonObject
        {
            ["model"] = ep.Model,
            ["temperature"] = temp,
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = userContent },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {Truncate(raw, 400)}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errEl))
            throw new InvalidOperationException(Truncate(errEl.ToString(), 400));

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var c0 = choices[0];
            if (c0.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                return content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : content.ToString();
            if (c0.TryGetProperty("text", out var t))
                return t.GetString() ?? "";
        }

        throw new InvalidOperationException("响应中没有 choices: " + Truncate(raw, 300));
    }

    // ---------------- Anthropic ----------------

    private async Task<string> CallAnthropicAsync(
        AiEndpointConfig ep, string system, string user,
        string? imgB64, string? imgMime, double temp, int maxTokens, CancellationToken ct)
    {
        var baseUrl = ep.BaseUrl.Contains("/v1", StringComparison.OrdinalIgnoreCase)
            ? ep.BaseUrl
            : CombineUrl(ep.BaseUrl, "/v1");
        var url = CombineUrl(baseUrl, "/messages");

        var content = new JsonArray();
        if (!string.IsNullOrEmpty(imgB64))
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = imgMime ?? "image/png",
                    ["data"] = imgB64,
                },
            });
        }
        content.Add(new JsonObject { ["type"] = "text", ["text"] = user });

        var body = new JsonObject
        {
            ["model"] = ep.Model,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temp,
            ["system"] = system,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = content },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", ep.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {Truncate(raw, 400)}");

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("content", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var blk in arr.EnumerateArray())
                if (blk.TryGetProperty("text", out var t))
                    sb.Append(t.GetString());
            return sb.ToString();
        }

        throw new InvalidOperationException("响应中没有 content: " + Truncate(raw, 300));
    }

    // ---------------- Gemini ----------------

    private async Task<string> CallGeminiAsync(
        AiEndpointConfig ep, string system, string user,
        string? imgB64, string? imgMime, double temp, int maxTokens, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(ep.BaseUrl)
            ? "https://generativelanguage.googleapis.com/v1beta"
            : ep.BaseUrl.TrimEnd('/');

        if (!baseUrl.Contains("/v1", StringComparison.OrdinalIgnoreCase))
            baseUrl = CombineUrl(baseUrl, "/v1beta");

        var url = $"{baseUrl}/models/{ep.Model}:generateContent?key={Uri.EscapeDataString(ep.ApiKey)}";

        var parts = new JsonArray();
        if (!string.IsNullOrEmpty(imgB64))
        {
            parts.Add(new JsonObject
            {
                ["inline_data"] = new JsonObject
                {
                    ["mime_type"] = imgMime ?? "image/png",
                    ["data"] = imgB64,
                },
            });
        }
        parts.Add(new JsonObject { ["text"] = user });

        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = system } },
            },
            ["contents"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["parts"] = parts },
            },
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = temp,
                ["maxOutputTokens"] = maxTokens,
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {Truncate(raw, 400)}");

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("candidates", out var cands)
            && cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0)
        {
            var sb = new StringBuilder();
            if (cands[0].TryGetProperty("content", out var c) && c.TryGetProperty("parts", out var ps))
                foreach (var p in ps.EnumerateArray())
                    if (p.TryGetProperty("text", out var t))
                        sb.Append(t.GetString());
            return sb.ToString();
        }

        throw new InvalidOperationException("响应中没有 candidates: " + Truncate(raw, 300));
    }

    // ---------------- 工具 ----------------

    private static string CombineUrl(string baseUrl, string path)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) b = "https://api.openai.com/v1";
        // 允许用户直接粘贴完整 endpoint
        if (b.EndsWith(path, StringComparison.OrdinalIgnoreCase)) return b;
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            || b.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            return b;
        return b + path;
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    public void Dispose() => _http.Dispose();
}
