using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ExitBoost.App.Controls;

/// <summary>График задержки в реальном времени.</summary>
public sealed class LatencyChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IList<double>), typeof(LatencyChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(LatencyChart),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(LatencyChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(LatencyChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public IList<double>? Samples
    {
        get => (IList<double>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public LatencyChart()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    private System.Collections.Specialized.INotifyCollectionChanged? _observed;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != SamplesProperty) return;

        if (_observed is not null)
            _observed.CollectionChanged -= OnCollectionChanged;
        _observed = e.NewValue as System.Collections.Specialized.INotifyCollectionChanged;
        if (_observed is not null)
            _observed.CollectionChanged += OnCollectionChanged;

        InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        InvalidateVisual();

    private FormattedText MakeText(string text, Typeface typeface, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, PixelsPerDip);

    private double PixelsPerDip =>
        VisualTreeHelper.GetDpi(this).PixelsPerDip;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;

        var samples = Samples;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
        var labelBrush = new SolidColorBrush(Color.FromArgb(120, 140, 149, 166));
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // Сетка
        for (int i = 0; i <= 4; i++)
        {
            double y = 8 + (h - 26) * i / 4.0;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        if (samples is null || samples.Count < 2)
        {
            var hint = MakeText(Caption.Length > 0 ? Caption : "Нет данных — подключитесь к узлу", typeface, 12, labelBrush);
            dc.DrawText(hint, new Point(6, h / 2 - 8));
            return;
        }

        double maxVal = samples.Max();
        double minVal = samples.Min();
        double top = Math.Max(maxVal * 1.15, 20);
        double bottom = Math.Max(0, minVal * 0.75);
        if (top - bottom < 10) top = bottom + 10;

        const double left = 6, right = 6, topPad = 8, bottomPad = 18;
        double plotW = w - left - right;
        double plotH = h - topPad - bottomPad;

        double step = plotW / (samples.Count - 1);
        var points = new List<Point>(samples.Count);
        for (int i = 0; i < samples.Count; i++)
        {
            double x = left + step * i;
            double v = Math.Min(Math.Max(samples[i], bottom), top);
            double y = topPad + plotH * (1 - (v - bottom) / (top - bottom));
            points.Add(new Point(x, y));
        }

        // Заливка под графиком
        if (FillBrush is not null)
        {
            var figure = new PathFigure { StartPoint = new Point(points[0].X, h - bottomPad) };
            figure.Segments.Add(new LineSegment(points[0], true));
            for (int i = 1; i < points.Count; i++) figure.Segments.Add(new LineSegment(points[i], true));
            figure.Segments.Add(new LineSegment(new Point(points[^1].X, h - bottomPad), true));
            figure.IsClosed = true;

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            dc.DrawGeometry(FillBrush, null, geometry);
        }

        // Линия
        var lineFigure = new PathFigure { StartPoint = points[0], IsClosed = false };
        for (int i = 1; i < points.Count; i++) lineFigure.Segments.Add(new LineSegment(points[i], true));
        var lineGeometry = new PathGeometry();
        lineGeometry.Figures.Add(lineFigure);
        dc.DrawGeometry(null, new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, lineGeometry);

        // Точка последнего замера
        dc.DrawEllipse(LineBrush, null, points[^1], 3.5, 3.5);

        // Подписи min/max
        var maxText = MakeText($"{Math.Round(top)} мс", typeface, 10.5, labelBrush);
        dc.DrawText(maxText, new Point(left, 0));

        var minText = MakeText($"{Math.Round(bottom)} мс", typeface, 10.5, labelBrush);
        dc.DrawText(minText, new Point(left, h - 15));

        if (Caption.Length > 0)
        {
            var cap = MakeText(Caption, typeface, 10.5, labelBrush);
            dc.DrawText(cap, new Point(w - cap.Width - 4, h - 15));
        }
    }
}

