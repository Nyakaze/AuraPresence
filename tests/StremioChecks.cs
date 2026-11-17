#:property TargetFramework=net10.0-windows10.0.19041.0
#:project ../plugins/Aura.Presence.Stremio/Aura.Presence.Stremio.csproj
#:project ../src/Aura.Presence.Sdk/Aura.Presence.Sdk.csproj

using Aura.Presence.Sdk;
using Aura.Presence.Stremio;
using Aura.Presence.Services.AniList;

var fansub = ReleaseName.Parse("[Judas] Shinja Zero - S01E04.mkv");
Check(fansub is { Title: "Shinja Zero", Season: 1, Episode: 4, FansubStyle: true }, "fansub release with SxxEyy");

var scene = ReleaseName.Parse("eternally.yours.s01e04.1080p.web.h264-cakes%255BEZTVx.to%255D.mkv");
Check(scene is { Title: "Eternally Yours", Season: 1, Episode: 4, FansubStyle: false }, "double-encoded scene release");

var absolute = ReleaseName.Parse("[SubsPlease] Frieren - 12 (1080p) [ABCD1234].mkv");
Check(absolute is { Title: "Frieren", Season: null, Episode: 12, FansubStyle: true }, "anime absolute episode");

var movie = ReleaseName.Parse("Dune.Part.Two.2024.2160p.WEB-DL.mkv");
Check(movie is { Title: "Dune Part Two", Season: null, Episode: null }, "movie release");

var debrid = ReleaseName.Parse("EKAYWwFRO9u-Ji4T?name=As+a+Reincarnated+Aristocrat,+I'll+Use+My+Appraisal+Skill+to+Rise+in+the+World");
Check(debrid is { Title: "As a Reincarnated Aristocrat, I'll Use My Appraisal Skill to Rise in the World", Episode: null }, "debrid name parameter");

var dotted = ReleaseName.Parse("abc?name=Mr.+Robot+S02E03");
Check(dotted is { Title: "Mr Robot", Season: 2, Episode: 3 }, "name parameter keeps dotted titles");

var token = ReleaseName.Parse("T-tQZwfxGtdBm_NafX6D9dKYRd8aQHmoCtHOFlhFDChE5Bi8OJ8trI-kHP7lGX_ZD5bEUNv6wubYcUTWL6O673iCcQ.FdNYx3QIqZwXqLrEPvR");
Check(token is { HasTitle: false, Episode: null }, "opaque stream token has no title");

var encoded = ReleaseName.Parse("%255BIronclad%255D%2520Yasei%2520no%2520Last%2520Boss%2520ga%2520Arawareta%2521%2520-%2520S02E01%2520%255BWEB.1080p.AV1%255D.mkv");
Check(encoded is { Title: "Yasei no Last Boss ga Arawareta!", Season: 2, Episode: 1, FansubStyle: true }, "double-encoded fansub release");

var series = StremioVideoId.Parse("tt28313387:2:5");
Check(series.Ids[MediaIdKeys.Imdb] == "tt28313387" && series.Season == 2 && series.Episode == 5, "imdb series id");

var anime = StremioVideoId.Parse("kitsu:46474:7");
Check(anime.Ids[MediaIdKeys.Kitsu] == "46474" && anime.Season is null && anime.Episode == 7, "kitsu episode id");

var library = StremioLibrary.Parse("""
    {"uid":"u","items":{
      "tt43650265":{"_id":"tt43650265","name":"Full Clearing Another World under a Goddess with Zero Believers","type":"series","poster":"p1","_mtime":"2026-11-17T10:39:41.071Z","state":{"video_id":"tt43650265:1:4"}},
      "tt33078239":{"_id":"tt33078239","name":"Eternally Yours","type":"series","poster":"p2","_mtime":"2026-11-17T10:46:29.199Z","state":{"video_id":"tt33078239:1:4"}},
      "tt0000001":{"_id":"tt0000001","name":"Never Watched","type":"movie","_mtime":"2026-11-17T11:00:00Z","state":{"video_id":null}}}}
    """);
