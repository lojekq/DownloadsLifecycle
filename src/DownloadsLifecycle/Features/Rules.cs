using System.Globalization;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Shell
{
    // The app's settings fields (same partial class as Shell/Settings.cs)
    public sealed partial class Settings
    {
        public bool AutoExpired { get; set; }                 // rule 1: lifetime over → Recycle Bin; off by default
        public bool AutoDuplicates { get; set; }              // rule 2: exact "(1)" / " - Copy" duplicates → Recycle Bin; off by default
        public int GraceHours { get; set; } = 24;             // rules never touch younger files
        public long AutoMaxBytes { get; set; } = 4L << 30;    // rules never touch bigger files
        public int MassLimit { get; set; } = 20;              // more expiring at once → ask in the inbox
        public bool Reminder { get; set; } = true;
        public string ReminderTime { get; set; } = "19:00";
        public string? RemindedOn { get; set; }               // local date, yyyy-MM-dd
        public bool ContextMenu { get; set; } = true;         // registered by itself only when installed (00-shared §2.4)
        public List<string> RecentFolders { get; set; } = [];
        public List<string> ExtraFolders { get; set; } = [];      // v1.0: watched like Downloads (Telegram Desktop…)
        public List<Features.SiteRule> SiteRules { get; set; } = []; // v1.0: first match wins
        public bool AutoInstallers { get; set; }              // v1.0: exact MSI matches → Recycle Bin after ExactDays; off by default
        public bool AutoArchives { get; set; }                // v1.0: archives extracted at 100 % → Recycle Bin after ExactDays; off by default
        public int ExactDays { get; set; } = 3;
        public bool WelcomeDone { get; set; }
    }
}

namespace DownloadsLifecycle.Features
{
    /// <summary>
    /// v1.0 site rule: a domain (with its subdomains) and optional extensions → move to a folder, a lifetime of N days,
    /// or "offer for cleanup". Auto applies it by itself; otherwise it waits in the inbox with an Apply button. "clean" is never automatic.
    /// </summary>
    public sealed class SiteRule
    {
        public string Domain { get; set; } = "";
        public string Extensions { get; set; } = "";     // "exe, msi" — empty: any file
        public string Action { get; set; } = "ttl";       // ttl | move | clean
        public string? Folder { get; set; }
        public int Days { get; set; } = 7;
        public bool Auto { get; set; }

        static string Norm(string d) => d.Trim().TrimStart('.').ToLowerInvariant();

        public bool Matches(Item i)
        {
            var domain = Norm(Domain);
            if (domain.Length == 0 || i.IsDir || i.Src is null) return false;
            var hosts = new[] { ZoneReader.Host(i.Src.Referrer), ZoneReader.Host(i.Src.Url) }.OfType<string>();
            if (!hosts.Any(h => h == domain || h.EndsWith("." + domain, StringComparison.Ordinal))) return false;
            var exts = Extensions.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(e => "." + e.Trim().TrimStart('.').ToLowerInvariant()).ToList();
            return exts.Count == 0 || exts.Any(e => i.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)); // ".tar.gz" works too
        }

