using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace NaxEventViewer.App;

/// <summary>
/// A lightweight horizontal wrapping panel so level filter buttons flow onto a second row instead of clipping on narrow windows.
/// </summary>
public sealed partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 4;
    public double VerticalSpacing { get; set; } = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        double lineWidth = 0;
        double lineHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;
        var childConstraint = new Size(double.PositiveInfinity, availableSize.Height);

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(childConstraint);
            Size desired = child.DesiredSize;
            double nextWidth = lineWidth == 0 ? desired.Width : lineWidth + HorizontalSpacing + desired.Width;
            if (lineWidth > 0 && nextWidth > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + VerticalSpacing;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
            }
            else
            {
                lineWidth = nextWidth;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }
        }
        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;
        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double y = 0;
        double lineHeight = 0;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            Size desired = child.DesiredSize;
            if (x > 0 && x + desired.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
            }
            child.Arrange(new Rect(x, y, desired.Width, desired.Height));
            x += desired.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }
        return finalSize;
    }
}

/// <summary>
/// A zero-dependency draggable splitter bar for resizing panes and list columns. Double-tapping restores the default size.
/// </summary>
public sealed partial class PaneSplitter : ContentControl
{
    private readonly Border _indicator;
    private bool _dragging;
    private double _lastPosition;
    private Orientation _orientation = Orientation.Vertical;

    public event Action<double>? DragDelta;
    public event Action? DragCompleted;
    public event Action? ResetRequested;

    public PaneSplitter()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = false;
        _indicator = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Width = 1,
            Opacity = 0.0,
        };
        Content = _indicator;
        ApplyOrientation();

        PointerEntered += (_, _) => ShowIndicator(hover: true);
        PointerExited += (_, _) => { if (!_dragging) ShowIndicator(hover: false); };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
        PointerCaptureLost += (_, _) => FinishDrag();
        DoubleTapped += (_, _) => ResetRequested?.Invoke();
    }

    public Orientation Orientation
    {
        get => _orientation;
        set
        {
            if (_orientation == value) return;
            _orientation = value;
            ApplyOrientation();
        }
    }

    private void ApplyOrientation()
    {
        bool vertical = _orientation == Orientation.Vertical;
        ProtectedCursor = InputSystemCursor.Create(vertical ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
        if (vertical)
        {
            _indicator.Width = 2;
            _indicator.Height = double.NaN;
            _indicator.HorizontalAlignment = HorizontalAlignment.Center;
            _indicator.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            _indicator.Width = double.NaN;
            _indicator.Height = 2;
            _indicator.HorizontalAlignment = HorizontalAlignment.Stretch;
            _indicator.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    private void ShowIndicator(bool hover)
    {
        if (hover && Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out object? brush) && brush is Brush accent)
            _indicator.Background = accent;
        _indicator.Opacity = hover ? 0.65 : 0.0;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed) return;
        _dragging = CapturePointer(e.Pointer);
        if (!_dragging) return;
        _lastPosition = _orientation == Orientation.Vertical ? point.Position.X : point.Position.Y;
        ShowIndicator(hover: true);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        PointerPoint point = e.GetCurrentPoint(null);
        double current = _orientation == Orientation.Vertical ? point.Position.X : point.Position.Y;
        double delta = current - _lastPosition;
        if (Math.Abs(delta) >= 0.5)
        {
            _lastPosition = current;
            DragDelta?.Invoke(delta);
        }
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        ReleasePointerCapture(e.Pointer);
        FinishDrag();
        e.Handled = true;
    }

    private void FinishDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        ShowIndicator(hover: false);
        DragCompleted?.Invoke();
    }
}
