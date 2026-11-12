using DataQualityRules.Api;
using DataQualityRules.Api.Ai;
using DataQualityRules.Api.Catalog;
using DataQualityRules.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Rules") ?? "Data Source=rules.db"));

builder.Services.AddScoped<CsvImporter>();
builder.Services.AddScoped<ValidationService>();
builder.Services.AddScoped<RuleService>();

// IA generativa: regra a partir de texto e sugestão de correção. Sempre com aprovação humana.
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.Section));
var ai = builder.Configuration.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
if (ai.Provider == AiProvider.Simulator)
{
    builder.Services.AddSingleton<IChatModel, SimulatedChatModel>();
}
else if (ai.Provider == AiProvider.OpenAiCompatible)
{
    builder.Services
        .AddHttpClient<IChatModel, OpenAiCompatibleChatModel>(c => c.BaseAddress = new Uri(ai.BaseUrl))
        .AddStandardResilienceHandler();
}

if (ai.Provider != AiProvider.Off)
{
    builder.Services.AddScoped<AiBudget>();
    builder.Services.AddScoped<RuleWriter>();
    builder.Services.AddScoped<FixSuggester>();
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    if (app.Configuration.GetValue("Demo:Seed", true))
    {
        await DemoData.SeedAsync(db, TimeProvider.System);
    }
}

app.UseExceptionHandler();
app.MapOpenApi(); // documento OpenAPI em /openapi/v1.json
app.MapApi();
app.MapGet("/", () => Results.Redirect("/openapi/v1.json"));

app.Run();

public partial class Program;
