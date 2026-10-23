using System.IO;
using System.Windows.Media;
using Wpf.Ui.Controls;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>One item as the lists show it. Rebuilt from each snapshot, so no change notifications.</summary>
public sealed class Row(Item item, DateTime nowUtc)
{
    static readonly Color[] Palette =
    [
        Color.FromRgb(0x2B, 0x7C, 0xD3), Color.FromRgb(0x8E, 0x44, 0xAD), Color.FromRgb(0x16, 0xA0, 0x85), Color.FromRgb(0xD3, 0x54, 0x00),
        Color.FromRgb(0xC0, 0x39, 0x2B), Color.FromRgb(0x27, 0xAE, 0x60), Color.FromRgb(0xB7, 0x95, 0x0B), Color.FromRgb(0x5D, 0x6D, 0x7E),
    ];
    static readonly Dictionary<Color, SolidColorBrush> Brushes = [];

    public Item Item { get; } = item;
    public string Key => Item.Key;
    public string Name => Item.Name;
    public string? Badge { get; } = Fmt.Badge(item, nowUtc);
    /// <summary>The badge's tooltip: for a duplicate, what made it one.</summary>
    public string? BadgeTip => Item.Suggestion?.Type == "duplicate" && Badge is not null
        ? string.Format(Loc.T("Badge.DuplicateTip"), Item.Suggestion.Subject, Fmt.Size(Item.Size)) : Badge;
    public bool Cleanable { get; } = Rules.ToClean(item, nowUtc);
    public bool IsRule => Item.Suggestion?.Type == "rule";
    public string FolderText => System.IO.Path.GetDirectoryName(Item.Path) ?? "";
    public string SiteText => Item.Site ?? Loc.T("Site.Unknown");
    public string SiteLetter => Item.Site is { } s ? char.ToUpperInvariant(s[0]).ToString() : "?";
    public string SizeText => Fmt.Size(Item.Size);
    public string AgeText { get; } = Fmt.Ago(item.CreatedUtc < item.FirstSeenUtc ? item.CreatedUtc : item.FirstSeenUtc, nowUtc);
    public string DateText => Item.CreatedUtc.ToLocalTime().ToString("d");
    public string TypeText => Loc.T("Kind." + Item.Kind);
    public string ReferrerText => Item.Src?.Referrer ?? "—";
    public string UrlText => Item.Src?.Url ?? "—";
    public string ZoneText => Item.Src?.Zone is { } z ? $"{z} · {Loc.T(z switch { 3 => "Zone.Internet", 4 => "Zone.Restricted", 1 => "Zone.Intranet", 2 => "Zone.Trusted", _ => "Zone.Local" })}" : "—";
    public string HashText => Item.Sha256 ?? "—";

    /// <summary>What screen readers and UI automation call the row.</summary>
    public override string ToString() => Badge is null ? Name : $"{Name}, {Badge}";

    public string Status { get; } =
        item.Kept ? Loc.T("Status.Kept")
        : item.TtlUtc is { } t ? string.Format(Loc.T("Status.Until"), t.ToLocalTime().ToString(t - nowUtc < TimeSpan.FromDays(1) ? "t" : "d"))
        : item.Suggestion is not null ? Loc.T("Status.Suggested")
        : "";

    public SolidColorBrush SiteBrush
    {
        get
        {
            var c = Item.Site is { } s ? Palette[(int)((uint)StableHash(s) % Palette.Length)] : Color.FromRgb(0x80, 0x80, 0x80);
            if (!Brushes.TryGetValue(c, out var b)) { b = new SolidColorBrush(c); b.Freeze(); Brushes[c] = b; }
            return b;
        }
    }

    public SymbolRegular Icon => Item.Kind switch
    {
        Kind.Folder => SymbolRegular.Folder24,
        Kind.Installer => SymbolRegular.AppGeneric24,
        Kind.Archive => SymbolRegular.FolderZip24,
        _ => Path.GetExtension(Item.Path).ToLowerInvariant() switch
        {
            ".pdf" => SymbolRegular.DocumentPdf24,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" => SymbolRegular.Image24,
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => SymbolRegular.Video24,
            ".mp3" or ".flac" or ".wav" or ".ogg" or ".m4a" => SymbolRegular.MusicNote224,
            _ => SymbolRegular.Document24,
        },
    };

    /// <summary>string.GetHashCode is randomized per process; colours must not change between runs.</summary>
    static int StableHash(string s)
    {
        var h = 17;
        foreach (var ch in s) h = unchecked(h * 31 + ch);
        return h;
    }
}
