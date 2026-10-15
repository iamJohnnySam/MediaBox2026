#:project MediaBox2026/MediaBox2026.csproj
using System.Diagnostics;
using MediaBox2026.Services;

// same show, punctuation differs -> must be >= 0.5 (FindTvShow's threshold)
Debug.Assert(FileNameParser.FuzzyMatch("X-Men 97", "X Men 97") == 1.0);
Debug.Assert(FileNameParser.FuzzyMatch("X-Men 97 (2024)", "X Men 97") >= 0.5);
Debug.Assert(FileNameParser.FuzzyMatch("Marvel's Daredevil", "Marvels Daredevil") == 1.0);
Debug.Assert(FileNameParser.FuzzyMatch("Spider-Man", "Spider Man") == 1.0);
Debug.Assert(FileNameParser.FuzzyMatch("Pokemon", "Pokemon") == 1.0);

// different shows -> must stay under it
Debug.Assert(FileNameParser.FuzzyMatch("Ted", "Superman The Animated Series") < 0.5);
Debug.Assert(FileNameParser.FuzzyMatch("X-Men 97", "The X Files") < 0.5);
Debug.Assert(FileNameParser.FuzzyMatch("Ted Lasso", "Ted") >= 0.5); // subset rule, unchanged

// watchlist quality tiers: acceptable -> auto, above-standard -> wait/ask, above 1080p -> ask only
Debug.Assert(FileNameParser.IsQualityAcceptable("720p"));
Debug.Assert(!FileNameParser.IsQualityAcceptable("1080p"));
Debug.Assert(!FileNameParser.IsAbove1080p("1080p"));   // 1080p can auto-download after the window
Debug.Assert(FileNameParser.IsAbove1080p("2160p"));    // 4K never does

// "3D" must not read as resolution 3 and slip past the <=720 standard (YTS quality string)
Debug.Assert(!FileNameParser.IsQualityAcceptable("3D"));
Debug.Assert(!FileNameParser.IsQualityAcceptable("Angry Birds (2016) [3D] [HSBS] [YTS.AG]"));
Debug.Assert(FileNameParser.IsQualityAcceptable("480p"));
Debug.Assert(FileNameParser.IsQualityAcceptable(null));  // unknown stays acceptable

// A sequel scores 0.90 against its predecessor — above FindMovie's 0.6 — so the
// "already in library" watchlist guard must gate on the year, never on the name alone.
Debug.Assert(FileNameParser.FuzzyMatch("The Angry Birds Movie", "The Angry Birds Movie 2") > 0.6);
Debug.Assert(FileNameParser.FuzzyMatch("The Good Dinosaur", "Good Dinosaur") > 0.6);

// fallback picks the smallest above-standard release; "3D" sorts last
Debug.Assert(MovieWatchlistService.Resolution("1080p") < MovieWatchlistService.Resolution("2160p"));
Debug.Assert(MovieWatchlistService.Resolution("2160p") < MovieWatchlistService.Resolution("3D"));

// The "already in the library" guard parses a release name first. A season pack carries no SxxExx
// and therefore parses as a film — the TV-show lookup, not this flag, is what excludes it.
Debug.Assert(FileNameParser.Parse("Angry Birds (2016) [3D] [HSBS] [YTS.AG]").CleanName == "Angry Birds");
Debug.Assert(FileNameParser.Parse("Angry Birds (2016) [3D] [HSBS] [YTS.AG]").Year == 2016);
Debug.Assert(FileNameParser.Parse("Angry.Birds.2016.1080p.BluRay.6CH.ShAaNiG.mkv").CleanName == "Angry Birds");
Debug.Assert(!FileNameParser.Parse("The Grand Tour 2016 Seasons 1 to 5 Complete 720p WEB x264 [i_c]").IsTvShow);
Debug.Assert(FileNameParser.Parse("Futurama.S14E03.480p.x264-mSD[EZTVx.to].mkv").IsTvShow);

// A subtitle is renamed onto its video's stem, keeping its own name as the tail — that prefix is
// what makes Jellyfin associate it, and the tail is what keeps two subs for one episode distinct.
Debug.Assert(DownloadOrganizerService.BuildSubtitleName("The.Office.S01E03.mkv", "3_English.srt")
             == "The.Office.S01E03.3_English.srt");
