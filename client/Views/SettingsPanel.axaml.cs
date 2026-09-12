using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DroidLens.Client.Services;
using DroidLens.Client.ViewModels;

namespace DroidLens.Client.Views.Controls;

public partial class SettingsPanel : UserControl
{
    // Reuses MainWindow's MainViewModel instance instead of taking a bare
    // SettingsService — MainViewModel already owns Settings + Loc and
    // exposes ToggleTheme()/SetLanguage()/StopStreamOnDisconnect/
    // StopStreamOnAppClose, and routing every setting change through those
    // same members (instead of duplicating their logic here) keeps this
    // panel and MainWindow's own header toggle from ever disagreeing about
    // how a change is applied or displayed.
    private MainViewModel? _vm;

    // Guard so that programmatically setting AppDropdown.SelectedItem while
    // populating the language list in the constructor doesn't immediately fire
    // SelectionChanged back into _vm.
    private bool _suppressLanguageEvent;

    // Fired when the close (✕) button is clicked — MainWindow just hides this
    // panel in response instead of the old Window.Close(). No payload needed;
    // this control never closes itself, since it has no independent visibility
    // state of its own — the host (MainWindow) owns that via IsVisible.
    public event EventHandler? CloseRequested;

    // Fired instead of calling _vm.ToggleTheme() directly, carrying the click
    // origin translated into THIS panel's own coordinate space. MainWindow (the
    // only host that actually knows how to do the circular-reveal animation and
    // owns the theme-application logic) is responsible for further translating
    // that point into its own root Panel's space and applying the theme itself —
    // SettingsPanel has no idea a reveal animation exists, it just relays where
    // the click happened.
    public event EventHandler<Point>? ThemeToggleRequested;

    public SettingsPanel()
    {
        InitializeComponent();
    }

    // Called by MainWindow right before showing the panel (IsVisible = true),
    // so DataContext/language list are only wired up once we actually have a
    // MainViewModel to bind against — same information the old constructor
    // received as a parameter.
    public void Attach(MainViewModel vm)
    {
        if (_vm == vm) return;
        _vm = vm;
        DataContext = _vm;

        PopulateLanguageCombo();
        PopulateUiScaleCombo();
    }

    // ── Root (click-away to stop editing) ────────────────────────────────────

