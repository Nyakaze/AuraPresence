using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Aura.Presence.Stremio;

public sealed partial record PlayerTitle(string Show, string? EpisodeName, int Season, int Episode)
{
    private const string Separator = " - ";

    public static PlayerTitle? Parse(string? text)
    {
        if (text is null || SeasonEpisode().Match(text) is not { Success: true } match)
            return null;

        var head = text[..match.Index].Trim();
        var dash = head.IndexOf(Separator, StringComparison.Ordinal);
        var name = dash < 0 ? null : head[(dash + Separator.Length)..].Trim();

        return new PlayerTitle(dash < 0 ? head : head[..dash].Trim(), string.IsNullOrEmpty(name) ? null : name,
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"\((\d{1,3})x(\d{1,4})\)\s*$")]
    private static partial Regex SeasonEpisode();
}

public static class StremioPlayer
{
    private static readonly Condition TextElements =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);

    public static PlayerTitle? Read(string? expectedShow)
    {
        var window = WindowHandle();

        if (window == IntPtr.Zero)
            return null;

        try
        {
            var titles = AutomationElement.FromHandle(window)
                .FindAll(TreeScope.Descendants, TextElements)
                .Cast<AutomationElement>()
                .Select(element => PlayerTitle.Parse(element.Current.Name))
                .OfType<PlayerTitle>()
                .ToList();

            return titles.FirstOrDefault(title => expectedShow is not null && ReleaseName.TitlesMatch(expectedShow, title.Show))
                   ?? (titles.Count == 1 ? titles[0] : null);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IntPtr WindowHandle()
    {
        var running = Process.GetProcessesByName("stremio-shell-ng");

        try
        {
            return running.Select(process => process.MainWindowHandle).FirstOrDefault(handle => handle != IntPtr.Zero);
        }
        finally
        {
            foreach (var process in running)
                process.Dispose();
        }
    }
}
