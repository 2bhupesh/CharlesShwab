using AgenticSdlc.Core.Governance;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Web.Contracts;
using AgenticSdlc.Web.Services;

namespace AgenticSdlc.Web.Api;

public static class GovernanceEndpoints
{
    public static void MapGovernanceEndpoints(this RouteGroupBuilder api)
    {
        // All pending approvals across workflows (powers the home-page badge).
        api.MapGet("/approvals", async (ApprovalService approvals, CancellationToken ct) =>
            Results.Ok((await approvals.GetPendingAsync(null, ct)).Select(ReadModel.ToGate))).WithTags("Governance");

        var g = api.MapGroup("/workflows/{id:guid}").WithTags("Governance");

        g.MapGet("/approvals", async (Guid id, ApprovalService approvals, CancellationToken ct) =>
            Results.Ok((await approvals.GetPendingAsync(id, ct)).Select(ReadModel.ToGate)));

        g.MapPost("/gates/{gateId:guid}/decision", async (Guid id, Guid gateId, GateDecisionRequest req, ApprovalService approvals, CancellationToken ct) =>
        {
            var approve = req.Decision.Equals("approve", StringComparison.OrdinalIgnoreCase);
            var reject = req.Decision.Equals("reject", StringComparison.OrdinalIgnoreCase);
            if (!approve && !reject)
                return Results.Problem("Decision must be 'approve' or 'reject'.", statusCode: StatusCodes.Status400BadRequest);
            return await ApiHelpers.ControlAsync(() =>
                approvals.ApproveAsync(gateId, approve, req.Approver, req.Comment, requestChanges: reject, ct));
        });

        g.MapPost("/gates/{gateId:guid}/clarifications", async (Guid id, Guid gateId, ClarificationAnswersRequest req, ApprovalService approvals, CancellationToken ct) =>
        {
            if (req.Answers is null || req.Answers.Count == 0)
                return Results.Problem("At least one answer is required.", statusCode: StatusCodes.Status400BadRequest);
            var answers = req.Answers.Select(a => new ClarificationAnswer(a.QuestionId, a.Answer)).ToList();
            return await ApiHelpers.ControlAsync(() => approvals.AnswerClarificationAsync(gateId, req.Respondent, answers, ct));
        });

        g.MapGet("/decisions", async (Guid id, ReadModel read, CancellationToken ct) => Results.Ok(await read.GetDecisionsAsync(id, ct)));
        g.MapGet("/risks", async (Guid id, ReadModel read, CancellationToken ct) => Results.Ok(await read.GetRisksAsync(id, ct)));
        g.MapGet("/prompts", async (Guid id, Guid? nodeId, ReadModel read, CancellationToken ct) => Results.Ok(await read.GetPromptsAsync(id, nodeId, ct)));

        g.MapGet("/timeline", async (Guid id, long? afterSeq, TimelineService timeline, CancellationToken ct) =>
        {
            var entries = await timeline.GetAsync(id, afterSeq ?? 0, ct);
            return Results.Ok(entries.Select(e => new TimelineEntryDto(
                e.Seq, e.At, e.EventType, e.Actor, e.Summary, e.NodeId?.ToString(), e.NodeKey)));
        });
    }
}
