namespace NotOnlyFans.Service.Overclock;

/// <summary>
/// AMD's and Intel's driver libraries run inside the service. A crash in one of them would stop the service (the
/// fans go back to the BIOS) and, after the automatic restart, crash it again. So calls into them are marked on
/// disk while they run: if the service dies inside one, the mark stays and that library is not used again
/// (the app says why) until NotOnlyFans is installed again, which removes the marks.
/// Only the first look at the cards and each apply are marked (not the reads every few seconds), so the disk is
/// not written all the time.
/// </summary>
internal static class NativeGuard
{
    /// <summary>The service stopped inside <paramref name="library"/> before.</summary>
    public static bool Crashed(string library) => File.Exists(MarkPath(library));

    public static T Run<T>(string library, Func<T> call)
    {
        string mark = MarkPath(library);
        try
        {
            File.WriteAllText(mark, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not marked: still worth trying.
        }
        try
        {
            return call();
        }
        finally
        {
            // Reached for a normal return and for a .NET exception; only a crash of the process leaves the mark.
            try
            {
                File.Delete(mark);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string MarkPath(string library) => Path.Combine(DataDirectory.Root, $"{library}.crashed");
}
