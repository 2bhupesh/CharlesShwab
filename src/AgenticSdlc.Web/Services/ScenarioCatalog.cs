using AgenticSdlc.Web.Contracts;

namespace AgenticSdlc.Web.Services;

/// <summary>
/// The three demonstration scenarios and their prefilled requirement text. This is seed data — the
/// only place a scenario's specifics live; the platform code stays scenario-agnostic (NFR-1).
/// </summary>
public sealed class ScenarioCatalog
{
    public IReadOnlyList<ScenarioDescriptor> All { get; } = new List<ScenarioDescriptor>
    {
        new("greenfield", "Greenfield",
            "Develop a new system from a natural-language requirement, end to end.",
            "Build a URL shortener web service. Requirements: (1) POST /links accepts a JSON body with a target URL " +
            "and returns a short code and the short URL; (2) GET /{code} responds 302 redirecting to the original URL; " +
            "(3) unknown codes return 404; (4) target URLs must be validated as absolute http/https URLs — invalid input " +
            "returns 400 with a problem description; (5) short codes are 6-8 URL-safe characters and collision-free; " +
            "(6) storage may be in-memory for v1 but must be isolated behind an interface so a database can be added later; " +
            "(7) include unit tests covering link creation, redirect behavior, validation failures, and unknown codes. " +
            "Technical constraints: C# with ASP.NET minimal APIs on .NET 10, xUnit for tests, no external database, no authentication.",
            RequiresExistingCodebase: false),

        new("brownfield", "Brownfield",
            "Enhance an existing codebase, with impact analysis before any change.",
            "Enhance the existing URL shortener service found in this workspace. Add two features: (1) expiring links — " +
            "POST /links accepts an optional expiresAt UTC timestamp; resolving an expired code returns 410 Gone; links " +
            "without expiresAt never expire; (2) click analytics — record every successful redirect, and add " +
            "GET /links/{code}/stats returning total click count and last-clicked timestamp. Constraints: do not break the " +
            "existing POST /links and GET /{code} contracts; all existing tests must continue to pass; follow the existing " +
            "code style and the existing ILinkStore abstraction; add tests for expiry and for click counting.",
            RequiresExistingCodebase: true),

        new("ambiguous", "Ambiguous requirement",
            "Interpret a vague request, ask clarifying questions, and converge on a solution.",
            "We need something to share links better. The marketing team keeps complaining about it.",
            RequiresExistingCodebase: false),
    };

    public ScenarioDescriptor? Find(string id) =>
        All.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
