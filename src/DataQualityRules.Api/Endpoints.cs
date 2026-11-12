using DataQualityRules.Api.Ai;
using DataQualityRules.Api.Catalog;
using DataQualityRules.Api.Data;
using DataQualityRules.Api.Rules;
using Microsoft.EntityFrameworkCore;

namespace DataQualityRules.Api;

public sealed record RejectRequest(string Note);
public sealed record AiRuleRequest(string Request);
public sealed record ApplyFixRequest(string Field, string? Value);
public sealed record RuleView(int Id, string Name, string State, string Source, string? Request, RuleDefinition Definition, DateTime CreatedAtUtc);

public static class Endpoints
{
    /// <summary>Quem fez a ação, para o histórico. Num sistema real viria da autenticação.</summary>
    private static string Who(HttpContext http) =>
        PromptText.Sanitize(http.Request.Headers["X-User"].FirstOrDefault(), 50) is { Length: > 0 } user ? user : "anônimo";

    public static void MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // ---------- Catálogo ----------
        api.MapGet("/products", async (AppDbContext db, CancellationToken ct) =>
            await db.Products.AsNoTracking().OrderBy(p => p.Sku).ToListAsync(ct));

        api.MapPost("/catalog/import", async (HttpRequest request, CsvImporter importer, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            return Results.Ok(await importer.ImportAsync(reader, ct));
        }).Accepts<string>("text/csv");

        api.MapPost("/products/{sku}/apply-fix", async (string sku, ApplyFixRequest body, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var product = await db.Products.SingleOrDefaultAsync(p => p.Sku == sku, ct);
            if (product is null) return Results.NotFound();
            try
            {
                product.Set(body.Field, body.Value);
            }
            catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException)
            {
                return Results.Problem($"Não foi possível gravar \"{body.Field}\": valor ou campo inválido.", statusCode: 400);
            }

            product.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return Results.Ok(product);
        });

        // ---------- Regras ----------
        api.MapGet("/rules", async (string? state, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.Rules.AsNoTracking();
            if (Enum.TryParse<RuleState>(state, true, out var s)) query = query.Where(r => r.State == s);
            var rules = await query.OrderBy(r => r.State).ThenBy(r => r.Id).ToListAsync(ct);
            return rules.Select(r => new RuleView(r.Id, r.Name, r.State.ToString(), r.Source, r.Request, r.Definition, r.CreatedAtUtc));
        });

        api.MapPost("/rules", async (RuleDefinition definition, RuleService rules, HttpContext http, CancellationToken ct) =>
        {
            var errors = RuleValidator.Validate(definition);
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["rule"] = errors.ToArray() });
            var rule = await rules.ProposeAsync(definition, "manual", null, Who(http), ct);
            return Results.Created($"/api/rules/{rule.Id}", new { rule.Id, state = rule.State.ToString() });
        });

        api.MapPost("/rules/preview", async (RuleDefinition definition, ValidationService validation, AppDbContext db, CancellationToken ct) =>
        {
            var errors = RuleValidator.Validate(definition);
            if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["rule"] = errors.ToArray() });
            var active = (await db.Rules.AsNoTracking().Where(r => r.State == RuleState.Active).ToListAsync(ct)).Select(r => (r.Id, r.Definition));
            return Results.Ok(new { preview = await validation.DryRunAsync(definition, ct), findings = RuleLinter.Check(definition, active) });
        });

        api.MapPost("/rules/{id:int}/approve", async (int id, RuleService rules, HttpContext http, CancellationToken ct) =>
        {
            var result = await rules.ApproveAsync(id, Who(http), ct);
            return result.Ok ? Results.Ok(result) : Results.Conflict(result);
        });

        api.MapPost("/rules/{id:int}/reject", async (int id, RejectRequest body, RuleService rules, HttpContext http, CancellationToken ct) =>
        {
            var result = await rules.RejectAsync(id, Who(http), body.Note, ct);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        api.MapGet("/rules/{id:int}/history", async (int id, AppDbContext db, CancellationToken ct) =>
            await db.RuleHistory.AsNoTracking().Where(h => h.RuleId == id).OrderBy(h => h.AtUtc).ToListAsync(ct));

        // ---------- Validação ----------
        api.MapPost("/validation/run", async (ValidationService validation, CancellationToken ct) => await validation.RunAsync(ct));

        api.MapGet("/validation/latest", async (string? sku, AppDbContext db, CancellationToken ct) =>
        {
            var run = await db.ValidationRuns.AsNoTracking().OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
            if (run is null) return Results.NotFound();
            var items = db.Violations.AsNoTracking().Where(v => v.RunId == run.Id);
            if (!string.IsNullOrWhiteSpace(sku)) items = items.Where(v => v.Sku == sku);
            return Results.Ok(new { run.Id, run.AtUtc, run.Products, run.Rules, run.Violations, items = await items.ToListAsync(ct) });
        });

        // ---------- IA generativa ----------
        api.MapPost("/ai/rules", async (AiRuleRequest body, IServiceProvider services, HttpContext http, CancellationToken ct) =>
            services.GetService<RuleWriter>() is { } writer
                ? Results.Ok(await writer.DraftAsync(body.Request, Who(http), ct))
                : AiOff());

        api.MapPost("/ai/fixes/{sku}", async (string sku, IServiceProvider services, CancellationToken ct) =>
            services.GetService<FixSuggester>() is { } suggester
                ? await suggester.SuggestAsync(sku, ct) is { } result ? Results.Ok(result) : Results.NotFound()
                : AiOff());

        api.MapGet("/ai/usage", async (AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var today = clock.GetUtcNow().UtcDateTime.Date;
            var calls = await db.AiCalls.AsNoTracking().Where(c => c.AtUtc >= today).ToListAsync(ct);
            return new { calls = calls.Count, costUsd = calls.Sum(c => c.CostUsd), invalidAnswers = calls.Count(c => !c.ValidAnswer) };
        });
    }

    private static IResult AiOff() => Results.Problem("A IA está desligada (Ai:Provider = Off).", statusCode: 503);
}
