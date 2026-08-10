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

Console.WriteLine("ok");
