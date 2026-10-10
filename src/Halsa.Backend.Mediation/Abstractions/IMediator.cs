namespace Halsa.Backend.Mediation;

/// <summary>
/// Convenience facade over <see cref="ISender"/> and <see cref="IPublisher"/>.
/// Depend on the narrow interface you actually need; this one exists for composition roots.
/// </summary>
public interface IMediator : ISender, IPublisher;
