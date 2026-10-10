using System.Text.Json;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.UnitTests.SharedKernel;

/// <summary>Cached results travel through System.Text.Json, with whatever naming policy the cache uses.</summary>
public sealed class ResultSerializationTests
{
    public static TheoryData<JsonSerializerOptions> Options =>
        [JsonSerializerOptions.Default, new JsonSerializerOptions(JsonSerializerDefaults.Web)];

    [Theory]
    [MemberData(nameof(Options))]
    public void ASuccessfulResult_ShouldRoundTrip(JsonSerializerOptions options)
    {
        Result<string> original = Result.Success("value");

        Result<string> copy = JsonSerializer.Deserialize<Result<string>>(JsonSerializer.Serialize(original, options), options)!;

        copy.IsSuccess.ShouldBeTrue();
        copy.Value.ShouldBe("value");
        copy.Error.ShouldBe(Error.None);
    }

    [Theory]
    [MemberData(nameof(Options))]
    public void AnError_ShouldRoundTrip(JsonSerializerOptions options)
    {
        Error original = Error.NotFound("X.NotFound", "missing");

        Error copy = JsonSerializer.Deserialize<Error>(JsonSerializer.Serialize(original, options), options)!;

        copy.ShouldBe(original);
    }
}
