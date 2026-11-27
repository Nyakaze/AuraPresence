using Aura.Presence.Sdk;

namespace Aura.Presence.App.Arbitration;

public sealed record ActivityClaim(string SourceId, int Priority, PresenceActivity Activity, DateTimeOffset UpdatedAt);

public sealed class ActivityArbiter(Func<ActivityKind, int>? slotOf = null)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ActivityClaim> _claims = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _foreground = new(StringComparer.Ordinal);
    private readonly Dictionary<int, long> _otherGames = [];
    private readonly Func<ActivityKind, int> _slotOf = slotOf ?? (_ => 0);

    private string[] _order = [];
    private string? _pinnedSourceId;
    private bool _oneAtATime;
    private long _foregroundCount;
    private ActivityClaim[] _shown = [];

    public event Action<IReadOnlyList<ActivityClaim>>? ShownChanged;

    public IReadOnlyList<ActivityClaim> Shown
    {
        get
        {
            lock (_gate)
                return _shown;
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

    public bool OneAtATime
    {
        get
        {
            lock (_gate)
                return _oneAtATime;
        }
    }

    public int SlotOf(ActivityClaim claim) => _oneAtATime ? 0 : _slotOf(claim.Activity.Kind);

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

    public void SetOneAtATime(bool on)
    {
        UpdateAndNotify(() => _oneAtATime = on);
    }

    public void Foreground(string sourceId)
    {
        UpdateAndNotify(() => _foreground[sourceId] = ++_foregroundCount);
    }

    public void OtherGameInFront(int processId)
    {
        UpdateAndNotify(() => _otherGames[processId] = ++_foregroundCount);
    }

    public void OtherGameClosed(int processId)
    {
        UpdateAndNotify(() => _otherGames.Remove(processId));
    }

    private void UpdateAndNotify(Action mutate)
    {
        ActivityClaim[] next;
        bool changed;

        lock (_gate)
        {
            mutate();
            next = [.. Rank().DistinctBy(SlotOf)];
            changed = !next.SequenceEqual(_shown);
            _shown = next;
        }

        if (changed)
            ShownChanged?.Invoke(next);
    }

    private List<ActivityClaim> Rank()
    {
        var otherGameAt = _otherGames.Values.DefaultIfEmpty().Max();
        var claims = _claims.Values.Where(claim => !IsGame(claim) || claim.SourceId == _pinnedSourceId ||
                                                   _foreground.GetValueOrDefault(claim.SourceId) >= otherGameAt).ToList();
        var gamesAt = claims.Where(IsGame).Select(claim => Position(claim.SourceId)).DefaultIfEmpty().Min();

        return [.. claims
            .OrderBy(claim => claim.SourceId == _pinnedSourceId ? 0 : 1)
            .ThenBy(claim => IsGame(claim) ? gamesAt : Position(claim.SourceId))
            .ThenByDescending(claim => _foreground.GetValueOrDefault(claim.SourceId))
            .ThenBy(claim => Position(claim.SourceId))
            .ThenByDescending(claim => claim.Priority)
            .ThenByDescending(claim => claim.UpdatedAt)];
    }

    private bool IsGame(ActivityClaim claim) => _slotOf(claim.Activity.Kind) == 0;

    private int Position(string sourceId)
    {
        var index = Array.IndexOf(_order, sourceId);

        return index < 0 ? int.MaxValue : index;
    }
}
