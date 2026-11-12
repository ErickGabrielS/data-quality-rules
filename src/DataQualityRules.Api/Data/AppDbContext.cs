using System.Text.Json;
using DataQualityRules.Api.Catalog;
using DataQualityRules.Api.Rules;
using Microsoft.EntityFrameworkCore;

namespace DataQualityRules.Api.Data;

public enum RuleState
{
    Proposed = 1,
    Active = 2,
    Rejected = 3,
    Retired = 4
}

/// <summary>Regra guardada no banco. A definição fica em JSON, para ser trocada sem novo deploy.</summary>
public sealed class Rule
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string DefinitionJson { get; set; }
    public RuleState State { get; set; }

    /// <summary>"manual" ou "ai:modelo".</summary>
    public required string Source { get; set; }

    /// <summary>Pedido em texto que gerou a regra (quando veio da IA).</summary>
    public string? Request { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public RuleDefinition Definition => JsonSerializer.Deserialize<RuleDefinition>(DefinitionJson, RuleSchema.Json)!;
}

/// <summary>Histórico: toda mudança de estado de uma regra.</summary>
public sealed class RuleHistory
{
    public long Id { get; set; }
    public int RuleId { get; set; }
    public required string Action { get; set; }
    public required string By { get; set; }
    public string? Note { get; set; }
    public DateTime AtUtc { get; set; }
}

public sealed class ValidationRun
{
    public int Id { get; set; }
    public DateTime AtUtc { get; set; }
    public int Products { get; set; }
    public int Rules { get; set; }
    public int Violations { get; set; }
    public List<Violation> Items { get; set; } = [];
}

public sealed class Violation
{
    public long Id { get; set; }
    public int RunId { get; set; }
    public required string Sku { get; set; }
    public int RuleId { get; set; }
    public required string RuleName { get; set; }
    public required string Severity { get; set; }
    public required string Message { get; set; }
}

/// <summary>Uma chamada a um modelo de IA: base do limite diário e do controle de custo.</summary>
public sealed class AiCall
{
    public long Id { get; set; }
    public required string Provider { get; set; }
    public required string Purpose { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public bool ValidAnswer { get; set; }
    public DateTime AtUtc { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<RuleHistory> RuleHistory => Set<RuleHistory>();
    public DbSet<ValidationRun> ValidationRuns => Set<ValidationRun>();
    public DbSet<Violation> Violations => Set<Violation>();
    public DbSet<AiCall> AiCalls => Set<AiCall>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(e =>
        {
            e.HasKey(p => p.Sku);
            e.Property(p => p.Sku).HasMaxLength(30);
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Category).HasMaxLength(50);
            e.Property(p => p.Author).HasMaxLength(100);
            e.Property(p => p.Isbn).HasMaxLength(20);
            e.Property(p => p.Description).HasMaxLength(2000);
            e.Property(p => p.Price).HasPrecision(18, 2);
        });

        modelBuilder.Entity<Rule>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(100);
            e.Property(r => r.DefinitionJson).HasMaxLength(4000);
            e.Property(r => r.Source).HasMaxLength(100);
            e.Property(r => r.Request).HasMaxLength(500);
            e.HasIndex(r => r.State);
            e.Ignore(r => r.Definition);
        });

        modelBuilder.Entity<RuleHistory>(e =>
        {
            e.Property(h => h.Action).HasMaxLength(30);
            e.Property(h => h.By).HasMaxLength(100);
            e.Property(h => h.Note).HasMaxLength(500);
            e.HasIndex(h => h.RuleId);
        });

        modelBuilder.Entity<ValidationRun>().HasMany(r => r.Items).WithOne().HasForeignKey(v => v.RunId);

        modelBuilder.Entity<Violation>(e =>
        {
            e.Property(v => v.Sku).HasMaxLength(30);
            e.Property(v => v.RuleName).HasMaxLength(100);
            e.Property(v => v.Severity).HasMaxLength(10);
            e.Property(v => v.Message).HasMaxLength(300);
            e.HasIndex(v => new { v.RunId, v.Sku });
        });

        modelBuilder.Entity<AiCall>(e =>
        {
            e.Property(c => c.Provider).HasMaxLength(100);
            e.Property(c => c.Purpose).HasMaxLength(50);
            e.Property(c => c.CostUsd).HasPrecision(18, 6);
            e.HasIndex(c => c.AtUtc);
        });
    }
}
