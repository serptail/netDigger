using System.Windows.Media;
using NetDigger.Core;

namespace NetDigger.App;

/// <summary>One line in the live results list.</summary>
public sealed class ResourceRow
{
    private static readonly Brush OkFg = Frozen("#5CCFC0"), OkBg = Frozen("#032D32");
    private static readonly Brush WarnFg = Frozen("#E2B455"), WarnBg = Frozen("#2A2412");
    private static readonly Brush ErrFg = Frozen("#EE7B72"), ErrBg = Frozen("#2D1816");
    private static readonly Brush IdleFg = Frozen("#8F9BA5"), IdleBg = Frozen("#1B2226");

    public ResourceRow(ResourceRecord record)
    {
        Url = record.Url;
        IsPage = record.Kind == ResourceKind.Page;
        KindLabel = record.Kind.ToString();

        var status = record.StatusCode;
        var failed = record.Outcome == FetchOutcome.Failed;
        IsBroken = failed || status >= 400;

        StatusText = status?.ToString() ?? record.Outcome switch
        {
            FetchOutcome.Failed => "fail",
            FetchOutcome.BlockedByRobots => "robots",
            FetchOutcome.SkippedByLimit => "limit",
            _ => "-"
        };

        if (IsBroken) (StatusForeground, StatusBackground) = (ErrFg, ErrBg);
        else if (status is >= 200 and < 300) (StatusForeground, StatusBackground) = (OkFg, OkBg);
        else if (status is >= 300 || record.Outcome is FetchOutcome.BlockedByRobots or FetchOutcome.SkippedByLimit)
            (StatusForeground, StatusBackground) = (WarnFg, WarnBg);
        else (StatusForeground, StatusBackground) = (IdleFg, IdleBg);
    }

    public string Url { get; }
    public string KindLabel { get; }
    public string StatusText { get; }
    public bool IsPage { get; }
    public bool IsBroken { get; }
    public Brush StatusForeground { get; }
    public Brush StatusBackground { get; }

    private static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
