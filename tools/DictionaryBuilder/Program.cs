using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

// Converts the plain-text Project Gutenberg edition of Webster's Revised Unabridged
// Dictionary (1913), EBook #29765 (public domain in the US), into a compact SQLite
// lookup database bundled with the DisplayBook reader.
//
// Usage: dotnet run -- <path-to-raw-gutenberg-txt> <path-to-output-db>
//
// Source text: https://www.gutenberg.org/ebooks/29765.txt.utf-8
// See ../../src/DisplayBook.Viewer/Resources/Raw/Dictionary/SOURCE.md for provenance.

var headwordLineRegex = new Regex(@"^[A-Z][A-Z0-9 '\-.;,]{0,48}$", RegexOptions.Compiled);

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run -- <path-to-raw-gutenberg-txt> <path-to-output-db>");
    return 1;
}

var inputPath = args[0];
var outputPath = args[1];

Console.WriteLine($"Reading {inputPath} ...");
var rawText = await File.ReadAllTextAsync(inputPath, Encoding.UTF8);

var body = ExtractBody(rawText);
Console.WriteLine($"Body extracted: {body.Length:N0} characters.");

var entries = ParseEntries(body);
Console.WriteLine($"Parsed {entries.Count:N0} entries.");

// The source text repeats a headword for each distinct part of speech (e.g. "EXAMPLE"
// appears once as a noun and again as a verb). Group by the split headword text and
// join those senses into a single combined definition instead of overwriting one with
// another under the same key.
var mergedEntries = new List<(string Word, string Definition)>();
foreach (var group in entries
             .SelectMany(entry => SplitHeadwords(entry.Word).Select(word => (Word: word, entry.Definition)))
             .GroupBy(pair => pair.Word, StringComparer.Ordinal))
{
    var definitions = group.Select(pair => pair.Definition).Distinct().ToList();
    mergedEntries.Add((group.Key, string.Join(" ", definitions)));
}

Console.WriteLine($"Merged into {mergedEntries.Count:N0} unique headwords.");

if (File.Exists(outputPath))
{
    File.Delete(outputPath);
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

await using (var connection = new SqliteConnection($"Data Source={outputPath}"))
{
    await connection.OpenAsync();

    await using (var createCommand = connection.CreateCommand())
    {
        createCommand.CommandText = """
            CREATE TABLE Definitions (
                Word TEXT NOT NULL,
                WordKey TEXT NOT NULL,
                Definition TEXT NOT NULL
            );
            """;
        await createCommand.ExecuteNonQueryAsync();
    }

    var rowCount = 0;
    await using (var transaction = connection.BeginTransaction())
    {
        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = "INSERT INTO Definitions (Word, WordKey, Definition) VALUES ($word, $wordKey, $definition);";
        var wordParam = insertCommand.CreateParameter();
        wordParam.ParameterName = "$word";
        insertCommand.Parameters.Add(wordParam);
        var wordKeyParam = insertCommand.CreateParameter();
        wordKeyParam.ParameterName = "$wordKey";
        insertCommand.Parameters.Add(wordKeyParam);
        var definitionParam = insertCommand.CreateParameter();
        definitionParam.ParameterName = "$definition";
        insertCommand.Parameters.Add(definitionParam);

        var seenKeys = new HashSet<(string WordKey, string Word)>();
        foreach (var entry in mergedEntries)
        {
            foreach (var wordKey in BuildWordKeys(entry.Word))
            {
                if (!seenKeys.Add((wordKey, entry.Word)))
                {
                    continue;
                }

                wordParam.Value = entry.Word;
                wordKeyParam.Value = wordKey;
                definitionParam.Value = entry.Definition;
                await insertCommand.ExecuteNonQueryAsync();
                rowCount++;
            }
        }

        await transaction.CommitAsync();
    }

    await using (var indexCommand = connection.CreateCommand())
    {
        indexCommand.CommandText = "CREATE INDEX IX_Definitions_WordKey ON Definitions(WordKey);";
        await indexCommand.ExecuteNonQueryAsync();
    }

    Console.WriteLine($"Inserted {rowCount:N0} rows.");
}

// SQLite leaves free pages behind after CREATE TABLE/INDEX inside a session; VACUUM
// compacts the file to its real size before we ship it as an app asset.
await using (var vacuumConnection = new SqliteConnection($"Data Source={outputPath}"))
{
    await vacuumConnection.OpenAsync();
    await using var vacuumCommand = vacuumConnection.CreateCommand();
    vacuumCommand.CommandText = "VACUUM;";
    await vacuumCommand.ExecuteNonQueryAsync();
}

var finalSize = new FileInfo(outputPath).Length;
Console.WriteLine($"Wrote {outputPath} ({finalSize / 1024.0 / 1024.0:F1} MB).");
return 0;

static string ExtractBody(string rawText)
{
    var startMatch = Regex.Match(rawText, @"\*\*\* START OF THE PROJECT GUTENBERG EBOOK.*?\*\*\*", RegexOptions.IgnoreCase);
    var endMatch = Regex.Match(rawText, @"\*\*\* END OF THE PROJECT GUTENBERG EBOOK.*?\*\*\*", RegexOptions.IgnoreCase);
    if (!startMatch.Success || !endMatch.Success || endMatch.Index <= startMatch.Index)
    {
        throw new InvalidOperationException("Could not find Project Gutenberg START/END markers in the source text.");
    }

    var bodyStart = startMatch.Index + startMatch.Length;
    return rawText[bodyStart..endMatch.Index];
}

bool IsHeadwordLine(string line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        return false;
    }

    return headwordLineRegex.IsMatch(line) && line.Any(char.IsLetter);
}

