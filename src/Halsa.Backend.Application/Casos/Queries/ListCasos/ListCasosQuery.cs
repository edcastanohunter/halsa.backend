using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Application.Casos.Queries;

namespace Halsa.Backend.Application.Casos.Queries.ListCasos;

/// <summary>Retorna la lista paginada de casos visibles para el tenant/IPS del actor actual.</summary>
public sealed record ListCasosQuery(
    int Page = 1,
    int PageSize = 20,
    EstadoCaso? Estado = null) : IQuery<PagedList<CasoResumenResponse>>;
