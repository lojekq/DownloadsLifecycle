using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Tests;

[TestClass]
public static class Setup
{
    /// <summary>Tests never write into %APPDATA%\DownloadsLifecycle\logs: an installed copy may be using it.</summary>
    [AssemblyInitialize]
    public static void Init(TestContext _) => Log.Off = true;
}
