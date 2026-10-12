using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Halsa.Backend.Infrastructure.Database.Clinico;

/// <summary>
/// Default mappings for the value objects that cross <see cref="ClinicalDbContext.TenantDelCasoRemitido"/>:
/// a function's return value and parameters are not properties, so they need a type-level mapping.
/// </summary>
internal sealed class TenantIdConverter() : ValueConverter<TenantId, string>(v => v.Value, v => new TenantId(v));

/// <inheritdoc cref="TenantIdConverter" />
internal sealed class CasoMedicoIdConverter() : ValueConverter<CasoMedicoId, Guid>(v => v.Value, v => new CasoMedicoId(v));
