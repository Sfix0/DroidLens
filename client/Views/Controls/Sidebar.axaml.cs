using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DroidLens.Client.Models;
using DroidLens.Client.Services;
using DroidLens.Client.ViewModels;
using Material.Icons.Avalonia;
using JetBrains.Annotations;

namespace DroidLens.Client.Views.Controls;

// ── Left sidebar: Connection card (Auto-discovery / Manual IP tabs), Session
//    card (Start/Stop, Quality, Codec, Resolution, Virtual camera), and the
//    always-visible Quick Commands card (Flip/Rotate/BlackScreen/Flashlight).
//
//    Everything the host window (MainWindow) needs from this control goes
//    through the public methods/properties below — MainWindow never reaches
//    into this control's named elements directly. This mirrors the same
//    split MainWindow.Sidebar.cs used to have, just with an explicit
//    boundary now that the markup lives in its own UserControl. ──
public partial class Sidebar : UserControl
{
    private MainViewModel? _vm;

    // Guard flags so that syncing the ComboBoxes from VM state doesn't
    // immediately re-fire SelectionChanged back into the VM.
    private bool _suppressQualityEvent;
    private bool _suppressCodecEvent;
    private bool _suppressResolutionEvent;

    // Tracks which Connection tab is showing (Auto-discovery vs Manual IP).
    // Was shared with MainWindow.axaml.cs before (for the AutoDiscoveryPulsed
    // gate) — now fully internal, since PulseAutoDiscoveryIcon() below is the
    // only thing that needs it, and that's called from here.
    private bool _isManualTabActive;

    public Sidebar()
    {
        InitializeComponent();
    }

    // Called once by the host right after DataContext is assigned. Populates
    // the combo boxes and sets the initial Connection tab — replaces what
    // used to run directly in MainWindow's constructor.
    public void Initialize(MainViewModel vm)
    {
        _vm = vm;

        // Re-fire the pulse from here now that this control owns the icon —
        // host subscribes to VM events it still needs (JpegFrame etc.), but
        // AutoDiscoveryPulsed is purely a Sidebar concern now.
        _vm.AutoDiscoveryPulsed += () => PulseAutoDiscoveryIcon();

        PopulateCombos();
        SyncQualityCombo();
        SyncCodecCombo();
        SyncResolutionCombo();
        SetConnectionTab(manual: false);
    }

    // ── Public API for the host ─────────────────────────────────────────────

    public void SyncQualityCombo()
    {
        if (_vm is null) return;
        _suppressQualityEvent = true;
        QualityCombo.SelectedItem = _vm.CurrentQuality;
        _suppressQualityEvent = false;
    }

    public void SyncCodecCombo()
    {
        if (_vm is null) return;
        _suppressCodecEvent = true;
        CodecCombo.SelectedItem = _vm.CurrentCodec;
        _suppressCodecEvent = false;
    }

    public void SyncResolutionCombo()
    {
        if (_vm is null) return;
        _suppressResolutionEvent = true;
        ResolutionCombo.SelectedItem = _vm.CurrentResolution;
        _suppressResolutionEvent = false;
    }

    public void PopulateCombos()
    {
        if (_vm is null) return;

        _suppressQualityEvent = true;
        _suppressCodecEvent = true;
        _suppressResolutionEvent = true;

        QualityCombo.Items.Clear();
        QualityCombo.Items.Add(new DropdownItem(_vm.Loc["quality.low"], StreamQuality.LOW));
        QualityCombo.Items.Add(new DropdownItem(_vm.Loc["quality.medium"], StreamQuality.MEDIUM));
        QualityCombo.Items.Add(new DropdownItem(_vm.Loc["quality.high"], StreamQuality.HIGH));

        CodecCombo.Items.Clear();
        CodecCombo.Items.Add(new DropdownItem("MJPEG", VideoCodec.MJPEG));
        CodecCombo.Items.Add(new DropdownItem("H264", VideoCodec.H264));
        CodecCombo.Items.Add(new DropdownItem("H265", VideoCodec.H265));

        ResolutionCombo.Items.Clear();
        ResolutionCombo.Items.Add(new DropdownItem("SD · ≈4:3", StreamResolution.SD));
        ResolutionCombo.Items.Add(new DropdownItem("HD · 16:9", StreamResolution.HD));
        ResolutionCombo.Items.Add(new DropdownItem("Full HD · 16:9", StreamResolution.FHD));

        _suppressQualityEvent = false;
        _suppressCodecEvent = false;
        _suppressResolutionEvent = false;

        SyncQualityCombo();
        SyncCodecCombo();
        SyncResolutionCombo();
    }

