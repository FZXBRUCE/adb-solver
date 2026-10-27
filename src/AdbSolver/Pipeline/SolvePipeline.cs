using System.Text;
using System.Text.RegularExpressions;
using AdbSolver.Ai;
using AdbSolver.Core;

namespace AdbSolver.Pipeline;

public sealed record ParsedQuestion
{
    public string Type { get; init; } = "unknown";
    public string Question { get; init; } = "";
    public string Options { get; init; } = "";
    public string AnswerFormat { get; init; } = "";

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Question)
        || Question.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("unknown", StringComparison.OrdinalIgnoreCase) && Question.Trim().Length < 4;
}

public sealed record SolveOutcome
{
    public bool Ok { get; init; }
    public string Error { get; init; } = "";
    public string OcrText { get; init; } = "";
    public ParsedQuestion Question { get; init; } = new();
    public string SearchNotes { get; init; } = "";
    public string Answer { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
    public string ImagePath { get; init; } = "";
}

/// <summary>
/// 核心流水线：截图 → OCR → 题目解析 → 搜索 → 作答。
/// 每个阶段都单独容错，任何一个阶段失败都不会让整个程序崩溃。
/// </summary>
public sealed class SolvePipeline
{
    private readonly AiClient _ai;
    private readonly PipelineConfig _cfg;
    private int _consecutiveFailures;
    private int _processedCount;

    public event Action<string>? OnStage;
    public event Action<SolveOutcome>? OnResult;

    public int ProcessedCount => _processedCount;
    public int ConsecutiveFailures => _consecutiveFailures;

    public SolvePipeline(AiClient ai, PipelineConfig cfg)
    {
        _ai = ai;
        _cfg = cfg;
    }

    public void ResetFailureCounter() => _consecutiveFailures = 0;

