using System.Globalization;
using System.IO;
using DownloadsLifecycle.Features;
using Index = DownloadsLifecycle.Features.Index;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Tests;

[TestClass]
public class SafetyTests
{
    static readonly Facts Fine = new(Exists: true, Inside: true, SameFile: true, Unchanged: true, Kept: false, FixedDrive: true,
        LongPath: false, InUse: false, Size: 10 << 20, Age: TimeSpan.FromDays(3));
    const long Max = 4L << 30;

    static Verdict Manual(Facts f) => SafetyGate.Check(f, auto: false, 24, Max);
    static Verdict Auto(Facts f) => SafetyGate.Check(f, auto: true, 24, Max);

    [TestMethod]
    public void Gate()
    {
        Assert.AreEqual(Verdict.Ok, Auto(Fine));
        Assert.AreEqual(Verdict.Gone, Auto(Fine with { Exists = false }));
        Assert.AreEqual(Verdict.Outside, Manual(Fine with { Inside = false }));
        Assert.AreEqual(Verdict.Changed, Manual(Fine with { SameFile = false }));     // FileId reused, same name replaced
        Assert.AreEqual(Verdict.Changed, Manual(Fine with { Unchanged = false }));    // size or mtime moved since the decision
        Assert.AreEqual(Verdict.NotFixed, Manual(Fine with { FixedDrive = false }));  // USB or network: no Recycle Bin guarantee
        Assert.AreEqual(Verdict.LongPath, Manual(Fine with { LongPath = true }));
        Assert.AreEqual(Verdict.InUse, Auto(Fine with { InUse = true }));
        // Kept, young and big only stop the rules; the user may still let go by hand
        Assert.AreEqual(Verdict.Kept, Auto(Fine with { Kept = true }));
        Assert.AreEqual(Verdict.Ok, Manual(Fine with { Kept = true }));
        Assert.AreEqual(Verdict.TooYoung, Auto(Fine with { Age = TimeSpan.FromHours(23) }));
        Assert.AreEqual(Verdict.Ok, Manual(Fine with { Age = TimeSpan.FromMinutes(1) }));
        Assert.AreEqual(Verdict.TooBig, Auto(Fine with { Size = 5L << 30 }));
        Assert.AreEqual(Verdict.Ok, Manual(Fine with { Size = 5L << 30 }));
    }

    [TestMethod]
    public void PFromIsAbsoluteWithTwoNulls()
    {
        var p = Recycler.PFrom(@"C:\Demo\Downloads\dl-test-a.txt");
        Assert.AreEqual(@"C:\Demo\Downloads\dl-test-a.txt" + "\0\0", p);
        Assert.AreEqual(2, p.Count(c => c == '\0'));
        Assert.ThrowsExactly<ArgumentException>(() => Recycler.PFrom(@"dl-test-a.txt"));
        Assert.ThrowsExactly<ArgumentException>(() => Recycler.PFrom(@"C:dl-test-a.txt"));
        Assert.ThrowsExactly<ArgumentException>(() => Recycler.PFrom(@"C:\Demo\Downloads\*.txt"));
        Assert.ThrowsExactly<ArgumentException>(() => Recycler.PFrom(@"C:\Demo\Downloads\dl-test-?.txt"));
        Assert.ThrowsExactly<ArgumentException>(() => Recycler.PFrom("C:\\Demo\\a\0C:\\Windows"));
    }

    [TestMethod]
    public void UniqueNameNeverOverwrites()
    {
        var dir = Directory.CreateTempSubdirectory("dl-test-").FullName;
        Assert.AreEqual(Path.Combine(dir, "a.pdf"), Mover.UniqueName(dir, "a.pdf"));
        File.WriteAllText(Path.Combine(dir, "a.pdf"), "");
        Assert.AreEqual(Path.Combine(dir, "a (2).pdf"), Mover.UniqueName(dir, "a.pdf"));
        File.WriteAllText(Path.Combine(dir, "a (2).pdf"), "");
        Assert.AreEqual(Path.Combine(dir, "a (3).pdf"), Mover.UniqueName(dir, "a.pdf"));
        Directory.CreateDirectory(Path.Combine(dir, "folder"));
        Assert.AreEqual(Path.Combine(dir, "folder (2)"), Mover.UniqueName(dir, "folder"));
        Directory.Delete(dir, true);
    }

    [TestMethod]
    public void MoveAcrossFolderKeepsContents()
    {
        var a = Directory.CreateTempSubdirectory("dl-test-").FullName;
        var b = Directory.CreateTempSubdirectory("dl-test-").FullName;
        var src = Path.Combine(a, "dl-test-m.txt");
        File.WriteAllText(src, "move me");
        File.WriteAllText(Path.Combine(b, "dl-test-m.txt"), "already here");
        var to = Mover.Move(src, b, isDir: false, owner: 0);
        Assert.AreEqual(Path.Combine(b, "dl-test-m (2).txt"), to);
        Assert.AreEqual("move me", File.ReadAllText(to));
        Assert.AreEqual("already here", File.ReadAllText(Path.Combine(b, "dl-test-m.txt")));
        Assert.IsFalse(File.Exists(src));
        Directory.Delete(a, true);
        Directory.Delete(b, true);
    }

