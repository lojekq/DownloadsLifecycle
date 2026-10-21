using System.IO;
using System.IO.Compression;
using DownloadsLifecycle.Features;
using Index = DownloadsLifecycle.Features.Index;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Tests;

[TestClass]
public class DetectorTests
{
    [TestMethod]
    [DataRow("a (1).pdf", "a.pdf")]
    [DataRow("a(1).pdf", "a.pdf")]                 // Firefox
    [DataRow("a - Copy.pdf", "a.pdf")]              // Explorer EN
    [DataRow("a - Copy (2).pdf", "a.pdf")]
    [DataRow("a - копия (2).pdf", "a.pdf")]         // Explorer RU
    [DataRow("a — копия.pdf", "a.pdf")]
    [DataRow("a (1)", "a")]                         // no extension
    [DataRow("a.tar (1).gz", "a.tar.gz")]           // Chrome puts the number before the last extension
    [DataRow("report v1.2 (1)", "report v1.2")]     // ".2 (1)" is not an extension
    [DataRow("a.pdf", null)]
    [DataRow("Report (2023).pdf", null)]            // a year, not a copy number
    [DataRow("(1).pdf", null)]
    public void CopySuffixes(string name, string? original) => Assert.AreEqual(original, CopySuffix.Original(name));

    static Item I(string name, double daysAgo, bool kept = false) => new()
    {
        Key = name, Path = @"C:\d\" + name, Size = 10, CreatedUtc = DateTime.UtcNow.AddDays(-daysAgo), Kept = kept,
    };

    [TestMethod]
    public void KeeperIsTheOriginalNameThenTheOldest()
    {
        Assert.AreEqual("a.pdf", Duplicates.Keeper([I("a (1).pdf", 5), I("a.pdf", 1), I("a (2).pdf", 9)]).Name);
        Assert.AreEqual("a (2).pdf", Duplicates.Keeper([I("a (1).pdf", 5), I("a (2).pdf", 9)]).Name);  // all copies: the oldest
        Assert.AreEqual("y.pdf", Duplicates.Keeper([I("x.pdf", 1), I("y.pdf", 3)]).Name); // different names: the oldest
        Assert.AreEqual("a (1).pdf", Duplicates.Keeper([I("a (1).pdf", 1, kept: true), I("a.pdf", 5)]).Name); // the user's choice wins
    }

    [TestMethod]
    public void DuplicatesNeedTheSameHashAndOneCopyStays()
    {
        var dir = Directory.CreateTempSubdirectory("dl-test-").FullName;
        File.WriteAllText(Path.Combine(dir, "dl-test-a.txt"), "same");
        File.WriteAllText(Path.Combine(dir, "dl-test-a (1).txt"), "same");
        File.WriteAllText(Path.Combine(dir, "dl-test-b.txt"), "diff"); // same size, other contents
        var index = new Index();
        var scan = new Scanner(index, dir);
        foreach (var f in Directory.GetFiles(dir)) scan.Upsert(f, force: true);
        var found = Duplicates.Find(index, default);
        Assert.HasCount(1, found);
        var copy = index.Items[found.Keys.Single()];
        Assert.AreEqual("dl-test-a (1).txt", copy.Name);
        Assert.AreEqual("dl-test-a.txt", found.Values.Single().Subject);
        Directory.Delete(dir, true);
    }

    // ---- zip ----

