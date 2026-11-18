using Aura.Presence.App.Arbitration;

namespace Aura.Presence.App.Output;

public sealed record OutputStatus(string Summary, bool Healthy)
{
    public static OutputStatus Idle { get; } = new("Discord: waiting for an activity", true);
}

public interface IPresenceOutput : IAsyncDisposable
{
    event Action<OutputStatus>? StatusChanged;

    OutputStatus Status { get; }

    void Show(IReadOnlyList<ActivityClaim> shown);
}
