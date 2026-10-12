using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.UnitTests.Tenancy;

public sealed class IpsIdTests
{
    [Theory]
    [InlineData("110011234501")]  // exactamente 12 dígitos
    [InlineData("000000000000")]
    [InlineData("999999999999")]
    public void TryCreate_ShouldSucceed_WhenFormatIsValid(string value)
    {
        bool result = IpsId.TryCreate(value, out IpsId ipsId);

        result.ShouldBeTrue();
        ipsId.Value.ShouldBe(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("11001123450")]    // 11 dígitos
    [InlineData("1100112345010")] // 13 dígitos
    [InlineData("11001123450A")]  // letras
    [InlineData("1100-1123450")]  // guión
    [InlineData("1100 1123450")]  // espacio
    public void TryCreate_ShouldFail_WhenFormatIsInvalid(string? value)
    {
        bool result = IpsId.TryCreate(value, out IpsId ipsId);

        result.ShouldBeFalse();
        ipsId.ShouldBe(default(IpsId));
    }

    [Fact]
    public void ToString_ShouldReturnValue()
    {
        var ipsId = new IpsId("110011234501");
        ipsId.ToString().ShouldBe("110011234501");
    }
}
