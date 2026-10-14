using System.Globalization;
using System.Text;

namespace StockPanel;

/// <summary>A CSV file with a header row. Accepts comma, semicolon or tab separators; with semicolons
/// (what Excel writes on an Italian Windows) numbers are read with a decimal comma.</summary>
public sealed class CsvTable
{
    readonly Dictionary<string, int> _columns = new();

    public char Separator { get; }
    public IReadOnlyList<string[]> Rows { get; }

    CsvTable(char separator, string[] header, List<string[]> rows)
    {
        Separator = separator;
        Rows = rows;
        for (var i = 0; i < header.Length; i++) _columns.TryAdd(Normalize(header[i]), i);
    }

    public static CsvTable Parse(string text)
    {
        var separator = DetectSeparator(text);
        var lines = ParseRecords(text, separator);
        var header = lines.Count > 0 ? lines[0] : [];
        var rows = lines.Skip(1).Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
        return new CsvTable(separator, header, rows);
    }

    public bool Has(params string[] names) => names.Any(n => _columns.ContainsKey(Normalize(n)));

    /// <summary>The first of the named columns that exists, trimmed; "" when none does.</summary>
    public string Get(string[] row, params string[] names)
    {
        foreach (var n in names)
            if (_columns.TryGetValue(Normalize(n), out var i))
                return i < row.Length ? row[i].Trim() : "";
        return "";
    }

    public double? Number(string[] row, params string[] names) => ParseNumber(Get(row, names), Separator);

    public static double? ParseNumber(string s, char separator)
    {
        var t = new string(s.Where(c => char.IsDigit(c) || c is '.' or ',' or '-' or '+').ToArray());
        if (t.Length == 0) return null;
        if (separator == ';') t = t.Replace(".", "").Replace(',', '.');
        else t = t.Replace(",", "");
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    static string Normalize(string name) =>
        new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static char DetectSeparator(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var first = end < 0 ? text : text[..end];
        int commas = 0, semis = 0, tabs = 0;
        var quoted = false;
        foreach (var c in first)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == ',') commas++;
            else if (!quoted && c == ';') semis++;
            else if (!quoted && c == '\t') tabs++;
        }
        if (tabs > commas && tabs > semis) return '\t';
        return semis > commas ? ';' : ',';
    }

    static List<string[]> ParseRecords(string text, char sep)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { fields.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(field.ToString()); field.Clear();
                records.Add(fields.ToArray()); fields.Clear();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || fields.Count > 0) { fields.Add(field.ToString()); records.Add(fields.ToArray()); }
        return records;
    }
}
