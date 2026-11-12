using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataQualityRules.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DataQualityRules.Api.Ai;

public enum AiProvider
{
    Off,
    Simulator,
    OpenAiCompatible
}

public sealed class AiOptions
{
    public const string Section = "Ai";

    public AiProvider Provider { get; set; } = AiProvider.Simulator;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Nunca no appsettings: use user-secrets ou a variável de ambiente Ai__ApiKey.</summary>
    public string? ApiKey { get; set; }

    public decimal InputPricePerMillion { get; set; } = 0.15m;
    public decimal OutputPricePerMillion { get; set; } = 0.60m;
    public int DailyCallLimit { get; set; } = 50;
    public decimal DailyCostLimitUsd { get; set; } = 0.50m;
}

public sealed record ChatResult(string Text, int InputTokens, int OutputTokens);

public interface IChatModel
{
    string Name { get; }

    /// <param name="purpose">Para que é a chamada ("rule_from_text", "fix_suggestion"). O simulador usa para escolher a resposta.</param>
    Task<ChatResult> CompleteAsync(string purpose, string systemPrompt, string userPrompt, CancellationToken ct);
}

/// <summary>Cliente para APIs compatíveis com /v1/chat/completions, pedindo resposta em JSON.</summary>
public sealed class OpenAiCompatibleChatModel(HttpClient http, IOptions<AiOptions> options) : IChatModel
{
    public string Name => options.Value.Model;

    public async Task<ChatResult> CompleteAsync(string purpose, string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var opt = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = opt.Model,
                temperature = 0,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            })
        };

        if (!string.IsNullOrWhiteSpace(opt.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.ApiKey);
        }

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;
        var text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        var usage = root.TryGetProperty("usage", out var u) ? u : default;

        return new ChatResult(
            text,
            usage.ValueKind == JsonValueKind.Object ? usage.GetProperty("prompt_tokens").GetInt32() : 0,
            usage.ValueKind == JsonValueKind.Object ? usage.GetProperty("completion_tokens").GetInt32() : 0);
    }
}

/// <summary>Limite diário e registro de custo, compartilhado pelas funções de IA.</summary>
public sealed class AiBudget(AppDbContext db, IOptions<AiOptions> options, TimeProvider clock)
{
    public async Task<bool> HasBudgetAsync(CancellationToken ct)
    {
        var opt = options.Value;
        var today = clock.GetUtcNow().UtcDateTime.Date;

        // Soma no cliente: o SQLite não soma decimal no banco, e são poucas linhas por dia.
        var costs = await db.AiCalls.AsNoTracking().Where(c => c.AtUtc >= today).Select(c => c.CostUsd).ToListAsync(ct);
        return costs.Count < opt.DailyCallLimit && costs.Sum() < opt.DailyCostLimitUsd;
    }