Debug.Assert(DownloadOrganizerService.BuildSubtitleName("Angry.Birds.2016.1080p.mkv", "SDH.eng.HI.srt")
             == "Angry.Birds.2016.1080p.SDH.eng.HI.srt");
// two different subs for one video stay different files rather than colliding into (1)
Debug.Assert(DownloadOrganizerService.BuildSubtitleName("Ep.mkv", "3_English.srt")
          != DownloadOrganizerService.BuildSubtitleName("Ep.mkv", "4_English.srt"));

// planned downloads: the window opens on the configured day+hour, once per calendar month
var day14 = new DateTime(2026, 8, 14, 9, 0, 0);
Debug.Assert(TransmissionMonitorService.IsPromptDue(day14, 14, 8, null));
Debug.Assert(!TransmissionMonitorService.IsPromptDue(day14, 14, 8, new DateTime(2026, 8, 14, 8, 5, 0))); // already asked this month
Debug.Assert(TransmissionMonitorService.IsPromptDue(day14, 14, 8, new DateTime(2026, 7, 14, 8, 5, 0)));  // last month -> due again
Debug.Assert(!TransmissionMonitorService.IsPromptDue(new DateTime(2026, 8, 14, 7, 0, 0), 14, 8, null));  // before the hour
Debug.Assert(!TransmissionMonitorService.IsPromptDue(new DateTime(2026, 8, 13, 9, 0, 0), 14, 8, null));  // wrong day
Debug.Assert(!TransmissionMonitorService.IsPromptDue(day14, 0, 8, null));                                // 0 disables
// a year apart on the same month must still be due, or it would skip after 12 months
Debug.Assert(TransmissionMonitorService.IsPromptDue(day14, 14, 8, new DateTime(2025, 8, 14, 8, 5, 0)));

// the window closes at midnight, not 24h later
Debug.Assert(TransmissionMonitorService.ShouldRepark(new DateTime(2026, 8, 15, 0, 5, 0), new DateTime(2026, 8, 14, 23, 50, 0)));
Debug.Assert(!TransmissionMonitorService.ShouldRepark(new DateTime(2026, 8, 14, 23, 59, 0), new DateTime(2026, 8, 14, 8, 0, 0)));
Debug.Assert(!TransmissionMonitorService.ShouldRepark(new DateTime(2026, 8, 14, 9, 0, 0), null));

// new-download announcements report MB under a GB, GB at or above it
Debug.Assert(TransmissionMonitorService.FormatSize(714_663_936) == "682 MB");
Debug.Assert(TransmissionMonitorService.FormatSize(1_594_641_057) == "1.49 GB");

// A paired release is ONE file holding two aired episodes ("S13E01E02"). Both numbers have to
// survive, or MediaCatalogService records only the first, HasEpisode reports the second missing,
// and the RSS monitor fetches the same file again under whatever numbering the tracker used.
var pair = FileNameParser.Parse("Paw Patrol S13E01E02 Pups Solve a Snap-N-Wrap Problem - Pups Save Helga and Dingers 1080p NF WEB-DL DDP5 1 x264-LAZY[EZTVx.to].mkv");
Debug.Assert(pair.Season == 13 && pair.Episode == 1 && pair.EpisodeEnd == 2);
Debug.Assert(FileNameParser.Parse("Paw Patrol S12E13E14 Pups Save a Haunted Farm - Pups Save the Adventure Bay Boogie 1080p.mkv").EpisodeEnd == 14);
Debug.Assert(FileNameParser.Parse("Show.S01E01-E02.720p.mkv").EpisodeEnd == 2);   // dash-separated
Debug.Assert(FileNameParser.Parse("Show.S01E01 E02.720p.mkv").EpisodeEnd == 2);   // space-separated