    // ---- lifetime ----

    static Item T(string key, DateTime? ttl, bool kept = false, long size = 1 << 20) => new()
    {
        Key = key, Path = @"C:\Demo\Downloads\" + key, Size = size, TtlUtc = ttl, Kept = kept, CreatedUtc = DateTime.UtcNow.AddDays(-10),
    };

    [TestMethod]
    public void TtlCommands()
    {
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var i = T("a", null);
        Assert.AreEqual(now.AddDays(7), TtlCommand.Parse("7d")!.Apply(i, now, TimeSpan.FromDays(1)).TtlUtc);
        Assert.AreEqual(now.AddMinutes(7), TtlCommand.Parse("7d")!.Apply(i, now, TimeSpan.FromMinutes(1)).TtlUtc); // --ttl-minutes
        var kept = TtlCommand.Parse("keep")!.Apply(i with { TtlUtc = now, Suggestion = new("duplicate", "exact", "a") }, now, TimeSpan.FromDays(1));
        Assert.IsTrue(kept.Kept);
        Assert.IsNull(kept.TtlUtc);
        Assert.IsNull(kept.Suggestion);
        Assert.IsNull(TtlCommand.Parse("clear")!.Apply(i with { TtlUtc = now }, now, TimeSpan.FromDays(1)).TtlUtc);
        var date = TtlCommand.Parse("2026-11-01")!.Apply(i, now, TimeSpan.FromDays(1)).TtlUtc!.Value;
        Assert.AreEqual(new DateTime(2026, 11, 2), date.ToLocalTime().Date); // the whole chosen day is kept
        Assert.IsNull(TtlCommand.Parse("7"));
        Assert.IsNull(TtlCommand.Parse("-1d"));
        Assert.IsNull(TtlCommand.Parse("rm -rf"));
    }

    [TestMethod]
    public void ExpiryIsUtcAndMassExpiryAsks()
    {
        var now = DateTime.UtcNow;
        var s = new Settings { AutoExpired = true };
        var started = now.AddHours(-1);
        Assert.IsTrue(Rules.Expired(T("a", now.AddSeconds(-1)), now));
        Assert.IsFalse(Rules.Expired(T("a", now.AddSeconds(1)), now));
        Assert.IsFalse(Rules.Expired(T("a", now.AddDays(-1), kept: true), now));

        var few = Enumerable.Range(0, 20).Select(n => T($"f{n}", now.AddMinutes(-n - 1))).ToList();
        Assert.HasCount(20, Rules.AutoPlan(few, now, started, paused: false, s));
        var many = Enumerable.Range(0, 21).Select(n => T($"f{n}", now.AddMinutes(-n - 1))).ToList();
        Assert.HasCount(0, Rules.AutoPlan(many, now, started, paused: false, s)); // > 20 at once: they wait in the inbox
        Assert.IsTrue(many.All(i => Rules.InInbox(i, now, now, TimeSpan.FromDays(1))));

        Assert.HasCount(0, Rules.AutoPlan(few, now, now.AddMinutes(-5), paused: false, s)); // first 10 min after start: suggestions only
        Assert.HasCount(0, Rules.AutoPlan(few, now, started, paused: true, s));
        Assert.HasCount(0, Rules.AutoPlan(few, now, started, paused: false, new Settings()));    // rules are off by default
        Assert.HasCount(0, Rules.AutoPlan([T("big", now.AddMinutes(-1), size: 5L << 30)], now, started, false, s)); // 5 GB waits
    }

    [TestMethod]
    public void DuplicateRuleKeepsTheOriginal()
    {
        var now = DateTime.UtcNow;
        var s = new Settings { AutoDuplicates = true };
        Item D(string name, string? subject, double days = 2) => new()
        {
            Key = name, Path = @"C:\Demo\Downloads\" + name, Size = 100, Sha256 = "h", CreatedUtc = now.AddDays(-days),
            Suggestion = subject is null ? null : new("duplicate", "exact", subject),
        };
        var orig = D("a.pdf", null);
        var copy = D("a (1).pdf", "a.pdf");
        Assert.HasCount(1, Rules.AutoPlan([orig, copy], now, now.AddHours(-1), false, s));
        Assert.HasCount(0, Rules.AutoPlan([copy], now, now.AddHours(-1), false, s));                          // original gone: nothing
        Assert.HasCount(0, Rules.AutoPlan([orig, D("b.pdf", "a.pdf")], now, now.AddHours(-1), false, s));     // not a copy name: suggestion only
        Assert.HasCount(0, Rules.AutoPlan([orig, D("a (1).pdf", "a.pdf", days: 0.5)], now, now.AddHours(-1), false, s)); // younger than 24 h
    }

