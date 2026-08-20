using PinNote.Core.Models;
using PinNote.Core.Reminders;

namespace PinNote.Services;

internal sealed class ReminderScheduler : IDisposable
{
    private static readonly TimeSpan MaximumTimerPeriod = TimeSpan.FromDays(24);
    private readonly Action _onWake;
    private readonly System.Threading.Timer _timer;
    private bool _disposed;

    public ReminderScheduler(Action onWake)
    {
        _onWake = onWake;
        _timer = new System.Threading.Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Refresh(IReadOnlyCollection<NoteDocument> notes)
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (ReminderPlanner.GetDue(notes, now).Count > 0)
        {
            _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            return;
        }

        var next = ReminderPlanner.GetNextDue(notes, now);
        var delay = next is null ? Timeout.InfiniteTimeSpan : next.Value - now;
        if (delay != Timeout.InfiniteTimeSpan)
        {
            delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
            delay = delay > MaximumTimerPeriod ? MaximumTimerPeriod : delay;
        }

        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(object? state)
    {
        if (_disposed)
        {
            return;
        }

        _onWake();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
