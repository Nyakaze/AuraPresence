using Aura.Presence.App.Arbitration;
using Aura.Presence.Sdk;
using Crovus.Models;
using Crovus.RichPresence;

namespace Aura.Presence.App.Output;

public sealed class DiscordPresenceOutput : IPresenceOutput
{
    private static readonly Snowflake AuraApplication = new(1516903509058916543);

    private readonly AppLog _log = AppLog.For("Discord");
    private readonly CrovusLogBridge _crovusLog = new("Crovus");
    private readonly Lock _gate = new();

    private RichPresenceClient? _client;
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
                _client?.ClearActivity();

            _log.Info("Presence cleared.");

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
            Client().SetActivity(activity);
        }

        _log.Info($"Showing '{claim.SourceId}': {activity.Type} {activity.Name} | {activity.Details} | {activity.State}");
        RefreshStatus();
    }

    public async ValueTask DisposeAsync()
    {
        RichPresenceClient? client;

        lock (_gate)
        {
            client = _client;
            _client = null;
        }

        if (client is not null)
            await client.DisposeAsync();
    }

    private RichPresenceClient Client()
    {
        if (_client is not null)
            return _client;

        var client = new RichPresenceClient(RichPresenceClientOptions.For(AuraApplication), _crovusLog);
        client.StateChanged += _ => RefreshStatus();
        client.ActivityApplied += _ => OnApplied();
        client.ActivityRejected += OnRejected;

        _client = client;
        _ = client.StartAsync();

        return client;
    }

    private void OnApplied()
    {
        lock (_gate)
            _rejection = null;

        RefreshStatus();
    }

    private void OnRejected(RichPresenceFailure failure)
    {
        _log.Warn($"Discord rejected the activity: {failure.Message}");

        lock (_gate)
            _rejection = new OutputStatus($"Discord rejected the activity: {failure.Message}", false);

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        OutputStatus next;

        lock (_gate)
            next = _rejection ?? (_client is { } client ? Describe(client) : OutputStatus.Idle);

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
