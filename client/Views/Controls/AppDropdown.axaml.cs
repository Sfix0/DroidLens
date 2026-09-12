using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DroidLens.Client.Views.Controls;

/// <summary>
/// Optional item wrapper for AppDropdown — mirrors the old
/// "ComboBoxItem { Content = label, Tag = code }" pattern. Plain strings/
/// objects still work fine (ToString() becomes the label); use this when
/// you need a display label distinct from the underlying value, e.g.:
///     LanguageCombo.Items.Add(new DropdownItem("Українська", "uk"));
///     ...
///     if (LanguageCombo.SelectedItem is DropdownItem item) var code = item.Value;
/// </summary>
public sealed class DropdownItem
{
    public string Label { get; }
    public object? Value { get; }

    public DropdownItem(string label, object? value)
    {
        Label = label;
        Value = value;
    }

    public override string ToString() => Label;
}

/// <summary>
/// Drop-in ComboBox replacement whose open/close reads as the field itself
/// growing a body underneath it (see AppDropdown.axaml header comment for
/// why a re-skinned ComboBox popup couldn't achieve that).
///
/// API intentionally mirrors ComboBox (Items, SelectedIndex, SelectedItem,
/// SelectionChanged) so existing call sites like:
///     LanguageCombo.Items.Add(...);
///     LanguageCombo.SelectedIndex = 0;
///     LanguageCombo.SelectionChanged += LanguageCombo_SelectionChanged;
/// keep working after just changing the declared type from ComboBox to
/// AppDropdown in the .axaml and .axaml.cs.
/// </summary>
public partial class AppDropdown : UserControl
{
    /// <summary>Dedicated routed event for AppDropdown's SelectionChanged — avoids
    /// depending on SelectingItemsControl.SelectionChangedEvent, which belongs to
    /// a different control hierarchy than UserControl.</summary>
    public static readonly RoutedEvent<SelectionChangedEventArgs> SelectionChangedEvent =
        RoutedEvent.Register<AppDropdown, SelectionChangedEventArgs>(nameof(SelectionChanged), RoutingStrategies.Bubble);

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<AppDropdown, bool>(nameof(IsOpen));

    public static readonly StyledProperty<string?> SelectedLabelProperty =
        AvaloniaProperty.Register<AppDropdown, string?>(nameof(SelectedLabel));

    private static int _groupCounter;
    private readonly string _groupId = "AppDropdownGroup_" + _groupCounter++;

    private readonly AvaloniaList<object> _items = new();
    private int _selectedIndex = -1;
    private readonly List<RadioButton> _rows = new();

    // Scroll-dismiss plumbing: Popup stays in overlay/top-level coordinates and doesn't
    // follow its PlacementTarget when an ancestor ScrollViewer scrolls, so it visually
    // detaches ("відривається"). We close on ancestor scroll instead of blocking it.
    private TopLevel? _subscribedTopLevel;
    private DateTime _openedAt = DateTime.MinValue;
    private bool _isSyncingIsOpen;

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string? SelectedLabel
    {
        get => GetValue(SelectedLabelProperty);
        private set => SetValue(SelectedLabelProperty, value);
    }

    /// <summary>Mirrors ComboBox.Items — add plain strings, DropdownItem, or any object; ToString() is used as the row label.</summary>
    public IList Items => _items;

