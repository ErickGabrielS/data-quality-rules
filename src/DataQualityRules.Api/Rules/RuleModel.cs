using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DataQualityRules.Api.Catalog;

namespace DataQualityRules.Api.Rules;

/// <summary>
/// Condição declarativa. Ou é uma folha (<see cref="Field"/> + <see cref="Op"/> [+ <see cref="Value"/>]),
/// ou um grupo (<see cref="All"/>, <see cref="Any"/> ou <see cref="Not"/>). Exemplo:
/// <c>{"field": "category", "op": "equals", "value": "Livro"}</c>
/// </summary>
public sealed class Condition
{
    public string? Field { get; set; }
    public string? Op { get; set; }
    public JsonElement? Value { get; set; }
    public List<Condition>? All { get; set; }
    public List<Condition>? Any { get; set; }
    public Condition? Not { get; set; }
}

/// <summary>
/// Regra de qualidade: "quando <see cref="When"/> for verdadeiro, <see cref="Check"/> precisa ser verdadeiro".
/// Sem <see cref="When"/>, vale para todos os produtos.
/// </summary>
public sealed class RuleDefinition
{
    public string Name { get; set; } = string.Empty;
    public Condition? When { get; set; }
    public Condition? Check { get; set; }
    public string Severity { get; set; } = "error";
    public string Message { get; set; } = string.Empty;
}

public enum FieldType
{
    Text,
    Number
}

public static class RuleSchema
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly IReadOnlyDictionary<string, FieldType> Fields = new Dictionary<string, FieldType>
    {
        ["sku"] = FieldType.Text,
        ["name"] = FieldType.Text,
        ["category"] = FieldType.Text,
        ["author"] = FieldType.Text,
        ["isbn"] = FieldType.Text,
        ["description"] = FieldType.Text,
        ["price"] = FieldType.Number,
        ["stock"] = FieldType.Number
    };

    /// <summary>Operadores e se exigem valor.</summary>
    public static readonly IReadOnlyDictionary<string, bool> Ops = new Dictionary<string, bool>
    {
        ["required"] = false,
        ["empty"] = false,
        ["equals"] = true,
        ["notEquals"] = true,
        ["in"] = true,
        ["gt"] = true,
        ["gte"] = true,
        ["lt"] = true,
        ["lte"] = true,
        ["regex"] = true,
        ["minLength"] = true,
        ["maxLength"] = true
    };

    public static readonly string[] Severities = ["error", "warning"];

    internal static readonly HashSet<string> NumericOps = ["gt", "gte", "lt", "lte"];
    internal static readonly HashSet<string> LengthOps = ["minLength", "maxLength"];
    internal const int MaxDepth = 5;
}