List<(string Word, string Definition)> ParseEntries(string body)
{
    var paragraphs = Regex.Split(body, @"\r?\n[ \t]*\r?\n+")
        .Select(paragraph => paragraph.Trim('\r', '\n', ' ', '\t'))
        .Where(paragraph => paragraph.Length > 0)
        .ToList();

    var entries = new List<(string Word, string Definition)>();
    string? currentHeadword = null;
    var definitionBuilder = new StringBuilder();

    void FlushCurrent()
    {
        if (currentHeadword is not null && definitionBuilder.Length > 0)
        {
            entries.Add((currentHeadword, CollapseWhitespace(definitionBuilder.ToString())));
        }

        definitionBuilder.Clear();
    }

    foreach (var paragraph in paragraphs)
    {
        var newlineIndex = paragraph.IndexOf('\n');
        var firstLine = (newlineIndex >= 0 ? paragraph[..newlineIndex] : paragraph).Trim();

        if (IsHeadwordLine(firstLine))
        {
            FlushCurrent();
            currentHeadword = firstLine;
            continue;
        }

        if (currentHeadword is null)
        {
            continue;
        }

        // Most senses are marked with a leading "Defn:"; some (e.g. "BOOK") give the
        // sense text directly, optionally after a bare "1./2./3." sense number, with
        // no "Defn:" marker at all. Etym-only paragraphs (etymology long enough to
        // land in its own paragraph rather than sharing the headword's line) aren't
        // useful for a quick lookup, so those are the one paragraph kind we skip.
        if (paragraph.StartsWith("Etym:", StringComparison.Ordinal))
        {
            continue;
        }

        var content = paragraph.StartsWith("Defn:", StringComparison.Ordinal)
            ? paragraph["Defn:".Length..].Trim()
            : paragraph;

        if (content.Length == 0)
        {
            continue;
        }

        if (definitionBuilder.Length > 0)
        {
            definitionBuilder.Append(' ');
        }

        definitionBuilder.Append(content);
    }

    FlushCurrent();
    return entries;
}

static string CollapseWhitespace(string text) => Regex.Replace(text, @"\s+", " ").Trim();

static IEnumerable<string> SplitHeadwords(string headwordLine)
{
    foreach (var part in headwordLine.Split(';'))
    {
        var trimmed = part.Trim();
        if (trimmed.Length > 0)
        {
            yield return trimmed;
        }
    }
}

static IEnumerable<string> BuildWordKeys(string headword)
{
    var lower = headword.ToLowerInvariant().Trim();
    yield return lower;

    var withoutHyphens = lower.Replace("-", string.Empty);
    if (withoutHyphens.Length > 0 && withoutHyphens != lower)
    {
        yield return withoutHyphens;
    }
}
