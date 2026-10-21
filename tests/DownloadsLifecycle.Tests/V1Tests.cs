using System.IO;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers;
using DownloadsLifecycle.Features;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Tests;

/// <summary>v1.0: site rules, more archive formats, exact-match rules, the monthly line, extra folders, "Where is it from?".</summary>
[TestClass]
public class V1Tests
{
    static Item Dl(string name, string? referrer, string? url = null, bool dir = false) => new()
    {
        Key = name, Path = @"C:\Demo\Downloads\" + name, IsDir = dir, Size = 100, CreatedUtc = DateTime.UtcNow.AddDays(-10),
        Src = referrer is null && url is null ? null : new(referrer, url, 3),
    };

    [TestMethod]
    public void SiteRuleMatchesDomainSubdomainsAndExtensions()
    {
        var r = new SiteRule { Domain = "github.com", Extensions = "exe, .msi" };
        Assert.IsTrue(r.Matches(Dl("a.exe", "https://github.com/x/releases")));
        Assert.IsTrue(r.Matches(Dl("a.MSI", null, "https://objects.github.com/a.msi")));              // subdomain of the download host
        Assert.IsFalse(r.Matches(Dl("a.exe", "https://notgithub.com/")));                             // a suffix, not a substring
        Assert.IsFalse(r.Matches(Dl("a.zip", "https://github.com/")));
        Assert.IsFalse(r.Matches(Dl("a.exe", null)));                                                 // unknown source: never
        Assert.IsFalse(r.Matches(Dl("folder", "https://github.com/", dir: true)));                    // files only
        Assert.IsTrue(new SiteRule { Domain = ".huggingface.co" }.Matches(Dl("m.gguf", "https://huggingface.co/x")));
        Assert.IsTrue(new SiteRule { Domain = "example.com", Extensions = "tar.gz" }.Matches(Dl("x.tar.gz", "https://www.example.com/")));
        Assert.IsFalse(new SiteRule { Domain = " " }.Matches(Dl("a.exe", "https://github.com/")));

        var rules = new[] { new SiteRule { Domain = "github.com", Action = "move" }, new SiteRule { Domain = "github.com", Action = "ttl" } };
        Assert.AreEqual("move", SiteRule.First(rules, Dl("a.zip", "https://github.com/"))!.Action); // the first one wins
    }

    static void Tar(string path, CompressionType c, params (string Name, string Text)[] files)
    {
        using var w = WriterFactory.OpenWriter(path, ArchiveType.Tar, new WriterOptions(c));
        foreach (var (n, t) in files) w.Write(n, new MemoryStream(Encoding.UTF8.GetBytes(t)), DateTime.Now);
    }

    [TestMethod]
    public void TarAndTarGzAreChecked()
    {
        var dir = Directory.CreateTempSubdirectory("dl-test-").FullName;
        var files = Enumerable.Range(0, 10).Select(n => ($"pkg/f{n}.txt", new string('x', n + 1))).ToArray();
        foreach (var ext in new[] { ".tar", ".tar.gz" })
        {
            var archive = Path.Combine(dir, "dl-test-pkg" + ext);
            Tar(archive, ext == ".tar" ? CompressionType.None : CompressionType.GZip, files);
            Assert.IsTrue(Archives.Supported(archive));
            Assert.AreEqual("dl-test-pkg", Archives.Stem(archive));
            Assert.HasCount(10, Archives.Entries(archive)!);
        }
        foreach (var (n, t) in files) { Directory.CreateDirectory(Path.Combine(dir, "pkg")); File.WriteAllText(Path.Combine(dir, n.Replace('/', '\\')), t); }
        Assert.AreEqual((100, "pkg"), Archives.Check(Path.Combine(dir, "dl-test-pkg.tar.gz")));  // extracted right here, root folder kept
        File.Delete(Path.Combine(dir, "pkg", "f3.txt"));
        Assert.AreEqual(90, Archives.Check(Path.Combine(dir, "dl-test-pkg.tar"))!.Value.Percent);
        File.WriteAllText(Path.Combine(dir, "dl-test-broken.7z"), "not an archive");
        Assert.IsNull(Archives.Entries(Path.Combine(dir, "dl-test-broken.7z")));
        Assert.IsFalse(Archives.Supported(Path.Combine(dir, "a.gz")));
        Directory.Delete(dir, true);
    }

