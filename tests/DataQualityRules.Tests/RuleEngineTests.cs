using System.Text.Json;
using DataQualityRules.Api.Catalog;
using DataQualityRules.Api.Rules;

namespace DataQualityRules.Tests;

public static class R
{
    public static RuleDefinition Parse(string json) => JsonSerializer.Deserialize<RuleDefinition>(json, RuleSchema.Json)!;

    public static Product Book(Action<Product>? change = null)
    {
        var p = new Product { Sku = "LIV-1", Name = "Dom Casmurro", Category = "Livro", Author = "Machado de Assis", Isbn = "9788535910663", Description = "Clássico.", Price = 39.9m, Stock = 3 };
        change?.Invoke(p);
        return p;
    }
}

public sealed class RuleEvaluatorTests
{
    [Theory]
    [InlineData("""{"field":"isbn","op":"required"}""", true)]
    [InlineData("""{"field":"category","op":"equals","value":"livro"}""", true)] // sem diferenciar maiúsculas
    [InlineData("""{"field":"category","op":"in","value":["Jogos","Livro"]}""", true)]
    [InlineData("""{"field":"price","op":"gt","value":40}""", false)]
    [InlineData("""{"field":"stock","op":"lte","value":3}""", true)]
    [InlineData("""{"field":"isbn","op":"regex","value":"^[0-9]{13}$"}""", true)]
    [InlineData("""{"field":"name","op":"maxLength","value":5}""", false)]
    [InlineData("""{"all":[{"field":"price","op":"gt","value":0},{"field":"stock","op":"gte","value":0}]}""", true)]
    [InlineData("""{"any":[{"field":"author","op":"empty"},{"field":"isbn","op":"empty"}]}""", false)]
    [InlineData("""{"not":{"field":"description","op":"empty"}}""", true)]
    public void Evaluates_each_operator(string condition, bool expected)
    {
        var c = JsonSerializer.Deserialize<Condition>(condition, RuleSchema.Json)!;

        Assert.Equal(expected, RuleEvaluator.Evaluate(c, R.Book()));
    }

    [Fact]
    public void A_rule_that_does_not_apply_passes()
    {
        var rule = R.Parse("""{"name":"x","message":"x","when":{"field":"category","op":"equals","value":"Livro"},"check":{"field":"isbn","op":"required"}}""");

        Assert.True(RuleEvaluator.Passes(rule, R.Book(p => { p.Category = "Papelaria"; p.Isbn = null; })));
        Assert.False(RuleEvaluator.Passes(rule, R.Book(p => p.Isbn = " ")));
    }

    [Fact]
    public void Missing_number_fails_numeric_checks()
    {
        var rule = R.Parse("""{"name":"x","message":"x","check":{"field":"price","op":"gt","value":0}}""");

        Assert.False(RuleEvaluator.Passes(rule, R.Book(p => p.Price = null)));
    }
}

public sealed class RuleValidatorTests
{
    [Fact]
    public void A_well_formed_rule_has_no_errors()
    {
        var rule = R.Parse("""{"name":"ISBN","message":"Sem ISBN.","when":{"field":"category","op":"equals","value":"Livro"},"check":{"field":"isbn","op":"required"}}""");

        Assert.Empty(RuleValidator.Validate(rule));
    }

    [Theory]
    [InlineData("""{"field":"cor","op":"required"}""", "não existe")]
    [InlineData("""{"field":"name","op":"contains","value":"x"}""", "não existe")]
    [InlineData("""{"field":"name","op":"gt","value":3}""", "numéricos")]
    [InlineData("""{"field":"price","op":"equals"}""", "precisa de \"value\"")]
    [InlineData("""{"field":"price","op":"equals","value":"dez"}""", "precisa ser número")]
    [InlineData("""{"field":"isbn","op":"regex","value":"[0-9"}""", "regular inválida")]
    [InlineData("""{"field":"isbn","op":"required","value":1}""", "não usa")]
    [InlineData("""{"field":"isbn","op":"required","all":[]}""", "exatamente uma forma")]
    [InlineData("""{"not":{"not":{"not":{"not":{"not":{"field":"isbn","op":"required"}}}}}}""", "aninhada demais")]
    public void Malformed_conditions_are_explained(string check, string expected)
    {
        var rule = R.Parse($$"""{"name":"x","message":"x","check":{{check}}}""");

        Assert.Contains(RuleValidator.Validate(rule), e => e.Contains(expected));
    }

    [Fact]
    public void Name_message_and_severity_are_required()
    {
        var errors = RuleValidator.Validate(R.Parse("""{"name":"","message":"","severity":"fatal","check":{"field":"isbn","op":"required"}}"""));

        Assert.Equal(3, errors.Count);
    }
}

public sealed class RuleLinterTests
{
    private static (int, RuleDefinition) Active(int id, string check, string? when = null) =>
        (id, R.Parse($$"""{"name":"Ativa {{id}}","message":"x",{{(when is null ? "" : $"\"when\":{when},")}}"check":{{check}}}"""));

    [Fact]
    public void Same_when_and_check_is_a_duplicate()
    {
        var candidate = R.Parse("""{"name":"Nova","message":"y","check":{"field":"isbn","op":"required"}}""");

        var findings = RuleLinter.Check(candidate, [Active(1, """{"field":"isbn","op":"required"}""")]);

        Assert.Equal("duplicate", Assert.Single(findings).Kind);
    }

    [Fact]
    public void Required_versus_empty_is_a_contradiction()
    {
        var candidate = R.Parse("""{"name":"Nova","message":"y","check":{"field":"isbn","op":"empty"}}""");

        var findings = RuleLinter.Check(candidate, [Active(1, """{"field":"isbn","op":"required"}""")]);

        Assert.Equal("contradiction", Assert.Single(findings).Kind);
    }

    [Fact]
    public void Impossible_numeric_range_is_a_contradiction()
    {
        var candidate = R.Parse("""{"name":"Nova","message":"y","check":{"field":"price","op":"lt","value":10}}""");

        var findings = RuleLinter.Check(candidate, [Active(1, """{"field":"price","op":"gt","value":50}""")]);

        Assert.Equal("contradiction", Assert.Single(findings).Kind);
    }

    [Fact]
    public void Rules_for_different_scopes_do_not_conflict()
    {
        var candidate = R.Parse("""{"name":"Nova","message":"y","when":{"field":"category","op":"equals","value":"Papelaria"},"check":{"field":"isbn","op":"empty"}}""");

        var findings = RuleLinter.Check(candidate, [Active(1, """{"field":"isbn","op":"required"}""", """{"field":"category","op":"equals","value":"Livro"}""")]);

        Assert.Empty(findings);
    }
}

public sealed class CsvTests
{
    [Fact]
    public void Quoted_fields_keep_separators_and_quotes()
    {
        var parts = CsvImporter.Split("LIV-1;\"Romance; edição \"\"especial\"\"\";9", ';');

        Assert.Equal(["LIV-1", "Romance; edição \"especial\"", "9"], parts);
    }
}
