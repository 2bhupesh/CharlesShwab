# URL Shortener

A minimal URL shortener service built with ASP.NET minimal APIs on .NET 10.

## Endpoints

| Method | Route      | Description                                                        |
|--------|------------|--------------------------------------------------------------------|
| POST   | `/links`   | Body `{ "url": "https://..." }` → `{ "code", "shortUrl" }`.        |
| GET    | `/{code}`  | 302 redirect to the original URL, or 404 if the code is unknown.   |

Invalid (non-absolute or non-http/https) URLs return `400` with a problem description.

## Design

- **`ILinkStore`** abstracts storage. `InMemoryLinkStore` is the v1 implementation; a database-backed
  store can be dropped in without touching the API layer.
- **`ShortCode`** generates 7-character URL-safe codes, retried on the rare collision.
- **`UrlValidator`** enforces absolute http/https targets.

## Running

```bash
dotnet run --project src/UrlShortener.Api
```

## Testing

```bash
dotnet test
```

## Swapping storage

Implement `ILinkStore` and register it in `Program.cs`:

```csharp
builder.Services.AddSingleton<ILinkStore, YourDatabaseLinkStore>();
```