    public async Task RecordAsync(IChatModel model, string purpose, ChatResult answer, bool valid, CancellationToken ct)
    {
        var opt = options.Value;
        db.AiCalls.Add(new AiCall
        {
            Provider = model.Name,
            Purpose = purpose,
            InputTokens = answer.InputTokens,
            OutputTokens = answer.OutputTokens,
            CostUsd = model is SimulatedChatModel
                ? 0m
                : answer.InputTokens / 1_000_000m * opt.InputPricePerMillion + answer.OutputTokens / 1_000_000m * opt.OutputPricePerMillion,
            ValidAnswer = valid,
            AtUtc = clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(ct);
    }
}

public static class PromptText
{
    /// <summary>
    /// Higieniza texto que vai para dentro do prompt: tira controle e os sinais de tag
    /// (para não "fechar" a tag que isola o dado) e limita o tamanho.
    /// </summary>
    public static string Sanitize(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var clean = new string(value.Where(c => !char.IsControl(c) && c is not '<' and not '>').ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..max];
    }

    /// <summary>Pega o objeto JSON da resposta (alguns modelos embrulham em ```json ... ```).</summary>
    public static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}

/// <summary>
/// Modelo simulado e determinístico: lê os mesmos prompts que um modelo real leria e responde
/// com regras simples. Permite rodar o projeto, os testes e o CI sem chave de API.
/// </summary>
public sealed partial class SimulatedChatModel : IChatModel
{
    private static readonly Dictionary<string, string> FieldWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["isbn"] = "isbn", ["preço"] = "price", ["preco"] = "price", ["estoque"] = "stock", ["nome"] = "name",
        ["categoria"] = "category", ["descrição"] = "description", ["descricao"] = "description",
        ["autor"] = "author", ["sku"] = "sku"
    };

    public string Name => "simulator";

    public Task<ChatResult> CompleteAsync(string purpose, string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var json = purpose == RuleWriter.Purpose ? WriteRule(Tag(userPrompt, "request")) : SuggestFix(Tag(userPrompt, "product"));
        return Task.FromResult(new ChatResult(json, (systemPrompt.Length + userPrompt.Length) / 4, json.Length / 4));
    }

    private static string WriteRule(string request)
    {
        var text = request.ToLowerInvariant();
        string? F(string fragment) => FieldWords.Where(w => fragment.Contains(w.Key)).Select(w => w.Value).FirstOrDefault();

        object? when = null;
        var whenMatch = WhenPattern().Match(text);
        var main = whenMatch.Success ? text[..whenMatch.Index] : text;
        if (whenMatch.Success && F(whenMatch.Groups["f"].Value) is { } wf)
        {
            var original = request.Substring(whenMatch.Groups["v"].Index, whenMatch.Groups["v"].Length).Trim().TrimEnd('.');
            when = new { field = wf, op = "equals", value = original };
        }

        var field = F(main) ?? "name";
        object check;
        var number = NumberPattern().Match(main);

        if (main.Contains("no máximo") || main.Contains("no maximo"))
            check = new { field, op = "maxLength", value = number.Success ? int.Parse(number.Value) : 100 };
        else if (main.Contains("no mínimo") || main.Contains("no minimo"))
            check = new { field, op = "minLength", value = number.Success ? int.Parse(number.Value) : 1 };
        else if (main.Contains("maior ou igual"))
            check = new { field, op = "gte", value = number.Success ? decimal.Parse(number.Value) : 0 };
        else if (main.Contains("maior que") || main.Contains("positivo"))
            check = new { field, op = "gt", value = number.Success ? decimal.Parse(number.Value) : 0 };
        else if (main.Contains("menor que"))
            check = new { field, op = "lt", value = number.Success ? decimal.Parse(number.Value) : 0 };
        else if (main.Contains("13 dígitos") || main.Contains("13 digitos"))
            check = new { field, op = "regex", value = "^[0-9]{13}$" };
        else
            check = new { field, op = "required" };

        var name = request.Length <= 80 ? request.TrimEnd('.') : request[..80];
        return JsonSerializer.Serialize(new { name, when, check, severity = "error", message = $"{name}." },
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    private static string SuggestFix(string productJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(productJson) ? "{}" : productJson);
        var p = doc.RootElement;
        string? S(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var fixes = new List<object>();
        if (string.IsNullOrWhiteSpace(S("category")) && (!string.IsNullOrWhiteSpace(S("isbn")) || !string.IsNullOrWhiteSpace(S("author"))))
            fixes.Add(new { field = "category", value = "Livro", reason = "Tem ISBN ou autor, o que indica um livro." });
        if (string.IsNullOrWhiteSpace(S("description")) && !string.IsNullOrWhiteSpace(S("name")))
            fixes.Add(new { field = "description", value = $"{S("name")!.Trim()}{(S("author") is { } a && a.Length > 0 ? $", de {a}" : "")}.", reason = "Descrição básica a partir do nome e do autor; vale revisar." });
        if (S("name") is { } name && name != name.Trim())
            fixes.Add(new { field = "name", value = name.Trim(), reason = "Remove espaços sobrando no nome." });

        return JsonSerializer.Serialize(new { fixes, note = fixes.Count == 0 ? "Não dá para inferir a correção com segurança a partir dos dados." : null });
    }

    private static string Tag(string prompt, string tag) =>
        Regex.Match(prompt, $"<{tag}>(?<v>.*?)</{tag}>", RegexOptions.Singleline).Groups["v"].Value.Trim();

    [GeneratedRegex(@"quando (?:a |o )?(?<f>\w+) (?:for|é|e|=) (?<v>.+)$")]
    private static partial Regex WhenPattern();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?")]
    private static partial Regex NumberPattern();
}
