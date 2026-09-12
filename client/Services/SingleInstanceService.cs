using System.IO;
using System.Threading;

namespace DroidLens.Client.Services;

/// <summary>
/// Guarantees only one DroidLens process runs at a time. The second launch
/// signals the already-running instance (which restores its window) and exits
/// immediately instead of opening a duplicate window.
/// Mechanism: named mutex for ownership + named auto-reset event for the
/// "show yourself" signal. Both names use the Local\ prefix (per user session,
/// no admin rights needed — Global\ would require elevated privileges).
/// </summary>
public static class SingleInstanceService
{
    private const string MutexName = @"Local\DroidLens.SingleInstance.v1";
    private const string ShowEventName = @"Local\DroidLens.ShowFirstInstance.v1";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static CancellationTokenSource? _listenerCts;

    /// <summary>
    /// Tries to become the first (owning) instance. Returns false when another
    /// DroidLens process already holds the mutex — the caller should then call
    /// <see cref="SignalFirstInstance"/> and exit without creating any window.
    /// On success the mutex is held for the whole process lifetime (never
    /// released early), so keep the process alive — do not dispose.
    /// </summary>
    public static bool TryClaimFirstInstance()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (createdNew)
            {
                // We own it — create the signal event the second instance will Set().
                _showEvent = new EventWaitHandle(
                    initialState: false, EventResetMode.AutoReset, ShowEventName, out _);
                return true;
            }

            // Another instance owns the mutex — we must not hold our handle.
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch (AbandonedMutexException)
        {
            // Previous owner crashed without releasing — OS hands ownership to us.
            // Recreate the show event (the old one may still exist, open-or-create).
            try
            {
                _showEvent = new EventWaitHandle(
                    initialState: false, EventResetMode.AutoReset, ShowEventName, out _);
            }
            catch
            {
                // best-effort — single-instance still works via the mutex itself
            }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Mutex exists but we can't open it — another instance is running.
            return false;
        }
    }

    /// <summary>
    /// Called by the SECOND instance: wakes the first instance's window and returns.
    /// Never throws — worst case the signal is lost and the second instance just exits.
    /// </summary>
    public static void SignalFirstInstance()
    {
        // Small retry loop: the first instance creates the event right after
        // claiming the mutex, so a fresh-boot race is possible in theory.
        for (int i = 0; i < 5; i++)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                AppLog.E("SingleInstance", ex, () => "Failed to signal first instance");
                return;
            }
        }
    }

    /// <summary>
    /// Called once by the FIRST instance (after MainWindow exists): background loop
    /// that invokes <paramref name="onSecondInstance"/> every time another launch
    /// signals us. Runs on a background thread — the callback is responsible for
    /// marshalling to the UI thread itself.
    /// </summary>
    public static void StartListener(Action onSecondInstance)
    {
        if (_showEvent is null)
            return;

        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;
        var ev = _showEvent;

        var thread = new Thread(() =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // WaitOne returns when signalled OR when the token is cancelled
                    // (via the timeout poll below) — blocking forever would leak
                    // the thread past shutdown, so poll in 500ms slices.
                    if (ev.WaitOne(TimeSpan.FromMilliseconds(500)))
                    {
                        try
                        {
                            onSecondInstance();
                        }
                        catch (Exception ex)
                        {
                            AppLog.E("SingleInstance", ex, () => "Second-instance handler failed");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.E("SingleInstance", ex, () => "Listener loop crashed");
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceListener",
        };
        thread.Start();
    }

    public static void StopListener()
    {
        try
        {
            _listenerCts?.Cancel();
        }
        catch
        {
            // shutdown path — never throw
        }
    }
}
