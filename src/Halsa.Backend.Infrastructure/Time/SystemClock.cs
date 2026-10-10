using Halsa.Backend.SharedKernel.Abstractions;

namespace Halsa.Backend.Infrastructure.Time;

/// <summary>
/// Wraps <see cref="TimeProvider"/> so tests can inject <c>FakeTimeProvider</c> and the rest of the
/// codebase never touches <c>DateTime.UtcNow</c> directly.
/// </summary>
internal sealed class SystemClock(TimeProvider timeProvider) : IClock
{
    public DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public DateOnly TodayUtc => DateOnly.FromDateTime(UtcNow);
}
