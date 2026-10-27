namespace MangaLeech.Core;

internal interface IMangaSite : IAsyncDisposable
{
    Task<List<Chapter>> GetChapters(Client client, string url, CancellationToken ct);
    Task<Chapter> GetChapter(Client client, string url, CancellationToken ct);
    Task<List<string>> GetImages(Client client, string url, CancellationToken ct);
}
