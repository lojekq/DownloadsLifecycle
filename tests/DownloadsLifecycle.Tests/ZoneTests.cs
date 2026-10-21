using System.IO;
using DownloadsLifecycle.Features;

namespace DownloadsLifecycle.Tests;

[TestClass]
public class ZoneTests
{
    [TestMethod]
    public void ParsesCrLfAndLf()
    {
        foreach (var nl in new[] { "\r\n", "\n" })
        {
            var s = ZoneReader.Parse(string.Join(nl, "[ZoneTransfer]", "ZoneId=3", "ReferrerUrl=https://github.com/x/y/releases", "HostUrl=https://objects.githubusercontent.com/a/b.zip", ""));
            Assert.IsNotNull(s);
            Assert.AreEqual(3, s.Zone);
            Assert.AreEqual("github.com", s.Site); // referrer wins over the download host
            Assert.AreEqual("https://objects.githubusercontent.com/a/b.zip", s.Url);
        }
    }

    [TestMethod]
    public void HostUrlWhenNoReferrer() =>
        Assert.AreEqual("objects.githubusercontent.com", ZoneReader.Parse("[ZoneTransfer]\nZoneId=3\nHostUrl=https://objects.githubusercontent.com/f")!.Site);

    [TestMethod]
    public void OnlyZoneIdIsUnknownSite()
    {
        var s = ZoneReader.Parse("[ZoneTransfer]\r\nZoneId=3\r\n");
        Assert.AreEqual(3, s!.Zone);
        Assert.IsNull(s.Site);
    }

    [TestMethod]
    public void AboutBlobDataAndJunk()
    {
        Assert.IsNull(ZoneReader.Parse("[ZoneTransfer]\nZoneId=3\nHostUrl=about:internet")!.Site);
        Assert.IsNull(ZoneReader.Parse("[ZoneTransfer]\nHostUrl=data:text/plain;base64,AAAA")!.Site);
        Assert.AreEqual("mail.google.com", ZoneReader.Parse("[ZoneTransfer]\nHostUrl=blob:https://mail.google.com/0f0e-11")!.Site);
        Assert.AreEqual("example.com", ZoneReader.Parse("[ZoneTransfer]\nReferrerUrl=https://www.example.com/page")!.Site);
        Assert.IsNull(ZoneReader.Parse("garbage\0\u0001 = = ["));
        Assert.IsNull(ZoneReader.Parse("[Other]\nHostUrl=https://x.com/"));     // fields outside [ZoneTransfer] don't count
        Assert.IsNull(ZoneReader.Parse("[ZoneTransfer]\nZoneId=abc\nHostUrl="));
    }

    [TestMethod]
    public void ReadsTheStreamAndKeyFollowsRename()
    {
        var dir = Directory.CreateTempSubdirectory("dl-test-").FullName;
        var a = Path.Combine(dir, "dl-test-a.bin");
        File.WriteAllText(a, "x");
        File.WriteAllText(a + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/a.bin\r\n");
        Assert.AreEqual("example.com", ZoneReader.Read(a)!.Site);

        var key = Folder.Key(a);
        Assert.IsNotNull(key);
        var b = Path.Combine(dir, "dl-test-b.bin");
        File.Move(a, b);
        Assert.AreEqual(key, Folder.Key(b));
        Assert.AreEqual(key.Split('-')[0], Folder.Key(dir)!.Split('-')[0]); // folders have keys too, same volume
        Assert.IsNull(Folder.Key(a));
        Assert.IsNull(ZoneReader.Read(a));
        Directory.Delete(dir, true);
    }
}
