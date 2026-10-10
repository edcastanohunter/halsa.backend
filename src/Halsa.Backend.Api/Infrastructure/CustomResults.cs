using Halsa.Backend.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;

namespace Halsa.Backend.Api.Infrastructure;

/// <summary>
/// Single translation point from a domain <see cref="Result"/> to an HTTP response.
/// Handlers never mention status codes; this class never mentions business rules.
/// </summary>
public static class CustomResults
{
    public static IResult Problem(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess)
        {
            throw new InvalidOperationException("A successful result cannot be turned into a problem response.");
        }

        return Results.Problem(
            title: GetTitle(result.Error),
            detail: GetDetail(result.Error),
            type: GetType(result.Error.Type),
            statusCode: GetStatusCode(result.Error.Type),
            extensions: GetErrors(result));
    }

    private static string GetTitle(Error error) => error.Type switch
    {
        ErrorType.Validation => "Validation failed",
        ErrorType.Problem => "Bad request",
        ErrorType.NotFound => "Not found",
        ErrorType.Conflict => "Conflict",
        ErrorType.Forbidden => "Forbidden",
        ErrorType.Unauthorized => "Unauthorized",
        _ => "Server failure"
    };

    private static string GetDetail(Error error) => error.Type switch
    {
        ErrorType.Failure => "An unexpected error occurred.",
        _ => error.Description
    };

    private static string GetType(ErrorType errorType) => errorType switch
    {
        ErrorType.Validation => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        ErrorType.Problem => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        ErrorType.NotFound => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        ErrorType.Conflict => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        ErrorType.Forbidden => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        ErrorType.Unauthorized => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        _ => "https://tools.ietf.org/html/rfc9110#section-15.6.1"
    };

    private static int GetStatusCode(ErrorType errorType) => errorType switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Problem => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        _ => StatusCodes.Status500InternalServerError
    };

    private static Dictionary<string, object?>? GetErrors(Result result)
    {
        if (result.Error is not ValidationError validationError)
        {
            return new Dictionary<string, object?> { { "errorCode", result.Error.Code } };
        }

        return new Dictionary<string, object?>
        {
            { "errorCode", validationError.Code },
            {
                "errors",
                validationError.Errors
                    .Select(error => new { code = error.Code, description = error.Description })
                    .ToArray()
            }
        };
    }
}
