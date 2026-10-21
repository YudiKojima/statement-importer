namespace StatementImporter.Api.Imports;

public readonly record struct Paging(int Page, int PageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    public const int MaxPage = int.MaxValue / MaxPageSize;

    public int Offset => (Page - 1) * PageSize;

    public static Paging From(int? page, int? pageSize) =>
        new(Math.Clamp(page ?? 1, 1, MaxPage), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));
}
