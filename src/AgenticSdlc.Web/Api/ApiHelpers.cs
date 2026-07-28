namespace AgenticSdlc.Web.Api;

/// <summary>Shared helpers for control endpoints — async operations that map failures to status codes.</summary>
public static class ApiHelpers
{
    /// <summary>
    /// Runs a control action, returning 202 on success, 404 when the target is missing, and 409 for an
    /// invalid state transition (the core services throw <see cref="InvalidOperationException"/> for both).
    /// </summary>
    public static async Task<IResult> ControlAsync(Func<Task> action)
    {
        try
        {
            await action();
            return Results.Accepted();
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound)
                : Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
