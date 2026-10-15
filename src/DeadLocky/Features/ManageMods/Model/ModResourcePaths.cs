namespace DeadLocky.Features.ManageMods.Model;

internal static class ModResourcePaths
{
    public static bool IsGameContent(string path)
    {
        if (path.Contains('/'))
        {
            return true;
        }

        string name = Path.GetFileNameWithoutExtension(path);
        return !name.Equals("README", StringComparison.OrdinalIgnoreCase)
               && !name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
               && !name.Equals("COPYING", StringComparison.OrdinalIgnoreCase);
    }
}
