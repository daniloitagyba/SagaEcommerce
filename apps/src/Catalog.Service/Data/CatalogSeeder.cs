using Catalog.Service.Domain;

namespace Catalog.Service.Data;

/// <summary>
/// Demo data for the storefront - realistic-enough products
/// across categories with genuinely different attribute shapes, the whole
/// point of this being a document store rather than a relational table.
/// </summary>
public static class CatalogSeeder
{
    public static async Task SeedAsync(
        CategoryRepository categoryRepository,
        ProductRepository productRepository,
        CancellationToken cancellationToken)
    {
        var existingCategories = await categoryRepository.ListAsync(cancellationToken);
        if (existingCategories.Count > 0)
        {
            return;
        }

        var categories = new[]
        {
            new Category { Id = Guid.NewGuid().ToString("N"), Slug = "electronics", Name = "Eletrônicos" },
            new Category { Id = Guid.NewGuid().ToString("N"), Slug = "books", Name = "Livros" },
            new Category { Id = Guid.NewGuid().ToString("N"), Slug = "clothing", Name = "Roupas" },
            new Category { Id = Guid.NewGuid().ToString("N"), Slug = "home", Name = "Casa" }
        };
        foreach (var category in categories)
        {
            await categoryRepository.InsertAsync(category, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        var products = new[]
        {
            new Product
            {
                Name = "Notebook Ultraslim 14\"",
                Description = "Notebook leve com processador de última geração para produtividade no dia a dia.",
                CategorySlug = "electronics",
                Price = 4299.90m,
                Sku = "SKU-ELEC-001",
                Attributes = new Dictionary<string, string> { ["ram"] = "16GB", ["storage"] = "512GB SSD", ["cpu"] = "8-core" },
                Images = ["https://picsum.photos/seed/notebook/600/600"],
                CreatedAt = now
            },
            new Product
            {
                Name = "Fone de Ouvido Bluetooth",
                Description = "Cancelamento de ruído ativo e 30h de bateria.",
                CategorySlug = "electronics",
                Price = 349.90m,
                Sku = "SKU-ELEC-002",
                Attributes = new Dictionary<string, string> { ["battery"] = "30h", ["color"] = "Preto" },
                Images = ["https://picsum.photos/seed/headphones/600/600"],
                CreatedAt = now
            },
            new Product
            {
                Name = "Smartwatch Series X",
                Description = "Monitor de frequência cardíaca, GPS integrado e resistência à água.",
                CategorySlug = "electronics",
                Price = 899.00m,
                Sku = "SKU-ELEC-003",
                Attributes = new Dictionary<string, string> { ["display"] = "AMOLED 1.9\"", ["waterproof"] = "5ATM" },
                Images = ["https://picsum.photos/seed/smartwatch/600/600"],
                CreatedAt = now
            },
            new Product
            {
                Name = "Sistemas Distribuídos na Prática",
                Description = "Um guia sobre consistência, particionamento e tolerância a falhas.",
                CategorySlug = "books",
                Price = 89.90m,
                Sku = "SKU-BOOK-001",
                Attributes = new Dictionary<string, string> { ["author"] = "D. Itagyba", ["pages"] = "412", ["isbn"] = "978-3-16-148410-0" },
                Images = ["https://picsum.photos/seed/book1/600/600"],
                CreatedAt = now
            },
            new Product
            {
                Name = "Arquitetura de Microsserviços",
                Description = "Padrões, armadilhas e decisões reais de projeto.",
                CategorySlug = "books",
                Price = 74.90m,
                Sku = "SKU-BOOK-002",
                Attributes = new Dictionary<string, string> { ["author"] = "M. Fowler Jr.", ["pages"] = "356", ["isbn"] = "978-1-23-456789-0" },
                Images = ["https://picsum.photos/seed/book2/600/600"],
                CreatedAt = now
            },
            new Product
            {
                Name = "Camiseta Básica",
                Description = "100% algodão, corte reto.",
                CategorySlug = "clothing",
                Price = 59.90m,
                Sku = "SKU-CLTH-001",
                Attributes = new Dictionary<string, string> { ["size"] = "M", ["color"] = "Branco", ["material"] = "Algodão" },
                Images = ["https://picsum.photos/seed/tshirt/600/600"],
                CreatedAt = now
            },
            new Product
            {
                Name = "Jaqueta Corta-Vento",
                Description = "Impermeável, ideal para dias de chuva.",
                CategorySlug = "clothing",
                Price = 219.90m,
                Sku = "SKU-CLTH-002",
                Attributes = new Dictionary<string, string> { ["size"] = "G", ["color"] = "Azul Marinho", ["waterproof"] = "sim" },
                Images = ["https://picsum.photos/seed/jacket/600/600"],
                CreatedAt = now
            },
            new Product
            {
                Name = "Cafeteira Elétrica",
                Description = "Prepara até 12 xícaras, com desligamento automático.",
                CategorySlug = "home",
                Price = 179.90m,
                Sku = "SKU-HOME-001",
                Attributes = new Dictionary<string, string> { ["capacity"] = "12 xícaras", ["power"] = "900W" },
                Images = ["https://picsum.photos/seed/coffee/600/600"],
                CreatedAt = now
            },
            new Product
            {
                Name = "Kit Panelas Antiaderentes",
                Description = "Conjunto com 5 peças, cabo termoisolante.",
                CategorySlug = "home",
                Price = 349.90m,
                Sku = "SKU-HOME-002",
                Attributes = new Dictionary<string, string> { ["pieces"] = "5", ["material"] = "Alumínio antiaderente" },
                Images = ["https://picsum.photos/seed/pans/600/600"],
                CreatedAt = now
            }
        };

        foreach (var product in products)
        {
            await productRepository.InsertAsync(product, cancellationToken);
        }
    }
}
