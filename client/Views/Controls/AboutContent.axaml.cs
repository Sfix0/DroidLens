using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DroidLens.Client.ViewModels;

namespace DroidLens.Client.Views.Controls;

// Static data for licenses ItemsControl — not from ViewModel, populated once in PopulateLicenses().
public record ThirdPartyLicenseEntry(string Name, string License);

// Extracted from AboutPanel: app identity, core dependency links, and third-party licenses.
// Owns none of the tab/section navigation — that stays in AboutPanel (tabs) and HelpContent (help nav).
public partial class AboutContent : UserControl
{
    // Mirrors AboutPanel's own _vm/DataContext pattern (see SettingsPanel.Attach) — this control
    // needs the same MainViewModel for its Loc[...] bindings.
    private MainViewModel? _vm;

    // Author identity, shown under the tagline. Plain constants (not localized —
    // a person's name/handle doesn't get translated).
    private const string AuthorName      = "Rafik Akhmedov";
    private const string AuthorGitHubTag = "Sfix0";
    private const string AuthorGitHubUrl = "https://github.com/Sfix0";

    public AboutContent()
    {
        InitializeComponent();
        AuthorNameLabel.Text = AuthorName;
        AuthorHandleLabel.Text = AuthorGitHubTag;
        PopulateLicenses();
    }

    // Called by AboutPanel before showing the About tab. Sets DataContext for Loc[...] bindings.
    public void Attach(MainViewModel vm)
    {
        if (_vm == vm) return;
        _vm = vm;
        DataContext = _vm;

        ShowVersion();
    }

    // ── App identity ──────────────────────────────────────────────────────────

    private void ShowVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionLabel.Text = version is null ? string.Empty : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private void AuthorLink_Click(object? sender, RoutedEventArgs e)
        => OpenUrl(AuthorGitHubUrl);

    private void GitHubLink_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/Sfix0/DroidLens",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort: ignore failures.
        }
    }

    // Core-dependency cards (softcam/FFmpeg/ADB) are hand-authored in XAML now — see the asymmetric
    // Grid in AboutContent.axaml — instead of populated from a list, since each card is a distinct
    // size/position (softcam tall-left, FFmpeg/ADB stacked right), not a repeated template. Their
    // Tag="<url>" + Click="CoreComponentLink_Click" wiring below still applies to all three.
    private void CoreComponentLink_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string url }) OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
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

    // ── Third-party licenses ─────────────────────────────────────────────────
    // Compact list excludes Core dependencies (FFmpeg/softcam/ADB) to avoid duplicates.
    // Sdcb.FFmpeg is listed separately as LGPL-3.0 binding. Full list in THIRD_PARTY_LICENSES.md.
    private void PopulateLicenses()
    {
        var entries = new List<ThirdPartyLicenseEntry>
        {
            new("Sdcb.FFmpeg",                      "LGPL-3.0"),
            new("Base Classes (DirectShow, MS)",    "MIT"),
            new("Makaretu.Dns.Multicast.New",       "MIT"),
            new("Avalonia UI",                      "MIT"),
            new("Material.Icons.Avalonia",          "MIT"),
        };

        LicensesList.ItemsSource = entries;
    }
}