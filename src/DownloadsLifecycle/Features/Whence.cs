using System.IO;
using System.Windows;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>
/// v1.0 Explorer "Where is it from?": --whence "path" shows the site, both addresses and the date of one file, from its
/// Mark-of-the-Web or, if that is gone ("Unblock"), from the index. Works without the tray; copies the page address on Yes.
/// </summary>
public static class Whence
{
    public static int Verb(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0])) return 2;
        Settings.Load();
        Loc.Apply(Settings.Current.Language);
        var path = Path.GetFullPath(args[0]);
        var src = ZoneReader.Read(path) ?? Index.Peek(path)?.Src;
        var copy = src?.Referrer ?? src?.Url;
        var text = Text(Path.GetFileName(path), src, File.GetCreationTime(path)) + (copy is null ? "" : "\n\n" + Loc.T("Whence.Copy"));
        var answer = MessageBox.Show(text, Loc.T("Whence.Title"), copy is null ? MessageBoxButton.OK : MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes) Clipboard.SetText(copy!);
        return 0;
    }

    internal static string Text(string name, Source? src, DateTime createdLocal)
    {
        var date = createdLocal.ToString("g");
        if (src?.Site is null && src?.Referrer is null && src?.Url is null) return $"{name}\n\n{Loc.T("Whence.Unknown")}\n{string.Format(Loc.T("Whence.Created"), date)}";
        var lines = new List<string> { name, "" };
        if (src.Site is { } site) lines.Add(string.Format(Loc.T("Whence.Site"), site));
        if (src.Referrer is { } page) lines.Add(string.Format(Loc.T("Whence.Page"), page));
        if (src.Url is { } url) lines.Add(string.Format(Loc.T("Whence.Url"), url));
        lines.Add(string.Format(Loc.T("Whence.Downloaded"), date));
        return string.Join('\n', lines);
    }
}
