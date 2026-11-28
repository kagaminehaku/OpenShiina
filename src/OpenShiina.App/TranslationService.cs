// The shared translations for the player's screens (SharedTranslations): the catalog read once a
// run (again after an hour), when the setting CheckGitHub is on; and the translations downloaded
// brought up to date in the background as the setting TranslationUpdates says (only on a network
// not charged by the amount for Unmetered, never for Ask, and one of 5 MB or more only when asked),
// so that a game never waits for one: a game started meanwhile plays the translation it has, the
// new one is read the next time.

using OpenShiina.Game;

namespace OpenShiina.App;

public static class TranslationService
{
    // An update this large is downloaded only when the user asks for it
    private const long AskAbove = 5 << 20;

    private static Task<SharedCatalog?>? s_fetch;
    private static DateTime s_fetched;

    /// <summary>A downloaded translation was updated in the background: its title and game.</summary>
    public static event Action<SharedTranslation>? Updated;

    /// <summary>The catalog as last read (null before, or when it could not be read).</summary>
    public static SharedCatalog? Catalog { get; private set; }

    /// <summary>
    /// The catalog, read now when it has not been this run (or an hour ago), waiting at most
    /// <paramref name="wait"/> for it: null when it is not there by then or cannot be read
    /// (offline) or the player may not look (CheckGitHub off).
    /// </summary>
    public static async Task<SharedCatalog?> CatalogAsync(TimeSpan wait)
    {
        if (!PlayerSettings.Load().CheckGitHub)
            return null;
        if (s_fetch == null || (s_fetch.IsCompleted && (s_fetch.Result == null || DateTime.UtcNow - s_fetched > TimeSpan.FromHours(1))))
        {
            s_fetched = DateTime.UtcNow;
            s_fetch = Task.Run(async () =>
            {
                var catalog = await SharedTranslations.FetchAsync();
                if (catalog != null)
                    Catalog = catalog;
                return catalog;
            });
        }
        var fetch = s_fetch;
        return await Task.WhenAny(fetch, Task.Delay(wait)) == fetch ? await fetch : null;
    }

    /// <summary>Reads the catalog and updates the downloaded translations that changed, as the settings allow; in the background.</summary>
    public static async Task RefreshAsync()
    {
        var catalog = await CatalogAsync(TimeSpan.FromSeconds(60));
        if (catalog == null)
            return;
        var mode = PlayerSettings.Load().TranslationUpdates;
        if (mode == TranslationUpdates.Ask || (mode == TranslationUpdates.Unmetered && PlayerPlatform.MeteredNetwork()))
            return;
        foreach (var translation in catalog.Translations.Where(SharedTranslations.Outdated).ToList())
        {
            if (SharedTranslations.PendingBytes(translation) >= AskAbove)
                continue;
            try
            {
                await SharedTranslations.InstallAsync(translation);
                Updated?.Invoke(translation);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Next time
            }
        }
    }

    /// <summary>The shared translations of a game the user has not been shown yet, or ones downloaded that have an update waiting.</summary>
    public static bool HasNews(string game)
    {
        if (Catalog is not { } catalog)
            return false;
        var shared = catalog.For(game);
        var seen = SharedTranslations.ChoiceFor(game).Seen;
        return shared.Any(t => !seen.Contains(t.Path)) || shared.Any(SharedTranslations.Outdated);
    }
}
