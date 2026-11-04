using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace GTA.App;

internal static class PythonHighlighter
{
    private static readonly SolidColorBrush Default  = new(Color.FromRgb(190, 196, 210));
    private static readonly SolidColorBrush Comment  = new(Color.FromRgb(105, 112, 130));
    private static readonly SolidColorBrush Str      = new(Color.FromRgb(152, 195, 121));
    private static readonly SolidColorBrush Interp   = new(Color.FromRgb(120, 205, 190));
    private static readonly SolidColorBrush Num      = new(Color.FromRgb(209, 154, 102));
    private static readonly SolidColorBrush Keyword  = new(Color.FromRgb(198, 120, 221));
    private static readonly SolidColorBrush Func     = new(Color.FromRgb(97, 175, 239));
    private static readonly SolidColorBrush Builtin  = new(Color.FromRgb(86, 182, 194));
    private static readonly SolidColorBrush TypeName = new(Color.FromRgb(229, 192, 123));
    private static readonly SolidColorBrush SelfName = new(Color.FromRgb(224, 108, 117));

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del",
        "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in",
        "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while",
        "with", "yield",
    };

    private static readonly HashSet<string> Constants = new(StringComparer.Ordinal)
    {
        "True", "False", "None", "Ellipsis", "NotImplemented", "__debug__",
    };

    // Coloured only when called, e.g. print(...) / len(...).
    private static readonly HashSet<string> BuiltinFuncs = new(StringComparer.Ordinal)
    {
        "abs", "all", "any", "ascii", "bin", "breakpoint", "callable", "chr", "compile", "delattr",
        "dir", "divmod", "enumerate", "eval", "exec", "filter", "format", "getattr", "globals",
        "hasattr", "hash", "help", "hex", "id", "input", "isinstance", "issubclass", "iter", "len",
        "locals", "map", "max", "min", "next", "oct", "open", "ord", "pow", "print", "repr",
        "reversed", "round", "setattr", "sorted", "sum", "super", "vars", "zip", "__import__",
    };

    // Coloured always (type names), e.g. int, str, dict.
    private static readonly HashSet<string> BuiltinTypes = new(StringComparer.Ordinal)
    {
        "int", "str", "float", "bool", "list", "dict", "set", "tuple", "bytes", "bytearray",
        "object", "type", "complex", "frozenset", "range", "slice", "memoryview", "property",
        "staticmethod", "classmethod",
    };

    /// <summary>Appends the code as coloured Runs and LineBreaks.</summary>
    public static void Append(InlineCollection inlines, string code)
    {
        var w = new Writer(inlines);
        int n = code.Length;
        int i = 0;
        string prevWord = "";
        bool afterDot = false;

        while (i < n)
        {
            char c = code[i];

            // ── comment ───────────────────────────────────────────────────────
            if (c == '#')
            {
                int e = i;
                while (e < n && code[e] != '\n') e++;
                w.Add(code.Substring(i, e - i), Comment, italic: true);
                i = e;
                continue;
            }

            // ── string literal (optional r/b/u/f prefix) ──────────────────────
            int q = StringQuoteIndex(code, i);
            if (q >= 0)
            {
                i = ReadString(code, i, q, w);
                prevWord = "";
                afterDot = false;
                continue;
            }

            // ── decorator (@ as the first thing on a line) ────────────────────
            if (c == '@' && OnlyWhitespaceBefore(code, i))
            {
                int e = i + 1;
                while (e < n && (char.IsLetterOrDigit(code[e]) || code[e] == '_' || code[e] == '.')) e++;
                w.Add(code.Substring(i, e - i), TypeName);
                i = e;
                prevWord = "";
                afterDot = false;
                continue;
            }

            // ── number ────────────────────────────────────────────────────────
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(code[i + 1]) && !PrevIsOperandEnd(code, i)))
            {
                int j = ReadNumber(code, i);
                w.Add(code.Substring(i, j - i), Num);
                i = j;
                prevWord = "";
                afterDot = false;
                continue;
            }

            // ── identifier / keyword ──────────────────────────────────────────
            if (char.IsLetter(c) || c == '_')
            {
                int j = i + 1;
                while (j < n && (char.IsLetterOrDigit(code[j]) || code[j] == '_')) j++;
                string word = code.Substring(i, j - i);

                int k = j;
                while (k < n && (code[k] == ' ' || code[k] == '\t')) k++;
                bool isCall = k < n && code[k] == '(';

                IBrush brush;
                if (!afterDot && Keywords.Contains(word))       brush = Keyword;
                else if (!afterDot && Constants.Contains(word)) brush = Num;
                else if (prevWord == "def")                     brush = Func;
                else if (prevWord == "class")                   brush = TypeName;
                else if (afterDot)                              brush = isCall ? Func : Default;
                else if (word == "self" || word == "cls")       brush = SelfName;
                else if (isCall)                                brush = BuiltinFuncs.Contains(word) ? Builtin : Func;
                else if (BuiltinTypes.Contains(word))           brush = Builtin;
                else if (LooksLikeClassName(word))              brush = TypeName;
                else                                            brush = Default;

                w.Add(word, brush);
                prevWord = word;
                afterDot = false;
                i = j;
                continue;
            }

            // ── anything else (spaces, operators, brackets, …) ────────────────
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            {
                // Whitespace keeps the state, so "def   name" and "obj. attr" still work.
                w.Add(c.ToString(), Default);
            }
            else
            {
                w.Add(c.ToString(), Default);
                afterDot = c == '.';
                prevWord = "";
            }
            i++;
        }

        w.Flush();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Index of the opening quote if a string literal starts here; otherwise -1.</summary>
    private static int StringQuoteIndex(string s, int i)
    {
        char c = s[i];
        if (c == '"' || c == '\'') return i;
        if ("rRbBfFuU".IndexOf(c) < 0) return -1;

        int j = i;
        while (j < s.Length && j - i < 2 && "rRbBfFuU".IndexOf(s[j]) >= 0) j++;
        if (j < s.Length && (s[j] == '"' || s[j] == '\'')) return j;
        return -1;
    }

    /// <summary>Reads a whole string literal (prefix included).</summary>
    private static int ReadString(string s, int start, int q, Writer w)
    {
        int n = s.Length;
        bool isF = false;
        for (int p = start; p < q; p++)
            if (s[p] == 'f' || s[p] == 'F') isF = true;

        char quote = s[q];
        bool triple = q + 2 < n && s[q + 1] == quote && s[q + 2] == quote;
        int j = q + (triple ? 3 : 1);

        while (j < n)
        {
            char ch = s[j];
            if (ch == '\\') { j += 2; continue; }

            if (ch == quote)
            {
                if (!triple) { j++; break; }
                if (j + 2 < n && s[j + 1] == quote && s[j + 2] == quote) { j += 3; break; }
            }
            else if (ch == '\n' && !triple)
            {
                break;   // unterminated one-line string: stop at the line end
            }
            j++;
        }
        if (j > n) j = n;

        if (!isF)
        {
            w.Add(s.Substring(start, j - start), Str);
            return j;
        }

        // f-string: colour the {expression} parts differently.
        int lit = start;
        int k = start;
        while (k < j)
        {
            if (s[k] == '{')
            {
                if (k + 1 < j && s[k + 1] == '{') { k += 2; continue; }   // escaped {{

                if (k > lit) w.Add(s.Substring(lit, k - lit), Str);

                int depth = 0;
                int m = k;
                while (m < j)
                {
                    if (s[m] == '{') depth++;
                    else if (s[m] == '}')
                    {
                        depth--;
                        if (depth == 0) { m++; break; }
                    }
                    m++;
                }
                w.Add(s.Substring(k, m - k), Interp);
                k = m;
                lit = m;
                continue;
            }
            k++;
        }
        if (j > lit) w.Add(s.Substring(lit, j - lit), Str);
        return j;
    }

    private static int ReadNumber(string s, int i)
    {
        int n = s.Length;
        int j = i;

        if (s[i] == '0' && i + 1 < n && "xXbBoO".IndexOf(s[i + 1]) >= 0)
        {
            j = i + 2;
            while (j < n && (Uri.IsHexDigit(s[j]) || s[j] == '_')) j++;
            return j;
        }

        while (j < n && (char.IsDigit(s[j]) || s[j] == '_')) j++;
        if (j < n && s[j] == '.')
        {
            j++;
            while (j < n && (char.IsDigit(s[j]) || s[j] == '_')) j++;
        }
        if (j < n && (s[j] == 'e' || s[j] == 'E'))
        {
            int k = j + 1;
            if (k < n && (s[k] == '+' || s[k] == '-')) k++;
            if (k < n && char.IsDigit(s[k]))
            {
                j = k;
                while (j < n && char.IsDigit(s[j])) j++;
            }
        }
        if (j < n && (s[j] == 'j' || s[j] == 'J')) j++;
        return j;
    }

    private static bool OnlyWhitespaceBefore(string s, int i)
    {
        for (int p = i - 1; p >= 0; p--)
        {
            if (s[p] == '\n') return true;
            if (s[p] != ' ' && s[p] != '\t' && s[p] != '\r') return false;
        }
        return true;
    }

    // True when the char before i ends an operand (so a "." there is an attribute dot, not ".5").
    private static bool PrevIsOperandEnd(string s, int i)
    {
        if (i == 0) return false;
        char p = s[i - 1];
        return char.IsLetterOrDigit(p) || p == '_' || p == ')' || p == ']';
    }

    // CamelCase (Request, HTTPError) = class name. ALL_CAPS (P, MAX_N) = constant, stays plain.
    private static bool LooksLikeClassName(string w)
    {
        if (w.Length < 2 || !char.IsUpper(w[0])) return false;
        for (int p = 1; p < w.Length; p++)
            if (char.IsLower(w[p])) return true;
        return false;
    }

    // ── run writer ────────────────────────────────────────────────────────────

    /// <summary>Collects text into as few Runs as possible; merges neighbouring default-coloured text.</summary>
    private sealed class Writer
    {
        private readonly InlineCollection _inlines;
        private readonly StringBuilder _buf = new();

        public Writer(InlineCollection inlines) { _inlines = inlines; }

        public void Add(string text, IBrush brush, bool italic = false)
        {
            int start = 0;
            for (int k = 0; k <= text.Length; k++)
            {
                if (k < text.Length && text[k] != '\n') continue;

                int end = k;
                if (end > start && text[end - 1] == '\r') end--;
                if (end > start) AddSegment(text.Substring(start, end - start), brush, italic);

                if (k < text.Length)
                {
                    Flush();
                    _inlines.Add(new LineBreak());
                }
                start = k + 1;
            }
        }

        private void AddSegment(string seg, IBrush brush, bool italic)
        {
            if (ReferenceEquals(brush, Default) && !italic)
            {
                _buf.Append(seg);
                return;
            }

            Flush();
            var run = new Run(seg) { Foreground = brush };
            if (italic) run.FontStyle = FontStyle.Italic;
            _inlines.Add(run);
        }

        public void Flush()
        {
            if (_buf.Length == 0) return;
            _inlines.Add(new Run(_buf.ToString()) { Foreground = Default });
            _buf.Clear();
        }
    }
}
