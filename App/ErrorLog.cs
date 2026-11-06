namespace RaidAnsager.App;

/// <summary>Appends unexpected exceptions to %APPDATA%\RaidAnsager\fehler.log (at most 50 per run).</summary>
public static class ErrorLog
{
    private static readonly object Gate = new();
    private static int _count;

    public static string Path => System.IO.Path.Combine(AppPaths.Root, "fehler.log");

    public static void Write(string where, Exception ex)
    {
        lock (Gate)
        {
            if (++_count > 50) return;
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] Version {App.TrayApp.Version}\n{ex}\n\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
