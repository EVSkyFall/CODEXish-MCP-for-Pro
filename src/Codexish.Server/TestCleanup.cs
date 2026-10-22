namespace Codexish.Server;

// Test harness only. On Windows a recursive Directory.Delete returns without error while a deleted file is still
// held open by another process (a scanner, for example), leaving the emptied directories behind; the delete is
// therefore repeated until the directory is really gone.
internal static class TestCleanup
{
    public static bool RemoveDirectory(string path)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return true;
                // Git marks loose objects read-only, which a plain recursive delete refuses to remove.
                foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                }
                Directory.Delete(path, true);
                if (!Directory.Exists(path)) return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            Thread.Sleep(250);
        }
        Console.Error.WriteLine($"WARNING: could not remove the test directory {path}");
        return false;
    }
}
