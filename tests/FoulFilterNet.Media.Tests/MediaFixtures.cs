namespace FoulFilterNet.Media.Tests;

/// <summary>
/// Locates the generated media fixtures. Their exact spans live in
/// <c>tests/fixtures/media/manifest.json</c>.
/// </summary>
internal static class MediaFixtures
{
    private static readonly Lazy<string> Root = new(Locate);

    public static string Path(string fileName) => System.IO.Path.Combine(Root.Value, fileName);

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "tests", "fixtures", "media");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find tests/fixtures/media above {AppContext.BaseDirectory}.");
    }
}
