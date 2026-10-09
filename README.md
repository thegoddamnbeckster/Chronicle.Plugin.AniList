# Chronicle.Plugin.AniList

Anime metadata for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) from [AniList](https://anilist.co/).

**No API key or account required.** Install it, and anime shows and films are matched and filled in.

Covers titles (English, romaji and native, with your pick as the item's name), synopses, genres, tags, studios, voice cast,
staff, ratings, posters, banners, season structure, episode titles and thumbnails, and air dates.

## Media types

| Type | Shape | What it matches |
|------|-------|-----------------|
| `anime` | Show / Season / Episode | AniList entries of format TV, TV short and ONA |
| `anime_movies` | one flat item | AniList entries of format MOVIE |

OVAs, specials and music videos are not matched.

## How seasons work

AniList has no "series with seasons": every season is its own entry, linked to its neighbours by *prequel* and *sequel*
relations. The plugin turns that back into Show / Season / Episode:

* The **show** is the first season's entry. A search that lands on a later season ("Attack on Titan Season 2") is
  followed back to the first.
* Each following TV-like **sequel** is the next season. Movies, OVAs and specials in between are skipped.
* A sequel whose title says it is a continuation ("Part 2", "2nd Cour", "Final Chapters") is **folded into the season before
  it**, and episodes keep counting up, so a split season of 12 + 10 episodes is one season of 22.

When the numbering you use differs from AniList's (some long shows are one AniList entry but several seasons elsewhere), use
**Fix match** on the season, or rely on the other TV providers for episode numbering.

## Episode details

AniList does not have a full episode guide. Episode titles and thumbnails come from the streaming listings it carries, and air
dates from its airing schedule, which exists mostly for shows from the last decade. An episode with no title is still listed
as "Episode N", and one with no recorded air date simply has none.

## Fix match

Enter any of: an AniList id (`16498`), an AniList URL, `mal:16498`, or a MyAnimeList URL.

## Settings

| Setting | Default | Meaning |
|---------|---------|---------|
| Title language | English (falls back to romaji) | Which title is the item's name; the others are kept as alternate names |
| Include adult titles | off | Allow entries AniList marks adult to be matched |

## External ids

Stored under source `anilist`: `show:{id}`, `show:{id}/season:{n}`, `show:{id}/season:{n}/episode:{e}`, `movie:{id}`. The
MyAnimeList id is published as a cross-reference (`ids.mal`) for other plugins.

## Rate limiting

AniList allows 90 requests a minute. Requests are spaced about a second apart, and a 429 answer is waited out using its
`Retry-After`. Entries just fetched are remembered for ten minutes while a season chain is walked.

## Build and test

```powershell
dotnet build -c Release
dotnet test tests/Chronicle.Plugin.AniList.Tests                      # canned data, no network
$env:ANILIST_LIVE = "1"; dotnet test tests/Chronicle.Plugin.AniList.Tests   # also the real AniList
```

The project expects the Chronicle repository next to it (`../Chronicle`), like the other Chronicle plugins.