/// <summary>Avalia regras sobre um produto. Puro e sem estado: fácil de testar.</summary>
public static class RuleEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>true = o produto atende a regra (ou a regra não se aplica a ele).</summary>
    public static bool Passes(RuleDefinition rule, Product product) =>
        rule.When is not null && !Evaluate(rule.When, product) || Evaluate(rule.Check!, product);

    public static bool Applies(RuleDefinition rule, Product product) =>
        rule.When is null || Evaluate(rule.When, product);

    public static bool Evaluate(Condition c, Product p)
    {
        if (c.All is not null) return c.All.All(x => Evaluate(x, p));
        if (c.Any is not null) return c.Any.Any(x => Evaluate(x, p));
        if (c.Not is not null) return !Evaluate(c.Not, p);

        var raw = p.Get(c.Field!);
        var text = raw switch
        {
            null => null,
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            _ => raw.ToString()
        };
        var isEmpty = string.IsNullOrWhiteSpace(text);

        switch (c.Op)
        {
            case "required": return !isEmpty;
            case "empty": return isEmpty;
            case "equals": return !isEmpty && Same(raw, text!, c.Value!.Value);
            case "notEquals": return isEmpty || !Same(raw, text!, c.Value!.Value);
            case "in": return !isEmpty && c.Value!.Value.EnumerateArray().Any(v => Same(raw, text!, v));
            case "gt" or "gte" or "lt" or "lte":
            {
                if (raw is null) return false;
                var n = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
                var v = c.Value!.Value.GetDecimal();
                return c.Op switch { "gt" => n > v, "gte" => n >= v, "lt" => n < v, _ => n <= v };
            }
            case "regex":
                return !isEmpty && Regex.IsMatch(text!, c.Value!.Value.GetString()!, RegexOptions.None, RegexTimeout);
            case "minLength": return (text?.Trim().Length ?? 0) >= c.Value!.Value.GetInt32();
            case "maxLength": return (text?.Trim().Length ?? 0) <= c.Value!.Value.GetInt32();
            default: throw new InvalidOperationException($"Operador desconhecido: {c.Op}");
        }
    }

    private static bool Same(object? raw, string text, JsonElement value) =>
        raw is decimal or int
            ? value.ValueKind == JsonValueKind.Number && Convert.ToDecimal(raw, CultureInfo.InvariantCulture) == value.GetDecimal()
            : value.ValueKind == JsonValueKind.String && string.Equals(text.Trim(), value.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Confere se uma regra é bem formada antes de ela ser salva ou avaliada.
/// É a mesma porta de entrada para regras escritas por pessoas e pela IA.
/// </summary>
public static class RuleValidator
{
    public static IReadOnlyList<string> Validate(RuleDefinition rule)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(rule.Name)) errors.Add("Faltou o nome da regra.");
        if (rule.Name.Length > 100) errors.Add("O nome pode ter no máximo 100 caracteres.");
        if (string.IsNullOrWhiteSpace(rule.Message)) errors.Add("Faltou a mensagem mostrada quando a regra falha.");
        if (!RuleSchema.Severities.Contains(rule.Severity)) errors.Add("severity precisa ser \"error\" ou \"warning\".");

        if (rule.Check is null) errors.Add("Faltou o \"check\": o que precisa ser verdade.");
        else ValidateCondition(rule.Check, "check", 1, errors);

        if (rule.When is not null) ValidateCondition(rule.When, "when", 1, errors);

        return errors;
    }

    private static void ValidateCondition(Condition c, string path, int depth, List<string> errors)
    {
        if (depth > RuleSchema.MaxDepth)
        {
            errors.Add($"{path}: condição aninhada demais (máximo {RuleSchema.MaxDepth} níveis).");
            return;
        }

        var kinds = new[] { c.Field is not null || c.Op is not null, c.All is not null, c.Any is not null, c.Not is not null }.Count(k => k);
        if (kinds != 1)
        {
            errors.Add($"{path}: use exatamente uma forma: campo+operador, \"all\", \"any\" ou \"not\".");
            return;
        }

        if (c.All is not null || c.Any is not null)
        {
            var list = c.All ?? c.Any!;
            var name = c.All is not null ? "all" : "any";
            if (list.Count == 0) errors.Add($"{path}.{name}: lista vazia.");
            for (var i = 0; i < list.Count; i++) ValidateCondition(list[i], $"{path}.{name}[{i}]", depth + 1, errors);
            return;
        }

        if (c.Not is not null)
        {
            ValidateCondition(c.Not, $"{path}.not", depth + 1, errors);
            return;
        }

        if (c.Field is null || !RuleSchema.Fields.TryGetValue(c.Field, out var type))
        {
            errors.Add($"{path}: campo \"{c.Field}\" não existe. Campos: {string.Join(", ", RuleSchema.Fields.Keys)}.");
            return;
        }

        if (c.Op is null || !RuleSchema.Ops.TryGetValue(c.Op, out var needsValue))
        {
            errors.Add($"{path}: operador \"{c.Op}\" não existe. Operadores: {string.Join(", ", RuleSchema.Ops.Keys)}.");
            return;
        }

        if (!needsValue)
        {
            if (c.Value is not null) errors.Add($"{path}: \"{c.Op}\" não usa \"value\".");
            return;
        }

        if (c.Value is null)
        {
            errors.Add($"{path}: \"{c.Op}\" precisa de \"value\".");
            return;
        }

        var v = c.Value.Value;
        if (RuleSchema.NumericOps.Contains(c.Op))
        {
            if (type != FieldType.Number) errors.Add($"{path}: \"{c.Op}\" só serve para campos numéricos (price, stock).");
            else if (v.ValueKind != JsonValueKind.Number) errors.Add($"{path}: \"value\" precisa ser um número.");
        }
        else if (RuleSchema.LengthOps.Contains(c.Op))
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < 0) errors.Add($"{path}: \"value\" precisa ser um inteiro positivo.");
        }
        else if (c.Op == "in")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0) errors.Add($"{path}: \"in\" precisa de uma lista não vazia.");
        }
        else if (c.Op == "regex")
        {
            if (v.ValueKind != JsonValueKind.String) errors.Add($"{path}: \"value\" do regex precisa ser texto.");
            else
            {
                try { _ = new Regex(v.GetString()!, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException) { errors.Add($"{path}: expressão regular inválida."); }
            }
        }
        else if (type == FieldType.Number ? v.ValueKind != JsonValueKind.Number : v.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}: \"value\" precisa ser {(type == FieldType.Number ? "número" : "texto")} para o campo \"{c.Field}\".");
        }
    }
}
