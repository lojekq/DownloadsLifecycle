using System.Globalization;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>Sizes, counts and "2 days ago" in the UI language ("3.4 GB" / "3,4 ГБ", "1 файл / 2 файла / 5 файлов").</summary>
public static class Fmt
{
    static CultureInfo C => CultureInfo.CurrentUICulture;
    static bool Ru => C.TwoLetterISOLanguageName == "ru";

    public static string Size(long bytes)
    {
        string[] units = [Loc.T("Unit.B"), Loc.T("Unit.KB"), Loc.T("Unit.MB"), Loc.T("Unit.GB"), Loc.T("Unit.TB")];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} {units[0]}" : string.Format(C, v < 10 ? "{0:0.#} {1}" : "{0:0} {1}", v, units[u]);
    }

    /// <summary>Resource "one|few|many" (RU) or "one|other" (EN) with {0}.</summary>
    public static string Plural(string key, long n)
    {
        var forms = Loc.T(key).Split('|');
        int f;
        if (Ru) f = n % 10 == 1 && n % 100 != 11 ? 0 : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? 1 : 2;
        else f = n == 1 ? 0 : 1;
        return string.Format(C, forms[Math.Min(f, forms.Length - 1)], n);
    }

    public static string Files(long n) => Plural("N.Files", n);

    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var d = nowUtc - utc;
        return d.TotalMinutes < 1 ? Loc.T("Ago.Now")
            : d.TotalHours < 1 ? Plural("Ago.Minutes", (long)d.TotalMinutes)
            : d.TotalDays < 1 ? Plural("Ago.Hours", (long)d.TotalHours)
            : Plural("Ago.Days", (long)d.TotalDays);
    }

    /// <summary>"in 5 h", "in 3 days", "ended".</summary>
    public static string Until(DateTime utc, DateTime nowUtc)
    {
        var d = utc - nowUtc;
        return d <= TimeSpan.Zero ? Loc.T("Ttl.Ended")
            : d.TotalHours < 1 ? Plural("In.Minutes", Math.Max(1, (long)d.TotalMinutes))
            : d.TotalDays < 1 ? Plural("In.Hours", (long)d.TotalHours)
            : Plural("In.Days", (long)Math.Round(d.TotalDays));
    }

    public static string Command(TtlCommand c) => c.Op switch
    {
        "days" => Plural("Ttl.Days", c.Days),
        "date" => string.Format(Loc.T("Ttl.Until"), c.UntilLocal!.Value.ToString("d", C)),
        "keep" => Loc.T("Ttl.Keep"),
        _ => Loc.T("Ttl.Clear"),
    };

    /// <summary>The badge text of a suggestion or a lifetime; null when there is nothing to say.</summary>
    public static string? Badge(Item i, DateTime nowUtc)
    {
        if (i.Kept) return null;
        if (i.Suggestion is { } s)
            return s.Type switch
            {
                "installer" => string.Format(Loc.T("Badge.Installer." + s.Confidence), $"{s.Subject} {s.Version}".TrimEnd(), Loc.T("Why." + s.Why)),
                "archive" => s.Subject.Length == 0 ? string.Format(Loc.T("Badge.ArchiveHere"), s.Percent) : string.Format(Loc.T("Badge.Archive"), s.Subject + "\\", s.Percent),
                _ => string.Format(Loc.T("Badge.Duplicate"), s.Subject),
            };
        if (i.TtlUtc is { } t) return t <= nowUtc ? Loc.T("Badge.Expired") : string.Format(Loc.T("Badge.Expires"), Until(t, nowUtc));
        return null;
    }

    /// <summary>Journal reason "duplicate|a.pdf" → "Duplicate of a.pdf".</summary>
    public static string Reason(string reason)
    {
        if (reason.Length == 0) return "";
        var (code, arg) = reason.IndexOf('|') is var p and >= 0 ? (reason[..p], reason[(p + 1)..]) : (reason, "");
        if (TtlCommand.Parse(code) is { } c) return Command(c);
        var t = Loc.T("Reason." + code);
        return t.StartsWith('[') ? reason : string.Format(t, arg);
    }

    /// <summary>An exception for the log without its message: IO messages carry paths, and logs go to public issues (00-shared §2.3).</summary>
    public static string Err(Exception e) => $"{e.GetType().Name} (0x{e.HResult:X8}){Environment.NewLine}{e.StackTrace}";
}
