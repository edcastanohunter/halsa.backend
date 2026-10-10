namespace Halsa.Backend.Application.Abstractions.Models;

/// <summary>A page of results plus the metadata a client needs to ask for the next one.</summary>
public sealed record PagedList<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasNextPage => Page < TotalPages;

    public bool HasPreviousPage => Page > 1;

    public static PagedList<T> Empty(int page, int pageSize) => new([], page, pageSize, 0);
}
