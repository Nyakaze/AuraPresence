using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aura.Presence.Services.AniList;

public sealed class AniListException(string message, int status) : Exception(message)
{
    public int Status => status;

    public bool Unauthorized => status is 401 || Message.Contains("token", StringComparison.OrdinalIgnoreCase);

    public bool Transient => status is 408 or 429 or >= 500;
}

public sealed record AniListEntry(int MediaId, string Title, int? Episodes, int? Progress, string? Status);

public sealed class AniListAccount(HttpClient http, string token)
{
    private const string ViewerQuery = "query { Viewer { id name } }";

    private const string MediaQuery = """
        query ($id: Int) {
          Media(id: $id) { id episodes title { romaji english } mediaListEntry { progress status } }
        }
        """;

    private const string SaveMutation = """
        mutation ($mediaId: Int, $progress: Int, $status: MediaListStatus) {
          SaveMediaListEntry(mediaId: $mediaId, progress: $progress, status: $status) { id progress status }
        }
        """;

    public async Task<(int Id, string Name)> ViewerAsync(CancellationToken cancellationToken = default)
    {
        var viewer = (await AniListGraphQl.QueryAsync(http, ViewerQuery, new JsonObject(), token, cancellationToken)).GetProperty("Viewer");

        return (viewer.GetProperty("id").GetInt32(), viewer.GetProperty("name").GetString() ?? "");
    }

    public async Task<AniListEntry?> MediaAsync(int id, CancellationToken cancellationToken = default)
    {
        var data = await AniListGraphQl.QueryAsync(http, MediaQuery, new JsonObject { ["id"] = id }, token, cancellationToken);

        if (data.GetProperty("Media") is not { ValueKind: JsonValueKind.Object } media)
            return null;

        var titles = media.GetProperty("title");
        var entry = media.GetProperty("mediaListEntry");
        var listed = entry.ValueKind is JsonValueKind.Object;

        return new AniListEntry(
            media.GetProperty("id").GetInt32(),
            AniListGraphQl.Text(titles, "english") ?? AniListGraphQl.Text(titles, "romaji") ?? id.ToString(),
            AniListGraphQl.Number(media, "episodes"),
            listed ? AniListGraphQl.Number(entry, "progress") : null,
            listed ? AniListGraphQl.Text(entry, "status") : null);
    }

    public Task SaveProgressAsync(int mediaId, int progress, string status, CancellationToken cancellationToken = default) =>
        AniListGraphQl.QueryAsync(http, SaveMutation,
            new JsonObject { ["mediaId"] = mediaId, ["progress"] = progress, ["status"] = status }, token, cancellationToken);
}

internal static class AniListGraphQl
{
    private static readonly Uri Endpoint = new("https://graphql.anilist.co");

    public static async Task<JsonElement> QueryAsync(HttpClient http, string query, JsonObject variables, string? token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Content = new StringContent(new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString(),
            Encoding.UTF8, "application/json");
        request.Headers.Accept.ParseAdd("application/json");

        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AniListException($"AniList answered {status} {response.ReasonPhrase}.", status);
        }

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind is JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var error = errors[0];
            var errorStatus = Number(error, "status") ?? status;

            if (errorStatus is not 404)
                throw new AniListException(Text(error, "message") ?? $"AniList answered {errorStatus}.", errorStatus);
        }
        else if (!response.IsSuccessStatusCode)
        {
            throw new AniListException($"AniList answered {status} {response.ReasonPhrase}.", status);
        }

        return root.TryGetProperty("data", out var data) && data.ValueKind is JsonValueKind.Object
            ? data
            : throw new AniListException("AniList sent no data.", status);
    }

    public static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    public static int? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number ? value.GetInt32() : null;
}
