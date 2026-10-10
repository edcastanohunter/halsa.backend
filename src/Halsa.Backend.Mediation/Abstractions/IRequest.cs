namespace Halsa.Backend.Mediation;

/// <summary>
/// A message with exactly one handler and one response. Commands and queries are both requests;
/// the distinction between them lives one layer up, in the application's CQRS contracts.
/// </summary>
/// <typeparam name="TResponse">Type returned by the handler.</typeparam>
public interface IRequest<out TResponse>;
