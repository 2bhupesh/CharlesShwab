using AgenticSdlc.Core.Orchestration;
using AgenticSdlc.Web.Contracts;
using AgenticSdlc.Web.Services;

namespace AgenticSdlc.Web.Api;

public static class WorkflowEndpoints
{
    public static void MapScenarioEndpoints(this RouteGroupBuilder api, ScenarioCatalog catalog)
    {
        api.MapGet("/scenarios", () => Results.Ok(catalog.All)).WithTags("Scenarios");
    }

    public static void MapWorkflowEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/workflows").WithTags("Workflows");

        g.MapPost("", async (StartWorkflowRequest req, ScenarioCatalog catalog, WorkflowService svc, CancellationToken ct) =>
        {
            if (catalog.Find(req.Scenario) is null)
                return Results.Problem($"Unknown scenario '{req.Scenario}'.", statusCode: StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(req.Requirement))
                return Results.Problem("Requirement text is required.", statusCode: StatusCodes.Status400BadRequest);

            Guid? source = Guid.TryParse(req.SourceWorkflowId, out var s) ? s : null;
            var id = await svc.CreateAsync(req.Name ?? "", req.Requirement, req.Scenario.ToLowerInvariant(), source, ct);
            await svc.StartAsync(id, ct);
            return Results.Created($"/api/workflows/{id}", new { workflowId = id.ToString() });
        });

        g.MapGet("", async (string? status, string? scenario, ReadModel read, CancellationToken ct) =>
            Results.Ok(await read.ListAsync(status, scenario, ct)));

        g.MapGet("/{id:guid}", async (Guid id, ReadModel read, CancellationToken ct) =>
            await read.GetDetailAsync(id, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());

        g.MapPost("/{id:guid}/pause", (Guid id, WorkflowService svc, CancellationToken ct) =>
            ApiHelpers.ControlAsync(() => svc.PauseAsync(id, ct)));
        g.MapPost("/{id:guid}/stop", (Guid id, WorkflowService svc, CancellationToken ct) =>
            ApiHelpers.ControlAsync(() => svc.PauseAsync(id, ct))); // safe stop == pause (in-flight reverts)
        g.MapPost("/{id:guid}/resume", (Guid id, WorkflowService svc, CancellationToken ct) =>
            ApiHelpers.ControlAsync(() => svc.ResumeAsync(id, ct)));
        g.MapPost("/{id:guid}/cancel", (Guid id, WorkflowService svc, CancellationToken ct) =>
            ApiHelpers.ControlAsync(() => svc.CancelAsync(id, ct)));

        g.MapPost("/{id:guid}/replan", (Guid id, ReplanRequest req, WorkflowService svc, CancellationToken ct) =>
            Guid.TryParse(req.NodeId, out var nodeId)
                ? ApiHelpers.ControlAsync(() => svc.ReplanFromNodeAsync(id, nodeId, req.Reason, ct))
                : Task.FromResult(Results.Problem("Invalid nodeId.", statusCode: StatusCodes.Status400BadRequest)));
    }
}
