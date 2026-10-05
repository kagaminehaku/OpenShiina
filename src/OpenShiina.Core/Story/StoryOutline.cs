// The story's chapters as SRC_MAIN.SCN lays them out (docs/engine-notes.md, section 5): the
// opening files before the route menu, each route's files (the menu's options name them), and
// the ending files after the routes. Read from the code, so it holds for any game whose flow
// follows that pattern.

namespace OpenShiina.Story;

/// <summary>A scenario file that can be started from the chapter list.</summary>
/// <param name="File">File stem ("ORE02-01").</param>
/// <param name="Title">The file and its scene title, for the list.</param>
/// <param name="Group">"Opening", the route's name, or "Ending".</param>
public sealed record StoryChapter(string File, string Title, string Group);

public sealed class StoryOutline
{
    private readonly List<(string File, int Route)> m_files = new();

    /// <summary>The route menu's options, in order.</summary>
    public IReadOnlyList<string> Routes { get; }

    /// <summary>Every route played: the value of a[780] that leads to the ending.</summary>
    public int AllRoutes => (1 << Routes.Count) - 1;

    private const int Opening = -1, Ending = -2;

    private StoryOutline(ScnMachine flow)
    {
        var menu = flow.FindRouteMenu();
        Routes = menu?.Options ?? Array.Empty<string>();
        foreach (var (address, file, _) in flow.FindScenes())
        {
            string stem = Stem(file);
            if (m_files.Any(f => f.File.Equals(stem, StringComparison.OrdinalIgnoreCase)))
                continue;
            int route = menu == null ? Opening : address < menu.Address ? Opening : menu.RouteAt(address) is int r and >= 0 ? r : Ending;
            m_files.Add((stem, route));
        }
    }

    public static StoryOutline Read(byte[] flowCode) => new(new ScnMachine(flowCode));

    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path.Replace('\\', '/')).ToUpperInvariant();

    /// <summary>
    /// The routes played when <paramref name="file"/> is started from the chapter list: its own
    /// route added to <paramref name="played"/>, or all of them for an ending file.
    /// </summary>
    public int RoutesPlayedAt(string file, int played)
    {
        int index = m_files.FindIndex(f => f.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        int route = index >= 0 ? m_files[index].Route : Opening;
        return route >= 0 ? played | 1 << route : route == Ending ? AllRoutes : played;
    }

    /// <summary>
    /// Every scenario file in code order with its group. <paramref name="describe"/> gives a
    /// file's scene title, if it has one.
    /// </summary>
    public IEnumerable<StoryChapter> GetChapters(Func<string, string?>? describe = null)
    {
        foreach (var (file, route) in m_files)
        {
            string title = describe?.Invoke(file) is { Length: > 0 } scene ? $"{file}  {scene}" : file;
            string group = route switch { Opening => "Opening", Ending => "Ending", _ => Routes[route] };
            yield return new StoryChapter(file, title, group);
        }
    }
}
