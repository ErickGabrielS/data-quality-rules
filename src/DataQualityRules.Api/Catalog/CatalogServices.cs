using System.Globalization;
using System.Text.Json;
using DataQualityRules.Api.Data;
using DataQualityRules.Api.Rules;
using Microsoft.EntityFrameworkCore;

namespace DataQualityRules.Api.Catalog;

public sealed record ImportResult(int Created, int Updated, IReadOnlyList<string> Errors);

/// <summary>
/// Importa o catálogo de um CSV com cabeçalho (sku;name;category;author;isbn;description;price;stock).
/// Aceita ";" ou "," como separador e aspas em campos com separador. Linhas com erro são
/// relatadas e puladas: um CSV com uma linha ruim não perde as outras.
/// </summary>
public sealed class CsvImporter(AppDbContext db, TimeProvider clock)
{
    private static readonly string[] Columns = ["sku", "name", "category", "author", "isbn", "description", "price", "stock"];

    public async Task<ImportResult> ImportAsync(TextReader reader, CancellationToken ct)
    {
        var header = await reader.ReadLineAsync(ct);
        if (header is null) return new ImportResult(0, 0, ["Arquivo vazio."]);

        var sep = header.Count(c => c == ';') >= header.Count(c => c == ',') ? ';' : ',';
        var names = Split(header, sep).Select(h => h.Trim().ToLowerInvariant()).ToList();
        var missing = Columns.Except(names).ToList();
        if (missing.Count > 0) return new ImportResult(0, 0, [$"Faltam colunas: {string.Join(", ", missing)}."]);

        var existing = await db.Products.ToDictionaryAsync(p => p.Sku, StringComparer.OrdinalIgnoreCase, ct);
        int created = 0, updated = 0, line = 1;
        var errors = new List<string>();
        var now = clock.GetUtcNow().UtcDateTime;

        while (await reader.ReadLineAsync(ct) is { } row)
        {
            line++;
            if (string.IsNullOrWhiteSpace(row)) continue;

            var values = Split(row, sep);
            if (values.Count != names.Count)
            {
                errors.Add($"Linha {line}: esperava {names.Count} colunas, veio {values.Count}.");
                continue;
            }

            var get = (string col) => values[names.IndexOf(col)];
            var sku = get("sku").Trim();
            if (sku.Length is 0 or > 30)
            {
                errors.Add($"Linha {line}: sku vazio ou com mais de 30 caracteres.");
                continue;
            }

            var product = existing.GetValueOrDefault(sku);
            var isNew = product is null;
            product ??= new Product { Sku = sku };

            try
            {
                foreach (var col in Columns.Skip(1)) product.Set(col, get(col));
            }
            catch (FormatException)
            {
                errors.Add($"Linha {line}: preço ou estoque não é um número.");
                continue;
            }

            product.UpdatedAtUtc = now;
            if (isNew)
            {
                db.Products.Add(product);
                existing[sku] = product;
                created++;
            }
            else
            {
                updated++;
            }
        }

        await db.SaveChangesAsync(ct);
        return new ImportResult(created, updated, errors);
    }

    internal static List<string> Split(string line, char sep)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == sep && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }

        result.Add(current.ToString());
        return result;
    }
}

public sealed record RunSummary(int RunId, int Products, int Rules, int Violations, IReadOnlyList<RuleCount> ByRule);
public sealed record RuleCount(int RuleId, string RuleName, string Severity, int Count);
public sealed record DryRun(int Applies, int WouldFail, IReadOnlyList<string> ExampleSkus);

