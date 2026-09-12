using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DroidLens.Client.Models;
using DroidLens.Client.ViewModels;

namespace DroidLens.Client.Views.Controls;

// Extracted from AboutPanel: the Help tab's own left nav (7 sections) + right-pane content,
// including the Settings section's live dropdown previews. Self-contained — AboutPanel just
// shows/hides this whole control when the Help tab is active, same as it does for AboutContent.
public partial class HelpContent : UserControl
{
    // Mirrors AboutContent's own _vm/DataContext pattern — needed for Loc[...] bindings here too.
    private MainViewModel? _vm;

    public HelpContent()
    {
        InitializeComponent();
    }

    // Called by AboutPanel before showing the Help tab. Sets DataContext for Loc[...] bindings.
    public void Attach(MainViewModel vm)
    {
        if (_vm == vm) return;
        _vm = vm;
        DataContext = _vm;

        PopulateHelpSettingsPreviews();
    }

    // ── Section nav switcher ──────────────────────────────────────────────────
    // Same IsVisible-swap + Classes("active") pattern used elsewhere (e.g. AboutPanel's own
    // SetTab / Sidebar.SetConnectionTab), just one level deeper: this is Help's own 7-section
    // nav. Unlike AboutPanel's tab switch, there's no width animation here — nav items don't
    // collapse to icon-only, they just toggle a highlight (see .help-nav.active in Cards.axaml).
    // Tag on each HelpNav* button (set in XAML: Tag="start" etc.) keys straight into the two
    // parallel dictionaries below, so adding an 8th section later is a 2-line change here + the
    // matching XAML block, not a new if/else branch.

    private Dictionary<string, Button>? _helpNavButtons;
    private Dictionary<string, Control>? _helpSectionPanels;

    // Called once, lazily, from HelpSection_Click the first time it fires —
    // avoids doing this lookup work in the constructor before InitializeComponent's
    // named fields (HelpNavStart etc.) are guaranteed populated.
    private void EnsureHelpNavMaps()
    {
        if (_helpNavButtons != null) return;

        _helpNavButtons = new Dictionary<string, Button>
        {
            ["start"]    = HelpNavStart,
            ["settings"] = HelpNavSettings,
            ["quality"]  = HelpNavQuality,
            ["controls"] = HelpNavControls,
            ["preview"]  = HelpNavPreview,
            ["vcam"]     = HelpNavVcam,
            ["tray"]     = HelpNavTray,
        };

        _helpSectionPanels = new Dictionary<string, Control>
        {
            ["start"]    = HelpSecStart,
            ["settings"] = HelpSecSettings,
            ["quality"]  = HelpSecQuality,
            ["controls"] = HelpSecControls,
            ["preview"]  = HelpSecPreview,
            ["vcam"]     = HelpSecVcam,
            ["tray"]     = HelpSecTray,
        };
    }

    private void HelpSection_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } clicked) return;
        SetHelpSection(key);

        // Scrolling back to top on every section switch — without this, picking
        // a short section while scrolled deep into a long one (e.g. Controls)
        // would show a blank pane until the user manually scrolls back up.
        if (clicked.FindAncestorOfType<ScrollViewer>() is { } sv)
            sv.ScrollToHome();
    }

    private void SetHelpSection(string key)
    {
        EnsureHelpNavMaps();
        if (_helpNavButtons is null || _helpSectionPanels is null) return;
        if (!_helpNavButtons.ContainsKey(key)) return;

        foreach (var (k, btn) in _helpNavButtons)
            btn.Classes.Set("active", k == key);

        foreach (var (k, panel) in _helpSectionPanels)
            panel.IsVisible = k == key;
    }

    private void PopulateHelpSettingsPreviews()
    {
        HelpQualityPreview.Items.Clear();
        HelpCodecPreview.Items.Clear();
        HelpResolutionPreview.Items.Clear();

        if (_vm is not null)
        {
            HelpQualityPreview.Items.Add(_vm.Loc["quality.low"]);
            HelpQualityPreview.Items.Add(_vm.Loc["quality.medium"]);
            HelpQualityPreview.Items.Add(_vm.Loc["quality.high"]);
        }
        else
        {
            HelpQualityPreview.Items.Add("Low");
            HelpQualityPreview.Items.Add("Medium");
            HelpQualityPreview.Items.Add("High");
        }
        HelpQualityPreview.SelectedIndex = 1;

        // Codec/resolution values aren't localized elsewhere (real dropdowns in Sidebar
        // populate these from the active device's supported list, not from .lang) — using
        // representative example values here rather than wiring up a fake device list.
        HelpCodecPreview.Items.Add("H.264");
        HelpCodecPreview.Items.Add("H.265");
        HelpCodecPreview.Items.Add("MJPEG");
        HelpCodecPreview.SelectedIndex = 0;

        // Analogous to Sidebar.PopulateCombos() — same labels + StreamResolution values,
        // same order SD → HD → FHD, selection synced to ViewModel (HD fallback).
        HelpResolutionPreview.Items.Add(new DropdownItem("SD · ≈4:3", StreamResolution.SD));
        HelpResolutionPreview.Items.Add(new DropdownItem("HD · 16:9", StreamResolution.HD));
        HelpResolutionPreview.Items.Add(new DropdownItem("Full HD · 16:9", StreamResolution.FHD));
        if (_vm is not null)
            HelpResolutionPreview.SelectedItem = _vm.CurrentResolution;
        else
            HelpResolutionPreview.SelectedIndex = 1;
    }

    // Help > Getting started > step 1: download the Android app.
    // Points at the repo's Releases page (APK will be attached there once published) —
    // same TODO-URL placeholder pattern as AboutContent's GitHubLink_Click.
    private void DownloadAppLink_Click(object? sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/Sfix0/DroidLens/releases");

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort: ignore failures.
        }
    }
}