    [TestMethod]
    public void ExactMatchRulesWaitAndNeverTakeExe()
    {
        var now = DateTime.UtcNow;
        var s = new Settings { AutoInstallers = true, AutoArchives = true, ExactDays = 3 };
        Item I(string name, Suggestion sug) => new() { Key = name, Path = @"C:\Demo\Downloads\" + name, Size = 100, CreatedUtc = now.AddDays(-10), Suggestion = sug };
        var msiOld = I("a.msi", new("installer", "exact", "A", Why: "msi", FoundUtc: now.AddDays(-4)));
        var msiNew = I("b.msi", new("installer", "exact", "B", Why: "msi", FoundUtc: now.AddDays(-1)));
        var exe = I("c.exe", new("installer", "exact", "C", Why: "name", FoundUtc: now.AddDays(-30)));
        var zip100 = I("d.zip", new("archive", "exact", "d", Percent: 100, FoundUtc: now.AddDays(-5)));
        var zip99 = I("e.zip", new("archive", "likely", "e", Percent: 99, FoundUtc: now.AddDays(-5)));
        var plan = Rules.AutoPlan([msiOld, msiNew, exe, zip100, zip99], now, now.AddHours(-1), false, s);
        CollectionAssert.AreEquivalent(new[] { "a.msi", "d.zip" }, plan.Select(p => p.Item.Name).ToList());
        Assert.HasCount(0, Rules.AutoPlan([msiOld, zip100], now, now.AddHours(-1), false, new Settings())); // off by default
    }

    [TestMethod]
    public void OfferKeepsWhenItWasFound()
    {
        var t = DateTime.UtcNow.AddDays(-3);
        Assert.IsTrue(new Suggestion("archive", "exact", "x", Percent: 100).Same(new("archive", "exact", "x", Percent: 100, FoundUtc: t)));
        Assert.IsFalse(new Suggestion("archive", "exact", "x", Percent: 100).Same(new("archive", "likely", "x", Percent: 99, FoundUtc: t)));
        Assert.IsFalse(Rules.ToClean(Dl("a.exe", null) with { Suggestion = new("rule", "exact", "github.com", Why: "move") }, DateTime.UtcNow)); // waits for Apply
        Assert.IsTrue(Rules.ToClean(Dl("a.exe", null) with { Suggestion = new("site", "likely", "example.com") }, DateTime.UtcNow));
    }

    [TestMethod]
    public void FreedThisMonthCountsRecycledMinusRestored()
    {
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Local);
        JournalEntry E(DateTime local, string op, string path, long size) => new(local.ToUniversalTime(), op, path, size, "", "user");
        var journal = new[]
        {
            E(now.AddDays(-1), "recycle", @"C:\d\a", 100), E(now.AddDays(-2), "recycle", @"C:\d\b", 50),
            E(now.AddDays(-1), "restore", @"C:\d\b", 50), E(now.AddDays(-25), "recycle", @"C:\d\old", 999), // September
            E(now.AddDays(-3), "move", @"C:\d\c", 70),
        };
        Assert.AreEqual((1, 100L), Rules.FreedThisMonth(journal, now));
    }

    [TestMethod]
    public void ExtraFoldersAreChecked()
    {
        Assert.IsNotNull(Folder.Allowed(@"C:\"));                     // a drive root
        Assert.IsNotNull(Folder.Allowed(@"relative\path"));
        Assert.IsNotNull(Folder.Allowed(Path.GetDirectoryName(Folder.Path)!)); // holds the main folder
        Assert.IsNotNull(Folder.Allowed(Folder.Path));                // is the main folder
        Assert.IsNotNull(Folder.Allowed(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.IsNull(Folder.Allowed(Path.Combine(Folder.Sandbox, "Telegram Desktop")));
    }

    [TestMethod]
    public void WhenceText()
    {
        var t = Whence.Text("a.zip", new("https://github.com/x/releases", "https://objects.githubusercontent.com/a.zip", 3), new DateTime(2026, 10, 21, 13, 20, 0));
        StringAssert.Contains(t, "github.com");
        StringAssert.Contains(t, "https://github.com/x/releases");
        StringAssert.Contains(t, "https://objects.githubusercontent.com/a.zip");
        StringAssert.Contains(Whence.Text("b.txt", null, DateTime.Now), Loc.T("Whence.Unknown"));
    }
}