    /// <summary>处理一张截图，返回完整结果。</summary>
    public async Task<SolveOutcome> ProcessAsync(string imagePath, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stage = "";

        try
        {
            // ---------- 阶段 1：OCR ----------
            stage = "OCR 识别";
            OnStage?.Invoke(stage);
            var (b64, mime) = await LoadImageAsync(imagePath, ct).ConfigureAwait(false);
            if (b64 is null)
                return Fail("图片读取失败", sw, imagePath);

            var ocr = await _ai.ChatAsync("Ocr", Prompts.OcrSystem, Prompts.OcrUser, b64, mime,
                temperature: 0.0, maxTokens: 3000, ct: ct).ConfigureAwait(false);

            if (!ocr.Ok)
                return Fail("OCR 失败：" + ocr.Error, sw, imagePath);

            var ocrText = CleanOcr(ocr.Text);
            Log.Info($"OCR 完成（{ocr.ProviderName}）：{ocrText.Length} 字符");

            if (ocrText.Equals("EMPTY", StringComparison.OrdinalIgnoreCase) || ocrText.Length < 2)
            {
                Log.Info("截图无有效文字，跳过");
                return new SolveOutcome { Ok = false, Error = "截图无可识别文字", ImagePath = imagePath, Elapsed = sw.Elapsed };
            }

            // ---------- 阶段 2：题目解析 ----------
            stage = "题目解析";
            OnStage?.Invoke(stage);
            var parseUser = string.Format(Prompts.ParseUserTemplate, ocrText);
            var parsed = await _ai.ChatAsync("Ocr", Prompts.ParseSystem, parseUser,
                temperature: 0.0, maxTokens: 1500, ct: ct).ConfigureAwait(false);

            var q = parsed.Ok ? ParseQuestionBlock(parsed.Text) : ParseQuestionBlock(ocrText);

            // 解析失败时降级：直接把 OCR 文本当题干
            if (q.IsEmpty && ocrText.Length >= _cfg.MinQuestionChars)
            {
                Log.Warn("题目解析结果为空，降级使用 OCR 全文作为题干");
                q = new ParsedQuestion
                {
                    Type = GuessType(ocrText),
                    Question = ocrText,
                    Options = "",
                    AnswerFormat = "",
                };
            }

            if (q.IsEmpty)
            {
                Log.Info("未从截图中解析出有效题目，跳过");
                return new SolveOutcome
                {
                    Ok = false,
                    Error = "未解析出有效题目",
                    OcrText = ocrText,
                    Question = q,
                    ImagePath = imagePath,
                    Elapsed = sw.Elapsed,
                };
            }

            if (q.Question.Length < _cfg.MinQuestionChars)
            {
                Log.Info($"题干过短（{q.Question.Length} 字符），跳过");
                return new SolveOutcome
                {
                    Ok = false,
                    Error = "题干过短",
                    OcrText = ocrText,
                    Question = q,
                    ImagePath = imagePath,
                    Elapsed = sw.Elapsed,
                };
            }

            Log.Info($"题目: [{q.Type}] {Truncate(q.Question, 80)}");

            // ---------- 阶段 3：搜索 ----------
            var searchNotes = "";
            if (_cfg.EnableSearch)
            {
                stage = "联网检索";
                OnStage?.Invoke(stage);
                var searchUser = string.Format(Prompts.SearchUserTemplate,
                    q.Type, q.Question, string.IsNullOrWhiteSpace(q.Options) ? "NONE" : q.Options);

                var searchRes = await _ai.ChatAsync("Search", Prompts.SearchSystem, searchUser,
                    temperature: 0.3, maxTokens: 1500, ct: ct).ConfigureAwait(false);

                if (searchRes.Ok)
                {
                    searchNotes = searchRes.Text.Trim();
                    Log.Info($"检索完成（{searchRes.ProviderName}）");
                }
                else
                {
                    Log.Warn("检索阶段失败，将直接作答：" + searchRes.Error);
                }
            }

            // ---------- 阶段 4：作答 ----------
            stage = "生成答案";
            OnStage?.Invoke(stage);
            var solveSystem = string.Format(Prompts.SolveSystemTemplate, _cfg.AnswerStyle);
            var solveUser = _cfg.EnableSearch
                ? string.Format(Prompts.SolveUserTemplate, q.Type, q.Question,
                    string.IsNullOrWhiteSpace(q.Options) ? "NONE" : q.Options,
                    string.IsNullOrWhiteSpace(searchNotes) ? "(无参考资料)" : searchNotes)
                : string.Format(Prompts.DirectSolveUserTemplate, q.Type, q.Question,
                    string.IsNullOrWhiteSpace(q.Options) ? "NONE" : q.Options);

            var answer = await _ai.ChatAsync("Solve", solveSystem, solveUser,
                temperature: 0.2, maxTokens: 1200, ct: ct).ConfigureAwait(false);

            if (!answer.Ok)
                return Fail("作答失败：" + answer.Error, sw, imagePath, ocrText, q, searchNotes);

            var finalAnswer = CleanAnswer(answer.Text);

            _consecutiveFailures = 0;
            _processedCount++;

            var outcome = new SolveOutcome
            {
                Ok = true,
                OcrText = ocrText,
                Question = q,
                SearchNotes = searchNotes,
                Answer = finalAnswer,
                Elapsed = sw.Elapsed,
                ImagePath = imagePath,
            };

            Log.Info($"答案（{answer.ProviderName}，{sw.Elapsed.TotalSeconds:F1}s）: {Truncate(finalAnswer, 120)}");
            OnStage?.Invoke("完成");
            OnResult?.Invoke(outcome);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            Log.Info("流水线已取消");
            return new SolveOutcome { Ok = false, Error = "已取消", ImagePath = imagePath, Elapsed = sw.Elapsed };
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            Log.Error($"流水线异常（{stage}）", ex);
            return Fail($"{stage} 异常：{ex.Message}", sw, imagePath);
        }
    }

    private SolveOutcome Fail(string err, System.Diagnostics.Stopwatch sw, string imagePath,
        string ocr = "", ParsedQuestion? q = null, string search = "")
    {
        _consecutiveFailures++;
        Log.Warn(err);
        return new SolveOutcome
        {
            Ok = false,
            Error = err,
            OcrText = ocr,
            Question = q ?? new ParsedQuestion(),
            SearchNotes = search,
            ImagePath = imagePath,
            Elapsed = sw.Elapsed,
        };
    }

    // ---------------- 解析工具 ----------------