    // Crossfades the Connection card between its Idle and Connected panels.
    // Same timing/behavior as before, just moved onto this control.
    private const int ConnectionFadeOutMs = 150;
    private CancellationTokenSource? _connectionFadeCts;

    public async void SetConnectionState(bool isConnected)
    {
        _connectionFadeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _connectionFadeCts = cts;

        var hidingPanel  = isConnected ? IdleStatePanel      : ConnectedStatePanel;
        var showingPanel = isConnected ? ConnectedStatePanel : IdleStatePanel;

        hidingPanel.Classes.Add("hidden");
        try
        {
            await Task.Delay(ConnectionFadeOutMs, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return; // a newer state change took over mid-transition
        }

        hidingPanel.IsVisible  = false;
        showingPanel.IsVisible = true;

        Dispatcher.UIThread.Post(() => showingPanel.Classes.Remove("hidden"));
    }

    // Whether the manual-IP host field currently holds focus and the given
    // visual isn't inside it — used by MainWindow's click-away handler
    // (RootPanel_PointerPressed) without it needing to touch HostField
    // directly.
    public bool ShouldClearHostFieldFocus(Avalonia.Visual hitTarget)
        => HostField.IsInputFocused
           && !HostField.IsVisualAncestorOf(hitTarget)
           && hitTarget != HostField;

    public void ClearHostFieldFocus() => HostField.ClearFocus();

    // Plays the staggered cascade entrance (fade + translateY) on the three
    // cards — called once from MainWindow_Opened after the window itself has
    // been measured/rendered at least once. Returns true once all three cards
    // report a real, non-zero size (mirrors the old OnFirstLayout check).
    // SessionCard now also contains the virtual-camera row (merged from the
    // former standalone VCamCard) — one less card to wait on/animate.
    public bool TryPlayEntranceCascade()
    {
        if (ConnectionCard.Bounds.Height <= 0 || SessionCard.Bounds.Height <= 0 ||
            QuickCommandsCard.Bounds.Height <= 0)
            return false; // not actually measured/arranged yet — caller keeps waiting

        ConnectionCard.Classes.Add("shown");
        SessionCard.Classes.Add("shown");
        QuickCommandsCard.Classes.Add("shown");
        return true;
    }

    // One-shot pulse on each actual background mDNS requery tick, gated on
    // the Auto tab being visible — pulsing is pointless motion while the
    // user's looking at Manual IP. Public so Initialize() can wire it to
    // MainViewModel.AutoDiscoveryPulsed without the host needing to know
    // about AutoWifiIcon at all.
    public void PulseAutoDiscoveryIcon()
    {
        if (_isManualTabActive) return;
        AutoWifiIcon.Classes.Remove("auto-discover-pulse");
        AutoWifiIcon.Classes.Add("auto-discover-pulse");
    }

    // ── Combo boxes ──────────────────────────────────────────────────────────
    [UsedImplicitly]
    private async void QualityCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressQualityEvent || _vm is null) return;
        var selected = QualityCombo.SelectedItem;
        var q = selected switch
        {
            StreamQuality sq => sq,
            DropdownItem di when di.Value is StreamQuality sq => sq,
            _ => (StreamQuality?)null
        };
        if (q.HasValue) await _vm.SetQualityAsync(q.Value);
    }

    [UsedImplicitly]
    private async void CodecCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressCodecEvent || _vm is null) return;
        var selected = CodecCombo.SelectedItem;
        var c = selected switch
        {
            VideoCodec vc => vc,
            DropdownItem di when di.Value is VideoCodec vc => vc,
            _ => (VideoCodec?)null
        };
        if (c.HasValue) await _vm.SetCodecAsync(c.Value);
    }

    [UsedImplicitly]
    private async void ResolutionCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressResolutionEvent || _vm is null) return;
        var selected = ResolutionCombo.SelectedItem;
        var r = selected switch
        {
            StreamResolution sr => sr,
            DropdownItem di when di.Value is StreamResolution sr => sr,
            _ => (StreamResolution?)null
        };
        if (r.HasValue) await _vm.SetResolutionAsync(r.Value);
    }

    // ── Connection ──────────────────────────────────────────────────────────
    [UsedImplicitly]
    private async void ConnectBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ConnectAsync();
    }

    [UsedImplicitly]
    private async void DisconnectBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DisconnectAsync();
    }

    [UsedImplicitly]
    private async void AutoConnectToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is Button btn)
            await PlayIconWobbleWithSwapAsync(btn, () => _vm.ToggleAutoConnectForCurrentDevice());
        else
            _vm.ToggleAutoConnectForCurrentDevice();
    }

    private static async Task PlayIconWobbleWithSwapAsync(Button button, Action swap)
    {
        var icons = button.GetVisualDescendants().OfType<MaterialIcon>().ToList();
        if (icons.Count == 0) return;

        foreach (var icon in icons) icon.Classes.Add("spin");
        await Task.Delay(100);
        swap();
        await Task.Delay(100);
        foreach (var icon in icons) icon.Classes.Remove("spin");
    }

    private static readonly TimeSpan ScanSpinAnimationDuration = TimeSpan.FromMilliseconds(900);

    [UsedImplicitly]
    private async void ScanBtn_Click(object? sender, RoutedEventArgs e)
    {
        _vm?.ScanMdns();

        ScanIcon.Classes.Add("scan-spin");
        await Task.Delay(ScanSpinAnimationDuration);
        ScanIcon.Classes.Remove("scan-spin");
    }

    // ── Connection idle-state tabs (Auto-discovery / Manual IP) ────────────────
    [UsedImplicitly]
    private void AutoTab_Click(object? sender, RoutedEventArgs e)  => SetConnectionTab(manual: false);
    [UsedImplicitly]
    private void ManualTab_Click(object? sender, RoutedEventArgs e) => SetConnectionTab(manual: true);

    private const double TabExpandedWidth  = 128;
    private const double TabCollapsedWidth = 34;

    private void SetConnectionTab(bool manual)
    {
        _isManualTabActive = manual;

        AutoTabButton.Classes.Set("active", !manual);
        ManualTabButton.Classes.Set("active", manual);

        AutoTabButton.Width   = manual ? TabCollapsedWidth : TabExpandedWidth;
        ManualTabButton.Width = manual ? TabExpandedWidth  : TabCollapsedWidth;

        AutoDiscoveryPanel.IsVisible = !manual;
        ManualEntryPanel.IsVisible   = manual;
    }

    [UsedImplicitly]
    private async void DeviceItem_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && sender is Button { Tag: DiscoveredDevice device })
        {
            _vm.Host = device.IpAddress;
            await _vm.ConnectAsync();
        }
    }

    // ── Session ─────────────────────────────────────────────────────────────
    // Start/Stop needs to coordinate cold-start-blur/placeholder state that
    // lives in the Preview area (ShowStoppedPlaceholder/HideStoppedPlaceholder,
    // the _awaitingFirstFrameFlag), so — same pattern as RotateRequested above —
    // this control just raises the request and the host (MainWindow) does the
    // actual _vm.StartStreamAsync()/StopStreamAsync() plus placeholder handling.
    public event EventHandler? StreamToggleRequested;

    [UsedImplicitly]
    private void StreamToggle_Click(object? sender, RoutedEventArgs e)
        => StreamToggleRequested?.Invoke(this, EventArgs.Empty);

    // ── Virtual camera ─────────────────────────────────────────────────────
    [UsedImplicitly]
    private void VCamToggle_Click(object? sender, RoutedEventArgs e) => _vm?.ToggleVirtualCamera();

    // ── Live controls ───────────────────────────────────────────────────────
    [UsedImplicitly]
    private async void FlipCamera_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is Button btn)
            _ = PlayFlipCardAsync(btn);

        await _vm.FlipCameraAsync();
    }

    private async Task PlayFlipCardAsync(Button card)
    {
        card.Classes.Add("flipping");
        await Task.Delay(140);
        card.Classes.Remove("flipping");

        card.Classes.Add("flipped-in");
        await Task.Delay(320);
        card.Classes.Remove("flipped-in");
    }

    // Rotate needs to flip VideoImage's RenderTransform immediately for a
    // snappy click response — before the round-trip to the device even
    // starts (MainViewModel.Rotation only updates later, once the device
    // confirms). VideoImage lives in the Preview area, not here, so this
    // control just fires the request and lets the host (MainWindow) do the
    // immediate local flip + the actual _vm.RotateAsync() call, same as it
    // already coordinates cold-start-blur / placeholder state around
    // StreamToggle. See MainWindow.axaml.cs -> RotateRequested handler.
    public event EventHandler? RotateRequested;

    [UsedImplicitly]
    private void Rotate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
            _ = PlayIconWobbleAsync(btn);

        RotateRequested?.Invoke(this, EventArgs.Empty);
    }

    [UsedImplicitly]
    private async void BlackScreen_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is Button btn)
            _ = PlayIconWobbleAsync(btn);

        await _vm.ToggleBlackScreenAsync();
    }

    [UsedImplicitly]
    private async void Flashlight_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is Button btn)
            _ = PlayIconWobbleAsync(btn);

        await _vm.ToggleFlashlightAsync();
    }

    private static async Task PlayIconWobbleAsync(Button button)
    {
        if (button.FindDescendantOfType<MaterialIcon>() is not { } icon) return;

        icon.Classes.Add("spin");
        await Task.Delay(200);
        icon.Classes.Remove("spin");
    }
}