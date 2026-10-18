using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace QuestApps;

public sealed class StorageBar : Control
{
    public List<QuestApp> Apps { get; set; } = new();
    public long TotalKb { get; set; }
    public long UsedKb { get; set; }
    public QuestApp? Hovered { get; set; }

    private const double BarH = 30;

    private static readonly IBrush FreeBrush = new SolidColorBrush(Color.Parse("#FF232433"));
    private static readonly IBrush OtherBrush = new SolidColorBrush(Color.Parse("#FF454758"));
    private static readonly IBrush PopupFill = new SolidColorBrush(Color.Parse("#F20E0F18"));
    private static readonly IPen PopupBorder = new Pen(new SolidColorBrush(Color.Parse("#AAFFFFFF")), 1);
    private static readonly IPen HoverPen = new Pen(Brushes.White, 2);

    public StorageBar()
    {
        ClipToBounds = true;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 10 || h < 40 || TotalKb <= 0)
            return;

        ctx.FillRectangle(FreeBrush, new Rect(0, 0, w, BarH));

        long appsSum = Apps.Sum(a => Math.Max(a.SizeKb, 0));
        long other = Math.Max(0, UsedKb - appsSum);

        var rects = new Dictionary<string, Rect>();
        double x = 0;
        int i = 0;

        void Segment(long kb, IBrush brush, QuestApp? owner)
        {
            double width = w * kb / TotalKb;
            if (width < 1 || x >= w)
                return;
            var rect = new Rect(x, 0, Math.Min(width, w - x), BarH);
            ctx.FillRectangle(brush, rect);
            if (owner != null)
            {
                rects[owner.Package] = rect;
                if (ReferenceEquals(owner, Hovered))
                    ctx.DrawRectangle(null, HoverPen, rect.Deflate(0.5));
            }
            x += width;
        }

        // store = shades of blue, sideloaded = shades of orange
        foreach (var app in Apps)
            Segment(Math.Max(app.SizeKb, 0), AppBrush(app, i++), app);

        Segment(other, OtherBrush, null);

        // summary chip inside the bar
        var summary = new FormattedText(
            $"{100.0 * UsedKb / TotalKb:0.0}% used  •  free: {Fmt(TotalKb - UsedKb)}",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
        double sw = summary.WidthIncludingTrailingWhitespace + 18;
        double sx = Math.Clamp((w - sw) / 2, 2, Math.Max(2, w - sw - 2));
        var sbg = new Rect(sx, (BarH - 20) / 2, sw, 20);
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#990E0F18")), sbg);
        ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#44FFFFFF")), 1), sbg);
        ctx.DrawText(summary, new Point(sx + 9, sbg.Y + (20 - summary.Height) / 2));

        // tooltip BELOW the bar with a little arrow pointing up — never covers the bar
        if (Hovered is { SizeKb: > 0 } ha && rects.TryGetValue(ha.Package, out var r))
        {
            var version = string.IsNullOrEmpty(ha.Version) ? "" : "  v" + ha.Version;
            var text = new FormattedText(
                $"{ha.DisplayName}{version} — {ha.SizeText} ({100.0 * ha.SizeKb / TotalKb:0.0}%)",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.White);
            double bw = text.WidthIncludingTrailingWhitespace + 18;
            double bx = Math.Clamp(r.Center.X - bw / 2, 2, w - bw - 2);
            var bg = new Rect(bx, BarH + 12, bw, 24);

            double tipX = Math.Clamp(r.Center.X, bg.X + 12, bg.Right - 12);
            var arrow = new StreamGeometry();
            using (var s = arrow.Open())
            {
                s.BeginFigure(new Point(tipX, BarH + 3), true);
                s.LineTo(new Point(tipX - 6, bg.Top));
                s.LineTo(new Point(tipX + 6, bg.Top));
                s.EndFigure(true);
            }

            ctx.FillRectangle(PopupFill, bg);
            ctx.DrawGeometry(PopupFill, null, arrow);
            ctx.DrawGeometry(null, PopupBorder, arrow);
            ctx.DrawRectangle(PopupBorder, bg);
            ctx.DrawText(text, new Point(bg.X + 9, bg.Y + (bg.Height - text.Height) / 2));
        }
    }

    private static IBrush AppBrush(QuestApp app, int index)
    {
        double v = index % 2 == 0 ? 0.66 : 0.52;
        return new SolidColorBrush(app.IsStore ? Hsv(218, 0.55, v) : Hsv(26, 0.7, v));
    }

    private static string Fmt(long kb) =>
        kb >= 1024 * 1024 ? $"{kb / 1048576.0:0.0} GB" : $"{kb / 1024.0:0.0} MB";

    private static Color Hsv(double h, double s, double v)
    {
        double F(double n)
        {
            var k = (n + h / 60) % 6;
            return v - v * s * Math.Max(0, Math.Min(Math.Min(k, 4 - k), 1));
        }
        return Color.FromArgb(255, (byte)(F(5) * 255), (byte)(F(3) * 255), (byte)(F(1) * 255));
    }
}
