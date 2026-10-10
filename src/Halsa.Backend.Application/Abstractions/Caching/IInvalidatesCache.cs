namespace Halsa.Backend.Application.Abstractions.Caching;

/// <summary>
/// Opt-in marker for commands: once the command succeeds, every cached query tagged with one of
/// <see cref="CacheTagsToInvalidate"/> is evicted. A failed command evicts nothing.
/// </summary>
public interface IInvalidatesCache
{
    IReadOnlyCollection<string> CacheTagsToInvalidate { get; }
}