    [TestMethod]
    public void LifetimeFollowsRenameAndEndsOutsideTheFolder()
    {
        var dir = Directory.CreateTempSubdirectory("dl-test-").FullName;
        var outside = Directory.CreateTempSubdirectory("dl-test-").FullName;
        var a = Path.Combine(dir, "dl-test-ttl.txt");
        File.WriteAllText(a, "x");
        var index = new Index();
        var scan = new Scanner(index, dir);
        var key = scan.Upsert(a, force: true)!;
        index.Set(index.Items[key] with { TtlUtc = DateTime.UtcNow.AddDays(7) });

        var b = Path.Combine(dir, "dl-test-ttl-renamed.txt");
        File.Move(a, b);
        var later = DateTime.UtcNow.AddSeconds(10);
        scan.Touch(a, DateTime.UtcNow);
        scan.Touch(b, DateTime.UtcNow);
        scan.ProcessPending(later);
        Assert.HasCount(1, index.Items);
        Assert.AreEqual(b, index.Items[key].Path);
        Assert.IsNotNull(index.Items[key].TtlUtc); // same FileId: the lifetime stays

        var c = Path.Combine(outside, "dl-test-ttl-renamed.txt");
        File.Move(b, c);
        scan.Touch(b, DateTime.UtcNow);
        scan.ProcessPending(DateTime.UtcNow.AddSeconds(10));
        Assert.HasCount(0, index.Items); // carried away: no lifetime left anywhere
        Assert.IsNull(scan.Upsert(c, force: true)); // and it isn't picked up outside the folder
        Directory.Delete(dir, true);
        Directory.Delete(outside, true);
    }

    [TestMethod]
    public void ScannerSkipsPartialHiddenAndNotReady()
    {
        var dir = Directory.CreateTempSubdirectory("dl-test-").FullName;
        var index = new Index();
        var scan = new Scanner(index, dir);
        File.WriteAllText(Path.Combine(dir, "dl-test-x.crdownload"), "x");
        File.WriteAllText(Path.Combine(dir, "Unconfirmed 123.crdownload"), "x");
        File.WriteAllText(Path.Combine(dir, "dl-test-y.part"), "x");
        var hidden = Path.Combine(dir, "desktop.ini");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        File.WriteAllText(Path.Combine(dir, "dl-test-fresh.txt"), "just written"); // mtime now: not ready for 5 s
        scan.Reconcile();
        Assert.HasCount(0, index.Items);
        File.SetLastWriteTimeUtc(Path.Combine(dir, "dl-test-fresh.txt"), DateTime.UtcNow.AddMinutes(-1));
        scan.Reconcile();
        Assert.HasCount(1, index.Items);
        Directory.Delete(dir, true);
    }

    [TestMethod]
    public void StateSurvivesAndCorruptFallsBack()
    {
        var dir = Directory.CreateTempSubdirectory("dl-test-").FullName;
        var path = Path.Combine(dir, "state.json");
        var one = new Snapshot([T("a", DateTime.UtcNow) with { Src = new("https://github.com/x", "https://objects.githubusercontent.com/y", 3) }], DateTime.UtcNow);
        Index.Save(path, one);
        Index.Save(path, new([.. one.Items, T("b", null)], DateTime.UtcNow)); // the first becomes .bak
        Assert.HasCount(2, Index.Load(path)!.Items);
        Assert.AreEqual("github.com", Index.Load(path)!.Items[0].Site);
        File.WriteAllText(path, "{ \"version\": 1, \"items\": [ {");  // killed mid-write by something other than us
        Assert.HasCount(1, Index.Load(path)!.Items);                   // .bak
        Assert.HasCount(1, Directory.GetFiles(dir, "state.bad-*.json"));
        Directory.Delete(dir, true);
    }

    [TestMethod]
    public void RussianPlurals()
    {
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            Assert.AreEqual("1 файл", Fmt.Files(1));
            Assert.AreEqual("3 файла", Fmt.Files(3));
            Assert.AreEqual("12 файлов", Fmt.Files(12));
            Assert.AreEqual("21 файл", Fmt.Files(21));
            Assert.AreEqual("3,4 ГБ", Fmt.Size((long)(3.4 * (1L << 30))));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            Assert.AreEqual("1 file", Fmt.Files(1));
            Assert.AreEqual("12 files", Fmt.Files(12));
            Assert.AreEqual("3.4 GB", Fmt.Size((long)(3.4 * (1L << 30))));
        }
        finally { CultureInfo.CurrentUICulture = ui; }
    }
}
