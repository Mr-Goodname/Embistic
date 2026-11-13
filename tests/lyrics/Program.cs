using Embistic.Models;
using Embistic.Services;

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }

var multi = LyricsService.ParseLrc("[ar:Artist]\n[00:02.50][00:10.125]repeat\n[00:02.500]翻译\n[offset:-250]\n[00:12]<00:12.100>word <00:12.500>timed");
Check(multi.IsSynced && multi.Lines.Count == 3, "Multiple timestamp entries / translation grouping");
Check(multi.Lines[0].Time.TotalMilliseconds == 2250, "LRC file offset and two-digit fractional time");
Check(multi.Lines[0].Text == "repeat\n翻译", "Simultaneous translation retained");
Check(multi.Lines[1].Time.TotalMilliseconds == 9875, "Three-digit fractional time");
Check(multi.Lines[2].Text == "word timed", "Enhanced LRC timestamps stripped for line display");
Check(!multi.PlainText.Contains("Artist"), "Metadata removed from lyrics");
Check(LyricsService.FindLineIndex(multi, 2000) == -1, "Before first lyric");
Check(LyricsService.FindLineIndex(multi, 2000, 250) == 0, "Manual advance offset");
Check(LyricsService.FindLineIndex(multi, 10000) == 1, "Seek position lookup");
Check(LyricsService.FindLineIndex(multi, 99999999) == 2, "Past final line");
var hours = LyricsService.ParseLrc("[1:02:03.4]Long song\n[99:99]bad\n[02:05]valid");
Check(hours.Lines.Count == 2 && hours.Lines[1].Time.TotalMilliseconds == 3723400, "Hour timestamps; invalid seconds rejected");
var plain = LyricsService.ParseLrc("[ti:Song]\nText one\nText two", "Emby");
Check(!plain.IsSynced && plain.PlainText == "Text one\nText two" && plain.Source == "Emby", "Plain lyric fallback");

var track = new Track { Title = "旅の途中", Artist = "清浦夏実", Album = "旅の途中", DurationMs = 291000 };
var valid = new LyricsService.Candidate("旅の途中", "清浦夏実", "旅の途中", 291.1, false, "[00:01]Lyrics", "Lyrics");
Check(LyricsService.MatchScore(track, valid) >= 100, "Exact metadata matched");
Check(LyricsService.MatchScore(track, valid with { Artist = "Cover Artist" }) == 0, "Cover by different artist rejected");
Check(LyricsService.MatchScore(track, valid with { Title = "旅の途中 (instrumental)" }) == 0, "Different version rejected");
Check(LyricsService.MatchScore(track, valid with { Duration = 319 }) == 0, "Different recording duration rejected");
Check(LyricsService.MatchScore(track, valid with { Artist = "清浦夏実、Guest" }) >= 100, "Multi-artist catalog spelling accepted");

var importCache = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EmbisticLyricsTests", Guid.NewGuid().ToString("N"));
var importService = new LyricsService(importCache);
await importService.ImportLyricsAsync(track, "[00:01]My corrected lyric\n[00:02]Second line");
var reloadedImport = await new LyricsService(importCache).GetLyricsAsync(track, "[00:01]Server lyric");
Check(reloadedImport.Source == "本地 LRC" && reloadedImport.Lines[0].Text == "My corrected lyric", "Imported correction survives service restart and overrides server lyrics");
var otherTrack = new Track { Title = track.Title, Artist = "Other artist", Album = track.Album, DurationMs = track.DurationMs };
Check(await importService.GetImportedLyricsAsync(otherTrack) is null, "Import remains associated with exact song metadata");
await importService.ImportLyricsAsync(track, "[00:03]Updated correction");
Check((await importService.GetImportedLyricsAsync(track))?.Lines[0].Text == "Updated correction", "Re-import replaces previous correction atomically");
try
{
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await importService.ImportLyricsAsync(track, "[00:04]Canceled correction", canceled.Token);
    Check(false, "Canceled import must throw");
}
catch (OperationCanceledException) { checks++; }
Check((await importService.GetImportedLyricsAsync(track))?.Lines[0].Text == "Updated correction", "Canceled write preserves existing import");

if (args.Contains("--network"))
{
    var service = new LyricsService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EmbisticLyricsTests"));
    var result = await service.GetLyricsAsync(new Track { Title = "旅の途中", Artist = "清浦夏実", Album = "旅の途中", DurationMs = 293848 });
    Check(!string.IsNullOrWhiteSpace(result.Source) && result.IsSynced && result.Lines.Count > 10, "Public provider live lookup");
    Console.WriteLine($"Live lyrics: {result.Source}; synced={result.IsSynced}; lines={result.Lines.Count}");
}

var sampleIndex = Array.IndexOf(args, "--sample");
if (sampleIndex >= 0 && sampleIndex + 1 < args.Length)
{
    var samples = System.Text.Json.JsonSerializer.Deserialize<Track[]>(await System.IO.File.ReadAllTextAsync(args[sampleIndex + 1])) ?? [];
    var service = new LyricsService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EmbisticLyricsTests"));
    await Task.WhenAll(samples.Select(async sample =>
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.GetLyricsAsync(sample);
        Console.WriteLine($"SAMPLE {sample.Title} / {sample.Artist}: source={result.Source}; synced={result.IsSynced}; lines={result.Lines.Count}; {timer.Elapsed.TotalSeconds:0.0}s; {(result.IsSynced ? "OK" : result.PlainText)}");
    }));
}

Console.WriteLine($"PASS: {checks} lyric parser, matching, and timing checks.");