        public static SiteRule? First(IEnumerable<SiteRule> rules, Item i) => rules.FirstOrDefault(r => r.Matches(i));
    }

    /// <summary>A lifetime command: from the context menu (--ttl 7d "path") or the window.</summary>
    public sealed record TtlCommand(string Op, int Days = 0, DateTime? UntilLocal = null) // Op: days | date | keep | clear
    {
        /// <summary>"1d" "7d" "30d" "keep" "clear" "2026-11-01"; null for anything else.</summary>
        public static TtlCommand? Parse(string s) => s switch
        {
            "keep" => new("keep"),
            "clear" => new("clear"),
            _ when s.EndsWith('d') && int.TryParse(s[..^1], out var d) && d is > 0 and <= 3650 => new("days", d),
            _ when DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) => new("date", UntilLocal: t),
            _ => null,
        };

        public override string ToString() => Op switch { "days" => $"{Days}d", "date" => $"{UntilLocal:yyyy-MM-dd}", _ => Op };

        /// <summary>A day is a minute under --ttl-minutes. A date lasts to the end of that local day.</summary>
        public Item Apply(Item i, DateTime nowUtc, TimeSpan day) => Op switch
        {
            "days" => i with { TtlUtc = nowUtc + Days * day, Kept = false },
            "date" => i with { TtlUtc = UntilLocal!.Value.Date.AddDays(1).ToUniversalTime(), Kept = false },
            "keep" => i with { Kept = true, TtlUtc = null, Suggestion = null },
            _ => i with { TtlUtc = null },
        };
    }

    /// <summary>Who is in the inbox, what counts as "to clean", what the opt-in rules may do. Pure, tested.</summary>
    public static class Rules
    {
        public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(10); // only suggestions for 10 min after start

        public static bool Expired(Item i, DateTime nowUtc) => !i.Kept && i.TtlUtc <= nowUtc;

        /// <summary>Offered for cleanup: a detector's suggestion or an ended lifetime.</summary>
        public static bool ToClean(Item i, DateTime nowUtc) => !i.Kept && (i.Suggestion is { Type: not "rule" } || Expired(i, nowUtc));

        /// <summary>New since the last triage, offered for cleanup, or ending within a day.</summary>
        public static bool InInbox(Item i, DateTime lastTriageUtc, DateTime nowUtc, TimeSpan day) =>
            !i.Kept && (ToClean(i, nowUtc) || i.TtlUtc <= nowUtc + day || (i.TtlUtc is null && i.FirstSeenUtc > lastTriageUtc));

        /// <summary>
        /// What the rules recycle now, before the live safety check. Nothing in the first 10 min, while paused,
        /// or for ended lifetimes when more than MassLimit ended at once (those wait in the inbox for a confirmation).
        /// A duplicate goes only if its name is a copy of the kept file's and that file is still here with the same hash.
        /// </summary>
        public static List<(Item Item, string Rule)> AutoPlan(IReadOnlyList<Item> items, DateTime nowUtc, DateTime startedUtc, bool paused, Settings s)
        {
            var plan = new List<(Item, string)>();
            if (paused || nowUtc - startedUtc < StartDelay) return plan;
            bool Small(Item i) => !i.IsDir && i.Size <= s.AutoMaxBytes;
            if (s.AutoExpired)
            {
                var expired = items.Where(i => Expired(i, nowUtc)).ToList();
                if (expired.Count <= s.MassLimit) plan.AddRange(expired.Where(Small).Select(i => (i, "rule:expired")));
            }
            if (s.AutoDuplicates)
                foreach (var i in items.Where(i => !i.Kept && i.Suggestion?.Type == "duplicate" && Small(i) && nowUtc - i.CreatedUtc >= TimeSpan.FromHours(s.GraceHours)))
                    if (CopySuffix.Original(i.Name) is { } orig && string.Equals(orig, i.Suggestion!.Subject, StringComparison.OrdinalIgnoreCase)
                        && items.Any(k => k.Key != i.Key && k.Name == i.Suggestion.Subject && k.Sha256 == i.Sha256 && k.Sha256 is not null)
                        && plan.All(p => p.Item1.Key != i.Key))
                        plan.Add((i, "rule:duplicate"));
            bool Settled(Item i) => i.Suggestion?.FoundUtc is { } f && nowUtc - f >= TimeSpan.FromDays(s.ExactDays) && Small(i) && !i.Kept
                && nowUtc - i.CreatedUtc >= TimeSpan.FromHours(s.GraceHours) && plan.All(p => p.Item1.Key != i.Key);
            if (s.AutoInstallers) // MSI by ProductCode only: an exe match is never exact enough to delete by itself
                plan.AddRange(items.Where(i => i.Suggestion is { Type: "installer", Confidence: "exact", Why: "msi" } && Settled(i)).Select(i => (i, "rule:installer")));
            if (s.AutoArchives)
                plan.AddRange(items.Where(i => i.Suggestion is { Type: "archive", Percent: 100 } && Settled(i)).Select(i => (i, "rule:archive")));
            return plan;
        }

        /// <summary>v1.0 inbox line: files let go into the Recycle Bin this calendar month (local time), by you or a rule, minus the restored ones.</summary>
        public static (int Count, long Bytes) FreedThisMonth(IEnumerable<JournalEntry> journal, DateTime nowLocal)
        {
            var month = journal.Where(e => e.T.ToLocalTime() is var t && t.Year == nowLocal.Year && t.Month == nowLocal.Month).ToList();
            var restored = month.Where(e => e.Op == "restore").Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var gone = month.Where(e => e.Op == "recycle" && !restored.Contains(e.Path)).ToList();
            return (gone.Count, gone.Sum(e => e.Size));
        }

        /// <summary>Journal reason for an item: "duplicate|a.pdf", "installer|Git 2.47.0", "archive|blender", "expired" or "".</summary>
        public static string Reason(Item i, DateTime nowUtc) => i.Suggestion switch
        {
            { Type: "duplicate" } s => $"duplicate|{s.Subject}",
            { Type: "installer" } s => $"installer|{s.Subject} {s.Version}".TrimEnd(),
            { Type: "archive" } s => $"archive|{s.Subject}",
            { Type: "site" } s => $"site|{s.Subject}",
            _ when Expired(i, nowUtc) => "expired",
            _ => "",
        };
    }
}
