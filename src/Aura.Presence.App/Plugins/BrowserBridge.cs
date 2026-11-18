using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aura.Presence.Sdk;

namespace Aura.Presence.App.Plugins;

public sealed class BrowserBridge : IBrowser
{
    public const string Address = "http://localhost:47300/";
    public const string RequirementName = "browserExtension";

    private const string ExtensionOrigin = "chrome-extension://";
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan PageLifetime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan InterestLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly AppLog _log = AppLog.For("Browser");
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _interest = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Connection> _connections = [];
    private readonly HashSet<string> _reloadAsked = [];
    private string[] _sentHosts = [];
    private bool _listening;
    private DateTimeOffset _retryAt;
    private int _lastConnection;
    private long _lastFetch;

    public static BrowserBridge Shared { get; } = new();

    public static string ExtensionSource { get; } = Path.Combine(AppContext.BaseDirectory, "extension");

    public static string ExtensionFolder { get; } = Path.Combine(AppPaths.DataRoot, "browser-extension");

    public event Action? Changed;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return _connections.Count > 0;
        }
    }

    public IReadOnlyList<string> Browsers
    {
        get
        {
            lock (_gate)
                return [.. _connections.Select(connection => connection.Browser).Distinct()];
        }
    }

    public IReadOnlyList<BrowserPage> PagesOn(string host)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
            _interest[host] = now;

        EnsureListening();
        _ = SyncHostsAsync();

        lock (_gate)
            return
            [
                .. _connections.SelectMany(connection => connection.Pages.Values)
                    .Where(seen => seen.At > now - PageLifetime && string.Equals(seen.Page.Url.Host, host, StringComparison.OrdinalIgnoreCase))
                    .Select(seen => seen.Page)
            ];
    }

    public async Task<string?> FetchAsync(BrowserPage page, string path, CancellationToken cancellationToken)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("/\\", StringComparison.Ordinal))
            throw new ArgumentException("The path must start with a single / and stay on the page's own site.", nameof(path));

        if (ParseId(page.Id) is not { } target)
            return null;

        var (connectionId, tab) = target;
        Connection? connection;
        var id = Interlocked.Increment(ref _lastFetch);
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            connection = _connections.FirstOrDefault(candidate => candidate.Id == connectionId);
            connection?.Fetches.Add(id, reply);
        }

        if (connection is null)
            return null;

        try
        {
            await connection.SendAsync(new JsonObject { ["type"] = "fetch", ["id"] = id, ["tab"] = tab, ["path"] = path });
            return await reply.Task.WaitAsync(FetchTimeout, cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException or WebSocketException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            lock (_gate)
                connection.Fetches.Remove(id);
        }
    }

    public static string PrepareExtension()
    {
        if (!Directory.Exists(ExtensionSource))
            return ExtensionFolder;

        Directory.CreateDirectory(ExtensionFolder);

        foreach (var file in Directory.GetFiles(ExtensionSource))
        {
            var target = Path.Combine(ExtensionFolder, Path.GetFileName(file));

            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(file)))
                File.Copy(file, target, overwrite: true);
        }

        return ExtensionFolder;
    }

    public static string? ExtensionVersion(string folder)
    {
        var manifest = Path.Combine(folder, "manifest.json");

        return File.Exists(manifest) ? (string?)JsonNode.Parse(File.ReadAllText(manifest))?["version"] : null;
    }

    private static (int Connection, int Tab)? ParseId(string id)
    {
        var parts = id.Split(':');

        return parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var connection) &&
               int.TryParse(parts[1], CultureInfo.InvariantCulture, out var tab)
            ? (connection, tab)
            : null;
    }

    private void EnsureListening()
    {
        lock (_gate)
        {
            if (_listening || DateTimeOffset.UtcNow < _retryAt)
                return;

            _listening = true;
        }

        _ = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(Address);

        try
        {
            listener.Start();
            PrepareExtension();
        }
        catch (Exception exception) when (exception is HttpListenerException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Couldn't start listening on {Address} for the browser extension; trying again in 30 s.", exception);

            lock (_gate)
            {
                _listening = false;
                _retryAt = DateTimeOffset.UtcNow + RetryDelay;
            }

            return;
        }

        _log.Info($"Waiting for the browser extension on {Address}.");
        _ = Task.Run(PingAsync);

        while (true)
        {
            var context = await listener.GetContextAsync();
            _ = Task.Run(() => AcceptAsync(context));
        }
    }

    private async Task AcceptAsync(HttpListenerContext context)
    {
        var origin = context.Request.Headers["Origin"];

        if (!context.Request.IsWebSocketRequest || origin is null || !origin.StartsWith(ExtensionOrigin, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        WebSocket socket;

        try
        {
            socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        }
        catch (Exception exception) when (exception is WebSocketException or HttpListenerException)
        {
            return;
        }

        var connection = new Connection(Interlocked.Increment(ref _lastConnection), socket);
        string[] hosts;

        lock (_gate)
        {
            _connections.Add(connection);
            hosts = LiveHosts();
        }

        try
        {
            await connection.SendAsync(HostsMessage(hosts));
            await ReceiveAsync(connection);
        }
        catch (Exception exception) when (exception is WebSocketException or JsonException or InvalidOperationException or FormatException or ObjectDisposedException)
        {
        }
        finally
        {
            lock (_gate)
            {
                _connections.Remove(connection);

                foreach (var pending in connection.Fetches.Values)
                    pending.TrySetResult(null);
            }

            socket.Dispose();
            _log.Info($"{connection.Browser} disconnected.");
            Changed?.Invoke();
        }
    }

    private async Task ReceiveAsync(Connection connection)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (connection.Socket.State == WebSocketState.Open)
        {
            var result = await connection.Socket.ReceiveAsync(buffer, CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }

            message.Write(buffer, 0, result.Count);

            if (message.Length > MaxMessageBytes)
                throw new InvalidOperationException("The browser extension sent a message that is too large.");

            if (!result.EndOfMessage)
                continue;

            var json = JsonNode.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length))?.AsObject();
            message.SetLength(0);

            if (json is not null)
                await HandleAsync(connection, json);
        }
    }

    private async Task HandleAsync(Connection connection, JsonObject message)
    {
        switch ((string?)message["type"])
        {
            case "hello":
                var version = (string?)message["version"] ?? "";
                connection.Browser = (string?)message["browser"] is { Length: > 0 } browser ? browser : "A browser";
                _log.Info($"{connection.Browser} connected with extension {version}.");
                Changed?.Invoke();

                var current = ExtensionVersion(ExtensionFolder);
                bool reload;

                lock (_gate)
                    reload = current is not null && version != current && _reloadAsked.Add(version);

                if (reload)
                {
                    _log.Info($"Asking the extension to reload to {current}.");
                    await connection.SendAsync(new JsonObject { ["type"] = "reload" });
                }

                break;

            case "page" when ToPage(connection, message) is { } page:
                lock (_gate)
                    connection.Pages[(int)message["tab"]!] = new SeenPage(page, DateTimeOffset.UtcNow);

                break;

            case "gone":
                lock (_gate)
                    connection.Pages.Remove((int?)message["tab"] ?? -1);

                break;

            case "fetched":
                lock (_gate)
                    if (connection.Fetches.TryGetValue((long?)message["id"] ?? -1, out var pending))
                        pending.TrySetResult((string?)message["body"]);

                break;
        }
    }

    private static BrowserPage? ToPage(Connection connection, JsonObject message)
    {
        if ((int?)message["tab"] is not { } tab || !Uri.TryCreate((string?)message["url"], UriKind.Absolute, out var url))
            return null;

        var video = message["video"] is JsonObject media && (double?)media["position"] is { } position
            ? new BrowserVideo(TimeSpan.FromSeconds(position),
                (double?)media["duration"] is { } duration ? TimeSpan.FromSeconds(duration) : null,
                (bool?)media["paused"] ?? true)
            : null;

        return new BrowserPage($"{connection.Id}:{tab}", url, (string?)message["title"] ?? "", (bool?)message["active"] ?? false, video);
    }

    private async Task PingAsync()
    {
        using var timer = new PeriodicTimer(PingInterval);

        while (await timer.WaitForNextTickAsync())
        {
            Connection[] connections;
            var stale = DateTimeOffset.UtcNow - InterestLifetime;

            lock (_gate)
            {
                connections = [.. _connections];

                foreach (var connection in connections)
                foreach (var tab in connection.Pages.Where(seen => seen.Value.At < stale).Select(seen => seen.Key).ToList())
                    connection.Pages.Remove(tab);
            }

            await SyncHostsAsync();

            foreach (var connection in connections)
                await TrySendAsync(connection, new JsonObject { ["type"] = "ping" });
        }
    }

    private async Task SyncHostsAsync()
    {
        Connection[] connections;
        string[] hosts;

        lock (_gate)
        {
            hosts = LiveHosts();

            if (hosts.SequenceEqual(_sentHosts, StringComparer.OrdinalIgnoreCase))
                return;

            _sentHosts = hosts;
            connections = [.. _connections];
        }

        _log.Info(hosts.Length == 0 ? "No plugin reads the browser now." : $"Plugins read {string.Join(", ", hosts)} in the browser.");

        foreach (var connection in connections)
            await TrySendAsync(connection, HostsMessage(hosts));
    }

    private string[] LiveHosts()
    {
        var since = DateTimeOffset.UtcNow - InterestLifetime;

        return [.. _interest.Where(asked => asked.Value > since).Select(asked => asked.Key.ToLowerInvariant()).Order(StringComparer.Ordinal)];
    }

    private static JsonObject HostsMessage(string[] hosts) =>
        new() { ["type"] = "hosts", ["hosts"] = new JsonArray(hosts.Select(host => (JsonNode?)JsonValue.Create(host)).ToArray()) };

    private static async Task TrySendAsync(Connection connection, JsonObject message)
    {
        try
        {
            await connection.SendAsync(message);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private sealed record SeenPage(BrowserPage Page, DateTimeOffset At);

    private sealed class Connection(int id, WebSocket socket)
    {
        private readonly SemaphoreSlim _sending = new(1, 1);

        public int Id => id;

        public WebSocket Socket => socket;

        public string Browser { get; set; } = "A browser";

        public Dictionary<int, SeenPage> Pages { get; } = [];

        public Dictionary<long, TaskCompletionSource<string?>> Fetches { get; } = [];

        public async Task SendAsync(JsonObject message)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await _sending.WaitAsync();

            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                _sending.Release();
            }
        }
    }
}
