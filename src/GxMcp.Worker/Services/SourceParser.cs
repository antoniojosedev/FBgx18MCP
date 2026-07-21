using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Services
{
    public class ParsedCall
    {
        public string Callee { get; set; }
        public List<string> Args { get; set; } = new List<string>();
        public int LineNumber { get; set; }
        public int Column { get; set; }
    }

    public static class SourceParser
    {
        public static List<ParsedCall> ParseCalls(string source, bool includeComments = false)
        {
            var calls = new List<ParsedCall>();
            if (string.IsNullOrEmpty(source)) return calls;

            int i = 0;
            int line = 1;
            int lineStart = 0;
            int n = source.Length;

            while (i < n)
            {
                char c = source[i];

                if (c == '\n') { line++; lineStart = i + 1; i++; continue; }
                if (c == '\r') { i++; continue; }

                if (c == '"' || c == '\'')
                {
                    i = SkipString(source, i, c, ref line, ref lineStart);
                    continue;
                }

                if (!includeComments && c == '/' && i + 1 < n)
                {
                    if (source[i + 1] == '/') { i = SkipToEndOfLine(source, i); continue; }
                    if (source[i + 1] == '*') { i = SkipBlockComment(source, i, ref line, ref lineStart); continue; }
                }

                if (IsIdentStart(c))
                {
                    int idStart = i;
                    string ident = ReadQualifiedIdentifier(source, ref i);
                    SkipInlineWhitespace(source, ref i);
                    if (i < n && source[i] == '(')
                    {
                        var call = new ParsedCall
                        {
                            Callee = ident,
                            LineNumber = line,
                            Column = idStart - lineStart + 1
                        };
                        i++; // consume '('
                        call.Args = ReadArgs(source, ref i, ref line, ref lineStart);
                        calls.Add(call);
                    }
                    continue;
                }

                i++;
            }

            return calls;
        }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '&';
        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static string ReadQualifiedIdentifier(string s, ref int i)
        {
            int start = i;
            if (s[i] == '&') i++;
            while (i < s.Length && IsIdentChar(s[i])) i++;
            while (i < s.Length && s[i] == '.' && i + 1 < s.Length && IsIdentStart(s[i + 1]))
            {
                i++;
                if (s[i] == '&') i++;
                while (i < s.Length && IsIdentChar(s[i])) i++;
            }
            return s.Substring(start, i - start);
        }

        private static List<string> ReadArgs(string s, ref int i, ref int line, ref int lineStart)
        {
            var args = new List<string>();
            int depth = 0;
            var current = new System.Text.StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\n') { line++; lineStart = i + 1; current.Append(c); i++; continue; }
                if (c == '"' || c == '\'')
                {
                    int strStart = i;
                    i = SkipString(s, i, c, ref line, ref lineStart);
                    current.Append(s.Substring(strStart, i - strStart));
                    continue;
                }
                if (c == '(') { depth++; current.Append(c); i++; continue; }
                if (c == ')')
                {
                    if (depth == 0)
                    {
                        string trimmed = current.ToString().Trim();
                        if (trimmed.Length > 0 || args.Count > 0) args.Add(trimmed);
                        i++;
                        return args;
                    }
                    depth--;
                    current.Append(c);
                    i++;
                    continue;
                }
                if (c == ',' && depth == 0)
                {
                    args.Add(current.ToString().Trim());
                    current.Clear();
                    i++;
                    continue;
                }
                current.Append(c);
                i++;
            }
            return args;
        }

        private static int SkipString(string s, int i, char quote, ref int line, ref int lineStart)
        {
            i++;
            while (i < s.Length)
            {
                if (s[i] == '\n') { line++; lineStart = i + 1; }
                if (s[i] == quote)
                {
                    if (i + 1 < s.Length && s[i + 1] == quote) { i += 2; continue; } // escaped ""
                    return i + 1;                                                    // terminator
                }
                i++;
            }
            return i;
        }

        private static int SkipToEndOfLine(string s, int i)
        {
            while (i < s.Length && s[i] != '\n') i++;
            return i;
        }

        private static int SkipBlockComment(string s, int i, ref int line, ref int lineStart)
        {
            i += 2;
            while (i + 1 < s.Length)
            {
                if (s[i] == '\n') { line++; lineStart = i + 1; }
                if (s[i] == '*' && s[i + 1] == '/') return i + 2;
                i++;
            }
            return s.Length;
        }

        private static void SkipInlineWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        }
    }
}
