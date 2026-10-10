using Chronicle.Plugins.Models;
using static Chronicle.Plugin.AniList.Tests.FakeAniList;

namespace Chronicle.Plugin.AniList.Tests;

public class AniListProviderTests
{
    // A show shaped like the real thing: three TV entries (season 1, season 2, and season 2's second cour), a movie and an OVA.
    private static FakeAniList Catalogue() => new FakeAniList()
        .Add(new Entry(16498, "Shingeki no Kyojin", "Attack on Titan", Episodes: 25, Year: 2013, Sequel: 25777, IdMal: 16498,
            Description: "Humanity lives in cities.<br><br>Then a wall falls. &amp; more",
            Streaming: [(1, "To You, 2000 Years in the Future"), (2, "That Day")],
            Airing: [(1, 1365292800), (2, 1365897600)], Synonyms: ["AoT"]))
        .Add(new Entry(25777, "Shingeki no Kyojin Season 2", "Attack on Titan Season 2", Episodes: 12, Year: 2017, Prequel: 16498, Sequel: 99147,
            Streaming: [(1, "Beast Titan")]))
        .Add(new Entry(99147, "Shingeki no Kyojin Season 2 Part 2", "Attack on Titan Season 2 Part 2", Episodes: 6, Year: 2017, Prequel: 25777,
            Streaming: [(1, "Midnight Sun")]))
        .Add(new Entry(20, "Naruto", Episodes: 220, Year: 2002))
        .Add(new Entry(199, "Sen to Chihiro no Kamikakushi", "Spirited Away", Format: "MOVIE", Year: 2001, IdMal: 199));

    private static AniListMetadataProvider Provider(FakeAniList fake) => new(fake.Client());

