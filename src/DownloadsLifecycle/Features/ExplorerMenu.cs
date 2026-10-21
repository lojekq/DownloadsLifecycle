using Microsoft.Win32;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>
/// Classic Explorer menu in HKCU: "Lifetime ▸ 1 day / 7 days / 30 days / Keep / Remove lifetime" for files and folders,
/// and (v1.0) "Where is it from?" for files. Windows 11 shows them under "Show more options". Registered by itself only from
/// %LOCALAPPDATA%\Programs\DownloadsLifecycle\ (and re-pointed when the exe moved there); otherwise only by the switch in Settings (00-shared §2.4).
/// </summary>
public static class ExplorerMenu
{
    static readonly string[] Roots = [@"Software\Classes\*\shell\" + AppInfo.Name, @"Software\Classes\Directory\shell\" + AppInfo.Name];
    const string WhenceKey = @"Software\Classes\*\shell\" + AppInfo.Name + ".Whence";
    static readonly (string Arg, string Label)[] Commands = [("1d", "Menu.1d"), ("7d", "Menu.7d"), ("30d", "Menu.30d"), ("keep", "Menu.Keep"), ("clear", "Menu.Clear")];

    public static bool IsRegistered
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Roots[0]); return k is not null; }
    }

    static string Command(string arg) => $"\"{AppInfo.ExePath}\" --ttl {arg} \"%1\"";
    static string WhenceCommand => $"\"{AppInfo.ExePath}\" --whence \"%1\"";

    public static void Register()
    {
        if (Args.Current.Demo) return;
        Unregister();
        foreach (var root in Roots)
        {
            using var k = Registry.CurrentUser.CreateSubKey(root);
            k.SetValue("MUIVerb", Loc.T("Menu.Lifetime"));
            k.SetValue("SubCommands", "");
            k.SetValue("MultiSelectModel", "Player"); // one process per file, up to 100 files; the tray batches them
            k.SetValue("Icon", $"\"{AppInfo.ExePath}\",0");
            for (var n = 0; n < Commands.Length; n++)
            {
                using var c = k.CreateSubKey($@"shell\{n + 1:00}");
                c.SetValue("MUIVerb", Loc.T(Commands[n].Label));
                if (Commands[n].Arg == "keep") c.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord); // separator before
                using var cmd = c.CreateSubKey("command");
                cmd.SetValue("", Command(Commands[n].Arg));
            }
        }
        using (var w = Registry.CurrentUser.CreateSubKey(WhenceKey))
        {
            w.SetValue("MUIVerb", Loc.T("Menu.Whence"));
            w.SetValue("MultiSelectModel", "Single");
            w.SetValue("Icon", $"\"{AppInfo.ExePath}\",0");
            using var cmd = w.CreateSubKey("command");
            cmd.SetValue("", WhenceCommand);
        }
        Log.Info("context menu registered");
    }

    public static void Unregister()
    {
        if (Args.Current.Demo) return;
        foreach (var root in Roots.Append(WhenceKey)) Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
    }

    /// <summary>Installed start: register if wanted, or re-point when the exe moved (a new version replaced the old file or added an item).</summary>
    public static void Ensure()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Roots[0] + @"\shell\01\command");
            using var w = Registry.CurrentUser.OpenSubKey(WhenceKey + @"\command");
            if (k?.GetValue("") as string != Command("1d") || w?.GetValue("") as string != WhenceCommand) Register();
        }
        catch (Exception e) { Log.Error("context menu", e); }
    }
}
