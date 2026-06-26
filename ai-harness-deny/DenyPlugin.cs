using System.IO.Enumeration;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ai_harness_baselib;

namespace ai_harness_deny;

/// <summary>
/// Claude からのツール実行を deny するプラグイン。PreToolUse で発火し、設定ファイルの
/// 3 系統（rules / bash / files）のいずれかにマッチしたら拒否する（deny 先勝ち）。
///
///   rules … settings.json 風の <c>Tool("引数")</c> ルール。
///           Bash は command の前方一致、ファイル系ツールは file_path の glob 一致。
///   bash  … command を部分一致（含めば deny）。Bash ツール限定。
///   files … file_path を glob 一致（ファイルに触る全ツール対象）。
/// </summary>
public sealed partial class DenyPlugin : PluginBase
{
    public override string PluginName => "ai-harness-deny";

    /// <summary>PreToolUse の全ツールで発火する（イベントマッチ）。</summary>
    public override IReadOnlyList<string> Events => new[] { "PreToolUse" };

    /// <summary>deny ルールを保持する設定ファイル（YAML）。</summary>
    public override string ConfigName => "ai-harness-deny.yml";

    public override IEnumerable<LogEntry> Init()
    {
        yield return LogEntry.Info("初期化");
    }

    public override IEnumerable<LogEntry> Action(HookData data, PluginResult result)
    {
        // PreToolUse 以外は対象外（Events で絞っているが念のため自己フィルタ）。
        if (data.Event != HookEvent.PreToolUse)
        {
            yield break;
        }

        var toolName = data.ToolName;
        var command = ExtractCommand(data);
        var filePath = ExtractFilePath(data);

        // --- rules: Tool("引数") ---
        foreach (var raw in ReadList("rules"))
        {
            var rule = ParseRule(raw);
            if (rule is null)
            {
                yield return LogEntry.Warning($"rules の構文を解釈できない（無視）: {raw}");
                continue;
            }
            var (ruleTool, arg) = rule.Value;
            if (!string.Equals(ruleTool, toolName, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(ruleTool, "Bash", StringComparison.Ordinal))
            {
                // Bash は command の前方一致（settings.json 準拠）。
                if (command is not null && command.StartsWith(arg, StringComparison.Ordinal))
                {
                    yield return LogEntry.Warning($"rules で deny: {raw}");
                    Deny(result, $"rules によりブロック: {raw}（command='{command}'）");
                    yield break;
                }
            }
            else
            {
                // ファイル系ツールは file_path の glob 一致。
                if (PathMatches(arg, filePath))
                {
                    yield return LogEntry.Warning($"rules で deny: {raw}");
                    Deny(result, $"rules によりブロック: {raw}（file_path='{filePath}'）");
                    yield break;
                }
            }
        }

        // --- bash: command の部分一致（Bash 限定） ---
        if (string.Equals(toolName, "Bash", StringComparison.Ordinal) && command is not null)
        {
            foreach (var needle in ReadList("bash"))
            {
                if (needle.Length > 0 && command.Contains(needle, StringComparison.Ordinal))
                {
                    yield return LogEntry.Warning($"bash で deny: {needle}");
                    Deny(result, $"bash によりブロック: '{needle}' を含むコマンド（command='{command}'）");
                    yield break;
                }
            }
        }

        // --- files: file_path の glob 一致 ---
        // file_path を引数に取るツール（Read/Edit/Write 等）はもちろん、Bash のように
        // file_path を持たないツールでも command 内にパスが現れれば塞ぐ
        // （tail/cat/cp 等のシェル経由アクセスをすり抜けさせない）。
        foreach (var pattern in ReadList("files"))
        {
            if (filePath is not null && PathMatches(pattern, filePath))
            {
                yield return LogEntry.Warning($"files で deny: {pattern}");
                Deny(result, $"files によりブロック: '{pattern}'（file_path='{filePath}'）");
                yield break;
            }
            if (command is not null && CommandTouchesPath(pattern, command))
            {
                yield return LogEntry.Warning($"files で deny (command): {pattern}");
                Deny(result, $"files によりブロック: '{pattern}'（command='{command}'）");
                yield break;
            }
        }

        // ここに到達 = どのルールにもマッチせず ExitCode 0（許可）。
    }

    private static void Deny(PluginResult result, string reason)
    {
        result.ExitCode = 2;
        result.Reason = reason;
    }

    /// <summary>設定の指定キー（rules/bash/files）を文字列リストとして取得。未設定は空。</summary>
    private IReadOnlyList<string> ReadList(string key)
    {
        if (!Config.TryGetValue(key, out var value) || value is not System.Collections.IEnumerable seq
            || value is string)
        {
            return Array.Empty<string>();
        }
        var list = new List<string>();
        foreach (var item in seq)
        {
            var s = item?.ToString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                list.Add(s.Trim());
            }
        }
        return list;
    }

