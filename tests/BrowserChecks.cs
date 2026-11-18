#:property TargetFramework=net10.0-windows10.0.19041.0
#:property UseWPF=true
#:property PublishAot=false
#:project ../src/Aura.Presence.App/Aura.Presence.App.csproj

using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Aura.Presence.App.Plugins;
using Aura.Presence.Sdk;

const string host = "www.example.com";
var bridge = BrowserBridge.Shared;

Check(bridge.PagesOn(host).Count == 0 && !bridge.IsConnected, "nothing connected yet");

using var http = new HttpClient();
var refused = await Retry(() => http.GetAsync(BrowserBridge.Address));
Check(refused.StatusCode == HttpStatusCode.Forbidden, $"a plain request is refused, got {refused.StatusCode}");

using var socket = new ClientWebSocket();
socket.Options.SetRequestHeader("Origin", "chrome-extension://abcdefghijklmnop");
await socket.ConnectAsync(new Uri("ws://localhost:47300/"), CancellationToken.None);

var hosts = await ReceiveAsync(socket, "hosts");
Check(hosts["hosts"]!.AsArray().Select(node => (string?)node).SequenceEqual([host]), $"hosts sent on connect: {hosts.ToJsonString()}");

await SendAsync(socket, new JsonObject { ["type"] = "hello", ["version"] = "0.0.0", ["browser"] = "Test Browser" });
await SendAsync(socket, new JsonObject
{
    ["type"] = "page", ["tab"] = 7, ["url"] = $"https://{host}/watch/1", ["title"] = "Watching", ["active"] = true,
    ["video"] = new JsonObject { ["position"] = 30.5, ["duration"] = 60, ["paused"] = false }
});
await SendAsync(socket, new JsonObject { ["type"] = "page", ["tab"] = 8, ["url"] = "https://elsewhere.example/", ["title"] = "Other" });

var page = (await Until(() => bridge.PagesOn(host).FirstOrDefault()))!;
Check(page is { Title: "Watching", Active: true } && page.Video == new BrowserVideo(TimeSpan.FromSeconds(30.5), TimeSpan.FromSeconds(60), false),
    $"page read: {page}");
Check(bridge.PagesOn(host).Count == 1, "pages on other hosts are left out");
Check(bridge.IsConnected && bridge.Browsers.SequenceEqual(["Test Browser"]), "browser name");

var fetched = bridge.FetchAsync(page, "/api/title?id=1", CancellationToken.None);
var request = await ReceiveAsync(socket, "fetch");
Check((int?)request["tab"] == 7 && (string?)request["path"] == "/api/title?id=1", $"fetch request: {request.ToJsonString()}");
await SendAsync(socket, new JsonObject { ["type"] = "fetched", ["id"] = request["id"]!.DeepClone(), ["body"] = "{\"ok\":true}" });
Check(await fetched == "{\"ok\":true}", "fetch body comes back");

foreach (var path in new[] { "//evil.example/", "/\\evil.example", "https://evil.example/" })
    Check(await Throws(() => bridge.FetchAsync(page, path, CancellationToken.None)), $"'{path}' is refused");

await SendAsync(socket, new JsonObject { ["type"] = "gone", ["tab"] = 7 });
Check(await Until(() => bridge.PagesOn(host).Count == 0 ? "gone" : null) == "gone", "closed tab disappears");

await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
Check(await Until(() => bridge.IsConnected ? null : "closed") == "closed", "disconnect is noticed");

Console.WriteLine("All browser checks passed.");

static async Task<T> Retry<T>(Func<Task<T>> action)
{
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            return await action();
        }
        catch (HttpRequestException) when (attempt < 20)
        {
            await Task.Delay(100);
        }
    }
}

static async Task<T?> Until<T>(Func<T?> read) where T : class
{
    for (var attempt = 0; attempt < 40; attempt++)
    {
        if (read() is { } value)
            return value;

        await Task.Delay(50);
    }

    return null;
}

static async Task<bool> Throws(Func<Task> action)
{
    try
    {
        await action();
        return false;
    }
    catch (ArgumentException)
    {
        return true;
    }
}

static Task SendAsync(WebSocket socket, JsonObject message) =>
    socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

static async Task<JsonObject> ReceiveAsync(WebSocket socket, string type)
{
    var buffer = new byte[64 * 1024];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        var message = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!.AsObject();

        if ((string?)message["type"] == type)
            return message;
    }
}

static void Check(bool condition, string what)
{
    if (!condition)
        throw new Exception($"Failed: {what}");
}
