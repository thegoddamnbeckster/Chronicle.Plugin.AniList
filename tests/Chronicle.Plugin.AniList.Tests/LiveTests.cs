using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.AniList.Tests;

/// <summary>Runs only when ANILIST_LIVE=1: talks to the real graphql.anilist.co to prove the queries and the season logic
/// work against the real data, not just the stand-in.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ANILIST_LIVE") != "1") Skip = "Set ANILIST_LIVE=1 to run against the real AniList.";
    }
}

public sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("ANILIST_LIVE") != "1") Skip = "Set ANILIST_LIVE=1 to run against the real AniList.";
    }
}

public class AniListLiveTests
{
    private static AniListMetadataProvider Real()
    {
        var provider = new AniListMetadataProvider();
        provider.Configure(new Dictionary<string, string>());
        return provider;
    }

    [LiveFact]
    public async Task AttackOnTitan_IsOneShow_WithItsSeasonsInOrder()
    {
        using var provider = Real();

        var found = await provider.SearchAsync(new MediaSearchContext("Attack on Titan", Year: 2013, MediaTypeName: "anime"));
        Assert.Equal("show:16498", found[0].Metadata.ExternalId);

        var season2 = await provider.GetByIdAsync("show:16498/season:2");
        var season3 = await provider.GetByIdAsync("show:16498/season:3");
        Assert.Equal(2017, season2.Year);
        Assert.Equal(2018, season3.Year);
        Assert.Equal(22, season3.ExtendedData!.Value.GetProperty("episodes").GetInt32());   // 12 + 10: Part 2 is folded in

        var episodes = await provider.GetEpisodeListAsync("show:16498", 3);
        Assert.Equal(22, episodes.Count);
        Assert.Equal(22, episodes[^1].EpisodeNumber);
    }

    [LiveFact]
    public async Task ASpiritedAway_IsAFilm_AndCarriesItsCast()
    {
        using var provider = Real();

        var found = await provider.SearchAsync(new MediaSearchContext("Spirited Away", Year: 2001, MediaTypeName: "anime_movies"));

        Assert.Equal("movie:199", found[0].Metadata.ExternalId);
        Assert.NotEmpty(found[0].Metadata.Cast);
        Assert.True(await provider.HealthCheckAsync());
    }

    [LiveTheory]
    [InlineData("One Piece", 1999, "anime", "show:21")]
    [InlineData("Supernatural", 2011, "anime", "show:8986")]
    [InlineData("Hagane no Renkinjutsushi", 2003, "anime", "show:121")]
    [InlineData("Evangelion: 1.0 You Are (Not) Alone", 2007, "anime_movies", "movie:2759")]
    [InlineData("Patlabor: The Movie", 1989, "anime_movies", "movie:1095")]
    [InlineData("Vampire Hunter D: Bloodlust", 2000, "anime_movies", "movie:543")]
    public async Task TheLibrarysOwnTitles_AreFound(string name, int year, string type, string expected)
    {
        using var provider = Real();

        var found = await provider.SearchAsync(new MediaSearchContext(name, Year: year, MediaTypeName: type));

        Assert.NotEmpty(found);
        Assert.Equal(expected, found[0].Metadata.ExternalId);
    }

    [LiveFact]
    public async Task Appleseed_1988_IsFound_AsAFilm()
    {
        using var provider = Real();

        var found = await provider.SearchAsync(new MediaSearchContext("Appleseed", Year: 1988, MediaTypeName: "anime_movies"));

        Assert.NotEmpty(found);
        Assert.StartsWith("movie:", found[0].Metadata.ExternalId);
    }
}
