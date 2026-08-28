namespace Athena.WorldCompiler.Generation;

// Deliberately small parser for the indentation-only subset used by the pinned
// job/progression/skill-tree databases. skill_db.yml is scanned separately so
// its script block scalars never enter this parser.
internal abstract record SimpleYamlNode;
internal sealed record SimpleYamlScalar(string Value) : SimpleYamlNode;
internal sealed record SimpleYamlMap(IReadOnlyList<KeyValuePair<string, SimpleYamlNode>> Items) : SimpleYamlNode
{
    internal SimpleYamlNode? Optional(string key) => Items.LastOrDefault(item => item.Key == key).Value;
    internal SimpleYamlNode Required(string key, string context) => Optional(key) ?? throw new ArgumentException($"{context} is missing '{key}'.");
}
internal sealed record SimpleYamlSequence(IReadOnlyList<SimpleYamlNode> Items) : SimpleYamlNode;

internal static class SimpleYaml
{
    private readonly record struct Line(int Number, int Indent, string Text);

    internal static SimpleYamlMap Parse(string source, string sourceName)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select((text, index) => new { Text = text, Number = index + 1 })
            .Where(line => !string.IsNullOrWhiteSpace(line.Text) && !line.Text.TrimStart().StartsWith('#'))
            .Select(line => new Line(line.Number, line.Text.TakeWhile(character => character == ' ').Count(), StripComment(line.Text.Trim())))
            .Where(line => line.Text.Length > 0)
            .ToArray();
        var index = 0;
        var node = ParseBlock(lines, ref index, 0, sourceName);
        return node as SimpleYamlMap ?? throw new ArgumentException($"{sourceName} must have a mapping root.");
    }

    private static SimpleYamlNode ParseBlock(IReadOnlyList<Line> lines, ref int index, int indent, string sourceName) =>
        lines[index].Text.StartsWith("- ", StringComparison.Ordinal)
            ? ParseSequence(lines, ref index, indent, sourceName)
            : ParseMap(lines, ref index, indent, sourceName);

    private static SimpleYamlMap ParseMap(IReadOnlyList<Line> lines, ref int index, int indent, string sourceName)
    {
        var items = new List<KeyValuePair<string, SimpleYamlNode>>();
        while (index < lines.Count && lines[index].Indent == indent && !lines[index].Text.StartsWith("- ", StringComparison.Ordinal))
        {
            var line = lines[index++];
            var colon = line.Text.IndexOf(':');
            if (colon <= 0) throw new ArgumentException($"{sourceName}:{line.Number}: expected a mapping entry.");
            var key = line.Text[..colon].Trim();
            var value = line.Text[(colon + 1)..].Trim();
            SimpleYamlNode node;
            if (value.Length > 0) node = new SimpleYamlScalar(Unquote(value));
            else if (index < lines.Count && lines[index].Indent > indent) node = ParseBlock(lines, ref index, lines[index].Indent, sourceName);
            else node = new SimpleYamlMap([]);
            items.Add(new(key, node));
        }
        return new(items);
    }

    private static SimpleYamlSequence ParseSequence(IReadOnlyList<Line> lines, ref int index, int indent, string sourceName)
    {
        var items = new List<SimpleYamlNode>();
        while (index < lines.Count && lines[index].Indent == indent && lines[index].Text.StartsWith("- ", StringComparison.Ordinal))
        {
            var line = lines[index++];
            var remainder = line.Text[2..].Trim();
            if (remainder.Length == 0)
            {
                if (index >= lines.Count || lines[index].Indent <= indent) throw new ArgumentException($"{sourceName}:{line.Number}: empty sequence item.");
                items.Add(ParseBlock(lines, ref index, lines[index].Indent, sourceName));
                continue;
            }

            var colon = remainder.IndexOf(':');
            if (colon <= 0) { items.Add(new SimpleYamlScalar(Unquote(remainder))); continue; }
            var mapItems = new List<KeyValuePair<string, SimpleYamlNode>>();
            var key = remainder[..colon].Trim();
            var value = remainder[(colon + 1)..].Trim();
            SimpleYamlNode firstValue;
            if (value.Length > 0) firstValue = new SimpleYamlScalar(Unquote(value));
            else if (index < lines.Count && lines[index].Indent > indent) firstValue = ParseBlock(lines, ref index, lines[index].Indent, sourceName);
            else firstValue = new SimpleYamlMap([]);
            mapItems.Add(new(key, firstValue));
            if (index < lines.Count && lines[index].Indent > indent)
            {
                var continuationIndent = lines[index].Indent;
                var continuation = ParseMap(lines, ref index, continuationIndent, sourceName);
                mapItems.AddRange(continuation.Items);
            }
            items.Add(new SimpleYamlMap(mapItems));
        }
        return new(items);
    }

    private static string Unquote(string value) => value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')) ? value[1..^1] : value;
    private static string StripComment(string value)
    {
        var single = false; var doubleQuote = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\'' && !doubleQuote) single = !single;
            else if (value[index] == '"' && !single) doubleQuote = !doubleQuote;
            else if (value[index] == '#' && !single && !doubleQuote && (index == 0 || char.IsWhiteSpace(value[index - 1]))) return value[..index].TrimEnd();
        }
        return value;
    }
}

internal static class SimpleYamlAccess
{
    internal static SimpleYamlMap Map(this SimpleYamlNode node, string context) => node as SimpleYamlMap ?? throw new ArgumentException($"{context} must be a mapping.");
    internal static SimpleYamlSequence Sequence(this SimpleYamlNode node, string context) => node as SimpleYamlSequence ?? throw new ArgumentException($"{context} must be a sequence.");
    internal static string Scalar(this SimpleYamlNode node, string context) => (node as SimpleYamlScalar)?.Value ?? throw new ArgumentException($"{context} must be a scalar.");
    internal static ushort UShort(this SimpleYamlNode node, string context) => ushort.TryParse(node.Scalar(context), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($"{context} must be an unsigned 16-bit integer.");
    internal static ulong ULong(this SimpleYamlNode node, string context) => ulong.TryParse(node.Scalar(context), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($"{context} must be an unsigned integer.");
    internal static bool Bool(this SimpleYamlNode node, string context) => bool.TryParse(node.Scalar(context), out var value) ? value : throw new ArgumentException($"{context} must be true or false.");
}
