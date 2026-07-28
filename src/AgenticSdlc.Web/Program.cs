using System.Text.Json.Serialization;
using AgenticSdlc.Core;
using AgenticSdlc.Web.Api;
using AgenticSdlc.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// The platform: engine, agents, governance, persistence, observability, background runner.
// ANTHROPIC_API_KEY is read from the environment; everything else from the AgenticSdlc config section.
builder.Services.AddAgenticSdlcCore(builder.Configuration);

// Delivery-surface services.
builder.Services.AddSingleton<ScenarioCatalog>();
builder.Services.AddSingleton<ReadModel>();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

var app = builder.Build();

// Create the database (WAL) and workspace directories before serving.
await app.Services.InitializeAgenticSdlcAsync();

app.UseDefaultFiles();   // serve wwwroot/index.html at /
app.UseStaticFiles();

app.MapOpenApi();        // /openapi/v1.json

// CORS is intentionally absent: the dashboard and API share the same origin.
var api = app.MapGroup("/api");
api.MapScenarioEndpoints(app.Services.GetRequiredService<ScenarioCatalog>());
api.MapWorkflowEndpoints();
api.MapGovernanceEndpoints();
api.MapArtifactEndpoints();
api.MapMetricsEndpoints();
api.MapReviewEndpoints();
api.MapHealthEndpoints();

app.Run();

// Exposed so integration tests can drive the app via WebApplicationFactory.
public partial class Program { }
