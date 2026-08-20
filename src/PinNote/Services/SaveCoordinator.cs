using System.Windows.Threading;
using PinNote.Core.Models;
using PinNote.Core.Storage;

namespace PinNote.Services;

internal sealed class SaveCoordinator : IDisposable
{
    private readonly INoteStore _store;
    private readonly Func<NoteSnapshot> _capture;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _dirty;
    private bool _disposed;

    public SaveCoordinator(INoteStore store, Func<NoteSnapshot> capture)
    {
        _store = store;
        _capture = capture;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _timer.Tick += OnTimerTick;
    }

    public event Action<Exception>? SaveFailed;

    public void MarkDirty()
    {
        if (_disposed)
        {
            return;
        }

        _dirty = true;
        _timer.Stop();
        _timer.Start();
    }

    public async Task FlushAsync()
    {
        _timer.Stop();
        await _gate.WaitAsync();
        try
        {
            if (!_dirty || _disposed)
            {
                return;
            }

            var snapshot = _capture();
            _dirty = false;
            await _store.SaveAsync(snapshot);
        }
        catch (Exception exception)
        {
            _dirty = true;
            SaveFailed?.Invoke(exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        await FlushAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _gate.Dispose();
    }
}
