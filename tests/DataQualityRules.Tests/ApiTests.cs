using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataQualityRules.Api;
using DataQualityRules.Api.Ai;
using DataQualityRules.Api.Catalog;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace DataQualityRules.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _db = $"Data Source=dq-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _keepAlive;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _keepAlive = new SqliteConnection(_db);
        _keepAlive.Open();
        builder.UseSetting("ConnectionStrings:Rules", _db);
        builder.UseSetting("Ai:Provider", "Simulator");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _keepAlive?.Dispose();
    }
}

public sealed class ApiTests
{
    [Fact]
    public async Task Ai_rule_only_counts_in_validation_after_approval()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var before = await (await client.PostAsync("/api/validation/run", null)).Content.ReadFromJsonAsync<RunSummary>();
        var draft = await (await client.PostAsJsonAsync("/api/ai/rules", new AiRuleRequest("ISBN obrigatório quando a categoria for Livro")))
            .Content.ReadFromJsonAsync<RuleDraft>();
        var stillBefore = await (await client.PostAsync("/api/validation/run", null)).Content.ReadFromJsonAsync<RunSummary>();
        var approve = await client.PostAsync($"/api/rules/{draft!.RuleId}/approve", null);
        var after = await (await client.PostAsync("/api/validation/run", null)).Content.ReadFromJsonAsync<RunSummary>();

        Assert.Equal(before!.Violations, stillBefore!.Violations);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal(before.Violations + draft.Preview!.WouldFail, after!.Violations);
    }

    [Fact]
    public async Task A_conflicting_rule_cannot_be_approved()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var conflicting = new { name = "Categoria vazia", message = "m", check = new { field = "category", op = "empty" } };

        var created = await client.PostAsJsonAsync("/api/rules", conflicting);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var approve = await client.PostAsync($"/api/rules/{id}/approve", null);

        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        Assert.Contains("contradiction", await approve.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Malformed_manual_rules_are_refused_with_explanations()
    {
        using var factory = new ApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/rules",
            new { name = "x", message = "x", check = new { field = "cor", op = "required" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("não existe", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Csv_import_reports_bad_lines_and_keeps_the_good_ones()
    {
        using var factory = new ApiFactory();
        var csv = "sku;name;category;author;isbn;description;price;stock\nNEW-1;Livro novo;Livro;;;;10;1\nNEW-2;Ruim;Livro;;;;dez;1\n";

        var response = await factory.CreateClient().PostAsync("/api/catalog/import", new StringContent(csv, Encoding.UTF8, "text/csv"));
        var result = await response.Content.ReadFromJsonAsync<ImportResult>();

        Assert.Equal(1, result!.Created);
        Assert.Contains("Linha 3", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task History_records_who_proposed_and_who_rejected()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User", "erick");

        var draft = await (await client.PostAsJsonAsync("/api/ai/rules", new AiRuleRequest("estoque maior que 2"))).Content.ReadFromJsonAsync<RuleDraft>();
        var noReason = await client.PostAsJsonAsync($"/api/rules/{draft!.RuleId}/reject", new RejectRequest(""));
        await client.PostAsJsonAsync($"/api/rules/{draft.RuleId}/reject", new RejectRequest("regra de teste"));
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/rules/{draft.RuleId}/history");

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(["proposed", "rejected"], history.EnumerateArray().Select(h => h.GetProperty("action").GetString()));
        Assert.All(history.EnumerateArray(), h => Assert.Equal("erick", h.GetProperty("by").GetString()));
    }
}
