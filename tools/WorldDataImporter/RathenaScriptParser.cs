using System.Globalization;
using System.Text.RegularExpressions;
using Athena.WorldCompiler.Rathena;

internal sealed record ScriptParseIssue(string Construct, int RelativeLine, string SourceText);
internal sealed record ScriptParseResult(IReadOnlyList<ScriptInstructionDefinition> Instructions, IReadOnlyList<string> Commands, IReadOnlyList<ScriptParseIssue> Issues)
{
    public bool Executable => Issues.Count == 0;
}

internal static partial class RathenaScriptParser
{
    public static ScriptParseResult ParseOnTouch(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim().Equals("OnTouch:", StringComparison.Ordinal));
        if (start < 0) return new([], [], [new("OnTouch", 0, "missing OnTouch label")]);
        // Syntax is owned by the compiler parser. The narrow DTO lowering below is
        // temporary compatibility with ScriptExecutionSession during migration.
        var syntaxEnd = lines.Length > start && lines[^1].Trim() == "}" ? lines.Length - 1 : lines.Length;
        var syntax = new RathenaParser(string.Join('\n', lines[start..syntaxEnd]), "<script>", start + 1).ParseCompilationUnit();
        var syntaxIssues = syntax.Diagnostics.Where(issue => issue.Severity == "Error")
            .Select(issue => new ScriptParseIssue(issue.Construct ?? "syntax", issue.Span.Start.Line, issue.Message)).ToArray();
        if (syntaxIssues.Length > 0) return new([], [], syntaxIssues);
        var parser = new Parser(lines, start + 1);
        var instructions = parser.ParseBlock(false);
        return new(instructions, parser.Commands.Order(StringComparer.Ordinal).ToArray(), parser.Issues);
    }

    private sealed class Parser(string[] lines, int index)
    {
        private int _index = index;
        public HashSet<string> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ScriptParseIssue> Issues { get; } = [];

        public IReadOnlyList<ScriptInstructionDefinition> ParseBlock(bool stopAtBrace)
        {
            var result = new List<ScriptInstructionDefinition>();
            while (_index < lines.Length)
            {
                var raw = lines[_index]; var lineNumber = _index + 1; var line = raw.Trim(); _index++;
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                if (line == "}") return result;
                if (line == "end;") { Commands.Add("end"); continue; }
                if (TryMessage(line, out var text)) { Commands.Add("mes"); result.Add(new MessageInstruction(text)); continue; }
                if (line == "next;") { Commands.Add("next"); result.Add(new NextInstruction()); continue; }
                if (line == "close;") { Commands.Add("close"); result.Add(new CloseInstruction()); continue; }
                if (line == "close2;") { Commands.Add("close2"); result.Add(new Close2Instruction()); continue; }
                if (TrySetQuest(line, out var setQuestId)) { Commands.Add("setquest"); result.Add(new SetQuestInstruction(setQuestId)); continue; }
                if (TryCompleteQuest(line, out var questId)) { Commands.Add("completequest"); result.Add(new CompleteQuestInstruction(questId)); continue; }
                if (TryAssignment(line, out var variable, out var expression)) { Commands.Add("assignment"); result.Add(new AssignmentInstruction(variable, expression)); continue; }
                if (TryTransfer(line, "warp", out expression, out var x, out var y, out _, out _)) { Commands.Add("warp"); result.Add(new WarpInstruction(expression, x, y)); continue; }
                if (TryTransfer(line, "savepoint", out expression, out x, out y, out var rx, out var ry)) { Commands.Add("savepoint"); result.Add(new SavePointInstruction(expression, x, y, rx, ry)); continue; }
                if (TryQuestIf(line, out questId, out var expected, out var opensBlock))
                {
                    Commands.Add("isbegin_quest");
                    IReadOnlyList<ScriptInstructionDefinition> branch;
                    if (opensBlock) branch = ParseBlock(true);
                    else if (_index < lines.Length) branch = ParseSingle(lines[_index++], _index);
                    else branch = [];
                    result.Add(new IfQuestStateInstruction(questId, expected, branch, []));
                    continue;
                }
                if (TrySelectIf(line, out var options))
                {
                    Commands.Add("select");
                    var first = ParseBlock(true);
                    result.Add(new SelectInstruction(options.Select((option, optionIndex) => new SelectOptionDefinition(option, optionIndex == 0 ? first : [])).ToArray()));
                    continue;
                }
                Issue(CommandName(line), lineNumber, raw);
            }
            if (stopAtBrace) Issue("brace", lines.Length, "missing closing brace");
            return result;
        }

        private IReadOnlyList<ScriptInstructionDefinition> ParseSingle(string raw, int lineNumber)
        {
            var line = raw.Trim();
            if (TryCompleteQuest(line, out var questId)) { Commands.Add("completequest"); return [new CompleteQuestInstruction(questId)]; }
            if (line == "close;") { Commands.Add("close"); return [new CloseInstruction()]; }
            Issue(CommandName(line), lineNumber, raw); return [];
        }
        private void Issue(string construct, int line, string text) { Commands.Add(construct); Issues.Add(new(construct, line, text.Trim())); }
    }

    private static bool TryMessage(string line, out string text) { var match = MessageRegex().Match(line); text = match.Success ? Unescape(match.Groups[1].Value) : ""; return match.Success; }
    private static bool TryCompleteQuest(string line, out uint questId) { var match = CompleteQuestRegex().Match(line); return uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out questId); }
    private static bool TrySetQuest(string line, out uint questId) { var match = SetQuestRegex().Match(line); return uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out questId); }
    private static bool TryAssignment(string line, out string variable, out ScriptExpressionDefinition expression)
    {
        expression = default!;
        var match = AssignmentRegex().Match(line); variable = match.Success ? match.Groups[1].Value : "";
        return match.Success && TryExpression(match.Groups[2].Value.Trim(), out expression);
    }
    private static bool TryQuestIf(string line, out uint questId, out string expected, out bool block)
    {
        questId = 0;
        var match = QuestIfRegex().Match(line); block = match.Success && match.Groups[3].Success;
        expected = match.Success && match.Groups[2].Value == "1" ? "Active" : match.Groups[2].Value == "2" ? "Completed" : "Absent";
        return match.Success && uint.TryParse(match.Groups[1].Value, out questId);
    }
    private static bool TrySelectIf(string line, out IReadOnlyList<string> options)
    {
        options = []; var match = SelectIfRegex().Match(line); if (!match.Success) return false;
        options = QuotedStringRegex().Matches(match.Groups[1].Value).Select(item => Unescape(item.Groups[1].Value)).ToArray(); return options.Count > 0;
    }
    private static bool TryTransfer(string line, string command, out ScriptExpressionDefinition map, out ushort x, out ushort y, out ushort rx, out ushort ry)
    {
        map = default!; x = y = rx = ry = 0; var match = TransferRegex().Match(line);
        if (!match.Success || !match.Groups[1].Value.Equals(command, StringComparison.OrdinalIgnoreCase) || !TryExpression(match.Groups[2].Value.Trim(), out map)) return false;
        if (!ushort.TryParse(match.Groups[3].Value, out x) || !ushort.TryParse(match.Groups[4].Value, out y)) return false;
        if (match.Groups[5].Success) ushort.TryParse(match.Groups[5].Value, out rx);
        if (match.Groups[6].Success) ushort.TryParse(match.Groups[6].Value, out ry);
        return true;
    }
    private static bool TryExpression(string source, out ScriptExpressionDefinition expression)
    {
        expression = default!; var plus = TopLevel(source, '+');
        if (plus >= 0 && TryExpression(source[..plus].Trim(), out var left) && TryExpression(source[(plus + 1)..].Trim(), out var right)) { expression = new ConcatExpression(left, right); return true; }
        if (source.StartsWith('"') && source.EndsWith('"')) { expression = new StringLiteralExpression(Unescape(source[1..^1])); return true; }
        if (source.StartsWith(".@", StringComparison.Ordinal)) { expression = new VariableExpression(source); return true; }
        var npc = StrNpcInfoRegex().Match(source); if (npc.Success) { expression = new StrNpcInfoExpression(int.Parse(npc.Groups[1].Value, CultureInfo.InvariantCulture)); return true; }
        var replace = ReplaceRegex().Match(source);
        if (replace.Success && TryExpression(replace.Groups[1].Value.Trim(), out var value) && TryExpression(replace.Groups[2].Value.Trim(), out var search) && TryExpression(replace.Groups[3].Value.Trim(), out var replacement)) { expression = new ReplaceStringExpression(value, search, replacement); return true; }
        return false;
    }
    private static int TopLevel(string source, char token) { var depth = 0; var quoted = false; for (var i = 0; i < source.Length; i++) { if (source[i] == '"' && (i == 0 || source[i - 1] != '\\')) quoted = !quoted; if (quoted) continue; if (source[i] == '(') depth++; else if (source[i] == ')') depth--; else if (source[i] == token && depth == 0) return i; } return -1; }
    private static string CommandName(string line) { var end = line.IndexOfAny([' ', '(', ';']); return (end > 0 ? line[..end] : line).Trim(); }
    private static string Unescape(string value) => value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal);

    [GeneratedRegex("^mes\\s+\"(.*)\"\\s*;$")] private static partial Regex MessageRegex();
    [GeneratedRegex("^completequest\\s+(\\d+)\\s*;$")] private static partial Regex CompleteQuestRegex();
    [GeneratedRegex("^setquest\\s+(\\d+)\\s*;$")] private static partial Regex SetQuestRegex();
    [GeneratedRegex("^(\\.@[A-Za-z0-9_]+\\$?)\\s*=\\s*(.+);$")] private static partial Regex AssignmentRegex();
    [GeneratedRegex("^if\\s*\\(\\s*isbegin_quest\\((\\d+)\\)\\s*==\\s*([012])\\s*\\)\\s*(\\{)?$")] private static partial Regex QuestIfRegex();
    [GeneratedRegex("^if\\s*\\(\\s*select\\((.*)\\)\\s*==\\s*1\\s*\\)\\s*\\{$")] private static partial Regex SelectIfRegex();
    [GeneratedRegex("\"((?:\\\\.|[^\"])*)\"")] private static partial Regex QuotedStringRegex();
    [GeneratedRegex("^(warp|savepoint)\\s+(.+?)\\s*,\\s*(\\d+)\\s*,\\s*(\\d+)(?:\\s*,\\s*(\\d+)\\s*,\\s*(\\d+))?\\s*;$")] private static partial Regex TransferRegex();
    [GeneratedRegex("^strnpcinfo\\(\\s*(\\d+)\\s*\\)$")] private static partial Regex StrNpcInfoRegex();
    [GeneratedRegex("^replacestr\\(\\s*(.+?)\\s*,\\s*(\"(?:\\\\.|[^\"])*\")\\s*,\\s*(\"(?:\\\\.|[^\"])*\")\\s*\\)$")] private static partial Regex ReplaceRegex();
}