    // ScreenshotPathBox is a real editable TextBox with no "commit" button of
    // its own — LostFocus is what actually pushes the typed value into
    // _vm.ScreenshotPath (see ScreenshotPathBox_LostFocus). Avalonia never
    // moves keyboard focus away from a control just because the pointer was
    // pressed somewhere non-interactive (empty card space, labels, etc.), so
    // without this the box stayed focused/editable until the whole panel
    // closed. Handled at the root Border (covers the entire panel, including
    // the texture layer and every card) rather than per-card, so any "click
    // into empty space" anywhere in the panel ends editing the same way.
    private void RootBorder_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        // Only acts when the TextBox itself currently holds focus, and only
        // when the press didn't land inside it — clicking inside the box
        // (to reposition the caret, select text, etc.) must not blur it.
        if (ScreenshotPathBox.IsFocused
            && e.Source is Visual hit
            && !ScreenshotPathBox.IsVisualAncestorOf(hit)
            && hit != ScreenshotPathBox)
        {
            // Moving focus to the root Border itself (rather than calling
            // Focus() with no target) is what actually blurs the TextBox —
            // Border isn't normally focusable, so this needs Focusable=true
            // set alongside this handler in the .axaml for the call to stick.
            (sender as InputElement)?.Focus();
        }
    }

    // ── Header (close) ──────────────────────────────────────────────────────

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    // Same hover-zoom used by MainWindow's title-bar buttons (see
    // MainWindow.TitleBar.cs) — ported here rather than shared directly since
    // SettingsPanel is its own UserControl, not MainWindow itself, so it can't
    // call MainWindow's private instance handlers. Kept behavior-identical
    // (same 1.18 scale, same ScaleTransform target) so the close button here
    // is a genuine match for MainWindow's, not just a same-class lookalike.
    private void HeaderBtn_PointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
        => SetHeaderScale(sender, 1.18);

    private void HeaderBtn_PointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
        => SetHeaderScale(sender, 1.0);

    private static void SetHeaderScale(object? sender, double scale)
    {
        if (sender is Control control && control.RenderTransform is ScaleTransform st)
        {
            st.ScaleX = scale;
            st.ScaleY = scale;
        }
    }

    // ── Language ──────────────────────────────────────────────────────────────

    private void PopulateLanguageCombo()
    {
        _suppressLanguageEvent = true;

        // SupportedLanguages is auto-discovered from Assets/Locales/*.lang —
        // same source LocalizationService itself uses, so this list can never
        // drift out of sync with what's actually on disk. Each language's
        // display name comes from its own file's "lang.name" endonym key.
        foreach (string code in LocalizationService.SupportedLanguages)
            LanguageCombo.Items.Add(new DropdownItem(LocalizationService.GetLanguageDisplayName(code), code));

        var current = LanguageCombo.Items
            .OfType<DropdownItem>()
            .FirstOrDefault(i => (string?)i.Value == _vm!.Settings.Current.Language);
        LanguageCombo.SelectedItem = current ?? LanguageCombo.Items.OfType<DropdownItem>().FirstOrDefault();

_suppressLanguageEvent = false;
    }

    private void LanguageCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageEvent) return;
        if (LanguageCombo.SelectedItem is not DropdownItem item || item.Value is not string code) return;

        // MainViewModel.SetLanguage already does Loc.Load + Settings.UpdateLanguage +
        // OnPropertyChanged(string.Empty) — that last part is what makes every
        // {Binding Loc[...]} in both this window AND MainWindow re-resolve live,
        // since they share the same _vm/DataContext.
        _vm!.SetLanguage(code);
        PopulateUiScaleCombo();
    }

    // ── UI Scaling ────────────────────────────────────────────────────────────

    private bool _suppressUiScaleEvent;

    private void PopulateUiScaleCombo()
    {
        if (_vm is null) return;
        _suppressUiScaleEvent = true;
        UiScaleCombo.Items.Clear();

        var scales = new (double Scale, string Key)[]
        {
            (0.75, "scale.compact_xs"),
            (0.85, "scale.compact"),
            (1.00, "scale.default"),
            (1.15, "scale.large"),
            (1.25, "scale.large_xl")
        };

        foreach (var (scale, key) in scales)
        {
            string label = _vm.Loc[key];
            UiScaleCombo.Items.Add(new DropdownItem(label, scale));
        }

        double currentScale = _vm.UiScale;
        var selected = UiScaleCombo.Items
            .OfType<DropdownItem>()
            .FirstOrDefault(i => i.Value is double s && Math.Abs(s - currentScale) < 0.01)
            ?? UiScaleCombo.Items.OfType<DropdownItem>().FirstOrDefault(i => i.Value is double s && Math.Abs(s - 1.0) < 0.01);

        UiScaleCombo.SelectedItem = selected;
        _suppressUiScaleEvent = false;
    }

    private void UiScaleCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiScaleEvent) return;
        if (UiScaleCombo.SelectedItem is not DropdownItem item || item.Value is not double scale) return;

        _vm!.SetUiScale(scale);
    }

    // Theme is driven by controls:ThemeSwitch in the .axaml: IsDarkTheme is bound
    // one-way (so the switch's visuals always reflect the VM). Toggled no longer
    // calls _vm.ToggleTheme() directly — MainWindow needs to run the circular-reveal
    // animation *around* the theme flip (screenshot old state -> flip theme
    // underneath -> reveal it through an expanding circle), so this just relays
    // the click point up to whoever's actually listening (MainWindow), translated
    // from the ThemeSwitch's own space into this panel's space so the receiver
    // only has one more hop (panel -> window root) instead of needing to know
    // ThemeSwitch's internal layout too.
    private void ThemeSwitch_Toggled(object? sender, ThemeToggledEventArgs e)
    {
        if (sender is Visual switchVisual)
        {
            Point pointInPanel = switchVisual.TranslatePoint(e.PositionInSwitch, this) ?? e.PositionInSwitch;
            ThemeToggleRequested?.Invoke(this, pointInPanel);
        }
        else
        {
            _vm!.ToggleTheme(); // fallback: no reveal, but theme still flips
        }
    }

    // ── Stream-behavior switches ─────────────────────────────────────────────
    // Color + thumb position are now plain bindings in the .axaml (see
    // StopStreamOnDisconnect/StopStreamOnAppClose on MainViewModel) — these
    // handlers just flip the VM property; Settings persistence, UI color and
    // thumb offset all follow automatically from that one assignment. No more
    // local _settings copy or manual SetSwitch/TryGetResource color sync.

    private void StopOnDisconnectSwitch_Click(object? sender, RoutedEventArgs e)
        => _vm!.StopStreamOnDisconnect = !_vm.StopStreamOnDisconnect;

    private void StopOnAppCloseSwitch_Click(object? sender, RoutedEventArgs e)
        => _vm!.StopStreamOnAppClose = !_vm.StopStreamOnAppClose;

    // ── Preview FPS cap ───────────────────────────────────────────────────────
    // Same "just flip the VM property" pattern as the two switches above —
    // persistence + PreviewArea's actual skip-frame behavior both follow from
    // this one assignment (see MainViewModel.LimitPreviewFps / PreviewArea.axaml.cs).
    private void LimitPreviewFpsSwitch_Click(object? sender, RoutedEventArgs e)
        => _vm!.LimitPreviewFps = !_vm.LimitPreviewFps;

    // ── Ambient light fill ───────────────────────────────────────────────────
    // Same "just flip the VM property" pattern — persistence + the actual
    // gating in PreviewArea both follow from this one assignment (see
    // MainViewModel.AmbientFillEnabled / PreviewArea.axaml.cs:
    // ShouldUpdateAmbientFill).
    private void AmbientFillSwitch_Click(object? sender, RoutedEventArgs e)
        => _vm!.AmbientFillEnabled = !_vm.AmbientFillEnabled;

    // ── Screenshot save location ─────────────────────────────────────────────
    // TextBox is already TwoWay-bound to _vm.ScreenshotPath directly (see the
    // .axaml) — this handler exists only so a manually-typed path is committed
    // the moment the field loses focus, matching how the Host textbox
    // elsewhere in the app behaves, rather than only on window close.
    private void ScreenshotPathBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        // Binding already pushed the value into _vm.ScreenshotPath (which
        // normalizes/persists it — see MainViewModel). Nothing further to do;
        // this handler is a placeholder in case a future validation step
        // (e.g. "does this path exist / is it writable") needs a hook here
        // without touching the XAML again.
    }

    // "За замовчуванням" / "Reset to default" — only visible while
    // _vm.HasCustomScreenshotPath is true (see the .axaml binding), so a
    // click here always has something to actually reset.
    private void ResetScreenshotPath_Click(object? sender, RoutedEventArgs e)
        => _vm!.ResetScreenshotPath();

    // Opens the current screenshot folder in the OS file explorer — separate
    // from Browse, which *changes* ScreenshotPath. This one only ever reads
    // it. Creates the folder first if it doesn't exist yet (e.g. the default
    // Pictures\DroidLens before a first screenshot has ever been taken) so
    // the click never silently no-ops.
    private void OpenScreenshotFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string path = _vm!.ScreenshotPath;
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            // "explorer.exe <path>" (rather than Process.Start(path) with
            // UseShellExecute) is the most reliable way to open a folder
            // specifically, cross-checked against how BrowseScreenshotPath's
            // own folder picker resolves paths — avoids ambiguity if a path
            // ever looked executable.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort, same philosophy as TryGetStartFolderAsync below:
            // a failed "open folder" (permissions, drive unplugged, etc.) just
            // means nothing happens — not worth surfacing over something this
            // minor.
        }
    }

    // Copy-to-clipboard is now handled by the field's own native Ctrl+C
    // (select-all first for the full path if it's longer than the visible
    // width) — the standalone copy icon that used to live inside the field
    // is gone, so this handler is no longer wired to anything in the .axaml.

    private async void BrowseScreenshotPath_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storage) return;

        var startFolder = await TryGetStartFolderAsync(storage);

        var result = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = _vm!.Loc["settings.screenshot_path"],
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
        });

        if (result.Count == 0) return;

        // TryGetLocalPath can return null for non-filesystem providers (e.g.
        // some virtualized/cloud locations) — silently ignore rather than
        // persisting an unusable path; the existing value stays as-is.
        string? path = result[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            _vm.ScreenshotPath = path;
    }

    // Best-effort: point the picker at the currently configured folder if it
    // exists, so re-opening the dialog doesn't always reset to some OS
    // default. Failure here (folder deleted, path invalid, etc.) just means
    // the picker falls back to its own default — never worth surfacing to
    // the user over something this minor.
    private async Task<Avalonia.Platform.Storage.IStorageFolder?> TryGetStartFolderAsync(
        Avalonia.Platform.Storage.IStorageProvider storage)
    {
        try
        {
            string current = _vm!.ScreenshotPath;
            return Directory.Exists(current)
                ? await storage.TryGetFolderFromPathAsync(new Uri(current))
                : null;
        }
        catch
        {
            return null;
        }
    }
}