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

Console.WriteLine("ok");
