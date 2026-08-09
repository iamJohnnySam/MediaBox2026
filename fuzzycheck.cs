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

Console.WriteLine("ok");
