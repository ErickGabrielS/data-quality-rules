using System.Text.Json;

namespace DataQualityRules.Api.Rules;

public sealed record LintFinding(string Kind, string Message, int? OtherRuleId);

/// <summary>
/// Revisor de regras, sem IA: acha problemas entre uma regra nova e as que já estão ativas.
/// - duplicada: mesmo "when" e mesmo "check";
/// - contraditória: no mesmo "when", uma exige o campo preenchido e outra vazio;
/// - faixa impossível: no mesmo "when", limites numéricos que nenhum valor atende juntos.
/// Uma regra com achado não pode ser aprovada.
/// </summary>
public static class RuleLinter
{
    public static IReadOnlyList<LintFinding> Check(RuleDefinition candidate, IEnumerable<(int Id, RuleDefinition Rule)> active)
    {
        var findings = new List<LintFinding>();
        var candidateWhen = Key(candidate.When);

        foreach (var (id, rule) in active)
        {
            if (Key(rule.When) != candidateWhen) continue;

            if (Key(rule.Check) == Key(candidate.Check))
            {
                findings.Add(new("duplicate", $"Igual à regra ativa \"{rule.Name}\".", id));
                continue;
            }

            foreach (var a in Leaves(candidate.Check))
            foreach (var b in Leaves(rule.Check))
            {
                if (a.Field != b.Field) continue;

                if ((a.Op, b.Op) is ("required", "empty") or ("empty", "required"))
                {
                    findings.Add(new("contradiction", $"Contradiz \"{rule.Name}\": uma exige \"{a.Field}\" preenchido e a outra vazio.", id));
                }
                else if (Impossible(a, b))
                {
                    findings.Add(new("contradiction", $"Junto com \"{rule.Name}\", nenhum valor de \"{a.Field}\" atende as duas.", id));
                }
            }
        }

        return findings;
    }

    // Representação canônica para comparar condições independentemente de espaços e ordem de propriedades.
    private static string Key(Condition? c) => c is null ? "*" : JsonSerializer.Serialize(c, RuleSchema.Json);

    /// <summary>Folhas que precisam ser verdadeiras juntas (topo ou dentro de "all").</summary>
    private static IEnumerable<Condition> Leaves(Condition? c)
    {
        if (c is null) yield break;
        if (c.All is not null)
        {
            foreach (var leaf in c.All.SelectMany(Leaves)) yield return leaf;
        }
        else if (c.Field is not null)
        {
            yield return c;
        }
    }

    private static bool Impossible(Condition a, Condition b)
    {
        if (!RuleSchema.NumericOps.Contains(a.Op!) || !RuleSchema.NumericOps.Contains(b.Op!)) return false;

        var (lower, upper) = a.Op!.StartsWith('g') ? (a, b) : (b, a);
        if (!lower.Op!.StartsWith('g') || !upper.Op!.StartsWith('l')) return false;

        var lo = lower.Value!.Value.GetDecimal();
        var hi = upper.Value!.Value.GetDecimal();
        var inclusive = lower.Op == "gte" && upper.Op == "lte";
        return inclusive ? lo > hi : lo >= hi;
    }
}