    /// <summary>把 LLM 返回的 TYPE/QUESTION/OPTIONS 块解析成结构体。容错优先。</summary>
    public static ParsedQuestion ParseQuestionBlock(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new ParsedQuestion();

        var text = raw.Replace("\r\n", "\n").Trim();

        var type = ExtractField(text, "TYPE");
        var question = ExtractField(text, "QUESTION");
        var options = ExtractField(text, "OPTIONS");
        var fmt = ExtractField(text, "ANSWER_FORMAT");

        // 模型没按格式来时，退化为"整段当题干"
        if (string.IsNullOrWhiteSpace(question))
        {
            var lines = text.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("TYPE:", StringComparison.OrdinalIgnoreCase))
                .Where(l => !l.TrimStart().StartsWith("ANSWER_FORMAT:", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            question = string.Join("\n", lines).Trim();
        }

        if (string.IsNullOrWhiteSpace(type))
            type = GuessType(question + "\n" + options);

        if (string.Equals(options?.Trim(), "NONE", StringComparison.OrdinalIgnoreCase))
            options = "";

        return new ParsedQuestion
        {
            Type = NormalizeType(type),
            Question = question?.Trim() ?? "",
            Options = options?.Trim() ?? "",
            AnswerFormat = fmt?.Trim() ?? "",
        };
    }

    /// <summary>提取 "KEY: value" 形式的多行字段（value 可跨行）。</summary>
    private static string ExtractField(string text, string key)
    {
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        var capturing = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
            {
                if (capturing) break;
                capturing = true;
                var v = trimmed[(key.Length + 1)..].Trim();
                if (v.Length > 0) sb.AppendLine(v);
                continue;
            }

            if (capturing)
            {
                // 遇到下一个大写字段名就停
                if (Regex.IsMatch(trimmed, @"^[A-Z_]{3,}:\s"))
                    break;
                sb.AppendLine(line);
            }
        }

        return sb.ToString().Trim();
    }

    private static string NormalizeType(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "unknown";
        var s = t.Trim().ToLowerInvariant();
        if (s.StartsWith("choice") || s.Contains("选择")) return "choice";
        if (s.StartsWith("fill") || s.Contains("填空")) return "fill";
        if (s.StartsWith("judge") || s.Contains("判断")) return "judge";
        if (s.StartsWith("short") || s.Contains("简答")) return "short";
        return s.Length > 20 ? "unknown" : s;
    }

    private static string GuessType(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "unknown";
        if (Regex.IsMatch(text, @"^\s*[A-D][\.、\)]\s", RegexOptions.Multiline)) return "choice";
        if (text.Contains("正确") && text.Contains("错误")) return "judge";
        if (Regex.IsMatch(text, @"_{3,}|（\s*）|\(\s*\)")) return "fill";
        return "unknown";
    }

    /// <summary>清理 OCR 输出里的 Markdown 围栏和客套话。</summary>
    private static string CleanOcr(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        s = Regex.Replace(s, @"^```[a-zA-Z]*\s*", "");
        s = Regex.Replace(s, @"\s*```$", "");
        // 去掉模型偶尔加的引导语
        s = Regex.Replace(s, @"^(图片中的文字如下[：:]?|以下是图片中的文字[：:]?|文字内容如下[：:]?)\s*", "",
            RegexOptions.IgnoreCase);
        return s.Trim();
    }

    /// <summary>清理最终答案里的废话。</summary>
    private static string CleanAnswer(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        s = Regex.Replace(s, @"^```[a-zA-Z]*\s*", "");
        s = Regex.Replace(s, @"\s*```$", "");
        s = Regex.Replace(s, @"^(答案是|答案[:：]|正确答案是|正确答案[:：]|Answer[:：])\s*", "",
            RegexOptions.IgnoreCase);
        return s.Trim();
    }

    /// <summary>读图并转 base64。</summary>
    private static async Task<(string? Base64, string Mime)> LoadImageAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return (null, "image/png");

            // 大于 4MB 的图先不做特殊处理，直接发（绝大多数截图都远小于这个值）
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (bytes.Length == 0) return (null, "image/png");

            var mime = DetectMime(bytes);
            return (Convert.ToBase64String(bytes), mime);
        }
        catch (Exception ex)
        {
            Log.Error("读取图片失败", ex);
            return (null, "image/png");
        }
    }

    private static string DetectMime(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return "image/jpeg";
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46)
            return "image/gif";
        if (b.Length >= 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50)
            return "image/webp";
        return "image/png";
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