// A single episode must stay single. The quality tag is the trap: the second number is only read
// when a literal "E" precedes it, or "S08E29.720p" would close the span at episode 720.
Debug.Assert(FileNameParser.Parse("Paw.Patrol.S08E29.720p.NF.WEBRip.x264-GalaxyTV.mkv").Episode == 29);
Debug.Assert(FileNameParser.Parse("Paw.Patrol.S08E29.720p.NF.WEBRip.x264-GalaxyTV.mkv").EpisodeEnd == null);
Debug.Assert(FileNameParser.Parse("Futurama.S14E03.480p.x264-mSD[EZTVx.to].mkv").EpisodeEnd == null);
Debug.Assert(FileNameParser.Parse("PAW.Patrol.S01E01.Pups.and.the.Kitty-tastrophe.720p.mp4").EpisodeEnd == null);
// a backwards or absurdly wide span is a mis-parse, not a multi-episode file
Debug.Assert(FileNameParser.Parse("Show.S01E05E02.720p.mkv").EpisodeEnd == null);
Debug.Assert(FileNameParser.Parse("Show.S01E01E99.720p.mkv").EpisodeEnd == null);
// the span must not leak into the show name
Debug.Assert(FileNameParser.Parse("Paw Patrol S13E01E02 Pups Solve a Snap-N-Wrap Problem 1080p.mkv").CleanName == "Paw Patrol");

// Multi-episode separators seen in this library: "E01E02", "E01-E02", "E01+E02",
// bare "E17-18" and "E21 & 22".
Debug.Assert(FileNameParser.Parse("The Office S04E01+E02 Fun Run.mp4").EpisodeEnd == 2);
Debug.Assert(FileNameParser.Parse("Friends.S10E17-18.The.Last.One.720p.BluRay.mkv").EpisodeEnd == 18);
Debug.Assert(FileNameParser.Parse("Teenage Mutant Ninja Turtles S05E21 & 22 Planet of the Turtleoids.mkv").EpisodeEnd == 22);
Debug.Assert(FileNameParser.Parse("Spidey.and.His.Amazing.Friends.S04E15-E16.Mixing.Up.Trouble.mkv").EpisodeEnd == 16);

// An episode whose TITLE opens with a number must stay single. Whitespace is not a range
// marker, and claiming episodes that aren't there suppresses their download for good.
Debug.Assert(FileNameParser.Parse("Person of Interest S05E04 6,741.mp4").EpisodeEnd == null);
Debug.Assert(FileNameParser.Parse("Teenage Mutant Ninja Turtles S03E14 20,000 Leaks Under the City.mkv").EpisodeEnd == null);
Debug.Assert(FileNameParser.Parse("The Jetsons S03E03 9 to 5 to 9.mp4").EpisodeEnd == null);
Debug.Assert(FileNameParser.Parse("How I Met Your Mother S07E14 46 Minutes.mp4").EpisodeEnd == null);
Debug.Assert(FileNameParser.Parse("New Girl S05E14 300 Feet.mkv").EpisodeEnd == null);
// nor may a resolution, 4K or channel-count tag be read as the closing episode
Debug.Assert(FileNameParser.Parse("Paw.Patrol.S08E29.720p.NF.WEBRip.mkv").EpisodeEnd == null);
Debug.Assert(FileNameParser.Parse("Show.S01E05-2160p.WEB.mkv").EpisodeEnd == null);
Debug.Assert(FileNameParser.Parse("Show.S01E05-4K.WEB.mkv").EpisodeEnd == null);
Debug.Assert(FileNameParser.Parse("Show.S01E05-2CH.x265.mkv").EpisodeEnd == null);

// "Ep. 02-03" is a paired file in the same sense as "S13E01E02".
Debug.Assert(FileNameParser.Parse("PLANET SHEEN - Ep. 02-03 - Is This Cute and The Boy Next Dorkus (720p - Web-DL).mp4").Episode == 2);
Debug.Assert(FileNameParser.Parse("PLANET SHEEN - Ep. 02-03 - Is This Cute and The Boy Next Dorkus (720p - Web-DL).mp4").EpisodeEnd == 3);
Debug.Assert(FileNameParser.Parse("PLANET SHEEN - Ep. 50 - Banana Quest (720p - Web-DL).mp4").EpisodeEnd == null);

