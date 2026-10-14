using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Globalization;

namespace F3M.Client.Components.Charts;

/// <summary>One series of a chart: its name (legend, tooltip), its colour (a CSS value) and one value per label.</summary>
public sealed record ChartSeries(string Name, string Color, IReadOnlyList<double> Values);

/// <summary>The points of a chart over time: a full label (tooltip, table), a short one (axis) and one value per series.</summary>
public sealed record ChartPoints(List<string> Labels, List<string> ShortLabels, List<double[]> Values);

/// <summary>A row of a bar list or a segment of a share bar.</summary>
public sealed record ChartItem(string Label, double Value, string? Href = null, string? Color = null, string? Icon = null);

/// <summary>
/// The colours of the charts, validated for the site's dark surface: two series take blue and orange (they stay apart
/// for colour-blind readers too); a single series takes the site's amber. Status colours are only for states (errors,
/// outcomes) and always come with a label.
/// </summary>
public static class ChartColors
{
    public const string Accent = "var(--f3m-amber)";
    public const string Series1 = "var(--f3m-series-1)";
    public const string Series2 = "var(--f3m-series-2)";
    public const string Good = "var(--f3m-status-good)";
    public const string Warning = "var(--f3m-status-warning)";
    public const string Serious = "var(--f3m-status-serious)";
    public const string Critical = "var(--f3m-status-critical)";
    public const string Neutral = "var(--f3m-status-neutral)";
}

public static class ChartMath
{
    /// <summary>A round top for the value axis and the step between its lines (counts: never below 1).</summary>
    public static (double Top, double Step) NiceScale(double max, int targetSteps = 4)
    {
        if (max <= 0)
            return (targetSteps, 1);

        var rough = max / targetSteps;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var normalized = rough / magnitude;
        var step = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 2.5 ? 2.5 : normalized <= 5 ? 5 : 10) * magnitude;
        step = Math.Max(1, step);
        return (Math.Ceiling(max / step) * step, step);
    }

    /// <summary>1,284 / 12.9K / 4.2M.</summary>
    public static string Compact(double value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (value / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString("N0", CultureInfo.InvariantCulture)
    };

    public static string Px(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>A chart that draws at its container's real width (measured in the browser), so its text keeps its size.</summary>
public abstract class MeasuredChart : ComponentBase, IAsyncDisposable
{
    [Inject] protected IJSRuntime JS { get; set; } = default!;

    protected ElementReference Container;
    protected int Width = 560;

    private DotNetObjectReference<MeasuredChart>? self;
    private IJSObjectReference? watcher;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;

        try
        {
            self = DotNetObjectReference.Create(this);
            watcher = await JS.InvokeAsync<IJSObjectReference>("f3m.watchWidth", Container, self);
        }
        catch (JSException)
        {
            // Without measuring, the chart keeps its default width and is scaled to fit.
        }
    }

    [JSInvokable]
    public void OnWidth(int width)
    {
        if (Math.Abs(width - Width) < 2)
            return;
        Width = width;
        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (watcher is not null)
        {
            try
            {
                await watcher.InvokeVoidAsync("dispose");
                await watcher.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
            catch (JSException)
            {
            }
        }
        self?.Dispose();
        GC.SuppressFinalize(this);
    }
}
