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
                Images = ["data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA2MDAgNjAwIj4KICA8ZGVmcz4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYmciIHgxPSIwIiB5MT0iMCIgeDI9IjAiIHkyPSIxIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjRUFGM0ZDIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0Q2RTlGQSIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0ic2NyZWVuIiB4MT0iMCIgeTE9IjAiIHgyPSIwIiB5Mj0iMSI+CiAgICAgIDxzdG9wIG9mZnNldD0iMCIgc3RvcC1jb2xvcj0iIzFFNkZEOSIvPgogICAgICA8c3RvcCBvZmZzZXQ9IjEiIHN0b3AtY29sb3I9IiMxMjQ2OEYiLz4KICAgIDwvbGluZWFyR3JhZGllbnQ+CiAgICA8bGluZWFyR3JhZGllbnQgaWQ9ImRpc3BsYXkiIHgxPSIwIiB5MT0iMCIgeDI9IjEiIHkyPSIxIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjN0ZCQkZGIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iIzRBOTNGMCIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYmFzZSIgeDE9IjAiIHkxPSIwIiB4Mj0iMCIgeTI9IjEiPgogICAgICA8c3RvcCBvZmZzZXQ9IjAiIHN0b3AtY29sb3I9IiMyRTdCREIiLz4KICAgICAgPHN0b3Agb2Zmc2V0PSIxIiBzdG9wLWNvbG9yPSIjMEY0QTlDIi8+CiAgICA8L2xpbmVhckdyYWRpZW50PgogIDwvZGVmcz4KICA8cmVjdCB3aWR0aD0iNjAwIiBoZWlnaHQ9IjYwMCIgZmlsbD0idXJsKCNiZykiLz4KICA8ZWxsaXBzZSBjeD0iMzAwIiBjeT0iNDQ2IiByeD0iMTg4IiByeT0iMTYiIGZpbGw9IiMwQjNCN0EiIG9wYWNpdHk9IjAuMTIiLz4KCiAgPHJlY3QgeD0iMTQ2IiB5PSIxMjgiIHdpZHRoPSIzMDgiIGhlaWdodD0iMjA0IiByeD0iMTYiIGZpbGw9InVybCgjc2NyZWVuKSIvPgogIDxyZWN0IHg9IjE2NCIgeT0iMTQ2IiB3aWR0aD0iMjcyIiBoZWlnaHQ9IjE2OCIgcng9IjYiIGZpbGw9InVybCgjZGlzcGxheSkiLz4KICA8cGF0aCBkPSJNMTc2IDE1NiBMMjYwIDE1NiBMMTc2IDI0MCBaIiBmaWxsPSIjRkZGRkZGIiBvcGFjaXR5PSIwLjE4Ii8+CiAgPGNpcmNsZSBjeD0iMzAwIiBjeT0iMTM3IiByPSIzLjUiIGZpbGw9IiNCQkRCRkYiLz4KCiAgPHBhdGggZD0iTTEyMCAzNjYgTDQ4MCAzNjYgTDQ0MiAzMzIgTDE1OCAzMzIgWiIgZmlsbD0idXJsKCNiYXNlKSIvPgogIDxwYXRoIGQ9Ik05NiAzODggUTk2IDM2NiAxMjAgMzY2IEw0ODAgMzY2IFE1MDQgMzY2IDUwNCAzODggTDUwNCAzOTIgUTUwNCA0MDIgNDk0IDQwMiBMMTA2IDQwMiBROTYgNDAyIDk2IDM5MiBaIiBmaWxsPSJ1cmwoI2Jhc2UpIi8+CiAgPHJlY3QgeD0iMjcwIiB5PSIzODAiIHdpZHRoPSI2MCIgaGVpZ2h0PSI3IiByeD0iMy41IiBmaWxsPSIjMEIzQjdBIiBvcGFjaXR5PSIwLjM1Ii8+Cjwvc3ZnPgo="],
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
                Images = ["data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA2MDAgNjAwIj4KICA8ZGVmcz4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYmciIHgxPSIwIiB5MT0iMCIgeDI9IjAiIHkyPSIxIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjRUFGM0ZDIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0Q2RTlGQSIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYmFuZCIgeDE9IjAiIHkxPSIwIiB4Mj0iMCIgeTI9IjEiPgogICAgICA8c3RvcCBvZmZzZXQ9IjAiIHN0b3AtY29sb3I9IiMyRTdCREIiLz4KICAgICAgPHN0b3Agb2Zmc2V0PSIxIiBzdG9wLWNvbG9yPSIjMTI0NjhGIi8+CiAgICA8L2xpbmVhckdyYWRpZW50PgogICAgPGxpbmVhckdyYWRpZW50IGlkPSJjdXAiIHgxPSIwIiB5MT0iMCIgeDI9IjEiIHkyPSIxIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjMkU3QkRCIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iIzBGNEE5QyIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxyYWRpYWxHcmFkaWVudCBpZD0icGFkIiBjeD0iMC4zNSIgY3k9IjAuMyIgcj0iMC44Ij4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjOUFDOEZGIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iIzVDOUVFQyIvPgogICAgPC9yYWRpYWxHcmFkaWVudD4KICA8L2RlZnM+CiAgPHJlY3Qgd2lkdGg9IjYwMCIgaGVpZ2h0PSI2MDAiIGZpbGw9InVybCgjYmcpIi8+CiAgPGVsbGlwc2UgY3g9IjMwMCIgY3k9IjQ0NiIgcng9IjE1MCIgcnk9IjE0IiBmaWxsPSIjMEIzQjdBIiBvcGFjaXR5PSIwLjEyIi8+CgogIDxwYXRoIGQ9Ik0xNTAgMzIwIEExNTAgMTUwIDAgMCAxIDQ1MCAzMjAiIGZpbGw9Im5vbmUiIHN0cm9rZT0idXJsKCNiYW5kKSIgc3Ryb2tlLXdpZHRoPSIyNiIgc3Ryb2tlLWxpbmVjYXA9InJvdW5kIi8+CiAgPHBhdGggZD0iTTE1MCAzMjAgQTE1MCAxNTAgMCAwIDEgNDUwIDMyMCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSIjRkZGRkZGIiBzdHJva2Utd2lkdGg9IjQiIHN0cm9rZS1saW5lY2FwPSJyb3VuZCIgb3BhY2l0eT0iMC4zNSIgdHJhbnNmb3JtPSJ0cmFuc2xhdGUoMCwtNikiLz4KCiAgPGc+CiAgICA8cmVjdCB4PSIxMDQiIHk9IjI5NiIgd2lkdGg9Ijg2IiBoZWlnaHQ9IjE0MCIgcng9IjM0IiBmaWxsPSJ1cmwoI2N1cCkiLz4KICAgIDxlbGxpcHNlIGN4PSIxNDciIGN5PSIzNjYiIHJ4PSIyNiIgcnk9IjQ2IiBmaWxsPSJ1cmwoI3BhZCkiLz4KICA8L2c+CiAgPGc+CiAgICA8cmVjdCB4PSI0MTAiIHk9IjI5NiIgd2lkdGg9Ijg2IiBoZWlnaHQ9IjE0MCIgcng9IjM0IiBmaWxsPSJ1cmwoI2N1cCkiLz4KICAgIDxlbGxpcHNlIGN4PSI0NTMiIGN5PSIzNjYiIHJ4PSIyNiIgcnk9IjQ2IiBmaWxsPSJ1cmwoI3BhZCkiLz4KICA8L2c+Cjwvc3ZnPgo="],
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
                Images = ["data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA2MDAgNjAwIj4KICA8ZGVmcz4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYmciIHgxPSIwIiB5MT0iMCIgeDI9IjAiIHkyPSIxIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjRUFGM0ZDIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0Q2RTlGQSIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYmFuZCIgeDE9IjAiIHkxPSIwIiB4Mj0iMSIgeTI9IjAiPgogICAgICA8c3RvcCBvZmZzZXQ9IjAiIHN0b3AtY29sb3I9IiMxMjQ2OEYiLz4KICAgICAgPHN0b3Agb2Zmc2V0PSIxIiBzdG9wLWNvbG9yPSIjMkU3QkRCIi8+CiAgICA8L2xpbmVhckdyYWRpZW50PgogICAgPGxpbmVhckdyYWRpZW50IGlkPSJjYXNlIiB4MT0iMCIgeTE9IjAiIHgyPSIxIiB5Mj0iMSI+CiAgICAgIDxzdG9wIG9mZnNldD0iMCIgc3RvcC1jb2xvcj0iIzJFN0JEQiIvPgogICAgICA8c3RvcCBvZmZzZXQ9IjEiIHN0b3AtY29sb3I9IiMwRjRBOUMiLz4KICAgIDwvbGluZWFyR3JhZGllbnQ+CiAgICA8cmFkaWFsR3JhZGllbnQgaWQ9ImZhY2UiIGN4PSIwLjM1IiBjeT0iMC4zIiByPSIwLjkiPgogICAgICA8c3RvcCBvZmZzZXQ9IjAiIHN0b3AtY29sb3I9IiMxMjNBNzMiLz4KICAgICAgPHN0b3Agb2Zmc2V0PSIxIiBzdG9wLWNvbG9yPSIjMDgxRjQyIi8+CiAgICA8L3JhZGlhbEdyYWRpZW50PgogIDwvZGVmcz4KICA8cmVjdCB3aWR0aD0iNjAwIiBoZWlnaHQ9IjYwMCIgZmlsbD0idXJsKCNiZykiLz4KICA8ZWxsaXBzZSBjeD0iMzAwIiBjeT0iNDcwIiByeD0iMTIwIiByeT0iMTQiIGZpbGw9IiMwQjNCN0EiIG9wYWNpdHk9IjAuMTIiLz4KCiAgPHJlY3QgeD0iMjU4IiB5PSI4MCIgd2lkdGg9Ijg0IiBoZWlnaHQ9IjExOCIgcng9IjI0IiBmaWxsPSJ1cmwoI2JhbmQpIi8+CiAgPHJlY3QgeD0iMjU4IiB5PSI0MDIiIHdpZHRoPSI4NCIgaGVpZ2h0PSIxMTgiIHJ4PSIyNCIgZmlsbD0idXJsKCNiYW5kKSIvPgogIDxnIG9wYWNpdHk9IjAuNSIgc3Ryb2tlPSIjMEIzQjdBIiBzdHJva2Utd2lkdGg9IjMiIHN0cm9rZS1saW5lY2FwPSJyb3VuZCI+CiAgICA8bGluZSB4MT0iMjcyIiB5MT0iMTAwIiB4Mj0iMzI4IiB5Mj0iMTAwIi8+CiAgICA8bGluZSB4MT0iMjcyIiB5MT0iMTE4IiB4Mj0iMzI4IiB5Mj0iMTE4Ii8+CiAgICA8bGluZSB4MT0iMjcyIiB5MT0iNDgwIiB4Mj0iMzI4IiB5Mj0iNDgwIi8+CiAgICA8bGluZSB4MT0iMjcyIiB5MT0iNDk4IiB4Mj0iMzI4IiB5Mj0iNDk4Ii8+CiAgPC9nPgoKICA8Y2lyY2xlIGN4PSIzMDAiIGN5PSIzMDAiIHI9IjEyMCIgZmlsbD0idXJsKCNjYXNlKSIvPgogIDxjaXJjbGUgY3g9IjMwMCIgY3k9IjMwMCIgcj0iOTgiIGZpbGw9InVybCgjZmFjZSkiLz4KICA8cGF0aCBkPSJNMjEyIDI1MCBBOTggOTggMCAwIDEgMzAwIDIwMiIgZmlsbD0ibm9uZSIgc3Ryb2tlPSIjM0U3QkQ2IiBzdHJva2Utd2lkdGg9IjYiIHN0cm9rZS1saW5lY2FwPSJyb3VuZCIgb3BhY2l0eT0iMC41Ii8+CiAgPHJlY3QgeD0iNDE2IiB5PSIyNzgiIHdpZHRoPSIyMiIgaGVpZ2h0PSIzNCIgcng9IjciIGZpbGw9InVybCgjYmFuZCkiLz4KCiAgPGxpbmUgeDE9IjMwMCIgeTE9IjMwMCIgeDI9IjMwMCIgeTI9IjI0MCIgc3Ryb2tlPSIjRkY4QTVDIiBzdHJva2Utd2lkdGg9IjciIHN0cm9rZS1saW5lY2FwPSJyb3VuZCIvPgogIDxsaW5lIHgxPSIzMDAiIHkxPSIzMDAiIHgyPSIzNDYiIHkyPSIzMDAiIHN0cm9rZT0iI0JGRTBGRiIgc3Ryb2tlLXdpZHRoPSI3IiBzdHJva2UtbGluZWNhcD0icm91bmQiLz4KICA8Y2lyY2xlIGN4PSIzMDAiIGN5PSIzMDAiIHI9IjgiIGZpbGw9IiNCRkUwRkYiLz4KPC9zdmc+Cg=="],
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
                Images = ["data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA2MDAgNjAwIj4KICA8ZGVmcz4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYmciIHgxPSIwIiB5MT0iMCIgeDI9IjAiIHkyPSIxIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjRkRGMkU0Ii8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0ZCRTZDQyIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iY292ZXIiIHgxPSIwIiB5MT0iMCIgeDI9IjEiIHkyPSIxIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjRjA3OTFGIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0M0NTYwQSIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0icGFnZXMiIHgxPSIwIiB5MT0iMCIgeDI9IjEiIHkyPSIwIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjRkZGOEVDIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0YzRDlBRSIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICA8L2RlZnM+CiAgPHJlY3Qgd2lkdGg9IjYwMCIgaGVpZ2h0PSI2MDAiIGZpbGw9InVybCgjYmcpIi8+CiAgPGVsbGlwc2UgY3g9IjMwMCIgY3k9IjQ3MiIgcng9IjE1MCIgcnk9IjE0IiBmaWxsPSIjOEE0QTEyIiBvcGFjaXR5PSIwLjE1Ii8+CgogIDxyZWN0IHg9IjE4MSIgeT0iMTQxIiB3aWR0aD0iMjM4IiBoZWlnaHQ9IjMyNiIgcng9IjYiIGZpbGw9IiMwMDAwMDAyMiIvPgogIDxyZWN0IHg9IjE3OCIgeT0iMTM4IiB3aWR0aD0iMjM0IiBoZWlnaHQ9IjMyMiIgcng9IjYiIGZpbGw9InVybCgjY292ZXIpIi8+CgogIDxyZWN0IHg9IjM5OCIgeT0iMTQ2IiB3aWR0aD0iMTAiIGhlaWdodD0iMzA2IiBmaWxsPSJ1cmwoI3BhZ2VzKSIvPgogIDxyZWN0IHg9IjQwNCIgeT0iMTUwIiB3aWR0aD0iOCIgaGVpZ2h0PSIyOTgiIGZpbGw9InVybCgjcGFnZXMpIi8+CiAgPHJlY3QgeD0iNDA5IiB5PSIxNTQiIHdpZHRoPSI2IiBoZWlnaHQ9IjI5MCIgZmlsbD0idXJsKCNwYWdlcykiLz4KCiAgPHJlY3QgeD0iMjA2IiB5PSIxOTYiIHdpZHRoPSIxMzAiIGhlaWdodD0iMTEiIHJ4PSI1LjUiIGZpbGw9IiNGQ0VCRDYiLz4KICA8cmVjdCB4PSIyMDYiIHk9IjIyOCIgd2lkdGg9IjE3MCIgaGVpZ2h0PSIxMSIgcng9IjUuNSIgZmlsbD0iI0ZDRUJENiIvPgogIDxyZWN0IHg9IjIwNiIgeT0iMjYwIiB3aWR0aD0iMTUwIiBoZWlnaHQ9IjExIiByeD0iNS41IiBmaWxsPSIjRkNFQkQ2Ii8+CiAgPGNpcmNsZSBjeD0iMjEyIiBjeT0iMzIwIiByPSIxOCIgZmlsbD0iI0ZDRUJENiIgb3BhY2l0eT0iMC44NSIvPgoKICA8cGF0aCBkPSJNMzIwIDEzOCBMMzIwIDIzNiBMMzQ1IDIxMCBMMzcwIDIzNiBMMzcwIDEzOCBaIiBmaWxsPSIjRkZDODc2Ii8+CiAgPHBhdGggZD0iTTMyMCAxMzggTDM3MCAxMzggTDM3MCAxNTAgTDMyMCAxNTAgWiIgZmlsbD0iI0YyQTk0RSIvPgo8L3N2Zz4K"],
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
                Images = ["data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA2MDAgNjAwIj4KICA8ZGVmcz4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYmciIHgxPSIwIiB5MT0iMCIgeDI9IjAiIHkyPSIxIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjRkRGMkU0Ii8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0ZCRTZDQyIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0ibGVmdFBhZ2UiIHgxPSIwIiB5MT0iMCIgeDI9IjEiIHkyPSIwIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjQzQ1NjBBIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0YwNzkxRiIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0icmlnaHRQYWdlIiB4MT0iMCIgeTE9IjAiIHgyPSIxIiB5Mj0iMCI+CiAgICAgIDxzdG9wIG9mZnNldD0iMCIgc3RvcC1jb2xvcj0iI0YwNzkxRiIvPgogICAgICA8c3RvcCBvZmZzZXQ9IjEiIHN0b3AtY29sb3I9IiNDNDU2MEEiLz4KICAgIDwvbGluZWFyR3JhZGllbnQ+CiAgICA8cmFkaWFsR3JhZGllbnQgaWQ9Imd1dHRlciIgY3g9IjAuNSIgY3k9IjAuNSIgcj0iMC41Ij4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjM0ExODAwIiBzdG9wLW9wYWNpdHk9IjAuNCIvPgogICAgICA8c3RvcCBvZmZzZXQ9IjEiIHN0b3AtY29sb3I9IiMzQTE4MDAiIHN0b3Atb3BhY2l0eT0iMCIvPgogICAgPC9yYWRpYWxHcmFkaWVudD4KICA8L2RlZnM+CiAgPHJlY3Qgd2lkdGg9IjYwMCIgaGVpZ2h0PSI2MDAiIGZpbGw9InVybCgjYmcpIi8+CiAgPGVsbGlwc2UgY3g9IjMwMCIgY3k9IjQ2MCIgcng9IjE3MCIgcnk9IjE0IiBmaWxsPSIjOEE0QTEyIiBvcGFjaXR5PSIwLjE1Ii8+CgogIDxwYXRoIGQ9Ik0zMDAgMTg4IEMyNTggMTYwIDE5NiAxNTQgMTQ0IDE3MCBMMTQ0IDQyOCBDMTk2IDQxMiAyNTggNDE4IDMwMCA0NDYgWiIgZmlsbD0idXJsKCNsZWZ0UGFnZSkiLz4KICA8cGF0aCBkPSJNMzAwIDE4OCBDMzQyIDE2MCA0MDQgMTU0IDQ1NiAxNzAgTDQ1NiA0MjggQzQwNCA0MTIgMzQyIDQxOCAzMDAgNDQ2IFoiIGZpbGw9InVybCgjcmlnaHRQYWdlKSIvPgogIDxlbGxpcHNlIGN4PSIzMDAiIGN5PSIzMDciIHJ4PSIyNiIgcnk9IjE0MCIgZmlsbD0idXJsKCNndXR0ZXIpIi8+CgogIDxnIHN0cm9rZT0iI0ZDRUJENiIgc3Ryb2tlLXdpZHRoPSI2IiBzdHJva2UtbGluZWNhcD0icm91bmQiIG9wYWNpdHk9IjAuOSI+CiAgICA8bGluZSB4MT0iMTc2IiB5MT0iMjIyIiB4Mj0iMjcyIiB5Mj0iMjEyIi8+CiAgICA8bGluZSB4MT0iMTc2IiB5MT0iMjY0IiB4Mj0iMjcyIiB5Mj0iMjU0Ii8+CiAgICA8bGluZSB4MT0iMTc2IiB5MT0iMzA2IiB4Mj0iMjcyIiB5Mj0iMjk2Ii8+CiAgICA8bGluZSB4MT0iMTc2IiB5MT0iMzQ4IiB4Mj0iMjU2IiB5Mj0iMzQwIi8+CiAgPC9nPgogIDxnIHN0cm9rZT0iI0ZDRUJENiIgc3Ryb2tlLXdpZHRoPSI2IiBzdHJva2UtbGluZWNhcD0icm91bmQiIG9wYWNpdHk9IjAuOSI+CiAgICA8bGluZSB4MT0iMzI4IiB5MT0iMjEyIiB4Mj0iNDI0IiB5Mj0iMjIyIi8+CiAgICA8bGluZSB4MT0iMzI4IiB5MT0iMjU0IiB4Mj0iNDI0IiB5Mj0iMjY0Ii8+CiAgICA8bGluZSB4MT0iMzI4IiB5MT0iMjk2IiB4Mj0iNDI0IiB5Mj0iMzA2Ii8+CiAgICA8bGluZSB4MT0iMzQ0IiB5MT0iMzQwIiB4Mj0iNDI0IiB5Mj0iMzQ4Ii8+CiAgPC9nPgo8L3N2Zz4K"],
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
                Images = ["data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA2MDAgNjAwIj4KICA8ZGVmcz4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYmciIHgxPSIwIiB5MT0iMCIgeDI9IjAiIHkyPSIxIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjRUFGN0VDIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0QzRUZEOCIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYm9keSIgeDE9IjAiIHkxPSIwIiB4Mj0iMSIgeTI9IjEiPgogICAgICA8c3RvcCBvZmZzZXQ9IjAiIHN0b3AtY29sb3I9IiM0Q0FFNUMiLz4KICAgICAgPHN0b3Agb2Zmc2V0PSIxIiBzdG9wLWNvbG9yPSIjMkU4NTQwIi8+CiAgICA8L2xpbmVhckdyYWRpZW50PgogICAgPGxpbmVhckdyYWRpZW50IGlkPSJzaGFkZSIgeDE9IjAiIHkxPSIwIiB4Mj0iMSIgeTI9IjAiPgogICAgICA8c3RvcCBvZmZzZXQ9IjAiIHN0b3AtY29sb3I9IiMxRjZCMzAiIHN0b3Atb3BhY2l0eT0iMC4zNSIvPgogICAgICA8c3RvcCBvZmZzZXQ9IjEiIHN0b3AtY29sb3I9IiMxRjZCMzAiIHN0b3Atb3BhY2l0eT0iMCIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICA8L2RlZnM+CiAgPHJlY3Qgd2lkdGg9IjYwMCIgaGVpZ2h0PSI2MDAiIGZpbGw9InVybCgjYmcpIi8+CiAgPGVsbGlwc2UgY3g9IjMwMCIgY3k9IjQ3MiIgcng9IjE1MCIgcnk9IjE0IiBmaWxsPSIjMUY2QjMwIiBvcGFjaXR5PSIwLjE1Ii8+CgogIDxwYXRoIGQ9Ik0yMzggMTQ2IEwzMDAgMTc4IEwzNjIgMTQ2IEw0NDYgMTkyIEw0MTIgMjY0IEwzNzIgMjQyIEwzNzIgNDY0IEwyMjggNDY0IEwyMjggMjQyIEwxODggMjY0IEwxNTQgMTkyIFoiCiAgICAgICAgZmlsbD0idXJsKCNib2R5KSIgc3Ryb2tlPSIjMUY2QjMwIiBzdHJva2Utd2lkdGg9IjQiIHN0cm9rZS1saW5lam9pbj0icm91bmQiLz4KICA8cGF0aCBkPSJNMzAwIDE3OCBMMzYyIDE0NiBMNDAwIDE2NiBMMzcyIDI0MiBMMzcyIDQ2NCBMMzMwIDQ2NCBMMzMwIDE3OCBaIiBmaWxsPSJ1cmwoI3NoYWRlKSIvPgogIDxwYXRoIGQ9Ik0yNjIgMTUwIFEzMDAgMjAyIDMzOCAxNTAiIGZpbGw9Im5vbmUiIHN0cm9rZT0iIzFGNkIzMCIgc3Ryb2tlLXdpZHRoPSI4IiBzdHJva2UtbGluZWNhcD0icm91bmQiIG9wYWNpdHk9IjAuNTUiLz4KICA8cGF0aCBkPSJNMjYyIDE1MCBRMzAwIDE5NiAzMzggMTUwIiBmaWxsPSJub25lIiBzdHJva2U9IiNFQUY3RUMiIHN0cm9rZS13aWR0aD0iOSIvPgogIDxwYXRoIGQ9Ik0yMjggMjYwIEwyMjggNDQwIiBzdHJva2U9IiM3QkNCODgiIHN0cm9rZS13aWR0aD0iNCIgb3BhY2l0eT0iMC41IiBzdHJva2UtbGluZWNhcD0icm91bmQiLz4KPC9zdmc+Cg=="],
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
                Images = ["data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA2MDAgNjAwIj4KICA8ZGVmcz4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYmciIHgxPSIwIiB5MT0iMCIgeDI9IjAiIHkyPSIxIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjRUFGN0VDIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0QzRUZEOCIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYm9keSIgeDE9IjAiIHkxPSIwIiB4Mj0iMSIgeTI9IjEiPgogICAgICA8c3RvcCBvZmZzZXQ9IjAiIHN0b3AtY29sb3I9IiMyQjdBNUUiLz4KICAgICAgPHN0b3Agb2Zmc2V0PSIxIiBzdG9wLWNvbG9yPSIjMTI0RjNBIi8+CiAgICA8L2xpbmVhckdyYWRpZW50PgogICAgPGxpbmVhckdyYWRpZW50IGlkPSJzaGFkZSIgeDE9IjAiIHkxPSIwIiB4Mj0iMSIgeTI9IjAiPgogICAgICA8c3RvcCBvZmZzZXQ9IjAiIHN0b3AtY29sb3I9IiMwQTM1MjciIHN0b3Atb3BhY2l0eT0iMC40Ii8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iIzBBMzUyNyIgc3RvcC1vcGFjaXR5PSIwIi8+CiAgICA8L2xpbmVhckdyYWRpZW50PgogICAgPGxpbmVhckdyYWRpZW50IGlkPSJjdWZmIiB4MT0iMCIgeTE9IjAiIHgyPSIwIiB5Mj0iMSI+CiAgICAgIDxzdG9wIG9mZnNldD0iMCIgc3RvcC1jb2xvcj0iIzBBMzUyNyIvPgogICAgICA8c3RvcCBvZmZzZXQ9IjEiIHN0b3AtY29sb3I9IiMwNjJBMUUiLz4KICAgIDwvbGluZWFyR3JhZGllbnQ+CiAgPC9kZWZzPgogIDxyZWN0IHdpZHRoPSI2MDAiIGhlaWdodD0iNjAwIiBmaWxsPSJ1cmwoI2JnKSIvPgogIDxlbGxpcHNlIGN4PSIzMDAiIGN5PSI0ODYiIHJ4PSIxNzUiIHJ5PSIxNCIgZmlsbD0iIzBBMzUyNyIgb3BhY2l0eT0iMC4xNSIvPgoKICA8IS0tIExvbmctc2xlZXZlIHdpbmRicmVha2VyOiBzbGVldmVzIHJ1biB0aGUgZnVsbCBhcm0gbGVuZ3RoIGRvd24gdG8KICAgICAgIHJpYmJlZCBjdWZmcyBuZWFyIHRoZSBoZW0sIG5vdCB0aGUgY2FwIHNsZWV2ZXMgYSB0LXNoaXJ0IGhhcy4gLS0+CiAgPHBhdGggZD0iTTI1NSwxNTAgTDMwMCwxODggTDM0NSwxNTAgTDM3MCwxNjYgTDQxNiwxOTAgTDQxMCw0MjggTDM3OCw0MjggTDM1MCwyMzIgTDM2OCw0NzAgTDIzMiw0NzAgTDI1MCwyMzIgTDIyMiw0MjggTDE5MCw0MjggTDE4NCwxOTAgTDIzMCwxNjYgWiIKICAgICAgICBmaWxsPSJ1cmwoI2JvZHkpIiBzdHJva2U9IiMwQTM1MjciIHN0cm9rZS13aWR0aD0iNCIgc3Ryb2tlLWxpbmVqb2luPSJyb3VuZCIvPgogIDxwYXRoIGQ9Ik0zMDAsMTg4IEwzNDUsMTUwIEwzNzAsMTY2IEw0MTYsMTkwIEw0MTAsNDI4IEwzNzgsNDI4IEwzNTAsMjMyIEwzNjgsNDcwIEwzMjgsNDcwIEwzMjgsMTg4IFoiIGZpbGw9InVybCgjc2hhZGUpIi8+CgogIDxwYXRoIGQ9Ik0yNTUsMTUwIEwzMDAsMTg4IEwzNDUsMTUwIiBmaWxsPSJub25lIiBzdHJva2U9IiMwQTM1MjciIHN0cm9rZS13aWR0aD0iNiIgc3Ryb2tlLWxpbmVqb2luPSJyb3VuZCIvPgoKICA8cmVjdCB4PSIxODIiIHk9IjQxMCIgd2lkdGg9IjQ2IiBoZWlnaHQ9IjMwIiByeD0iOCIgZmlsbD0idXJsKCNjdWZmKSIvPgogIDxyZWN0IHg9IjM3MiIgeT0iNDEwIiB3aWR0aD0iNDYiIGhlaWdodD0iMzAiIHJ4PSI4IiBmaWxsPSJ1cmwoI2N1ZmYpIi8+CgogIDxyZWN0IHg9IjI5MiIgeT0iMjAwIiB3aWR0aD0iMTYiIGhlaWdodD0iMjcwIiByeD0iOCIgZmlsbD0iIzBBMzUyNyIgb3BhY2l0eT0iMC41NSIvPgogIDxjaXJjbGUgY3g9IjMwMCIgY3k9IjI0MCIgcj0iNyIgZmlsbD0iI0RGRjNFNiIvPgogIDxjaXJjbGUgY3g9IjMwMCIgY3k9IjI5MiIgcj0iNyIgZmlsbD0iI0RGRjNFNiIvPgogIDxjaXJjbGUgY3g9IjMwMCIgY3k9IjM0NCIgcj0iNyIgZmlsbD0iI0RGRjNFNiIvPgogIDxjaXJjbGUgY3g9IjMwMCIgY3k9IjM5NiIgcj0iNyIgZmlsbD0iI0RGRjNFNiIvPgogIDxwYXRoIGQ9Ik0zMDAsNDQwIEwzMTIsNDU2IEwyODgsNDU2IFoiIGZpbGw9IiNERkYzRTYiLz4KPC9zdmc+Cg=="],
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
                Images = ["data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA2MDAgNjAwIj4KICA8ZGVmcz4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYmciIHgxPSIwIiB5MT0iMCIgeDI9IjAiIHkyPSIxIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjRkJFQkU2Ii8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0Y3RDlDRiIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYm9keSIgeDE9IjAiIHkxPSIwIiB4Mj0iMSIgeTI9IjAiPgogICAgICA8c3RvcCBvZmZzZXQ9IjAiIHN0b3AtY29sb3I9IiNFMjQ3MkEiLz4KICAgICAgPHN0b3Agb2Zmc2V0PSIwLjUiIHN0b3AtY29sb3I9IiNDNDMzMTgiLz4KICAgICAgPHN0b3Agb2Zmc2V0PSIxIiBzdG9wLWNvbG9yPSIjOUMyNjBGIi8+CiAgICA8L2xpbmVhckdyYWRpZW50PgogICAgPGxpbmVhckdyYWRpZW50IGlkPSJ0YW5rIiB4MT0iMCIgeTE9IjAiIHgyPSIxIiB5Mj0iMCI+CiAgICAgIDxzdG9wIG9mZnNldD0iMCIgc3RvcC1jb2xvcj0iI0Y1QTM4NiIgc3RvcC1vcGFjaXR5PSIwLjg1Ii8+CiAgICAgIDxzdG9wIG9mZnNldD0iMC41IiBzdG9wLWNvbG9yPSIjQjIzMDE1IiBzdG9wLW9wYWNpdHk9IjAuNTUiLz4KICAgICAgPHN0b3Agb2Zmc2V0PSIxIiBzdG9wLWNvbG9yPSIjRjVBMzg2IiBzdG9wLW9wYWNpdHk9IjAuODUiLz4KICAgIDwvbGluZWFyR3JhZGllbnQ+CiAgICA8bGluZWFyR3JhZGllbnQgaWQ9ImNhcmFmZSIgeDE9IjAiIHkxPSIwIiB4Mj0iMSIgeTI9IjAiPgogICAgICA8c3RvcCBvZmZzZXQ9IjAiIHN0b3AtY29sb3I9IiM2QjM0MTgiLz4KICAgICAgPHN0b3Agb2Zmc2V0PSIwLjUiIHN0b3AtY29sb3I9IiMzRTIwMTAiLz4KICAgICAgPHN0b3Agb2Zmc2V0PSIxIiBzdG9wLWNvbG9yPSIjNkIzNDE4Ii8+CiAgICA8L2xpbmVhckdyYWRpZW50PgogIDwvZGVmcz4KICA8cmVjdCB3aWR0aD0iNjAwIiBoZWlnaHQ9IjYwMCIgZmlsbD0idXJsKCNiZykiLz4KICA8ZWxsaXBzZSBjeD0iMzAwIiBjeT0iNDcyIiByeD0iMTUwIiByeT0iMTQiIGZpbGw9IiM3QTJCMTAiIG9wYWNpdHk9IjAuMTUiLz4KCiAgPHBhdGggZD0iTTIyMCAxMTAgUTIwNiAxNDAgMjI0IDE2NCBNMzAwIDEwMCBRMjg2IDEzMCAzMDQgMTU0IE0zODAgMTEwIFEzNjYgMTQwIDM4NCAxNjQiCiAgICAgICAgZmlsbD0ibm9uZSIgc3Ryb2tlPSIjQzQzMzE4IiBzdHJva2Utd2lkdGg9IjkiIHN0cm9rZS1saW5lY2FwPSJyb3VuZCIgb3BhY2l0eT0iMC40NSIvPgoKICA8cmVjdCB4PSIxOTYiIHk9IjE1MCIgd2lkdGg9IjIwOCIgaGVpZ2h0PSIxNTAiIHJ4PSIxOCIgZmlsbD0idXJsKCNib2R5KSIvPgogIDxyZWN0IHg9IjIyMiIgeT0iMTcwIiB3aWR0aD0iNDYiIGhlaWdodD0iMTEwIiByeD0iOCIgZmlsbD0idXJsKCN0YW5rKSIvPgogIDxyZWN0IHg9IjIzMCIgeT0iMTIwIiB3aWR0aD0iMTQwIiBoZWlnaHQ9IjQwIiByeD0iMTAiIGZpbGw9IiM3QTJCMTAiLz4KICA8Y2lyY2xlIGN4PSIzNjAiIGN5PSIxOTYiIHI9IjkiIGZpbGw9IiNGRkQ5QTAiLz4KCiAgPHBhdGggZD0iTTIzMCAzMjAgTDM3MCAzMjAgTDM0OCA0NDIgTDI1MiA0NDIgWiIgZmlsbD0idXJsKCNjYXJhZmUpIi8+CiAgPHBhdGggZD0iTTI0MCAzMzAgTDM2MCAzMzAgTDM0NCA0MjQgTDI1NiA0MjQgWiIgZmlsbD0iI0REOEE0RiIgb3BhY2l0eT0iMC4xOCIvPgogIDxwYXRoIGQ9Ik0zNzAgMzQ4IFE0MTIgMzQ4IDQxMiAzODYgUTQxMiA0MjAgMzcwIDQyMCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSIjM0UyMDEwIiBzdHJva2Utd2lkdGg9IjE0IiBzdHJva2UtbGluZWNhcD0icm91bmQiLz4KICA8cmVjdCB4PSIyNTIiIHk9IjQ0MiIgd2lkdGg9Ijk4IiBoZWlnaHQ9IjE4IiByeD0iNSIgZmlsbD0iIzI0MTIwOSIvPgogIDxlbGxpcHNlIGN4PSIzMDAiIGN5PSIzMjAiIHJ4PSI3MCIgcnk9IjkiIGZpbGw9IiM3QTNEMUUiLz4KPC9zdmc+Cg=="],
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
                Images = ["data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA2MDAgNjAwIj4KICA8ZGVmcz4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iYmciIHgxPSIwIiB5MT0iMCIgeDI9IjAiIHkyPSIxIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjRkJFQkU2Ii8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0Y3RDlDRiIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0icG90IiB4MT0iMCIgeTE9IjAiIHgyPSIxIiB5Mj0iMCI+CiAgICAgIDxzdG9wIG9mZnNldD0iMCIgc3RvcC1jb2xvcj0iI0YwNzA0QSIvPgogICAgICA8c3RvcCBvZmZzZXQ9IjAuNDUiIHN0b3AtY29sb3I9IiNDNDMzMTgiLz4KICAgICAgPHN0b3Agb2Zmc2V0PSIxIiBzdG9wLWNvbG9yPSIjOEUyMjBDIi8+CiAgICA8L2xpbmVhckdyYWRpZW50PgogICAgPGxpbmVhckdyYWRpZW50IGlkPSJsaWQiIHgxPSIwIiB5MT0iMCIgeDI9IjEiIHkyPSIwIj4KICAgICAgPHN0b3Agb2Zmc2V0PSIwIiBzdG9wLWNvbG9yPSIjRkY5QjcyIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMC41IiBzdG9wLWNvbG9yPSIjREU1MzMwIi8+CiAgICAgIDxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iI0E4MkMxMiIvPgogICAgPC9saW5lYXJHcmFkaWVudD4KICAgIDxsaW5lYXJHcmFkaWVudCBpZD0iaGFuZGxlIiB4MT0iMCIgeTE9IjAiIHgyPSIwIiB5Mj0iMSI+CiAgICAgIDxzdG9wIG9mZnNldD0iMCIgc3RvcC1jb2xvcj0iIzVDMkExNCIvPgogICAgICA8c3RvcCBvZmZzZXQ9IjEiIHN0b3AtY29sb3I9IiMzQTFBMEMiLz4KICAgIDwvbGluZWFyR3JhZGllbnQ+CiAgPC9kZWZzPgogIDxyZWN0IHdpZHRoPSI2MDAiIGhlaWdodD0iNjAwIiBmaWxsPSJ1cmwoI2JnKSIvPgogIDxlbGxpcHNlIGN4PSIzMDAiIGN5PSI0NTIiIHJ4PSIxNjAiIHJ5PSIxNiIgZmlsbD0iIzdBMkIxMCIgb3BhY2l0eT0iMC4xNSIvPgoKICA8cGF0aCBkPSJNMTcyIDI5NiBMMTcyIDM3OCBRMTcyIDQxMiAyMDYgNDEyIEwzOTQgNDEyIFE0MjggNDEyIDQyOCAzNzggTDQyOCAyOTYgWiIgZmlsbD0idXJsKCNwb3QpIi8+CiAgPHJlY3QgeD0iMTcyIiB5PSIyOTYiIHdpZHRoPSIyNTYiIGhlaWdodD0iMTgiIGZpbGw9IiM4RTIyMEMiLz4KCiAgPHJlY3QgeD0iODgiIHk9IjI4MCIgd2lkdGg9Ijg2IiBoZWlnaHQ9IjI4IiByeD0iMTQiIGZpbGw9InVybCgjaGFuZGxlKSIvPgogIDxyZWN0IHg9IjQyNiIgeT0iMjgwIiB3aWR0aD0iODYiIGhlaWdodD0iMjgiIHJ4PSIxNCIgZmlsbD0idXJsKCNoYW5kbGUpIi8+CiAgPGNpcmNsZSBjeD0iMTEyIiBjeT0iMjk0IiByPSI1IiBmaWxsPSIjRjVDOUFFIi8+CiAgPGNpcmNsZSBjeD0iNDg4IiBjeT0iMjk0IiByPSI1IiBmaWxsPSIjRjVDOUFFIi8+CgogIDxlbGxpcHNlIGN4PSIzMDAiIGN5PSIyNzAiIHJ4PSIxMzYiIHJ5PSIyNCIgZmlsbD0idXJsKCNsaWQpIi8+CiAgPGVsbGlwc2UgY3g9IjMwMCIgY3k9IjI2NCIgcng9IjEzNiIgcnk9IjIyIiBmaWxsPSIjRkZCNDg5IiBvcGFjaXR5PSIwLjMiLz4KICA8cmVjdCB4PSIyODgiIHk9IjIzMCIgd2lkdGg9IjI0IiBoZWlnaHQ9IjI0IiByeD0iNiIgZmlsbD0idXJsKCNoYW5kbGUpIi8+CiAgPGNpcmNsZSBjeD0iMzAwIiBjeT0iMjM2IiByPSIxMiIgZmlsbD0iIzVDMkExNCIvPgogIDxlbGxpcHNlIGN4PSIyNTIiIGN5PSIyNjIiIHJ4PSI0NiIgcnk9IjgiIGZpbGw9IiNGRkUwQ0MiIG9wYWNpdHk9IjAuNTUiLz4KPC9zdmc+Cg=="],
                CreatedAt = now
            }
        };

        foreach (var product in products)
        {
            await productRepository.InsertAsync(product, cancellationToken);
        }
    }
}