// An uppercase "SxxXnn" is a short or webisode in this library, not episode nn. Reading
// "The Office S09X01 Retrospective" as S09E01 made the extra claim "New Guys", so the real
// episode looked present and was never fetched. Better unplaced than wrong.
Debug.Assert(!FileNameParser.Parse("The Office S09X01 Retrospective.mp4").IsTvShow);
Debug.Assert(!FileNameParser.Parse("Shaun the Sheep S04X01 The Farmer's Llamas (2015 360p re-blurip).mp4").IsTvShow);
// ...while the lowercase x stays the season-episode separator it always was
Debug.Assert(FileNameParser.Parse("The Wire [1x07] One Arrest.mkv").Season == 1);
Debug.Assert(FileNameParser.Parse("The Wire [1x07] One Arrest.mkv").Episode == 7);
Debug.Assert(FileNameParser.Parse("Superman The Animated Series - 2x06 #19 - Identity Crisis.avi").Episode == 6);

// Season-less shows numbered "E01" index as season 1 rather than not at all.
Debug.Assert(FileNameParser.Parse("Mr. Bean E01 Mr. Bean.mp4").Season == 1);
Debug.Assert(FileNameParser.Parse("Mr. Bean E01 Mr. Bean.mp4").Episode == 1);
Debug.Assert(FileNameParser.Parse("Shaun the Sheep Championsheeps E01 The Relay.mp4").Episode == 1);
// but a bare E must not be read out of the middle of a word, nor turn a film into an episode
Debug.Assert(!FileNameParser.Parse("Se7en.1995.1080p.BluRay.mkv").IsTvShow);
Debug.Assert(!FileNameParser.Parse("Mr Bean Animated Series - Art Thief.mp4").IsTvShow);
Debug.Assert(!FileNameParser.Parse("The SpongeBob Musical Live on Stage (2019 360p re-webrip).mp4").IsTvShow);

// Title tokens keep digits, so "Pt 1" and "Pt 2" are not the same episode. They also carry no
// word list: what counts as filler is decided per show by MediaCatalogService.
var pt1 = FileNameParser.EpisodeTitleTokens("How I Met Your Mother S08E11 The Final Page Pt 1.mp4");
var pt2 = FileNameParser.EpisodeTitleTokens("How I Met Your Mother S08E12 The Final Page Pt 2.mp4");
Debug.Assert(pt1.Contains("1") && pt2.Contains("2") && !pt1.SetEquals(pt2));

// Same two segments, numbers shifted by the tracker: the titles are identical.
var onDisk = FileNameParser.EpisodeTitleTokens("Paw Patrol S13E11E12 Pups Save a Woodland Walrus Pups Save a DJ in a Jam 720p AMZN WEB-DL.mkv");
var repost = FileNameParser.EpisodeTitleTokens("Paw Patrol S13E12E13 Pups Save a Woodland Walrus - Pups Save a DJ in a Jam 1080p AMZN WEB-DL.mkv");
Debug.Assert(onDisk.SetEquals(repost));
Debug.Assert(!onDisk.Contains("720p") && !onDisk.Contains("amzn"));          // metadata is cut at the quality tag
var fresh = FileNameParser.EpisodeTitleTokens("Paw Patrol S13E03E04 Pups Save the Orienteers - Pups Save Teatime 1080p.mkv");
Debug.Assert(!fresh.SetEquals(onDisk) && !fresh.IsSubsetOf(onDisk) && !onDisk.IsSubsetOf(fresh));
Debug.Assert(onDisk.Contains("walrus") && onDisk.Contains("woodland") && !onDisk.Contains("orienteers"));
Debug.Assert(fresh.Contains("orienteers") && fresh.Contains("teatime") && !fresh.Contains("walrus"));