    /// <summary>Optional ItemsSource support for populating items from an IEnumerable.</summary>
    public IEnumerable? ItemsSource
    {
        get => _items;
        set
        {
            _items.Clear();
            if (value is not null)
            {
                foreach (var item in value)
                    _items.Add(item);
            }
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetSelectedIndex(value, raiseEvent: true);
    }

    public object? SelectedItem
    {
        get => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;
        set
        {
            if (value is null)
            {
                SetSelectedIndex(-1, raiseEvent: true);
                return;
            }

            int idx = _items.IndexOf(value);
            if (idx < 0)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i] is DropdownItem di && Equals(di.Value, value))
                    {
                        idx = i;
                        break;
                    }
                }
            }

            SetSelectedIndex(idx, raiseEvent: true);
        }
    }

    public event EventHandler<SelectionChangedEventArgs>? SelectionChanged
    {
        add => AddHandler(SelectionChangedEvent, value);
        remove => RemoveHandler(SelectionChangedEvent, value);
    }

    static AppDropdown()
    {
        IsOpenProperty.Changed.AddClassHandler<AppDropdown>((x, e) => x.OnIsOpenPropertyChanged(e));
    }

    public AppDropdown()
    {
        InitializeComponent();

        _items.CollectionChanged += OnItemsCollectionChanged;

        var head = this.FindControl<ToggleButton>("Head");
        if (head is not null)
        {
            head.PropertyChanged += (_, args) =>
            {
                if (args.Property == ToggleButton.IsCheckedProperty)
                    UpdateOpenState();
            };
        }

        // Popup.IsLightDismissEnabled (set in the .axaml) already closes the
        // popup on any outside click/tap for us — no manual top-level
        // PointerPressed plumbing needed here anymore now that DropBody lives
        // in a real Popup instead of being laid out in-flow.

        // Fires when this control's effective viewport changes due to an ancestor
        // ScrollViewer scrolling — used as fallback if ScrollChanged doesn't fire.
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsOpen)
            RegisterScrollDismiss();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnregisterScrollDismiss();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Keep ToggleButton#Head checked-state in sync when IsOpen is changed
        // programmatically (e.g., auto-close on scroll). The XAML TwoWay binding
        // Head.IsChecked <-> Root.IsOpen would eventually propagate, but we sync
        // immediately to avoid a detached frame.
        if (change.Property == IsOpenProperty && !_isSyncingIsOpen)
        {
            var head = this.FindControl<ToggleButton>("Head");
            if (head is not null && head.IsChecked != IsOpen)
            {
                _isSyncingIsOpen = true;
                head.IsChecked = IsOpen;
                _isSyncingIsOpen = false;
            }
        }
    }

    private void OnIsOpenPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is bool isOpen)
        {
            if (isOpen)
            {
                _openedAt = DateTime.UtcNow;
                RegisterScrollDismiss();
            }
            else
            {
                UnregisterScrollDismiss();
            }
        }
        // Visual grow/shrink is driven by Head.IsChecked, but if IsOpen was set
        // before Head exists or before its binding propagated, ensure body syncs.
        UpdateOpenState();
    }

    private void RegisterScrollDismiss()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || _subscribedTopLevel == topLevel) return;

        UnregisterScrollDismiss();
        _subscribedTopLevel = topLevel;
        _subscribedTopLevel.AddHandler(ScrollViewer.ScrollChangedEvent, OnAncestorScrollChanged, RoutingStrategies.Bubble);
    }

    private void UnregisterScrollDismiss()
    {
        if (_subscribedTopLevel is null) return;
        _subscribedTopLevel.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnAncestorScrollChanged);
        _subscribedTopLevel = null;
    }

    private void OnAncestorScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!IsOpen) return;
        // Only dismiss if the ScrollViewer that scrolled is an ancestor of this
        // dropdown. The dropdown's own ScrollViewer (MaxHeight 220 inside the
        // Popup overlay) is NOT an ancestor — so scrolling the list itself won't
        // close it. Any outer SettingsPanel/main ScrollViewer is.
        if (e.Source is ScrollViewer sv)
        {
            // IsVisualAncestorOf returns true if sv is ancestor of this
            if (!sv.IsVisualAncestorOf(this))
                return;
            IsOpen = false;
            e.Handled = false; // let the scroll itself continue
        }
        else if (e.Source is Visual v && v.IsVisualAncestorOf(this))
        {
            // fallback for templated scroll hosts
            IsOpen = false;
        }
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (!IsOpen) return;
        // Ignore the initial viewport establishment right after open (layout
        // hasn't settled). Without this the first viewport change after popping
        // open would instantly close it.
        if ((DateTime.UtcNow - _openedAt).TotalMilliseconds < 150) return;

        // If the effective viewport shrinks/moves while open, the Head has been
        // scrolled. Close to avoid a detached Popup frame.
        // Avalonia's Popup doesn't auto-reposition on ancestor scroll (see
        // Popup.cs:LayoutUpdated only checks Bounds, not viewport offset), so
        // closing is the expected anchored behavior.
        IsOpen = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // When open, close immediately on wheel and let the scroll propagate to
        // the ancestor ScrollViewer. Previously this handled the event (e.Handled=true)
        // like ComboBox, which completely blocked scrolling — user requested
        // "просто закривалася" instead, so we close and don't block.
        // The inner dropdown's own ScrollViewer lives in the Popup overlay host,
        // not in this visual subtree, so its wheel never reaches here.
        if (IsOpen)
            IsOpen = false;

        base.OnPointerWheelChanged(e);
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildRows();
    }

    private void RebuildRows()
    {
        var host = this.FindControl<ContentControl>("ItemsHost");
        if (host is null) return;

        _rows.Clear();
        var panel = new StackPanel { HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch };
        foreach (var item in _items)
        {
            var row = new RadioButton
            {
                Classes = { "dd-item" },
                GroupName = _groupId,
                Content = item?.ToString() ?? string.Empty,
                Tag = item,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch
            };
            row.Click += OnItemClick;
            _rows.Add(row);
            panel.Children.Add(row);
        }
        host.Content = panel;

        // Re-apply selection now that rows exist
        SetSelectedIndex(_selectedIndex, raiseEvent: false);
        MeasureAndApplyBodyHeight();
    }

    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        var idx = _rows.IndexOf(rb);
        if (idx < 0) return;

        SetSelectedIndex(idx, raiseEvent: true);
        IsOpen = false;
    }

    private void SetSelectedIndex(int idx, bool raiseEvent)
    {
        if (idx < -1 || idx >= _items.Count) idx = -1;

        var oldIndex = _selectedIndex;
        var oldItem = oldIndex >= 0 && oldIndex < _items.Count ? _items[oldIndex] : null;

        _selectedIndex = idx;
        SelectedLabel = idx >= 0 ? _items[idx]?.ToString() : null;

        for (int i = 0; i < _rows.Count; i++)
            _rows[i].IsChecked = i == idx;

        if (raiseEvent && oldIndex != idx)
        {
            var newItem = idx >= 0 ? _items[idx] : null;
            RaiseEvent(new SelectionChangedEventArgs(
                SelectionChangedEvent,
                oldItem is null ? Array.Empty<object>() : new[] { oldItem },
                newItem is null ? Array.Empty<object>() : new[] { newItem }));
        }
    }

    private void UpdateOpenState()
    {
        var head = this.FindControl<ToggleButton>("Head");
        var body = this.FindControl<Border>("DropBody");
        if (body is null) return;

        // Head.IsChecked <-> IsOpen are TwoWay-bound in XAML, but binding propagation
        // can be one frame late. When user clicks Head, Head.IsChecked changes first
        // (this method is called via Head.PropertyChanged); when we programmatically
        // close on scroll, IsOpen changes first. Use the most recent True as open, and
        // keep both in sync immediately to avoid a detached frame.
        bool headChecked = head?.IsChecked == true;
        bool isOpen = IsOpen || headChecked;

        // If they diverge due to who changed first, reconcile without re-entrancy.
        if (head is not null && headChecked != IsOpen && !_isSyncingIsOpen)
        {
            _isSyncingIsOpen = true;
            IsOpen = headChecked;
            isOpen = headChecked;
            _isSyncingIsOpen = false;
        }
        else
        {
            // When close originated from IsOpen (scroll dismiss), IsOpen is false
            // but headChecked may still be true for one frame until binding syncs;
            // authoritative close = IsOpen false.
            if (!IsOpen) isOpen = false;
            else isOpen = headChecked || IsOpen;
        }

        if (isOpen)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            double scale = 1.0;
            if (topLevel is not null && this.TransformToVisual(topLevel) is { } matrix)
            {
                scale = matrix.M11;
            }

            var scaleControl = this.FindControl<LayoutTransformControl>("PopupScaleControl");
            if (scaleControl is not null)
            {
                if (scale > 0.01 && Math.Abs(scale - 1.0) > 0.001)
                    scaleControl.LayoutTransform = new ScaleTransform(scale, scale);
                else
                    scaleControl.LayoutTransform = null;
            }

            var popup = this.FindControl<Popup>("DropPopup");
            if (popup is not null)
                popup.VerticalOffset = -1 * scale;

            body.Classes.Add("expanded");
            MeasureAndApplyBodyHeight();
        }
        else
        {
            body.Classes.Remove("expanded");
            body.Height = 0;
        }
    }

    private void MeasureAndApplyBodyHeight()
    {
        var head = this.FindControl<ToggleButton>("Head");
        var body = this.FindControl<Border>("DropBody");
        if (body is null) return;
        // Guard mirrors UpdateOpenState: allow if either IsOpen or Head.IsChecked signals open
        if (!IsOpen && head?.IsChecked != true) return;

        // IMPORTANT: Border.Height is an explicit value (0 when closed, or a
        // previous target when re-measuring after Items changed) — Measure()
        // clamps the returned DesiredSize to that already-set Height instead
        // of the content's natural size, so a stale/zero Height here silently
        // caps every row after the first. Unset it first (NaN = auto) so the
        // measure pass reflects the content's real height, then re-apply the
        // explicit value afterward for the Height Transition to animate from.
        body.Height = double.NaN;
        body.InvalidateMeasure();
        body.UpdateLayout();
        var headWidth = head?.Bounds.Width ?? 0;
        body.Measure(new Size(headWidth > 0 ? headWidth : double.PositiveInfinity, double.PositiveInfinity));
        var target = Math.Min(body.DesiredSize.Height, 220 + 8);
        body.Height = target;
    }
}