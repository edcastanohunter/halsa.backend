using System.Collections.Concurrent;
using System.Reflection;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Behaviors;

/// <summary>
/// Builds a failed <see cref="Result"/> or <see cref="Result{TValue}"/> when only the closed
/// response type is known at compile time. The reflected factory is resolved once per type.
/// </summary>
internal static class ResultFactory
{
    private static readonly ConcurrentDictionary<Type, Func<Error, object>> Factories = new();

    public static TResponse Failure<TResponse>(Error error)
        where TResponse : Result
    {
        if (typeof(TResponse) == typeof(Result))
        {
            return (TResponse)(object)Result.Failure(error);
        }

        Func<Error, object> factory = Factories.GetOrAdd(typeof(TResponse), CreateFactory);

        return (TResponse)factory(error);
    }

    private static Func<Error, object> CreateFactory(Type responseType)
    {
        Type valueType = responseType.GetGenericArguments()[0];

        MethodInfo failure = typeof(Result)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m is { Name: nameof(Result.Failure), IsGenericMethodDefinition: true } &&
                         m.GetParameters().Length == 1)
            .MakeGenericMethod(valueType);

        return error => failure.Invoke(null, [error])!;
    }
}
