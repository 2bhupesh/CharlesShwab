using System.Net.ServerSentEvents;
using AgenticSdlc.Web.Realtime;

namespace AgenticSdlc.Web.Api;

public static class EventStreamEndpoints
{
    public static void MapEventStreamEndpoints(this RouteGroupBuilder api)
    {
        // Server-sent events: server→client only, native EventSource reconnection, no client library.
        api.MapGet("/events", (HttpContext ctx, EventBroadcaster broadcaster, string? workflowId, long? lastEventId, CancellationToken ct) =>
        {
            // Resume from the query param or the browser's automatic Last-Event-ID header.
            long? resume = lastEventId;
            if (resume is null && long.TryParse(ctx.Request.Headers["Last-Event-ID"], out var headerSeq))
                resume = headerSeq;

            var items = broadcaster.Subscribe(workflowId, resume, ct)
                .Select(e => new SseItem<SseEnvelope>(e, e.Type) { EventId = e.Seq.ToString() });

            return TypedResults.ServerSentEvents(items);
        }).WithTags("Events");
    }
}
