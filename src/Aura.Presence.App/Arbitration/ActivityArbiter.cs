using Aura.Presence.Sdk;

namespace Aura.Presence.App.Arbitration;

public sealed record ActivityClaim(string SourceId, int Priority, PresenceActivity Activity, DateTimeOffset UpdatedAt);

public sealed class ActivityArbiter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ActivityClaim> _claims = new(StringComparer.Ordinal);

    private string? _pinnedSourceId;
    private ActivityClaim? _current;

    public event Action<ActivityClaim?>? CurrentChanged;

    public ActivityClaim? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Submit(string sourceId, int priority, PresenceActivity activity)
    {
        UpdateAndNotify(() => _claims[sourceId] = new ActivityClaim(sourceId, priority, activity, DateTimeOffset.UtcNow));
    }

    public void Withdraw(string sourceId)
    {
        UpdateAndNotify(() => _claims.Remove(sourceId));
    }

    public void Pin(string? sourceId)
    {
        UpdateAndNotify(() => _pinnedSourceId = sourceId);
    }

    private void UpdateAndNotify(Action mutate)
    {
        ActivityClaim? next;
        bool changed;

        lock (_gate)
        {
            mutate();
            next = SelectWinner();
            changed = !Equals(next, _current);
            _current = next;
        }

        if (changed)
        {
            CurrentChanged?.Invoke(next);
        }
    }

    private ActivityClaim? SelectWinner()
    {
        if (_pinnedSourceId is not null && _claims.TryGetValue(_pinnedSourceId, out var pinned))
        {
            return pinned;
        }

        return _claims.Values
            .OrderByDescending(claim => claim.Priority)
            .ThenByDescending(claim => claim.UpdatedAt)
            .FirstOrDefault();
    }
}
