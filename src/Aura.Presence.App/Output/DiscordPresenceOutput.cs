using Aura.Presence.App.Arbitration;
using Aura.Presence.Sdk;
using Crovus.Models;
using Crovus.RichPresence;

namespace Aura.Presence.App.Output;

public sealed class DiscordPresenceOutput : IPresenceOutput
{
    private readonly AppLog _log = AppLog.For("Discord");
    private readonly CrovusLogBridge _crovusLog = new("Crovus");
    private readonly Lock _gate = new();
    private readonly Dictionary<Snowflake, RichPresenceClient> _clients = [];

    private Snowflake? _activeApplication;
    private OutputStatus _status = OutputStatus.Idle;
    private OutputStatus? _rejection;

    public event Action<OutputStatus>? StatusChanged;

    public OutputStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public void Show(ActivityClaim? claim)
    {
        if (claim is null)
        {
            lock (_gate)
            {
                foreach (var client in _clients.Values)
                    client.ClearActivity();
            }

            _log.Info("Presence cleared.");

            return;
        }

        if (!ActivityMapper.TryParseApplicationId(claim.Activity.ApplicationId, out var applicationId))
        {
            _log.Error($"Plugin '{claim.SourceId}' reported an invalid Discord application id '{claim.Activity.ApplicationId}'.");
            Publish(new OutputStatus($"Discord: plugin '{claim.SourceId}' has an invalid app id", false));

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
            _rejection = null;
            _activeApplication = applicationId;

            foreach (var (id, other) in _clients)
            {
                if (id != applicationId)
                    other.ClearActivity();
            }

            ClientFor(applicationId).SetActivity(activity);
        }

        _log.Info($"Showing '{claim.SourceId}': {activity.Type} | {activity.Details} | {activity.State}");
        RefreshStatus();
    }

    public async ValueTask DisposeAsync()
    {
        RichPresenceClient[] clients;

        lock (_gate)
        {
            clients = [.. _clients.Values];
            _clients.Clear();
        }

        foreach (var client in clients)
            await client.DisposeAsync();
    }

    private RichPresenceClient ClientFor(Snowflake applicationId)
    {
        if (_clients.TryGetValue(applicationId, out var existing))
            return existing;

        var client = new RichPresenceClient(RichPresenceClientOptions.For(applicationId), _crovusLog);
        client.StateChanged += _ => RefreshStatus();
        client.ActivityApplied += _ => OnApplied();
        client.ActivityRejected += failure => OnRejected(applicationId, failure);

        _clients[applicationId] = client;
        _ = client.StartAsync();

        return client;
    }

    private void OnApplied()
    {
        lock (_gate)
            _rejection = null;

        RefreshStatus();
    }

    private void OnRejected(Snowflake applicationId, RichPresenceFailure failure)
    {
        _log.Warn($"Discord rejected the activity for app {applicationId}: {failure.Message}");

        lock (_gate)
            _rejection = new OutputStatus($"Discord rejected the activity: {failure.Message}", false);

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        OutputStatus next;

        lock (_gate)
        {
            if (_rejection is not null)
                next = _rejection;
            else if (_activeApplication is { } active && _clients.TryGetValue(active, out var client))
                next = Describe(client);
            else
                next = OutputStatus.Idle;
        }

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
                $"Discord: app id rejected ({failure.Message})", false),
            { } failure => new OutputStatus($"Discord: {failure.Message}", false)
        };
    }
}
