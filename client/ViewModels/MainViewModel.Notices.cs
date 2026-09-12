namespace DroidLens.Client.ViewModels;

/// <summary>Severity of a user-visible notice shown in the notice banner.</summary>
public enum NoticeKind
{
    Info,
    Warning,
    Error
}

public partial class MainViewModel
{
    // ── Notice banner (top-right) ─────────────────────────────────────────────
    // Single-slot "last error" surface for the release build (no debugger
    // attached): file logging was deliberately skipped, so this banner is the
    // only feedback a user gets. Only actionable failures go here (connect /
    // lost / unstable / stream / VCam / decoder / screenshot / USB) — transient
    // background retries (mDNS requery, single dropped frame, RTT jitter) stay
    // silent. Dedup + auto-hide keep it from spamming.

    private static readonly TimeSpan NoticeVisibleDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan NoticeDedupWindow = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _noticeCts;
    private string _lastNoticeText = "";
    private DateTime _lastNoticeAt = DateTime.MinValue;

    private string _noticeText = "";
    public string NoticeText
    {
        get => _noticeText;
        private set { _noticeText = value; OnPropertyChanged(); }
    }

    private NoticeKind _noticeKind = NoticeKind.Error;
    public NoticeKind NoticeKind
    {
        get => _noticeKind;
        private set { _noticeKind = value; OnPropertyChanged(); }
    }

    private Material.Icons.MaterialIconKind _noticeIconKind =
        Material.Icons.MaterialIconKind.AlertCircleOutline;
    public Material.Icons.MaterialIconKind NoticeIconKind
    {
        get => _noticeIconKind;
        private set { _noticeIconKind = value; OnPropertyChanged(); }
    }

    private bool _isNoticeVisible;
    public bool IsNoticeVisible
    {
        get => _isNoticeVisible;
        private set { _isNoticeVisible = value; OnPropertyChanged(); }
    }

    /// <summary>Shows the banner. Same text within the dedup window only
    /// extends visibility instead of replaying. Safe to call from any thread.
    /// Icon defaults per severity when not given explicitly.</summary>
    public void ShowNotice(
        string message,
        NoticeKind kind = NoticeKind.Error,
        Material.Icons.MaterialIconKind? icon = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var iconKind = icon ?? kind switch
        {
            NoticeKind.Warning => Material.Icons.MaterialIconKind.AlertOutline,
            NoticeKind.Info    => Material.Icons.MaterialIconKind.InformationOutline,
            _                => Material.Icons.MaterialIconKind.AlertCircleOutline
        };
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var now = DateTime.UtcNow;
            if (message == _lastNoticeText && now - _lastNoticeAt < NoticeDedupWindow)
            {
                RestartNoticeTimer();
                return;
            }

            _lastNoticeText = message;
            _lastNoticeAt = now;
            NoticeText = message;
            NoticeKind = kind;
            NoticeIconKind = iconKind;
            IsNoticeVisible = true;
            RestartNoticeTimer();
        });
    }

    public void ClearNotice()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _noticeCts?.Cancel();
            _noticeCts = null;
            IsNoticeVisible = false;
        });
    }

    private void RestartNoticeTimer()
    {
        _noticeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _noticeCts = cts;
        _ = Task.Delay(NoticeVisibleDuration, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => IsNoticeVisible = false);
        }, TaskScheduler.Default);
    }

    private void DisposeNotice()
    {
        _noticeCts?.Cancel();
        _noticeCts?.Dispose();
        _noticeCts = null;
    }
}