    static string Zip(string dir, string name, params (string Entry, string Text)[] files)
    {
        var path = Path.Combine(dir, name);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (e, t) in files)
        {
            using var w = new StreamWriter(z.CreateEntry(e).Open());
            w.Write(t);
        }
        return path;
    }

    static void Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [TestMethod]
    public void ZipFullPartialRootAndSizes()
    {
        var dir = Directory.CreateTempSubdirectory("dl-test-").FullName;
        var files = Enumerable.Range(0, 50).Select(n => ($"f{n}.txt", new string('x', n + 1))).ToArray();

        // Explorer "Extract all": <name>\ as is → 100 %
        var full = Zip(dir, "dl-test-full.zip", files);
        foreach (var (e, t) in files) Put(Path.Combine(dir, "dl-test-full", e), t);
        Assert.AreEqual((100, "dl-test-full"), ZipCoverage.Check(full));

        // one file removed → 98 %, two → 96 % (below the threshold)
        File.Delete(Path.Combine(dir, "dl-test-full", "f0.txt"));
        Assert.AreEqual(98, ZipCoverage.Check(full)!.Value.Percent);
        File.Delete(Path.Combine(dir, "dl-test-full", "f1.txt"));
        Assert.IsLessThan(ZipCoverage.Threshold, ZipCoverage.Check(full)!.Value.Percent);

        // one root folder: name\name\… (Explorer) and name\… with the root dropped (7-Zip "Extract to name\")
        var rooted = files.Select(f => ($"dl-test-root/{f.Item1}", f.Item2)).ToArray();
        var root = Zip(dir, "dl-test-root.zip", rooted);
        foreach (var (e, t) in files) Put(Path.Combine(dir, "dl-test-root", "dl-test-root", e), t);
        Assert.AreEqual(100, ZipCoverage.Check(root)!.Value.Percent);
        var other = Zip(dir, "dl-test-other.zip", files.Select(f => ($"inner/{f.Item1}", f.Item2)).ToArray());
        foreach (var (e, t) in files) Put(Path.Combine(dir, "dl-test-other", e), t);
        Assert.AreEqual(100, ZipCoverage.Check(other)!.Value.Percent);

        // same names, other sizes → not extracted from this archive
        var sizes = Zip(dir, "dl-test-sizes.zip", files);
        foreach (var (e, t) in files) Put(Path.Combine(dir, "dl-test-sizes", e), t + "!");
        Assert.AreEqual(0, ZipCoverage.Check(sizes)!.Value.Percent);

        // nothing next to it
        Assert.AreEqual(0, ZipCoverage.Check(Zip(dir, "dl-test-lonely.zip", files))?.Percent ?? 0);
        Directory.Delete(dir, true);
    }

    // ---- exe ----

    static readonly List<UninstallEntry> Installed =
    [
        new("HKCU", "Microsoft Visual Studio Code (User)_is1", "Microsoft Visual Studio Code (User)", "1.95.3", "Microsoft Corporation", null, null, false),
        new("HKLM", "Git_is1", "Git", "2.47.0", "The Git Development Community", null, null, false),
        new("HKLM", "{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}_Display.Driver", "NVIDIA Graphics Driver 566.36", "566.36", "NVIDIA Corporation", null, null, false),
        new("HKLM", "GitHubDesktop", "GitHub Desktop", "3.4.9", "GitHub, Inc.", null, null, false),
        new("HKLM", "{guid}", "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.40.33810", "14.40.33810", "Microsoft Corporation", null, null, true),
    ];

    static ExeInfo NoInfo => new(null, null, null, null, null);

    [TestMethod]
    public void VsCodeIsExact()
    {
        var m = ExeMatcher.Match("VSCodeUserSetup-x64-1.95.3.exe",
            new("Microsoft Visual Studio Code", "Microsoft Corporation", "Visual Studio Code Setup", "1.95.3", "1.95.3.0"), Installed);
        Assert.AreEqual("Microsoft Visual Studio Code (User)", m!.Entry.DisplayName);
        Assert.IsTrue(m.Exact);
    }

    [TestMethod]
    public void GitIsExactFromTheFileName()
    {
        var m = ExeMatcher.Match("Git-2.47.0-64-bit.exe", NoInfo, Installed);
        Assert.AreEqual("Git", m!.Entry.DisplayName);
        Assert.IsTrue(m.Exact);
    }

    [TestMethod]
    public void GitHubDesktopIsNotGit()
    {
        var m = ExeMatcher.Match("GitHubDesktopSetup-x64.exe", NoInfo, Installed);
        Assert.AreEqual("GitHub Desktop", m!.Entry.DisplayName);
        Assert.IsFalse(m.Exact); // no version in the name: likely
    }

    [TestMethod]
    public void PortableExeWithoutAnEntryIsNeverOffered()
    {
        Assert.IsNull(ExeMatcher.Match("scrcpy.exe", new("scrcpy", "Genymobile", null, "3.1", "3.1"), Installed));
        Assert.IsNull(ExeMatcher.Match("ffmpeg.exe", NoInfo, Installed));
    }

    [TestMethod]
    public void NvidiaDriverByVersionAndPublisher()
    {
        var m = ExeMatcher.Match("566.36-desktop-win10-win11-64bit-international-dch-whql.exe",
            new("NVIDIA Install Application", "NVIDIA Corporation", "NVIDIA Install Application", "566.36", "566.36"), Installed);
        Assert.AreEqual("NVIDIA Graphics Driver 566.36", m!.Entry.DisplayName);
        Assert.IsFalse(m.Exact);
        Assert.AreEqual("version", m.Why);
    }

    [TestMethod]
    public void HiddenComponentsDoNotCount() =>
        Assert.IsNull(ExeMatcher.Match("VC_redist.x64.exe", new("Microsoft Visual C++ 2022 Redistributable (x64)", "Microsoft Corporation", null, "14.40.33810.0", "14.40.33810.0"),
            Installed.Where(e => e.SystemComponent).ToList()));

    [TestMethod]
    public void VersionsLoseTrailingZeros()
    {
        CollectionAssert.AreEquivalent(new[] { "1.95.3" }, ExeMatcher.Versions("1.95.3.0").ToList());
        CollectionAssert.AreEquivalent(new[] { "566.30", "2.40" }, ExeMatcher.Versions("566.30 and 2.40.0").ToList()); // only whole ".0" parts go
    }
}
