using DownloadsLifecycle.Features;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Demo;

/// <summary>
/// --demo: believable data for screenshots and GIFs (08 §10). Paths under C:\Users\demo\, invented files; never real data.
/// Under --demo no real services start, nothing is written, no update check (00-shared §2.3).
/// </summary>
public static class DemoData
{
    public static List<JournalEntry> Journal { get; } = [];

    const string Dir = @"C:\Users\demo\Downloads\";
    const long KB = 1024, MB = KB * 1024, GB = MB * 1024;

    public static void Fill()
    {
        var now = DateTime.UtcNow;
        var lastTriage = now.AddDays(-2);
        var items = new List<Item>();
        var n = 0;
        Item Add(string name, long size, double daysAgo, string? site, string? referrer = null, string? url = null, Suggestion? s = null,
            bool kept = false, DateTime? ttl = null, bool dir = false)
        {
            var t = now.AddDays(-daysAgo);
            var i = new Item
            {
                Key = $"DEMO-{++n:00}", Path = Dir + name, IsDir = dir, Size = size, MtimeUtc = t, CreatedUtc = t, FirstSeenUtc = t,
                Kind = Item.KindOf(name, dir), Kept = kept, TtlUtc = ttl, Suggestion = s,
                Src = site is null ? null : new(referrer ?? $"https://{site}/", url ?? $"https://{site}/download/{Uri.EscapeDataString(name)}", 3),
            };
            items.Add(i);
            return i;
        }
        const string gh = "github.com", ghFiles = "https://objects.githubusercontent.com/github-production-release-asset-2e65be/";

        // In the inbox: new since the last triage, or offered for cleanup
        Add("VSCodeUserSetup-x64-1.95.3.exe", 98 * MB, 0.6, "code.visualstudio.com", "https://code.visualstudio.com/download",
            "https://vscode.download.prss.microsoft.com/dbazure/download/stable/VSCodeUserSetup-x64-1.95.3.exe",
            new("installer", "exact", "Microsoft Visual Studio Code (User)", "1.95.3", Why: "name"));
        Add("Git-2.47.0-64-bit.exe", 65 * MB, 1.2, gh, "https://github.com/git-for-windows/git/releases", ghFiles + "23216272/Git-2.47.0-64-bit.exe",
            new("installer", "exact", "Git", "2.47.0", Why: "name"));
        Add("blender-4.3.0-windows-x64.zip", 381 * MB, 1.5, "download.blender.org", "https://www.blender.org/download/",
            "https://download.blender.org/release/Blender4.3/blender-4.3.0-windows-x64.zip",
            new("archive", "exact", "blender-4.3.0-windows-x64", Percent: 100));
        Add("blender-4.3.0-windows-x64", 1180 * MB, 1.5, null, dir: true);
        Add("invoice_2026-10.pdf", 214 * KB, 1.0, "mail.google.com", "https://mail.google.com/", "blob:https://mail.google.com/7d1f0c2e");
        Add("invoice_2026-10 (1).pdf", 214 * KB, 0.3, "mail.google.com", "https://mail.google.com/", "blob:https://mail.google.com/9a3b44f1",
            new("duplicate", "exact", "invoice_2026-10.pdf"));
        Add("llama-b4500-bin-win-cuda-x64.zip", 182 * MB, 0.2, gh, "https://github.com/ggml-org/llama.cpp/releases", ghFiles + "612354784/llama-b4500-bin-win-cuda-x64.zip");
        Add("screenshot_0412.png", (long)(1.4 * MB), 0.1, "cdn.discordapp.com", "https://discord.com/", "https://cdn.discordapp.com/attachments/1/2/screenshot_0412.png");
        Add("notes.txt", 3 * KB, 0.05, null);
        Add("LM-Studio-0.3.9-Setup.exe", 412 * MB, 0.15, gh, "https://github.com/lmstudio-ai/lmstudio/releases", ghFiles + "1/LM-Studio-0.3.9-Setup.exe",
            new("rule", "exact", "github.com", Why: "move", Arg: @"D:\Installers"));

        // Older, already sorted
        Add("Qwen3-8B-Q4_K_M.gguf", 5 * GB, 9, "huggingface.co", "https://huggingface.co/Qwen/Qwen3-8B-GGUF", "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf", kept: true);
        Add("scrcpy-win64-v3.1.zip", 7 * MB, 4, gh, "https://github.com/Genymobile/scrcpy/releases", ghFiles + "180123/scrcpy-win64-v3.1.zip", ttl: now.AddDays(3));
        foreach (var (name, size, days) in new[]
                 {
                     ("PowerToysUserSetup-0.86.0-x64.exe", 248 * MB, 12.0), ("ripgrep-14.1.1-x86_64-pc-windows-msvc.zip", 2 * MB, 15.0),
                     ("ffmpeg-master-latest-win64-gpl.zip", 141 * MB, 20.0), ("keepassxc-2.7.9-Win64.msi", 41 * MB, 22.0),
                     ("ShareX-17.0.0-setup.exe", 9 * MB, 25.0), ("nvim-win64.msi", 11 * MB, 30.0), ("yt-dlp.exe", 17 * MB, 31.0),
                     ("LocalSend-1.16.1-windows-x86-64.exe", 14 * MB, 33.0), ("obsidian-1.7.7.exe", 89 * MB, 40.0),
                 })
            Add(name, size, days, gh, "https://github.com/", ghFiles + name);
        Add("ggml-large-v3-turbo.bin", 1550 * MB, 14, "huggingface.co");
        Add("sd_xl_base_1.0.safetensors", (long)(6.5 * GB), 45, "huggingface.co");
        foreach (var (name, size, days) in new[]
                 {
                     ("Project brief.pdf", 1 * MB, 6.0), ("budget-2026.xlsx", 88 * KB, 8.0), ("photos-trip.zip", 912 * MB, 11.0),
                     ("slides-final.pptx", 14 * MB, 18.0), ("contract-draft.docx", 61 * KB, 27.0),
                 })
            Add(name, size, days, "drive.google.com", "https://drive.google.com/", "https://drive.usercontent.google.com/download?id=demo");
        Add("scan_0001.pdf", 2 * MB, 19, null);
        Add("setup-notes.md", 9 * KB, 26, null);

        Engine.ShowDemo(new(items, lastTriage));
        Settings.Current.WelcomeDone = true;
        Settings.Current.ExtraFolders = [Dir + "Telegram Desktop"];
        Settings.Current.SiteRules =
        [
            new() { Domain = "github.com", Extensions = "exe, msi", Action = "move", Folder = @"D:\Installers" },
            new() { Domain = "huggingface.co", Extensions = "gguf, safetensors", Action = "ttl", Days = 30, Auto = true },
            new() { Domain = "cdn.discordapp.com", Action = "clean" },
        ];
        FilesPage.Preset = (true, "Git-2.47.0-64-bit.exe");

        Journal.AddRange(
        [
            new(now.AddHours(-3), "move", Dir + "lecture-07.mp4", 734 * MB, "", "user", @"D:\Video\Courses\lecture-07.mp4"),
            new(now.AddHours(-5), "recycle", Dir + "report-final (1).pdf", 2 * MB, "duplicate|report-final.pdf", "rule:duplicate"),
            new(now.AddHours(-26), "recycle", Dir + "OBS-Studio-30.2.3-Windows-Installer.exe", 140 * MB, "installer|OBS Studio 30.2.3", "user"),
            new(now.AddHours(-27), "recycle", Dir + "wallpapers.zip", 56 * MB, "expired", "rule:expired"),
            new(now.AddHours(-28), "ttl", Dir + "scrcpy-win64-v3.1.zip", 7 * MB, "7d", "user"),
            new(now.AddDays(-2), "move", Dir + "tax-return-2025.pdf", 640 * KB, "", "user", @"C:\Users\demo\Documents\Taxes\tax-return-2025.pdf"),
            new(now.AddDays(-3), "recycle", Dir + "7z2408-x64.exe", (long)(1.6 * MB), "installer|7-Zip 24.08 (x64)", "user"),
            new(now.AddDays(-4), "keep", Dir + "Qwen3-8B-Q4_K_M.gguf", 5 * GB, "", "user"),
            new(now.AddDays(-5), "move", Dir + "PowerToysSetup-0.85.1-x64.exe", 236 * MB, "site|github.com", "rule:site", @"D:\Installers\PowerToysSetup-0.85.1-x64.exe"),
            new(now.AddDays(-6), "recycle", Dir + "node-v22.11.0-x64.msi", 31 * MB, "installer|Node.js 22.11.0", "rule:installer"),
        ]);
    }
}
