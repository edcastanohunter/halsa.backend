using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.UnitTests.Tenancy;

public sealed class TenantIdTests
{
    [Theory]
    [InlineData("ARL_A")]
    [InlineData("ARL_B")]
    [InlineData("TENANT1")]
    [InlineData("AB")]                    // mínimo 2 caracteres
    [InlineData("ABCDEFGHIJ1234567890")] // máximo 20 caracteres
    [InlineData("A1")]
    [InlineData("T_E_N_A_N_T")]
    public void TryCreate_ShouldSucceed_WhenFormatIsValid(string value)
    {
        bool result = TenantId.TryCreate(value, out TenantId tenantId);

        result.ShouldBeTrue();
        tenantId.Value.ShouldBe(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")]                       // solo 1 carácter
    [InlineData("ABCDEFGHIJ12345678901")]  // 21 caracteres
    [InlineData("arl_a")]                  // minúsculas
    [InlineData("ARL A")]                  // espacio
    [InlineData("ARL-A")]                  // guión no permitido
    [InlineData("ARL.A")]                  // punto no permitido
    public void TryCreate_ShouldFail_WhenFormatIsInvalid(string? value)
    {
        bool result = TenantId.TryCreate(value, out TenantId tenantId);

        result.ShouldBeFalse();
        tenantId.ShouldBe(default(TenantId));
    }

    [Fact]
    public void ToString_ShouldReturnValue()
    {
        var tenantId = new TenantId("ARL_A");
        tenantId.ToString().ShouldBe("ARL_A");
    }
}
