using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>
/// The index: items by key plus the last triage time. Only the worker touches an instance (no locks); the UI gets <see cref="Snapshot"/>s.
/// Stored in %APPDATA%\DownloadsLifecycle\state.json; nothing is ever written into the files themselves (no ADS, 08 §5).
/// </summary>
public sealed class Index
{
    public Dictionary<string, Item> Items { get; } = [];
    public DateTime LastTriageUtc { get; set; }
    public bool Dirty { get; set; }

    public void Set(Item i)
    {
        if (Items.TryGetValue(i.Key, out var old) && old == i) return;
        Items[i.Key] = i;
        Dirty = true;
    }

    public void Remove(string key) { if (Items.Remove(key)) Dirty = true; }

    public Item? ByPath(string path) => Items.Values.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    public Snapshot Snapshot() => new([.. Items.Values], LastTriageUtc);

    public static Index From(Snapshot s)
    {
        var x = new Index { LastTriageUtc = s.LastTriageUtc };
        foreach (var i in s.Items) x.Items[i.Key] = i;
        return x;
    }

    // ---- state.json ----

    sealed record StateFile(int Version, DateTime LastTriageUtc, List<Item> Items);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string StatePath => Path.Combine(AppInfo.DataDir, "state.json");
    public static string JournalPath => Path.Combine(AppInfo.DataDir, "journal.jsonl");

    /// <summary>Corrupt → state.bad-&lt;time&gt;.json, then .bak, then null (an empty index: only sources of already-unblocked files are lost).</summary>
    public static Snapshot? Load(string path)
    {
        var f = AtomicFile.Read(path, s => JsonSerializer.Deserialize<StateFile>(s, Json));
        return f is null ? null : new(f.Items.Where(i => i.Key is not null && i.Path is not null).ToList(), f.LastTriageUtc);
    }

    /// <summary>Atomic: .tmp → flush → File.Replace with .bak, so a kill during the write leaves a readable file.</summary>
    public static void Save(string path, Snapshot s) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new StateFile(1, s.LastTriageUtc, [.. s.Items]), Json));

    // ---- journal.jsonl: append only, lines older than 90 days dropped at start ----

    static readonly Lock Gate = new();
    public static event Action? Journaled;

    public static void Append(JournalEntry e)
    {
        if (Args.Current.Demo) Demo.DemoData.Journal.Insert(0, e);
        else
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppInfo.DataDir);
                    File.AppendAllText(JournalPath, JsonSerializer.Serialize(e, Json) + "\n");
                }
            }
            catch (Exception x) { Log.Error("journal append", x); }
        Journaled?.Invoke();
    }

    /// <summary>Newest first; unreadable lines are skipped.</summary>
    public static List<JournalEntry> ReadJournal()
    {
        if (Args.Current.Demo) return [.. Demo.DemoData.Journal];
        lock (Gate)
        {
            if (!File.Exists(JournalPath)) return [];
            var list = File.ReadLines(JournalPath).Select(ParseLine).OfType<JournalEntry>().ToList();
            list.Reverse();
            return list;
        }
    }

    public static void RotateJournal(DateTime nowUtc)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(JournalPath)) return;
                var lines = File.ReadAllLines(JournalPath);
                var keep = lines.Where(l => ParseLine(l) is { } e && e.T > nowUtc.AddDays(-90)).ToList();
                if (keep.Count < lines.Length) AtomicFile.WriteAllText(JournalPath, string.Concat(keep.Select(l => l + "\n")));
            }
        }
        catch (Exception e) { Log.Error("journal rotation", e); }
    }

    static JournalEntry? ParseLine(string line)
    {
        try { return JsonSerializer.Deserialize<JournalEntry>(line, Json); }
        catch (JsonException) { return null; }
    }
}
