using CiFail.Core.Storage;

namespace CiFail.Server.Dashboard;

/// <summary>
/// Presentation helpers + the inlined theme for the C#-rendered dashboard (R28). The dashboard is
/// Blazor static SSR — every value a component renders is HTML-encoded by Razor — so the only raw
/// HTML these produce is the tiny, fixed <c>StatusTag</c> span (emitted via <c>MarkupString</c>).
/// The CSS lives here as a constant so it can be inlined in <c>App.razor</c> without a wwwroot /
/// static-web-assets pipeline (this is a hosted library, not a web app).
/// </summary>
internal static class Dash
{
    /// <summary>A local, human time like the old dashboard's <c>toLocaleString()</c>.</summary>
    public static string When(DateTimeOffset dt) => dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>A 0..1 ratio as a whole percent.</summary>
    public static string Pct(double x) => Math.Round(x * 100) + "%";

    /// <summary>A duration rendered compactly (d/h/m/s), or an em dash when null.</summary>
    public static string Dur(TimeSpan? span)
    {
        if (span is null) return "—";
        var secs = span.Value.TotalSeconds;
        if (secs >= 86400) return (secs / 86400).ToString("0.0") + "d";
        if (secs >= 3600) return (secs / 3600).ToString("0.0") + "h";
        if (secs >= 60) return Math.Round(secs / 60) + "m";
        return Math.Round(secs) + "s";
    }

    /// <summary>A fixed status pill — the only raw HTML the dashboard emits (no user data in it).</summary>
    public static string StatusTag(string status, string? source)
    {
        if (status == AnalysisStatus.Resolved)
        {
            var auto = source == ResolutionSource.Auto ? " auto" : "";
            return $"<span class=\"tag resolved\">✓{auto}</span>";
        }
        return "<span class=\"tag open\">open</span>";
    }

    /// <summary>The dashboard theme, ported from the old embedded page; inlined by App.razor.</summary>
    public const string Css = """
    :root {
      --bg: #0f1419; --panel: #1a2029; --border: #2a323d; --fg: #e6e9ee;
      --muted: #8b96a5; --accent: #4ea1ff; --green: #4caf7d; --amber: #d8a657; --red: #e06c75;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0; background: var(--bg); color: var(--fg);
      font: 14px/1.5 system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
    }
    a { color: var(--accent); text-decoration: none; }
    a:hover { text-decoration: underline; }
    header {
      display: flex; align-items: center; gap: 12px; flex-wrap: wrap;
      padding: 12px 16px; background: var(--panel); border-bottom: 1px solid var(--border);
    }
    header h1 { font-size: 16px; margin: 0; font-weight: 600; }
    header h1 span { color: var(--accent); }
    .grow { flex: 1; }
    .filters { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin: 0; }
    input, select, button {
      background: var(--bg); color: var(--fg); border: 1px solid var(--border);
      border-radius: 6px; padding: 6px 9px; font: inherit;
    }
    button { cursor: pointer; }
    button:hover { border-color: var(--accent); }
    button.primary { background: var(--accent); color: #04121f; border-color: var(--accent); font-weight: 600; }
    .layout { display: flex; gap: 0; height: calc(100vh - 58px); }
    .list { flex: 1; overflow: auto; }
    .detail {
      width: 42%; max-width: 560px; overflow: auto; padding: 16px;
      border-left: 1px solid var(--border); background: var(--panel);
    }
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: 8px 12px; border-bottom: 1px solid var(--border); white-space: nowrap; }
    th { position: sticky; top: 0; background: var(--panel); color: var(--muted); font-weight: 500; z-index: 1; }
    tbody tr:hover { background: #161c24; }
    tbody tr.sel { background: #1d2733; }
    td.wrap { white-space: normal; color: var(--muted); max-width: 360px; overflow: hidden; text-overflow: ellipsis; }
    .tag { display: inline-block; padding: 1px 7px; border-radius: 999px; font-size: 12px; border: 1px solid var(--border); }
    .open { color: var(--amber); border-color: var(--amber); }
    .resolved { color: var(--green); border-color: var(--green); }
    .muted { color: var(--muted); }
    .banner { padding: 10px 16px; background: #2a1f1f; color: var(--red); border-bottom: 1px solid var(--border); }
    .empty { padding: 40px 16px; color: var(--muted); text-align: center; }
    .pager { display: flex; gap: 14px; align-items: center; justify-content: center;
             padding: 12px 16px; border-top: 1px solid var(--line); font-size: 13px; }
    .pager a { color: var(--accent); text-decoration: none; }
    .pager a:hover { text-decoration: underline; }
    pre { white-space: pre-wrap; word-break: break-word; background: var(--bg); border: 1px solid var(--border); border-radius: 6px; padding: 10px; margin: 6px 0 14px; }
    .detail h2 { font-size: 15px; margin: 0 0 4px; }
    .detail .resolve { display: flex; gap: 8px; margin-top: 10px; }
    .detail .resolve input[name=note] { flex: 1; }
    .kv { display: grid; grid-template-columns: 120px 1fr; gap: 4px 10px; margin: 10px 0 14px; }
    .kv div:nth-child(odd) { color: var(--muted); }
    code { color: var(--accent); }
    .ph { color: var(--muted); padding: 40px 16px; text-align: center; }
    .trends { display: flex; gap: 12px; flex-wrap: wrap; padding: 12px 16px; background: var(--panel); border-bottom: 1px solid var(--border); }
    .card { background: var(--bg); border: 1px solid var(--border); border-radius: 8px; padding: 8px 12px; min-width: 86px; }
    .card .n { font-size: 20px; font-weight: 600; }
    .card .n.warn { color: var(--amber); }
    .card .l { color: var(--muted); font-size: 12px; }
    .card.wide { flex: 1; min-width: 220px; }
    .card.wide ul { margin: 6px 0 0; padding: 0; list-style: none; }
    .card.wide li { display: flex; justify-content: space-between; gap: 10px; }
    .card.wide li + li { margin-top: 2px; }
    .card.wide li span { color: var(--muted); }
    .card.wide li code { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

    /* Failures-over-time sparkline (R32). Inline SVG, no script: the viewBox is fixed and
       preserveAspectRatio=none stretches it to whatever width the card gets. */
    .card.spark { min-width: 260px; }
    .sparkline { display: block; width: 100%; height: 40px; margin-top: 6px; }
    .sparkline polyline {
      fill: none; stroke: var(--accent); stroke-width: 1;
      /* The viewBox is stretched non-uniformly, which would smear the stroke width with it. */
      vector-effect: non-scaling-stroke;
      stroke-linejoin: round; stroke-linecap: round;
    }
    .spark-axis { display: flex; justify-content: space-between; color: var(--muted); font-size: 11px; }

    /* Cluster drill-down (R32): <details>/<summary>, so it works with scripting disabled. */
    .cluster details summary {
      display: flex; justify-content: space-between; gap: 10px;
      cursor: pointer; list-style: none;
    }
    .cluster details summary::-webkit-details-marker { display: none; }
    .cluster details summary::before { content: "▸ "; color: var(--muted); }
    .cluster details[open] summary::before { content: "▾ "; }
    .cluster-body { margin: 4px 0 8px 14px; font-size: 12px; }
    .cluster-members { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 4px; }
    .login { max-width: 360px; margin: 12vh auto; padding: 0 16px; }
    .login h1 { font-size: 18px; font-weight: 600; }
    .login h1 span { color: var(--accent); }
    .loginbox { display: flex; gap: 8px; margin-top: 12px; }
    .loginbox input { flex: 1; }
    """;
}