/// <summary>Executa as regras ativas sobre o catálogo e guarda o resultado.</summary>
public sealed class ValidationService(AppDbContext db, TimeProvider clock)
{
    public async Task<RunSummary> RunAsync(CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().ToListAsync(ct);
        var rules = await db.Rules.AsNoTracking().Where(r => r.State == RuleState.Active).ToListAsync(ct);

        var run = new ValidationRun { AtUtc = clock.GetUtcNow().UtcDateTime, Products = products.Count, Rules = rules.Count };

        foreach (var rule in rules)
        {
            var def = rule.Definition;
            foreach (var product in products.Where(p => !RuleEvaluator.Passes(def, p)))
            {
                run.Items.Add(new Violation
                {
                    Sku = product.Sku,
                    RuleId = rule.Id,
                    RuleName = rule.Name,
                    Severity = def.Severity,
                    Message = def.Message.Length <= 300 ? def.Message : def.Message[..300]
                });
            }
        }

        run.Violations = run.Items.Count;
        db.ValidationRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var byRule = run.Items
            .GroupBy(v => (v.RuleId, v.RuleName, v.Severity))
            .Select(g => new RuleCount(g.Key.RuleId, g.Key.RuleName, g.Key.Severity, g.Count()))
            .OrderByDescending(r => r.Count)
            .ToList();

        return new RunSummary(run.Id, run.Products, run.Rules, run.Violations, byRule);
    }

    /// <summary>Simula uma regra no catálogo atual, sem gravar nada.</summary>
    public async Task<DryRun> DryRunAsync(RuleDefinition rule, CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().ToListAsync(ct);
        var applies = products.Where(p => RuleEvaluator.Applies(rule, p)).ToList();
        var failing = applies.Where(p => !RuleEvaluator.Passes(rule, p)).Select(p => p.Sku).ToList();
        return new DryRun(applies.Count, failing.Count, failing.Take(5).ToList());
    }

    /// <summary>Regras ativas que o produto não atende.</summary>
    public async Task<List<(Rule Rule, RuleDefinition Definition)>> FailingRulesAsync(Product product, CancellationToken ct)
    {
        var rules = await db.Rules.AsNoTracking().Where(r => r.State == RuleState.Active).ToListAsync(ct);
        return rules.Select(r => (r, r.Definition)).Where(x => !RuleEvaluator.Passes(x.Definition, product)).ToList();
    }
}

public sealed record DecisionResult(bool Ok, string Message, IReadOnlyList<LintFinding>? Findings = null);

/// <summary>Ciclo de vida das regras: proposta, aprovação (com revisão), recusa e histórico.</summary>
public sealed class RuleService(AppDbContext db, TimeProvider clock)
{
    public async Task<Rule> ProposeAsync(RuleDefinition def, string source, string? request, string by, CancellationToken ct)
    {
        var rule = new Rule
        {
            Name = def.Name.Trim(),
            DefinitionJson = JsonSerializer.Serialize(def, RuleSchema.Json),
            State = RuleState.Proposed,
            Source = source,
            Request = request is { Length: > 500 } ? request[..500] : request,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        };
        db.Rules.Add(rule);
        await db.SaveChangesAsync(ct);
        await HistoryAsync(rule.Id, "proposed", by, source, ct);
        return rule;
    }

    public async Task<DecisionResult> ApproveAsync(int id, string by, CancellationToken ct)
    {
        var rule = await db.Rules.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null || rule.State != RuleState.Proposed) return new(false, $"A regra {id} não existe ou não está em proposta.");

        var active = (await db.Rules.AsNoTracking().Where(r => r.State == RuleState.Active).ToListAsync(ct))
            .Select(r => (r.Id, r.Definition));
        var findings = RuleLinter.Check(rule.Definition, active);
        if (findings.Count > 0) return new(false, "A regra tem conflito com regras ativas e não foi aprovada.", findings);

