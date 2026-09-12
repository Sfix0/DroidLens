using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DroidLens.Client.Views.Controls;

/// <summary>
/// Drop-in TextBox replacement with AppDropdown's borderless card-surface look
/// (see AppTextField.axaml header comment for the visual rationale). API
/// mirrors TextBox closely (Text, LostFocus) so existing call sites like:
///     ScreenshotPathBox.Text
///     ScreenshotPathBox_LostFocus
/// keep working after just changing the declared type from TextBox to
/// AppTextField in the .axaml and .axaml.cs.
/// </summary>
public partial class AppTextField : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<AppTextField, string?>(nameof(Text), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Mirrors TextBox.Watermark — placeholder text shown when Text is empty.</summary>
    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<AppTextField, string?>(nameof(Watermark));

    // Defaults to the same 14,12 AppDropdown's Head uses. Overridable per
    // instance so call sites with flanking icon buttons inside the same
    // rounded surface (e.g. ScreenshotPathBox's copy/folder icons) can widen
    // one side to keep the text clear of them, instead of the icons
    // overlapping typed text.
    public static new readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<AppTextField, Thickness>(nameof(Padding), new Thickness(14, 12));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public new Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    /// <summary>Mirrors TextBox.LostFocus — fires once the inner TextBox actually loses focus,
    /// e.g. to commit a manually-typed path (see ScreenshotPathBox_LostFocus in SettingsPanel).</summary>
    public new event EventHandler<RoutedEventArgs>? LostFocus;

    public AppTextField()
    {
        InitializeComponent();
    }

    // Lets a host (e.g. MainWindow's click-away-to-blur handler) end editing
    // without needing to know this control wraps an inner TextBox — mirrors
    // how SettingsPanel currently blurs ScreenshotPathBox by focusing its
    // root Border instead. Moving focus to FieldSurface itself (rather than
    // calling Focus() with no target) is what actually blurs InnerBox; Border
    // isn't normally focusable, so FieldSurface needs Focusable=true in the
    // .axaml for this to stick.
    public void ClearFocus()
    {
        var surface = this.FindControl<Border>("FieldSurface");
        surface?.Focus();
    }

    /// <summary>True while the inner TextBox actually holds keyboard focus — lets a host
    /// check before deciding whether a click-away should blur this field (see MainWindow's
    /// RootPanel_PointerPressed), the same way SettingsPanel checks ScreenshotPathBox.IsFocused.</summary>
    public bool IsInputFocused => this.FindControl<TextBox>("InnerBox")?.IsFocused == true;

    // Toggles the "focused" visual state (background lighten + slight grow —
    // see Border#FieldSurface.focused in the .axaml) in lockstep with the
    // inner TextBox's real focus state, rather than trying to re-derive focus
    // from IsFocused polling.
    private void InnerBox_GotFocus(object? sender, GotFocusEventArgs e)
    {
        var surface = this.FindControl<Border>("FieldSurface");
        surface?.Classes.Add("focused");
    }

    private void InnerBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        var surface = this.FindControl<Border>("FieldSurface");
        surface?.Classes.Remove("focused");

        // Relay to our own LostFocus so call sites (e.g. SettingsPanel's
        // ScreenshotPathBox_LostFocus) don't need to know this control wraps
        // an inner TextBox at all.
        LostFocus?.Invoke(this, e);
    }
}
