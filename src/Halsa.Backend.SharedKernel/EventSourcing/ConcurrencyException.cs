using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.SharedKernel.EventSourcing;

/// <summary>Raised when two writers race on the same stream.</summary>
public sealed class ConcurrencyException : DomainException
{
    public ConcurrencyException(string streamId, int expectedVersion, int actualVersion)
        : base($"Stream '{streamId}' was at version {actualVersion} but version {expectedVersion} was expected.")
    {
        StreamId = streamId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public string StreamId { get; }

    public int ExpectedVersion { get; }

    public int ActualVersion { get; }
}