        rule.State = RuleState.Active;
        await db.SaveChangesAsync(ct);
        await HistoryAsync(id, "approved", by, null, ct);
        return new(true, $"Regra \"{rule.Name}\" ativada.");
    }

    public async Task<DecisionResult> RejectAsync(int id, string by, string note, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(note)) return new(false, "Informe o motivo da recusa.");
        var rule = await db.Rules.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null || rule.State != RuleState.Proposed) return new(false, $"A regra {id} não existe ou não está em proposta.");

        rule.State = RuleState.Rejected;
        await db.SaveChangesAsync(ct);
        await HistoryAsync(id, "rejected", by, note.Trim(), ct);
        return new(true, $"Regra \"{rule.Name}\" recusada.");
    }

    public async Task HistoryAsync(int ruleId, string action, string by, string? note, CancellationToken ct)
    {
        db.RuleHistory.Add(new RuleHistory
        {
            RuleId = ruleId,
            Action = action,
            By = by.Length <= 100 ? by : by[..100],
            Note = note is { Length: > 500 } ? note[..500] : note,
            AtUtc = clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Catálogo e regras de exemplo: uma livraria com problemas de cadastro de propósito.</summary>
public static class DemoData
{
    public const string SampleCsv =
        """
        sku;name;category;author;isbn;description;price;stock
        LIV-001;Dom Casmurro;Livro;Machado de Assis;9788535910663;Clássico da literatura brasileira.;39.90;12
        LIV-002;O Cortiço;Livro;Aluísio Azevedo;;Romance naturalista.;29.90;5
        LIV-003;Memórias Póstumas de Brás Cubas;Livro;Machado de Assis;9788525406248;;34.50;0
        LIV-004;Vidas Secas;Livro;Graciliano Ramos;9788501060019;Retrato do sertão.;0;7
        LIV-005;A Hora da Estrela;Livro;;9788532508120;Último romance de Clarice.;32.00;3
        LIV-006;Macunaíma;;Mário de Andrade;9788520932513;O herói sem nenhum caráter.;27.90;9
        LIV-007;Grande Sertão: Veredas;Livro;Guimarães Rosa;978-85-35;Obra-prima de Rosa.;89.90;2
        LIV-008;Capitães da Areia;Livro;Jorge Amado;;Meninos de rua em Salvador.;31.90;-1
        PAP-001;Caderno 10 matérias;Papelaria;;;Caderno espiral 200 folhas.;24.90;40
        PAP-002;Caneta azul;Papelaria;;;Caneta esferográfica.;-2.50;100
        PAP-003;  marca-texto amarelo ;Papelaria;;;;6.90;30
        JOG-001;Quebra-cabeça 1000 peças;Jogos;;;Paisagem de Paraty.;79.90;4
        """;

    public static IEnumerable<RuleDefinition> ActiveRules()
    {
        JsonElement V(object o) => JsonSerializer.SerializeToElement(o);

        yield return new RuleDefinition
        {
            Name = "Preço maior que zero",
            Check = new Condition { Field = "price", Op = "gt", Value = V(0) },
            Message = "O preço precisa ser maior que zero."
        };
        yield return new RuleDefinition
        {
            Name = "Estoque não negativo",
            Check = new Condition { Field = "stock", Op = "gte", Value = V(0) },
            Message = "O estoque não pode ser negativo."
        };
        yield return new RuleDefinition
        {
            Name = "Categoria obrigatória",
            Check = new Condition { Field = "category", Op = "required" },
            Message = "Todo produto precisa de categoria."
        };
        yield return new RuleDefinition
        {
            Name = "Descrição recomendada",
            Check = new Condition { Field = "description", Op = "required" },
            Severity = "warning",
            Message = "Produto sem descrição vende menos."
        };
    }

    public static async Task SeedAsync(AppDbContext db, TimeProvider clock, CancellationToken ct = default)
    {
        if (!await db.Products.AnyAsync(ct))
        {
            await new CsvImporter(db, clock).ImportAsync(new StringReader(SampleCsv), ct);
        }

        if (!await db.Rules.AnyAsync(ct))
        {
            var rules = new RuleService(db, clock);
            foreach (var def in ActiveRules())
            {
                var rule = await rules.ProposeAsync(def, "manual", null, "seed", ct);
                await rules.ApproveAsync(rule.Id, "seed", ct);
            }
        }
    }
}
