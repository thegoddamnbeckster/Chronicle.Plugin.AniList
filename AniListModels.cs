namespace Chronicle.Plugin.AniList;

// The parts of AniList's GraphQL responses this plugin reads. Property names follow the GraphQL field names
// (the client matches them without regard to case).

internal sealed class GqlResponse<T>
{
    public T? Data { get; set; }
    public List<GqlError>? Errors { get; set; }
}

internal sealed class GqlError
{
    public string? Message { get; set; }
    public int? Status { get; set; }
}

internal sealed class MediaEnvelope { public AniMedia? Media { get; set; } }

internal sealed class PageEnvelope { public MediaPage? Page { get; set; } }

internal sealed class MediaPage { public List<AniMedia>? Media { get; set; } }

internal sealed class AniMedia
{
    public int Id { get; set; }
    public int? IdMal { get; set; }
    public AniTitle? Title { get; set; }
    public List<string>? Synonyms { get; set; }
    public string? Type { get; set; }
    public string? Format { get; set; }
    public string? Status { get; set; }
    public string? Description { get; set; }
    public AniDate? StartDate { get; set; }
    public AniDate? EndDate { get; set; }
    public string? Season { get; set; }
    public int? SeasonYear { get; set; }
    public int? Episodes { get; set; }
    public int? Duration { get; set; }
    public string? CountryOfOrigin { get; set; }
    public string? Source { get; set; }
    public bool? IsAdult { get; set; }
    public int? AverageScore { get; set; }
    public int? MeanScore { get; set; }
    public int? Popularity { get; set; }
    public int? Favourites { get; set; }
    public List<string>? Genres { get; set; }
    public List<AniTag>? Tags { get; set; }
    public AniCover? CoverImage { get; set; }
    public string? BannerImage { get; set; }
    public AniTrailer? Trailer { get; set; }
    public AniStudios? Studios { get; set; }
    public AniCharacters? Characters { get; set; }
    public AniStaff? Staff { get; set; }
    public AniRelations? Relations { get; set; }
    public List<AniStreamingEpisode>? StreamingEpisodes { get; set; }
    public List<AniExternalLink>? ExternalLinks { get; set; }
    public AniAiringSchedule? AiringSchedule { get; set; }
    public AniNextAiring? NextAiringEpisode { get; set; }
}

internal sealed class AniTitle { public string? Romaji { get; set; } public string? English { get; set; } public string? Native { get; set; } }
internal sealed class AniDate { public int? Year { get; set; } public int? Month { get; set; } public int? Day { get; set; } }
internal sealed class AniTag { public string? Name { get; set; } public int? Rank { get; set; } public bool? IsMediaSpoiler { get; set; } }
internal sealed class AniCover { public string? ExtraLarge { get; set; } public string? Large { get; set; } public string? Color { get; set; } }
internal sealed class AniTrailer { public string? Id { get; set; } public string? Site { get; set; } }

internal sealed class AniStudios { public List<AniStudioEdge>? Edges { get; set; } }
internal sealed class AniStudioEdge { public bool? IsMain { get; set; } public AniStudio? Node { get; set; } }
internal sealed class AniStudio { public int Id { get; set; } public string? Name { get; set; } public bool? IsAnimationStudio { get; set; } }

internal sealed class AniName { public string? Full { get; set; } public string? Native { get; set; } }
internal sealed class AniImage { public string? Large { get; set; } }
internal sealed class AniPerson { public int Id { get; set; } public AniName? Name { get; set; } public AniImage? Image { get; set; } }

internal sealed class AniCharacters { public List<AniCharacterEdge>? Edges { get; set; } }
internal sealed class AniCharacterEdge { public string? Role { get; set; } public AniPerson? Node { get; set; } public List<AniPerson>? VoiceActors { get; set; } }

internal sealed class AniStaff { public List<AniStaffEdge>? Edges { get; set; } }
internal sealed class AniStaffEdge { public string? Role { get; set; } public AniPerson? Node { get; set; } }

internal sealed class AniRelations { public List<AniRelationEdge>? Edges { get; set; } }
internal sealed class AniRelationEdge { public string? RelationType { get; set; } public AniMedia? Node { get; set; } }

internal sealed class AniStreamingEpisode { public string? Title { get; set; } public string? Thumbnail { get; set; } public string? Url { get; set; } }
internal sealed class AniExternalLink { public string? Site { get; set; } public string? Url { get; set; } }

internal sealed class AniAiringSchedule { public AniPageInfo? PageInfo { get; set; } public List<AniAiring>? Nodes { get; set; } }
internal sealed class AniPageInfo { public bool? HasNextPage { get; set; } }
internal sealed class AniAiring { public int Episode { get; set; } public long AiringAt { get; set; } }
internal sealed class AniNextAiring { public int Episode { get; set; } public long AiringAt { get; set; } }
