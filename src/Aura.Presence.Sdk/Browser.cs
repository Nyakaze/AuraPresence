namespace Aura.Presence.Sdk;

public sealed record BrowserVideo(TimeSpan Position, TimeSpan? Duration, bool Paused);

public sealed record BrowserPage(string Id, Uri Url, string Title, bool Active, BrowserVideo? Video);

public interface IBrowser
{
    bool IsConnected { get; }

    IReadOnlyList<BrowserPage> PagesOn(string host);

    Task<string?> FetchAsync(BrowserPage page, string path, CancellationToken cancellationToken);
}