// Filler is whatever a show repeats on most of its own files -- no word list decides it.
// In PAW Patrol that is the title formula; in Reacher it is the release group.
var paw = new MediaBox2026.Models.TvShow { Name = "Paw Patrol", Episodes = [
    new() { Season = 13, Episode = 1,  FileName = "Paw Patrol S13E01E02 Pups Save the Snap - Pups Save Helga 720p.mkv" },
    new() { Season = 13, Episode = 3,  FileName = "Paw Patrol S13E03E04 Pups Save the Orienteers - Pups Save Teatime 720p.mkv" },
    new() { Season = 13, Episode = 11, FileName = "Paw Patrol S13E11E12 Pups Save a Woodland Walrus Pups Save a DJ in a Jam 720p.mkv" },
    new() { Season = 13, Episode = 5,  FileName = "Paw Patrol S13E05E06 Pups Save the Alien - Pups Save a Duckling 720p.mkv" },
]};
var pawFiller = MediaCatalogService.ShowFillerWords(paw);
Debug.Assert(pawFiller.Contains("pups") && pawFiller.Contains("save"));      // the formula
Debug.Assert(!pawFiller.Contains("walrus") && !pawFiller.Contains("teatime")); // the identity

var reacher = new MediaBox2026.Models.TvShow { Name = "Reacher", Episodes = [
    new() { Season = 3, Episode = 1, FileName = "Reacher.S03E01.WEB.x264-TORRENTGALAXY.mkv" },
    new() { Season = 3, Episode = 2, FileName = "Reacher.S03E02.WEB.x264-TORRENTGALAXY.mkv" },
    new() { Season = 3, Episode = 3, FileName = "Reacher.S03E03.WEB.x264-TORRENTGALAXY.mkv" },
]};
var reacherFiller = MediaCatalogService.ShowFillerWords(reacher);
// titleless releases: every word is filler, so nothing is left to call a duplicate on
Debug.Assert(reacherFiller.Contains("torrentgalaxy") && reacherFiller.Contains("web"));
Debug.Assert(FileNameParser.EpisodeTitleTokens("Reacher.S03E04.WEB.x264-TORRENTGALAXY.mkv")
             .Except(reacherFiller).Count() < 2);
// a name with no SxxExx tag has nothing to measure
Debug.Assert(FileNameParser.EpisodeTitleTokens("Paw Patrol Season 13 Complete 1080p.mkv").Count == 0);

// An episode gone from a show's folder is only a deletion if its file has left the library.
// Splitting Planet Sheen out of Jimmy Neutron wrote 18 tombstones that each said "never
// re-download" about an episode that had merely been filed under the show it belongs to.
{
    var previous = new Dictionary<(int, int), string>
    {
        [(1, 34)] = "PLANET SHEEN - Ep. 34-35 - Scape Doat and Haute CuiSheen (720p - Web-DL).mp4",
        [(1, 50)] = "PLANET SHEEN - Ep. 50 - Banana Quest (720p - Web-DL).mp4",
        [(1, 2)]  = "JIMMY NEUTRON - S01 E02 - Normal Boy (576p - DVDRip).mp4",
        [(1, 3)]  = "JIMMY NEUTRON - S01 E03 - Birth of a Salesman (576p - DVDRip).mp4",
    };
    // E03 is still in this folder; the two Planet Sheen files moved to their own show; E02 was
    // genuinely deleted.
    var current = new[] { (1, 3) };
    var library = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PLANET SHEEN - Ep. 34-35 - Scape Doat and Haute CuiSheen (720p - Web-DL).mp4",
        "PLANET SHEEN - Ep. 50 - Banana Quest (720p - Web-DL).mp4",
        "JIMMY NEUTRON - S01 E03 - Birth of a Salesman (576p - DVDRip).mp4",
    };

    var (gone, moved) = MediaCatalogService.ClassifyRemovedEpisodes(previous, current, library);

    Debug.Assert(gone.Count == 1 && gone[0] == (1, 2));            // the only real deletion
    Debug.Assert(moved.Count == 2);                                 // both relocated, not tombstoned
    Debug.Assert(moved.All(m => m.FileName.StartsWith("PLANET SHEEN")));
    Debug.Assert(!gone.Contains((1, 3)));                           // still on disk, untouched
    Debug.Assert(!gone.Contains((1, 34)) && !gone.Contains((1, 50)));

    // With nothing moved, every absence is a deletion -- the behaviour this guard must not lose.
    var (allGone, noneMoved) = MediaCatalogService.ClassifyRemovedEpisodes(
        previous, Array.Empty<(int, int)>(), []);
    Debug.Assert(allGone.Count == 4 && noneMoved.Count == 0);
}

Console.WriteLine("ok");