    [Theory]
    [InlineData("16498", 16498, null, null, false)]
    [InlineData("show:16498", 16498, null, null, false)]
    [InlineData("movie:199", 199, null, null, false)]
    [InlineData("anilist:16498", 16498, null, null, false)]
    [InlineData("show:16498/season:2", 16498, 2, null, false)]
    [InlineData("show:16498/season:2/episode:7", 16498, 2, 7, false)]
    [InlineData("https://anilist.co/anime/16498/Shingeki-no-Kyojin/", 16498, null, null, false)]
    [InlineData("mal:16498", 16498, null, null, true)]
    [InlineData("https://myanimelist.net/anime/199/Spirited_Away", 199, null, null, true)]
    public void IdsInEveryAcceptedFormat_AreUnderstood(string text, int id, int? season, int? episode, bool mal)
    {
        var parsed = AniListMetadataProvider.ParseId(text);

        Assert.NotNull(parsed);
        Assert.Equal((id, season, episode, mal), (parsed!.Id, parsed.Season, parsed.Episode, parsed.Mal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tt0903747")]
    [InlineData("show:abc")]
    public void SomethingThatIsNotAnAniListId_IsRefused(string text) =>
        Assert.Null(AniListMetadataProvider.ParseId(text));

    [Fact]
    public void Descriptions_LoseTheirMarkupButKeepParagraphs()
    {
        var text = AniListMetadataProvider.CleanDescription("Humanity lives in cities.<br><br>Then a wall <i>falls</i>. &amp; more");

        Assert.Equal("Humanity lives in cities.\n\nThen a wall falls. & more", text);
        Assert.Null(AniListMetadataProvider.CleanDescription("  "));
    }

    [Fact]
    public async Task SearchingForAShow_FindsItsFirstSeason_EvenWhenALaterSeasonMatchesToo()
    {
        var provider = Provider(Catalogue());

        var result = await provider.SearchAsync(new MediaSearchContext("Attack on Titan", Year: 2013, MediaTypeName: "anime"));

        var best = result[0];
        Assert.Equal("show:16498", best.Metadata.ExternalId);
        Assert.Equal("anilist", best.Metadata.Source);
        Assert.Equal("Attack on Titan", best.Metadata.Title);
        Assert.True(best.Score >= 80);
    }

    [Fact]
    public async Task ASearchHitOnALaterSeason_IsTurnedIntoTheShow()
    {
        var provider = Provider(Catalogue());

        var result = await provider.SearchAsync(new MediaSearchContext("Attack on Titan Season 2", MediaTypeName: "anime"));

        Assert.Equal("show:16498", result[0].Metadata.ExternalId);
    }

    [Fact]
    public async Task AnimeSearch_NeverReturnsAFilm_AndAnimeMovieSearch_NeverReturnsAShow()
    {
        var provider = Provider(Catalogue());

        var shows = await provider.SearchAsync(new MediaSearchContext("Spirited Away", MediaTypeName: "anime"));
        var films = await provider.SearchAsync(new MediaSearchContext("Spirited Away", Year: 2001, MediaTypeName: "anime_movies"));
        var wrong = await provider.SearchAsync(new MediaSearchContext("Naruto", MediaTypeName: "anime_movies"));

        Assert.Empty(shows);
        Assert.Equal("movie:199", films[0].Metadata.ExternalId);
        Assert.Equal(24, films[0].Metadata.RuntimeMinutes);
        Assert.Empty(wrong);
    }

    [Fact]
    public async Task AKnownId_SkipsTheTextSearch_AndIsAnIdentifierMatch()
    {
        var fake = Catalogue();
        var provider = Provider(fake);

        var result = await provider.SearchAsync(new MediaSearchContext("whatever it is called locally", MediaTypeName: "anime",
            KnownExternalIds: new Dictionary<string, string> { ["mal"] = "16498" }));

        Assert.Equal("show:16498", result[0].Metadata.ExternalId);
        Assert.True(result[0].IdentifierMatch);
        Assert.Equal(100, result[0].Score);
    }

    [Fact]
    public async Task ADifferentYear_ScoresLowerThanTheRightOne()
    {
        var provider = Provider(Catalogue());

        var right = (await provider.SearchAsync(new MediaSearchContext("Naruto", Year: 2002, MediaTypeName: "anime")))[0].Score;
        var wrong = (await provider.SearchAsync(new MediaSearchContext("Naruto", Year: 1999, MediaTypeName: "anime")))[0].Score;

        Assert.True(right > wrong);
    }

    [Fact]
    public async Task EverythingAniListKnowsIsKept()
    {
        var provider = Provider(Catalogue());

        var show = await provider.GetByIdAsync("16498");

        Assert.Equal("Attack on Titan", show.Title);
        Assert.Equal(2013, show.Year);
        Assert.Equal("https://img/16498.jpg", show.PosterUrl);
        Assert.Equal("https://img/16498-banner.jpg", show.BannerUrl);
        Assert.Equal(8.4, show.Rating);
        Assert.Contains("Action", show.Genres);
        Assert.Equal(["Military"], show.Tags);                                // the spoiler tag is not published
        Assert.Contains("Shingeki no Kyojin", show.AlternateNames);
        Assert.Contains("AoT", show.AlternateNames);
        Assert.DoesNotContain("Attack on Titan", show.AlternateNames);
        Assert.Equal("Yuki Kaji", show.Cast[0].Name);                          // the voice actor is the cast member...
        Assert.Equal("Eren Yeager", show.Cast[0].Role);                        // ...and the character is the role
        Assert.Contains(show.Crew, c => c.Job == "Director" && c.Name == "Tetsuro Araki");
        Assert.Contains(show.Crew, c => c.Job == "Animation Studio" && c.Name == "Wit Studio");
        Assert.DoesNotContain(show.Crew, c => c.Name == "Some Publisher");

        var extra = show.ExtendedData!.Value;
        Assert.Equal(16498, extra.GetProperty("ids").GetProperty("anilist").GetInt32());
        Assert.Equal(16498, extra.GetProperty("ids").GetProperty("mal").GetInt32());
        Assert.Equal("2013-04-07", extra.GetProperty("air_date").GetString());
        Assert.Equal("Eren Yeager", extra.GetProperty("characters")[0].GetProperty("name").GetString());
        Assert.Equal("Wit Studio", extra.GetProperty("studios")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("english", "Attack on Titan")]
    [InlineData("romaji", "Shingeki no Kyojin")]
    [InlineData("native", "日本語")]
    public async Task TheTitleLanguageSettingChoosesTheName(string setting, string expected)
    {
        var provider = Provider(Catalogue());
        provider.Configure(new Dictionary<string, string> { ["title_language"] = setting });

        Assert.Equal(expected, (await provider.GetByIdAsync("16498")).Title);
    }

    [Fact]
    public async Task ASplitSeason_IsOneSeason_WithEpisodesNumberedStraightThrough()
    {
        var provider = Provider(Catalogue());

        var seasons = await Task.WhenAll(provider.GetByIdAsync("show:16498/season:1"), provider.GetByIdAsync("show:16498/season:2"),
                                         provider.GetByIdAsync("show:16498/season:3"));

        Assert.Equal("Season 1", seasons[0].Title);
        Assert.Equal("Season 2", seasons[1].Title);
        Assert.Equal(18, seasons[1].ExtendedData!.Value.GetProperty("episodes").GetInt32());   // 12 + 6
        Assert.Equal(2017, seasons[1].Year);
        Assert.Equal("show:16498/season:3", seasons[2].ExternalId);
        Assert.Equal("", seasons[2].Title);                                                    // there is no third season
    }

    [Fact]
    public async Task TheEpisodeList_KeepsCountingAcrossTheParts_AndCarriesTitlesAndAirDates()
    {
        var provider = Provider(Catalogue());

        var one = await provider.GetEpisodeListAsync("show:16498", 1);
        var two = await provider.GetEpisodeListAsync("show:16498", 2);

        Assert.Equal(25, one.Count);
        Assert.Equal("To You, 2000 Years in the Future", one[0].Title);
        Assert.Equal("2013-04-07", one[0].AirDate);
        Assert.Equal("https://img/e1.jpg", one[0].StillUrl);
        Assert.Equal("Episode 3", one[2].Title);                       // AniList has no title for it: still listed
        Assert.Equal(18, two.Count);
        Assert.Equal("Beast Titan", two[0].Title);
        Assert.Equal(13, two[12].EpisodeNumber);                       // part two's first episode
        Assert.Equal("Midnight Sun", two[12].Title);
    }

    [Fact]
    public async Task SeasonAndEpisodeSearches_UseTheParentsStoredId()
    {
        var provider = Provider(Catalogue());
        var ids = new Dictionary<string, string> { ["parent_anilist"] = "show:16498/season:2" };

        var season = await provider.SearchAsync(new MediaSearchContext("Season 2", HierarchyLevel: 1, ItemNumber: 2,
            KnownExternalIds: new Dictionary<string, string> { ["parent_anilist"] = "show:16498" }));
        var episode = await provider.SearchAsync(new MediaSearchContext("Midnight Sun", HierarchyLevel: 2, ItemNumber: 13, KnownExternalIds: ids));

        Assert.Equal("show:16498/season:2", season[0].Metadata.ExternalId);
        Assert.Equal("Midnight Sun", episode[0].Metadata.Title);
        Assert.Equal("show:16498/season:2/episode:13", episode[0].Metadata.ExternalId);
    }

    [Fact]
    public async Task ASeasonOrEpisodeThatDoesNotExist_IsNoMatch_NotAnError()
    {
        var provider = Provider(Catalogue());
        var ids = new Dictionary<string, string> { ["parent_anilist"] = "show:16498/season:1" };

        Assert.Empty(await provider.SearchAsync(new MediaSearchContext("x", HierarchyLevel: 2, ItemNumber: 99, KnownExternalIds: ids)));
        Assert.Empty(await provider.SearchAsync(new MediaSearchContext("x", HierarchyLevel: 1, ItemNumber: 9, KnownExternalIds: ids)));
        Assert.Empty(await provider.SearchAsync(new MediaSearchContext("x", HierarchyLevel: 2, ItemNumber: 1)));   // no parent id at all
    }

    [Fact]
    public async Task AnIdAniListDoesNotHave_ComesBackEmpty()
    {
        var provider = Provider(Catalogue());

        var result = await provider.GetByIdAsync("424242");

        Assert.Equal("", result.Title);
        Assert.Equal("424242", result.ExternalId);
    }

    [Fact]
    public async Task AMyAnimeListId_FindsTheEntry()
    {
        var provider = Provider(Catalogue());

        Assert.Equal("Spirited Away", (await provider.GetByIdAsync("mal:199")).Title);
        Assert.Equal("movie:199", (await provider.GetByIdAsync("https://myanimelist.net/anime/199/x")).ExternalId);
    }

    [Fact]
    public async Task AFailingService_IsAnError_NotAnEmptyAnswer()
    {
        var fake = Catalogue();
        fake.FailWith = System.Net.HttpStatusCode.InternalServerError;
        var provider = Provider(fake);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetByIdAsync("16498"));
        Assert.False(await provider.HealthCheckAsync());
    }

    [Fact]
    public async Task WalkingASeasonChain_DoesNotAskForTheSameEntryTwice()
    {
        var fake = Catalogue();
        var provider = Provider(fake);

        await provider.GetByIdAsync("show:16498/season:2");
        var before = fake.Requests;
        await provider.GetByIdAsync("show:16498/season:2");

        Assert.Equal(before, fake.Requests);
    }

    [Fact]
    public async Task AskingForEachEpisodeInTurn_FetchesTheAiringScheduleOnlyOnce()
    {
        var fake = Catalogue();
        var provider = Provider(fake);
        var ids = new Dictionary<string, string> { ["parent_anilist"] = "show:16498/season:1" };

        for (var e = 1; e <= 5; e++)
            await provider.SearchAsync(new MediaSearchContext("x", HierarchyLevel: 2, ItemNumber: e, KnownExternalIds: ids));

        Assert.Equal(1, fake.Queries.Count(q => q.Contains("airingSchedule")));
    }

    [Fact]
    public async Task HealthCheck_IsTrue_WhenAniListAnswers() =>
        Assert.True(await Provider(Catalogue()).HealthCheckAsync());

    [Fact]
    public void ItDeclaresWhatItHandles_AndItsSettings()
    {
        var provider = new AniListMetadataProvider();

        Assert.Equal(["anime", "anime_movies"], provider.GetSupportedMediaTypes().Select(t => t.MediaTypeName));
        Assert.Equal(3, provider.GetSupportedMediaTypes()[0].HierarchyLevels);
        Assert.Equal(1, provider.GetSupportedMediaTypes()[1].HierarchyLevels);
        Assert.Equal(["title_language", "include_adult"], provider.GetSettingsSchema().Settings.Select(s => s.Key));
        provider.Configure(new Dictionary<string, string>());   // empty settings must not throw
        provider.Dispose();
    }

    [Fact]
    public void TheManifest_DeclaresTheTwoStandardTasks_AndTheVersionMatchesTheCode()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "manifest.json");
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "manifest.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.Equal(new AniListMetadataProvider().Version, root.GetProperty("version").GetString());
        Assert.Equal(new AniListMetadataProvider().PluginId, root.GetProperty("plugin_id").GetString());
        var tasks = root.GetProperty("background_tasks").EnumerateArray().Select(t => t.GetProperty("task_id").GetString()).ToList();
        Assert.Equal(["fetch-missing-metadata", "resync-all-metadata"], tasks);
    }

    [Fact]
    public async Task NotConfigured_IsAClearError()
    {
        var provider = new AniListMetadataProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync(new MediaSearchContext("x")));
        Assert.False(await provider.HealthCheckAsync());
    }
}
