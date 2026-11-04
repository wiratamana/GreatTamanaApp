using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace GTA.App;

// Support helpers for FileToolCard (the live "Write" / "Edit" widget)

internal static class PartialJson
{
    internal readonly struct Field
    {
        public Field(string value, bool complete) { Value = value; Complete = complete; }
        public string Value    { get; }
        public bool   Complete { get; }
    }

    public static Dictionary<string, Field> ParseTopLevel(string json)
    {
        var result = new Dictionary<string, Field>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(json)) return result;

        int n = json.Length;
        int i = 0;
        SkipWs(json, ref i);
        if (i >= n || json[i] != '{') return result;
        i++;

        while (i < n)
        {
            while (i < n && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
            if (i >= n || json[i] == '}') break;
            if (json[i] != '"') break;

            string key = ReadString(json, ref i, out bool keyDone);
            if (!keyDone) break;

            SkipWs(json, ref i);
            if (i >= n || json[i] != ':') break;
            i++;
            SkipWs(json, ref i);
            if (i >= n) break;

            char c = json[i];
            if (c == '"')
            {
                string v = ReadString(json, ref i, out bool done);
                result[key] = new Field(v, done);
                if (!done) break;
            }
            else if (c == '{' || c == '[')
            {
                if (!SkipBalanced(json, ref i)) break;
            }
            else
            {
                int s = i;
                while (i < n && json[i] != ',' && json[i] != '}' && !char.IsWhiteSpace(json[i])) i++;
                bool done = i < n;
                result[key] = new Field(json.Substring(s, i - s), done);
                if (!done) break;
            }
        }

        return result;
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    /// <summary>Reads a JSON string starting at the opening quote. Returns the decoded text so far.</summary>
    private static string ReadString(string s, ref int i, out bool complete)
    {
        complete = false;
        var sb = new StringBuilder();
        int n = s.Length;
        i++; // opening quote

        while (i < n)
        {
            char ch = s[i];
            if (ch == '"')
            {
                i++;
                complete = true;
                return sb.ToString();
            }

            if (ch == '\\')
            {
                if (i + 1 >= n) break; // escape not fully arrived yet
                char e = s[i + 1];
                switch (e)
                {
                    case 'n': sb.Append('\n'); i += 2; continue;
                    case 't': sb.Append('\t'); i += 2; continue;
                    case 'r': sb.Append('\r'); i += 2; continue;
                    case 'b': sb.Append('\b'); i += 2; continue;
                    case 'f': sb.Append('\f'); i += 2; continue;
                    case '/': sb.Append('/');  i += 2; continue;
                    case '\\': sb.Append('\\'); i += 2; continue;
                    case '"': sb.Append('"');  i += 2; continue;
                    case 'u':
                        if (i + 5 >= n) { i = n; break; } // \uXXXX not fully arrived yet
                        if (int.TryParse(s.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            sb.Append((char)code);
                        i += 6;
                        continue;
                    default:
                        sb.Append(e); i += 2; continue;
                }
                break; // only reached from the incomplete \u case
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }

    private static bool SkipBalanced(string s, ref int i)
    {
        int depth = 0;
        bool inStr = false;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (c == '\\') i++;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']')
            {
                depth--;
                if (depth == 0) { i++; return true; }
            }
        }
        return false;
    }
}

/// <summary>Very small, fast, regex based syntax colouring (One-Dark-like palette).</summary>
internal static class CodeHighlighter
{
    internal enum Lang { Plain, CLike, Hash, Markup, Json, Sql }

    private static readonly SolidColorBrush Default  = new(Color.FromRgb(190, 196, 210));
    private static readonly SolidColorBrush Comment  = new(Color.FromRgb(105, 112, 130));
    private static readonly SolidColorBrush Str      = new(Color.FromRgb(152, 195, 121));
    private static readonly SolidColorBrush Num      = new(Color.FromRgb(209, 154, 102));
    private static readonly SolidColorBrush Keyword  = new(Color.FromRgb(198, 120, 221));
    private static readonly SolidColorBrush Func     = new(Color.FromRgb(97, 175, 239));
    private static readonly SolidColorBrush TypeName = new(Color.FromRgb(229, 192, 123));
    private static readonly SolidColorBrush Tag      = new(Color.FromRgb(224, 108, 117));

    public static IBrush DefaultBrush => Default;

    private const string Keywords =
        "abstract|as|async|await|base|bool|break|case|catch|char|class|const|continue|default|delegate|do|double|" +
        "else|enum|event|explicit|export|extends|extern|false|finally|fixed|float|for|foreach|from|function|get|" +
        "goto|if|implements|implicit|import|in|int|interface|internal|is|let|lock|long|namespace|new|null|object|" +
        "operator|out|override|package|params|partial|private|protected|public|readonly|ref|return|sealed|set|" +
        "short|static|string|struct|super|switch|this|throw|true|try|type|typeof|uint|ulong|unchecked|unsafe|" +
        "using|var|virtual|void|volatile|while|yield|def|elif|lambda|pass|None|True|False|self|fn|mut|impl|pub|" +
        "use|mod|match|loop|where|trait|func|go|defer|chan|select|range|val|fun|when|nil|undefined|require|" +
        "module|of|with|and|or|not|until|then|end|local|echo|fi|esac|done|exit|begin|record|init|required|" +
        "describe|it|expect|beforeEach|afterEach|beforeAll|afterAll";

    private const string CommonTail =
        @"|(?<s>""(?:[^""\\]|\\.)*(?:""|$)|'(?:[^'\\]|\\.)*(?:'|$)|`(?:[^`\\]|\\.)*(?:`|$))" +
        @"|(?<n>\b0[xX][0-9a-fA-F]+\b|\b\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?[fFdDmMuUlL]?\b)" +
        @"|(?<k>\b(?:" + Keywords + @")\b)" +
        @"|(?<f>\b[A-Za-z_]\w*(?=\s*\())" +
        @"|(?<t>\b[A-Z][A-Za-z0-9_]*\b)";

    private static readonly Regex CLikeRx  = new(@"(?<c>//.*|/\*.*?(?:\*/|$))" + CommonTail, RegexOptions.CultureInvariant);
    private static readonly Regex HashRx   = new(@"(?<c>#.*)" + CommonTail, RegexOptions.CultureInvariant);
    private static readonly Regex SqlRx    = new(@"(?<c>--.*|/\*.*?(?:\*/|$))" + CommonTail, RegexOptions.CultureInvariant);
    private static readonly Regex MarkupRx = new(
        @"(?<c><!--.*?(?:-->|$))|(?<s>""[^""]*""?|'[^']*'?)|(?<g></?[A-Za-z][\w:.\-]*|/?>|\?>|<\?[\w\-]+)|(?<a>\b[A-Za-z_:][\w:.\-]*(?==))",
        RegexOptions.CultureInvariant);
    private static readonly Regex JsonRx   = new(
        @"(?<g>""(?:[^""\\]|\\.)*""(?=\s*:))|(?<s>""(?:[^""\\]|\\.)*(?:""|$))|(?<n>-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)|(?<k>\b(?:true|false|null)\b)",
        RegexOptions.CultureInvariant);

    public static Lang Detect(string? path)
    {
        string ext = string.Empty;
        if (!string.IsNullOrEmpty(path))
        {
            int dot = path.LastIndexOf('.');
            if (dot >= 0 && dot < path.Length - 1) ext = path[(dot + 1)..].ToLowerInvariant();
        }

        return ext switch
        {
            "cs" or "js" or "jsx" or "ts" or "tsx" or "mjs" or "cjs" or "java" or "c" or "cpp" or "cc" or "h" or
            "hpp" or "go" or "rs" or "kt" or "kts" or "swift" or "php" or "css" or "scss" or "dart" or "scala" or
            "glsl" or "hlsl" or "frag" or "vert" or "shader" => Lang.CLike,

            "py" or "sh" or "bash" or "zsh" or "ps1" or "psm1" or "yaml" or "yml" or "toml" or "ini" or "cfg" or
            "conf" or "rb" or "pl" or "r" or "gitignore" or "env" or "dockerfile" => Lang.Hash,

            "html" or "htm" or "xml" or "xaml" or "axaml" or "csproj" or "props" or "targets" or "svg" or
            "config" or "resx" or "vue" => Lang.Markup,

            "json" or "jsonc" => Lang.Json,
            "sql" => Lang.Sql,
            _ => Lang.Plain,
        };
    }

    /// <summary>Appends one source line (coloured Runs) to <paramref name="inlines"/>. No trailing line break.</summary>
    public static void AppendLine(InlineCollection inlines, string line, Lang lang)
    {
        if (line.Length == 0) return;

        // Minified / giant lines: regex colouring is not worth the cost.
        if (lang == Lang.Plain || line.Length > 600)
        {
            inlines.Add(new Run(line) { Foreground = Default });
            return;
        }

        Regex rx = lang switch
        {
            Lang.CLike  => CLikeRx,
            Lang.Hash   => HashRx,
            Lang.Sql    => SqlRx,
            Lang.Markup => MarkupRx,
            Lang.Json   => JsonRx,
            _           => CLikeRx,
        };

        int pos = 0;
        foreach (Match m in rx.Matches(line))
        {
            if (m.Length == 0) continue;
            if (m.Index > pos)
                inlines.Add(new Run(line.Substring(pos, m.Index - pos)) { Foreground = Default });

            IBrush brush = Default;
            bool italic  = false;
            if      (m.Groups["c"].Success) { brush = Comment;  italic = true; }
            else if (m.Groups["s"].Success) brush = Str;
            else if (m.Groups["n"].Success) brush = Num;
            else if (m.Groups["k"].Success) brush = Keyword;
            else if (m.Groups["f"].Success) brush = Func;
            else if (m.Groups["t"].Success) brush = TypeName;
            else if (m.Groups["g"].Success) brush = Tag;
            else if (m.Groups["a"].Success) brush = Num;

            var run = new Run(m.Value) { Foreground = brush };
            if (italic) run.FontStyle = FontStyle.Italic;
            inlines.Add(run);

            pos = m.Index + m.Length;
        }

        if (pos < line.Length)
            inlines.Add(new Run(line.Substring(pos)) { Foreground = Default });
    }
}
