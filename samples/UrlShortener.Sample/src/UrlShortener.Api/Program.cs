using UrlShortener.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ILinkStore, InMemoryLinkStore>();

var app = builder.Build();

// Create a short link for an absolute http/https URL.
app.MapPost("/links", (CreateLinkRequest request, ILinkStore store, HttpContext ctx) =>
{
    if (!UrlValidator.IsValid(request.Url))
        return Results.Problem("Target URL must be an absolute http/https URL.", statusCode: StatusCodes.Status400BadRequest);

    var link = store.Create(request.Url);
    var shortUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/{link.Code}";
    return Results.Ok(new CreateLinkResponse(link.Code, shortUrl));
});

// Resolve a short code to its target with a 302 redirect, or 404 if unknown.
app.MapGet("/{code}", (string code, ILinkStore store) =>
{
    var link = store.Get(code);
    return link is null ? Results.NotFound() : Results.Redirect(link.TargetUrl, permanent: false);
});

app.Run();

public record CreateLinkRequest(string Url);
public record CreateLinkResponse(string Code, string ShortUrl);
