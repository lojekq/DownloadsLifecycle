using System.Text.Json.Serialization;

namespace DownloadsLifecycle.Features;

public enum Kind { Other, Installer, Archive, Folder }

/// <summary>Where a file came from, taken once from Zone.Identifier when the file first appeared (survives "Unblock").</summary>
public sealed record Source(string? Referrer, string? Url, int? Zone)
{
    /// <summary>The site people recognise: the referrer's host (github.com), else the download host (objects.githubusercontent.com).</summary>
    [JsonIgnore] public string? Site => ZoneReader.Host(Referrer) ?? ZoneReader.Host(Url);
}

/// <summary>
/// An offer. Type: installer | archive | duplicate | site (a site rule offers it for cleanup) | rule (a site rule's move or lifetime waits for Apply).
/// Confidence: exact | likely | other (another version installed). Subject: product name, extracted folder, the kept duplicate's name, or the rule's domain.
/// Arg: a rule's folder or days. FoundUtc: when this offer first appeared (exact-match rules wait N days from it).
/// Kept structured so the UI can say it in either language.
/// </summary>
public sealed record Suggestion(string Type, string Confidence, string Subject, string? Version = null, int Percent = 0, string? Why = null,
    string? Arg = null, DateTime? FoundUtc = null)
{
    /// <summary>The same offer, whenever it was found.</summary>
    public bool Same(Suggestion? o) => o is not null && this with { FoundUtc = null } == o with { FoundUtc = null };
}

/// <summary>One top-level entry of the watched folder. Immutable: the worker swaps records, the UI gets snapshots.</summary>
public sealed record Item
{
    public required string Key { get; init; }       // VolumeSerial-FileId
    public required string Path { get; init; }
    public bool IsDir { get; init; }
    public long Size { get; init; }
    public DateTime MtimeUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime FirstSeenUtc { get; init; }
    public Kind Kind { get; init; }
    public Source? Src { get; init; }
    public string? Sha256 { get; init; }
    public string? HashStamp { get; init; }         // size:mtime the hash belongs to
    public bool Kept { get; init; }
    public DateTime? TtlUtc { get; init; }
    public bool Ruled { get; init; }                // a site rule was applied (or handled) once: never again
    public Suggestion? Suggestion { get; init; }

    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public string? Site => Src?.Site;
    [JsonIgnore] public string Stamp => $"{Size}:{MtimeUtc.Ticks}";

    public static Kind KindOf(string path, bool dir) => dir ? Kind.Folder : System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".exe" or ".msi" => Kind.Installer,
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".tgz" or ".xz" or ".bz2" or ".zst" => Kind.Archive,
        _ => Kind.Other,
    };
}

/// <summary>What the UI sees: an immutable copy of the index.</summary>
public sealed record Snapshot(IReadOnlyList<Item> Items, DateTime LastTriageUtc)
{
    public static readonly Snapshot Empty = new([], DateTime.MinValue);
}

/// <summary>journal.jsonl line. Op: recycle | move | ttl | keep | gone. By: user | rule:expired | rule:duplicate.</summary>
public sealed record JournalEntry(DateTime T, string Op, string Path, long Size, string Reason, string By, string? To = null, string? Sha256 = null);
