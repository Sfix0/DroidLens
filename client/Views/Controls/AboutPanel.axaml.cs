using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DroidLens.Client.ViewModels;

namespace DroidLens.Client.Views.Controls;

// After the About/Help split: this is now just the tab switcher + close button.
// App identity/links/licenses live in AboutContent; Help nav/sections live in HelpContent.
// Both are attached the same MainViewModel this control gets from MainWindow.
public partial class AboutPanel : UserControl
{
    // Raised when close (✕) is clicked. MainWindow hides the panel.
    public event EventHandler? CloseRequested;

    public AboutPanel()
    {
        InitializeComponent();
    }

    // Called by MainWindow before showing. Forwards to both child controls so their
    // Loc[...] bindings and one-time setup (version label, help previews) are ready.
    public void Attach(MainViewModel vm)
    {
        AboutContentControl.Attach(vm);
        HelpContentControl.Attach(vm);
    }

    // ── Header (close) ──────────────────────────────────────────────────────

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ── About / Help tab switcher ───────────────────────────────────────────
    // Like Sidebar.SetConnectionTab: active tab expands, inactive collapses to icon.
    // 30px (vs Sidebar's 34px) fits the 52px header; must match XAML widths.
    private const double TabExpandedWidth  = 128;
    private const double TabCollapsedWidth = 30;

    private void AboutTab_Click(object? sender, RoutedEventArgs e) => SetTab(help: false);
    private void HelpTab_Click(object? sender, RoutedEventArgs e)  => SetTab(help: true);

    private void SetTab(bool help)
    {
        AboutTabButton.Classes.Set("active", !help);
        HelpTabButton.Classes.Set("active", help);

        AboutTabButton.Width = help ? TabCollapsedWidth : TabExpandedWidth;
        HelpTabButton.Width  = help ? TabExpandedWidth  : TabCollapsedWidth;

        AboutContentControl.IsVisible = !help;
        HelpContentControl.IsVisible  = help;
    }

    // Hover-zoom like SettingsPanel close button. Duplicated per UserControl.
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
}