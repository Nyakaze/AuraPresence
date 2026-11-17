using Aura.Presence.Sdk;

namespace Aura.Presence.App.Arbitration;

public sealed record ActivityClaim(string SourceId, int Priority, PresenceActivity Activity, DateTimeOffset UpdatedAt);

public sealed class ActivityArbiter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ActivityClaim> _claims = new(StringComparer.Ordinal);

    private string[] _order = [];
    private string? _pinnedSourceId;
    private ActivityClaim? _current;

    public event Action<ActivityClaim?>? CurrentChanged;

    public ActivityClaim? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public string? PinnedSourceId
    {
        get
        {
            lock (_gate)
                return _pinnedSourceId;
        }
    }

    public IReadOnlyList<ActivityClaim> Ranked()
    {
        lock (_gate)
            return Rank();
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

    public void SetOrder(IEnumerable<string> sourceIds)
    {
        UpdateAndNotify(() => _order = [.. sourceIds]);
    }

    private void UpdateAndNotify(Action mutate)
    {
        ActivityClaim? next;
        bool changed;

        lock (_gate)
        {
            mutate();
            next = Rank().FirstOrDefault();
            changed = !Equals(next, _current);
            _current = next;
        }

        if (changed)
            CurrentChanged?.Invoke(next);
    }

    private List<ActivityClaim> Rank() =>
        [.. _claims.Values
            .OrderBy(claim => claim.SourceId == _pinnedSourceId ? 0 : 1)
            .ThenBy(claim => Position(claim.SourceId))
            .ThenByDescending(claim => claim.Priority)
            .ThenByDescending(claim => claim.UpdatedAt)];

    private int Position(string sourceId)
    {
        var index = Array.IndexOf(_order, sourceId);

        return index < 0 ? int.MaxValue : index;
    }
}
