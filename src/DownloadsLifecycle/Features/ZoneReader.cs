using System.IO;

namespace DownloadsLifecycle.Features;

/// <summary>Reads the Mark-of-the-Web stream path:Zone.Identifier. Only reads: removing it is Windows' protection, not ours to touch.</summary>
public static class ZoneReader
{
    /// <summary>Null when there is no stream (incognito, Telegram, unblocked, not NTFS) or it cannot be read.</summary>
    public static Source? Read(string path)
    {
        try { return Parse(File.ReadAllText(path + ":Zone.Identifier")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>[ZoneTransfer] section: ZoneId, ReferrerUrl, HostUrl. CRLF or LF, any order, junk ignored; null if nothing useful.</summary>
    public static Source? Parse(string text)
    {
        string? referrer = null, url = null;
        int? zone = null;
        var inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inSection = line.Equals("[ZoneTransfer]", StringComparison.OrdinalIgnoreCase); continue; }
            var eq = line.IndexOf('=');
            if (!inSection || eq <= 0) continue;
            var (k, v) = (line[..eq].Trim(), line[(eq + 1)..].Trim());
            if (k.Equals("ZoneId", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var z)) zone = z;
            else if (k.Equals("ReferrerUrl", StringComparison.OrdinalIgnoreCase) && v.Length > 0) referrer = v;
            else if (k.Equals("HostUrl", StringComparison.OrdinalIgnoreCase) && v.Length > 0) url = v;
        }
        return referrer is null && url is null && zone is null ? null : new(referrer, url, zone);
    }

    /// <summary>"https://www.github.com/x" → github.com; blob:https://x/… → x; about:, data:, junk → null.</summary>
    public static string? Host(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) url = url[5..];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "ftp") || u.Host.Length == 0) return null;
        var h = u.Host.ToLowerInvariant();
        return h.StartsWith("www.") ? h[4..] : h;
    }
}