    /// <summary><c>Tool("引数")</c> を (Tool, 引数) へ分解。解釈不能は null。クオートは省略可。</summary>
    private static (string Tool, string Arg)? ParseRule(string raw)
    {
        var m = RuleRegex().Match(raw);
        return m.Success ? (m.Groups["tool"].Value, m.Groups["arg"].Value) : null;
    }

    [GeneratedRegex("""^\s*(?<tool>\w+)\s*\(\s*["']?(?<arg>.*?)["']?\s*\)\s*$""")]
    private static partial Regex RuleRegex();

    /// <summary>
    /// glob パターンとファイルパスの一致。パスは <c>/</c> 区切りに正規化し、フルパスと各
    /// パス区切りサフィックスのいずれかにマッチすれば true（相対パターンが絶対パスにも効く）。
    /// </summary>
    private static bool PathMatches(string pattern, string? path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(pattern))
        {
            return false;
        }
        var normPattern = pattern.Replace('\\', '/');
        var normPath = path.Replace('\\', '/');

        if (FileSystemName.MatchesSimpleExpression(normPattern, normPath, ignoreCase: true))
        {
            return true;
        }
        // 区切りごとのサフィックス（例: ".claude/harness/*" を絶対パスにマッチさせる）。
        var idx = normPath.IndexOf('/');
        while (idx >= 0 && idx + 1 < normPath.Length)
        {
            var suffix = normPath[(idx + 1)..];
            if (FileSystemName.MatchesSimpleExpression(normPattern, suffix, ignoreCase: true))
            {
                return true;
            }
            idx = normPath.IndexOf('/', idx + 1);
        }
        return false;
    }

    /// <summary>
    /// シェルコマンドのトークン区切り。空白とシェルメタ文字（パイプ・リダイレクト・クオート・
    /// 環境変数代入の <c>=</c> 等）で分割し、各トークンを 1 つのパス候補として扱う。
    /// 厳密なシェルパースではないが、tail/cat/cp 等の引数パスを拾うには十分。
    /// </summary>
    private static readonly char[] ShellSeparators =
        { ' ', '\t', '\n', '\r', '|', '&', ';', '<', '>', '(', ')', '{', '}', '"', '\'', '=', '`' };

    /// <summary>command を区切りでトークン分割し、いずれかのトークンが glob にマッチすれば true。</summary>
    private static bool CommandTouchesPath(string pattern, string command)
    {
        foreach (var token in command.Split(ShellSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (PathMatches(pattern, token))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>tool_input.command を取り出す（無ければ null）。</summary>
    private static string? ExtractCommand(HookData data) =>
        AsString(GetMember(data.ToolInput, "command"));

    /// <summary>マッチ対象のファイルパス。tool_input.file_path 優先、無ければトップレベル file_path。</summary>
    private static string? ExtractFilePath(HookData data) =>
        AsString(GetMember(data.ToolInput, "file_path")) ?? data.FilePath;

    private static JsonNode? GetMember(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var v) ? v : null;

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
