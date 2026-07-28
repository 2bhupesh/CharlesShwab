namespace AgenticSdlc.Core.Resilience;

/// <summary>
/// Exponential backoff with jitter for node retries (spec §6, FR-20). Deterministic randomness is
/// avoided in tests by allowing a fixed jitter seed; production varies it per node attempt.
/// </summary>
public sealed class RetryPolicy
{
    private readonly int _baseSeconds;

    public RetryPolicy(int baseDelaySeconds) => _baseSeconds = Math.Max(1, baseDelaySeconds);

    /// <summary>Delay before <paramref name="attempt"/> (1-based). attempt 1 → base, 2 → 2×base, ...</summary>
    public TimeSpan Delay(int attempt, double jitterFactor)
    {
        var exp = _baseSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        // jitterFactor in [0,1) maps to ±20% jitter.
        var jitter = 1.0 + (jitterFactor - 0.5) * 0.4;
        return TimeSpan.FromSeconds(exp * jitter);
    }
}
