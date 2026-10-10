namespace Halsa.Backend.SharedKernel.Abstractions;

/// <summary>
/// Time as a dependency, so handlers stay deterministic under test.
/// Always UTC: Npgsql maps <c>timestamptz</c> to UTC <see cref="DateTime"/> values.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }

    DateOnly TodayUtc { get; }
}
