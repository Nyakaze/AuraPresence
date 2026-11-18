using Aura.Presence.App.Arbitration;
using Aura.Presence.Sdk;
using Crovus.Models;
using Crovus.RichPresence;

namespace Aura.Presence.App.Output;

public sealed class DiscordPresenceOutput : IPresenceOutput
{
    private static readonly Snowflake[] Apps = [new(1572723734564053173), new(1516903509058916543)];

    private readonly AppLog _log = AppLog.For("Discord");
    private readonly CrovusLogBridge _crovusLog = new("Crovus");
    private readonly Lock _gate = new();
    private readonly RichPresenceClient?[] _clients = new RichPresenceClient?[Apps.Length];
    private readonly ActivityClaim?[] _sent = new ActivityClaim?[Apps.Length];
    private readonly OutputStatus?[] _rejections = new OutputStatus?[Apps.Length];

    private OutputStatus _status = OutputStatus.Idle;

    public event Action<OutputStatus>? StatusChanged;

    public static int SlotOf(ActivityKind kind) => kind is ActivityKind.Watching or ActivityKind.Listening ? 1 : 0;

    public OutputStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public void Show(IReadOnlyList<ActivityClaim> shown)
    {
        for (var slot = 0; slot < Apps.Length; slot++)
            Show(slot, shown.FirstOrDefault(claim => SlotOf(claim.Activity.Kind) == slot));

        RefreshStatus();
    }

    public async ValueTask DisposeAsync()
    {
        RichPresenceClient?[] clients;

        lock (_gate)
        {
            clients = [.. _clients];
            Array.Clear(_clients);
        }

        foreach (var client in clients)
        {
            if (client is not null)
                await client.DisposeAsync();
        }
    }

    private void Show(int slot, ActivityClaim? claim)
    {
        lock (_gate)
        {
            if (Equals(_sent[slot], claim))
                return;

            _sent[slot] = claim;
        }

        if (claim is null)
        {
            lock (_gate)
                _clients[slot]?.ClearActivity();

            _log.Info($"Cleared {Apps[slot]}.");

            return;
        }

        var activity = ActivityMapper.ToRichPresence(claim.Activity);
        var problems = activity.Validate();

        if (problems.Count > 0)
        {
            _log.Error($"Activity from '{claim.SourceId}' cannot be shown: {string.Join(" ", problems)}");

            return;
        }

        lock (_gate)
        {
            _rejections[slot] = null;
            Client(slot).SetActivity(activity);
        }

        _log.Info($"Showing '{claim.SourceId}' on {Apps[slot]}: {activity.Type} {activity.Name} | {activity.Details} | {activity.State}");
    }

    private RichPresenceClient Client(int slot)
    {
        if (_clients[slot] is { } existing)
            return existing;

        var client = new RichPresenceClient(RichPresenceClientOptions.For(Apps[slot]), _crovusLog);
        client.StateChanged += _ => RefreshStatus();
        client.ActivityApplied += _ => OnApplied(slot);
        client.ActivityRejected += failure => OnRejected(slot, failure);

        _clients[slot] = client;
        _ = client.StartAsync();

        return client;
    }

    private void OnApplied(int slot)
    {
        lock (_gate)
            _rejections[slot] = null;

        RefreshStatus();
    }

    private void OnRejected(int slot, RichPresenceFailure failure)
    {
        _log.Warn($"Discord rejected the activity on {Apps[slot]}: {failure.Message}");

        lock (_gate)
            _rejections[slot] = new OutputStatus($"Discord rejected the activity: {failure.Message}", false);

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        OutputStatus next;

        lock (_gate)
            next = _clients
                       .Select((client, slot) => _rejections[slot] ?? (client is null ? null : Describe(client)))
                       .OfType<OutputStatus>()
                       .OrderBy(status => status.Healthy)
                       .FirstOrDefault()
                   ?? OutputStatus.Idle;

        Publish(next);
    }

    private void Publish(OutputStatus next)
    {
        lock (_gate)
        {
            if (_status == next)
                return;

            _status = next;
        }

        StatusChanged?.Invoke(next);
    }

    private static OutputStatus Describe(RichPresenceClient client)
    {
        if (client.State is RichPresenceState.Connected && client.User is { } user)
            return new OutputStatus($"Discord: connected as {user.DisplayName}", true);

        return client.LastFailure switch
        {
            null => new OutputStatus("Discord: connecting…", true),
            { Kind: RichPresenceFailureKind.DiscordNotRunning } => new OutputStatus("Discord: not running", false),
            { Kind: RichPresenceFailureKind.AccessDenied } => new OutputStatus(
                "Discord: access denied — Discord runs as administrator, Aura does not", false),
            { Kind: RichPresenceFailureKind.HandshakeRejected } failure => new OutputStatus(
                $"Discord: Aura's app id was rejected ({failure.Message})", false),
            { } failure => new OutputStatus($"Discord: {failure.Message}", false)
        };
    }
}
