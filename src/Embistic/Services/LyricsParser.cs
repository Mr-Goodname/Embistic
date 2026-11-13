using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Embistic.Models;

namespace Embistic.Services;

/// <summary>Reads timing from Enhanced LRC, YRC, QRC and TTML without inventing per-character timestamps.</summary>
internal static class LyricsParser
{
    private static readonly Regex Stamp = new(@"\[(?:(?<h>\d{1,2}):)?(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?\]", RegexOptions.CultureInvariant);
    private static readonly Regex WordStamp = new(@"<(?:(?<h>\d{1,2}):)?(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?>", RegexOptions.CultureInvariant);
    private static readonly Regex Offset = new(@"\[offset\s*:\s*(?<n>[+-]?\d+)\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Metadata = new(@"^\s*(?:\[(?:ar|al|ti|au|by|re|ve|length|id|tool|offset)\s*:[^\]]*\]\s*)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MillisecondLine = new(@"^\s*\[(?<start>\d+),(?<duration>\d+)\](?<body>.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex YrcWord = new(@"\((?<start>\d+),(?<duration>\d+),(?:\d+)\)(?<text>[^()]*)", RegexOptions.CultureInvariant);
    private static readonly Regex QrcWord = new(@"(?<text>[^()]+)\((?<start>\d+),(?<duration>\d+)\)", RegexOptions.CultureInvariant);

    public static LyricsDocument Parse(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return LyricsDocument.Empty();
        text = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.TrimStart().StartsWith('<'))
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
                var xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                if (xml.Root?.Name.LocalName == "tt") return ParseTtml(xml, source);
                var lyric = xml.Descendants().Attributes().FirstOrDefault(a => a.Name.LocalName == "LyricContent");
                if (lyric is not null) text = lyric.Value.Replace("\r\n", "\n").Replace('\r', '\n');
            }
            catch (XmlException) { /* A text lyric beginning with < is still a useful fallback. */ }
        }
        long offset = 0;
        foreach (Match match in Offset.Matches(text))
            if (long.TryParse(match.Groups["n"].Value, out var value)) offset = Math.Clamp(value, -86_400_000, 86_400_000);
        var lines = new List<LyricsLine>();
        var plain = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            if (Metadata.IsMatch(raw)) continue;
            var msLine = MillisecondLine.Match(raw);
            if (msLine.Success && Number(msLine, "start") is { } start && Number(msLine, "duration") is { } duration)
            {
                var body = msLine.Groups["body"].Value;
                var words = new List<LyricsWord>();
                var content = new StringBuilder();
                var pattern = YrcWord.IsMatch(body) ? YrcWord : QrcWord;
                foreach (Match word in pattern.Matches(body))
                {
                    var value = word.Groups["text"].Value;
                    var begin = Number(word, "start"); var length = Number(word, "duration");
                    if (begin is null || length is null) continue;
                    words.Add(new LyricsWord(Ms(begin.Value + offset), Ms(begin.Value + length.Value + offset), value, content.Length));
                    content.Append(value);
                }
                if (words.Count == 0) content.Append(body);
                lines.Add(new LyricsLine(Ms(start + offset), content.ToString()) { Words = words, EndTime = Ms(start + duration + offset) });
                if (content.Length > 0) plain.Add(content.ToString());
                continue;
            }
            var stamps = Stamp.Matches(raw);
            var bodyText = Offset.Replace(Stamp.Replace(raw, ""), "");
            var contentText = WordStamp.Replace(bodyText, "").Trim();
            if (contentText.Length > 0) plain.Add(contentText);
            foreach (Match stamp in stamps)
            {
                if (Clock(stamp) is not { } lineStart) continue;
                var wordStamps = WordStamp.Matches(bodyText);
                var words = new List<LyricsWord>();
                var content = new StringBuilder();
                if (wordStamps.Count > 0)
                {
                    content.Append(bodyText[..wordStamps[0].Index].TrimStart());
                    for (var i = 0; i < wordStamps.Count; i++)
                    {
                        var token = wordStamps[i];
                        var endIndex = i + 1 < wordStamps.Count ? wordStamps[i + 1].Index : bodyText.Length;
                        var value = bodyText[(token.Index + token.Length)..endIndex];
                        var begin = Clock(token);
                        var end = i + 1 < wordStamps.Count ? Clock(wordStamps[i + 1]) : null;
                        if (begin is not null && value.Length > 0)
                            words.Add(new LyricsWord(Ms(begin.Value + offset), Ms(Math.Max(begin.Value, end ?? begin.Value) + offset), value, content.Length));
                        content.Append(value);
                    }
                }
                else content.Append(contentText);
                lines.Add(new LyricsLine(Ms(lineStart + offset), content.ToString().TrimEnd()) { Words = words,
                    EndTime = wordStamps.Count > 0 && Clock(wordStamps[^1]) is { } last && wordStamps[^1].Index + wordStamps[^1].Length == bodyText.TrimEnd().Length ? Ms(last + offset) : null });
            }
        }
        return Document(lines, plain, source);
    }

    private static LyricsDocument Document(List<LyricsLine> timed, IEnumerable<string> plain, string source)
    {
        var lines = timed.OrderBy(x => x.Time).GroupBy(x => x.Time).Select(group =>
        {
            var text = new StringBuilder(); var words = new List<LyricsWord>(); TimeSpan? end = null;
            foreach (var line in group.DistinctBy(x => x.Text))
            {
                if (text.Length > 0 && line.Text.Length > 0) text.Append('\n');
                var shift = text.Length;
                words.AddRange(line.Words.Select(w => w with { TextIndex = w.TextIndex + shift }));
                text.Append(line.Text);
                if (line.EndTime is { } e && (end is null || e > end)) end = e;
            }
            return new LyricsLine(group.Key, text.ToString()) { Words = words, EndTime = end };
        }).ToArray();
        // Enhanced LRC defines starts. The next line bounds a final token when it has no explicit closing tag.
        for (var i = 0; i + 1 < lines.Length; i++)
            if (lines[i].Words.Any(w => w.End == w.Start))
                lines[i] = lines[i] with { Words = lines[i].Words.Select(w => w.End == w.Start ? w with { End = lines[i + 1].Time > w.Start ? lines[i + 1].Time : w.Start } : w).ToArray() };
        return new LyricsDocument { Lines = lines, PlainText = string.Join('\n', plain), Source = source, IsSynced = lines.Any(x => x.Text.Length > 0) };
    }

    private static LyricsDocument ParseTtml(XDocument xml, string source)
    {
        var lines = new List<LyricsLine>();
        foreach (var paragraph in xml.Descendants().Where(e => e.Name.LocalName == "p"))
        {
            var start = TtmlTime(paragraph.Attribute("begin")?.Value);
            if (start is null) continue;
            var end = TtmlTime(paragraph.Attribute("end")?.Value) ?? (TtmlTime(paragraph.Attribute("dur")?.Value) is { } duration ? start + duration : null);
            var content = new StringBuilder(); var words = new List<LyricsWord>();
            Append(paragraph, start.Value, end);
            lines.Add(new LyricsLine(Ms(start.Value), content.ToString()) { Words = words, EndTime = end is { } e ? Ms(e) : null });
            void Append(XElement node, double inheritedStart, double? inheritedEnd)
            {
                var begin = TtmlTime(node.Attribute("begin")?.Value) ?? inheritedStart;
                var finish = TtmlTime(node.Attribute("end")?.Value) ?? (TtmlTime(node.Attribute("dur")?.Value) is { } d ? begin + d : inheritedEnd);
                foreach (var child in node.Nodes())
                {
                    if (child is XElement element)
                    {
                        if (element.Name.LocalName == "br") content.Append('\n'); else Append(element, begin, finish);
                    }
                    else if (child is XText value)
                    {
                        var text = Regex.Replace(value.Value, @"\s+", " ");
                        if (string.IsNullOrWhiteSpace(text) && content.Length == 0) continue;
                        if (node.Name.LocalName == "span" && finish is { } f && f >= begin && text.Length > 0)
                            words.Add(new LyricsWord(Ms(begin), Ms(f), text, content.Length));
                        content.Append(text);
                    }
                }
            }
        }
        return Document(lines, lines.Select(l => l.Text), source);
    }

    private static double? TtmlTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"^(?<n>\d+(?:\.\d+)?)(?<u>ms|s|m|h)$", RegexOptions.CultureInvariant);
        if (match.Success && double.TryParse(match.Groups["n"].Value, CultureInfo.InvariantCulture, out var n))
            return n * (match.Groups["u"].Value switch { "ms" => 1, "s" => 1000, "m" => 60_000, _ => 3_600_000 });
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time)) return time.TotalMilliseconds;
        return null;
    }
    private static long? Number(Match match, string group) => long.TryParse(match.Groups[group].Value, out var n) && n <= 86_400_000 ? n : null;
    private static long? Clock(Match match)
    {
        var h = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var m = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var s = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        if (s > 59 || (match.Groups["h"].Success && m > 59)) return null;
        var f = match.Groups["f"].Value;
        return ((h * 60L + m) * 60 + s) * 1000 + (f.Length > 0 ? int.Parse(f.PadRight(3, '0'), CultureInfo.InvariantCulture) : 0);
    }
    private static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(Math.Clamp(value, -172_800_000, 172_800_000));
}
