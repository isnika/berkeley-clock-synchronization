namespace Berkeley.Client;

public static class ClientPaths
{
    // Retain the original Avalonia data location so existing Windows users keep their configuration and history.
    // CrossPlatform is a historical directory name; this application now supports Windows only.
    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BerkeleyTimeSync", "CrossPlatform");
    public static string GetDataDirectory(string[] args)
    {
        if (args.Length == 0) return DefaultDataDirectory;
        if (args.Length != 2 || args[0] != "--data-dir" || !Path.IsPathFullyQualified(args[1]))
            throw new ArgumentException("Usage: BerkeleyTimeSync [--data-dir <absolute-directory>]");
        return Path.GetFullPath(args[1]);
    }
}
