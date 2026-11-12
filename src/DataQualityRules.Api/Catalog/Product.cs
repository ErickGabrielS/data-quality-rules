using System.Globalization;

namespace DataQualityRules.Api.Catalog;

/// <summary>Produto do catálogo de uma livraria fictícia.</summary>
public sealed class Product
{
    public required string Sku { get; set; }
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Author { get; set; }
    public string? Isbn { get; set; }
    public string? Description { get; set; }
    public decimal? Price { get; set; }
    public int? Stock { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Lê um campo pelo nome usado nas regras.</summary>
    public object? Get(string field) => field switch
    {
        "sku" => Sku,
        "name" => Name,
        "category" => Category,
        "author" => Author,
        "isbn" => Isbn,
        "description" => Description,
        "price" => Price,
        "stock" => Stock,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Campo desconhecido.")
    };

    /// <summary>Grava um campo a partir de texto (correções sugeridas e importação).</summary>
    public void Set(string field, string? value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        switch (field)
        {
            case "name": Name = v; break;
            case "category": Category = v; break;
            case "author": Author = v; break;
            case "isbn": Isbn = v; break;
            case "description": Description = v; break;
            case "price": Price = v is null ? null : decimal.Parse(v.Replace(',', '.'), CultureInfo.InvariantCulture); break;
            case "stock": Stock = v is null ? null : int.Parse(v, CultureInfo.InvariantCulture); break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, "Campo não pode ser alterado.");
        }
    }

    public Product Clone() => (Product)MemberwiseClone();
}