Check(library is { Name: "Eternally Yours", VideoId: "tt33078239:1:4" }, "latest watched library entry");

var switchedAt = DateTimeOffset.Parse("2026-11-17T10:44:29Z");
var stale = new LibraryEntry("tt43650265:1:4", "Full Clearing Another World under a Goddess with Zero Believers", "series", null,
    DateTimeOffset.Parse("2026-11-17T10:39:41Z"));
Check(!StremioLibrary.Matches(stale, scene, switchedAt, null), "old entry for another show is not matched");
Check(StremioLibrary.Matches(library!, scene, switchedAt, "tt43650265:1:4"), "fresh entry for the new show is matched");
Check(!StremioLibrary.Matches(library!, scene, switchedAt, "tt33078239:1:4"), "entry of the previous file is not reused");
Check(StremioLibrary.Matches(library!, scene, switchedAt.AddHours(1), null), "title match works without timing");
Check(!StremioLibrary.Matches(library!, ReleaseName.Parse("Eternally.Yours.S01E05.mkv"), switchedAt, null), "episode mismatch is rejected");
Check(!StremioLibrary.Matches(stale, fansub, switchedAt.AddHours(1), null), "romaji file name alone does not match the english entry");
Check(StremioLibrary.Matches(stale, fansub, switchedAt.AddHours(1), null, "Full Clearing Another World under a Goddess with Zero Believers"), "looked-up english title matches the entry");

Check(Aura.Presence.Services.Cinemeta.CinemetaClient.NamesMatch("Dune Part Two", "Dune: Part Two"), "cinemeta name match ignores punctuation");
Check(!Aura.Presence.Services.Cinemeta.CinemetaClient.NamesMatch("Dune Part Two", "Arrival"), "cinemeta name mismatch");

Check(AniListClient.TitlesMatch("Frieren: Beyond Journey's End Season 2", null, "Frieren: Beyond Journey's End"), "anilist title match");
Check(!AniListClient.TitlesMatch("Breaking Bad", "Bokura no", null), "anilist title mismatch");

var show = new NowPlaying { Title = "As a Reincarnated Aristocrat", Kind = MediaKind.Episode, Season = 3, Episode = 2 };
Check(StremioPlugin.StateLine(show) == "S03E02", "episode state line");
Check(StremioPlugin.StateLine(show with { State = PlaybackState.Paused }) == "S03E02 - Paused", "paused episode state line");
Check(StremioPlugin.StateLine(show with { Kind = MediaKind.Other }) is null, "an unknown episode shows no made-up label");
Check(StremioPlugin.StateLine(show with { Kind = MediaKind.Other, State = PlaybackState.Paused }) == "Paused", "an unknown episode still shows pause");
Check(StremioPlugin.StateLine(show with { Kind = MediaKind.Movie, Year = 2024 }) == "Movie (2024)", "movie state line");

var playerText = PlayerTitle.Parse("The Iceblade Sorcerer Shall Rule the World - The Boy Who Is the World's Strongest Sorcerer Dons a Mask... Again (2x2)");
Check(playerText is { Show: "The Iceblade Sorcerer Shall Rule the World", EpisodeName: "The Boy Who Is the World's Strongest Sorcerer Dons a Mask... Again", Season: 2, Episode: 2 },
    "player text gives show, episode name, season and episode");
Check(PlayerTitle.Parse("Frieren (1x13)") is { Show: "Frieren", EpisodeName: null, Season: 1, Episode: 13 }, "player text without an episode name");
Check(PlayerTitle.Parse("Dune: Part Two") is null, "player text of a movie has no episode");
Check(PlayerTitle.Parse("00:24:15") is null, "player times are not titles");

Console.WriteLine("All Stremio checks passed.");

static void Check(bool condition, string what)
{
    if (condition)
        return;

    Console.Error.WriteLine($"FAILED: {what}");
    Environment.Exit(1);
}
