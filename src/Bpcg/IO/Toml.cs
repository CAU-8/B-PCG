using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bpcg.IO;

/// <summary>TOML 문서가 올바르지 않을 때 (Python tomllib.TOMLDecodeError).</summary>
public sealed class TomlDecodeException : Exception
{
    public TomlDecodeException(string message) : base(message)
    {
    }
}

/// <summary>TOML 날짜·시각 값. 설정에서는 쓰지 않고 형 검사에서 거부하려고만 둡니다.</summary>
/// <param name="Kind">"date", "datetime", "time" (Python 형 이름).</param>
/// <param name="Text">TOML 원문.</param>
public sealed record TomlDateTime(string Kind, string Text);

// 이 파일은 CPython 3.13 Lib/tomllib/_parser.py·_re.py 를 C# 으로 옮겼습니다. 원본의 저작권과 허가 고지:
//
// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 Taneli Hukkinen
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
// associated documentation files (the "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the
// following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial
// portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
// LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
// EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN
// AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE
// OR OTHER DEALINGS IN THE SOFTWARE.

/// <summary>
/// Python 3.13 <c>tomllib</c>(TOML 1.0.0)을 그대로 옮긴 TOML 읽기 (MIT, 위 고지문).
/// </summary>
/// <remarks>
/// 값 모델: 표는 <c>OrderedDictionary&lt;string, object?&gt;</c>(문서 순서), 배열은 <c>List&lt;object?&gt;</c>,
/// 정수는 long, 실수는 double, bool, string, 날짜·시각은 <see cref="TomlDateTime"/>.
/// Python 과 다른 점은 하나뿐입니다: int64 범위를 넘는 정수는 Python 이 받지만 여기서는 오류입니다
/// (docs/csharp_port.md 6장 core ①, 의도한 차이).
/// </remarks>
public static class Toml
{
    private static readonly HashSet<char> AsciiCtrl = BuildCtrl();
    private static readonly HashSet<char> IllegalBasicStrChars = Without(AsciiCtrl, "\t");
    private static readonly HashSet<char> IllegalMultilineBasicStrChars = Without(AsciiCtrl, "\t\n");
    private static readonly HashSet<char> IllegalLiteralStrChars = IllegalBasicStrChars;
    private static readonly HashSet<char> IllegalMultilineLiteralStrChars = IllegalMultilineBasicStrChars;
    private static readonly HashSet<char> IllegalCommentChars = IllegalBasicStrChars;
    private static readonly HashSet<char> TomlWs = [' ', '\t'];
    private static readonly HashSet<char> TomlWsAndNewline = [' ', '\t', '\n'];
    private static readonly HashSet<char> BareKeyChars =
        [.. "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"];
    private static readonly HashSet<char> KeyInitialChars = [.. BareKeyChars, '"', '\''];
    private static readonly HashSet<char> HexDigitChars = [.. "0123456789abcdefABCDEF"];

    private static readonly Dictionary<string, string> BasicStrEscapeReplacements = new()
    {
        ["\\b"] = "\u0008",
        ["\\t"] = "\u0009",
        ["\\n"] = "\u000A",
        ["\\f"] = "\u000C",
        ["\\r"] = "\u000D",
        ["\\\""] = "\"",
        ["\\\\"] = "\\",
    };

    private const string TimeReStr =
        "([01][0-9]|2[0-3]):([0-5][0-9]):([0-5][0-9])(?:\\.([0-9]{1,6})[0-9]*)?";

    private static readonly Regex ReNumber = new(
        "\\G(?:0(?:x[0-9A-Fa-f](?:_?[0-9A-Fa-f])*|b[01](?:_?[01])*|o[0-7](?:_?[0-7])*)"
        + "|[+-]?(?:0|[1-9](?:_?[0-9])*)"
        + "(?<floatpart>(?:\\.[0-9](?:_?[0-9])*)?(?:[eE][+-]?[0-9](?:_?[0-9])*)?))",
        RegexOptions.CultureInvariant);

    private static readonly Regex ReLocalTime = new("\\G" + TimeReStr, RegexOptions.CultureInvariant);

    private static readonly Regex ReDateTime = new(
        "\\G([0-9]{4})-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])"
        + "(?:[Tt ]" + TimeReStr + "(?:([Zz])|([+-])([01][0-9]|2[0-3]):([0-5][0-9]))?)?",
        RegexOptions.CultureInvariant);

    private const int MaxKeyParts = 1000; // sys.getrecursionlimit() 기본값

    private const int FlagFrozen = 0;
    private const int FlagExplicitNest = 1;

    private static HashSet<char> BuildCtrl()
    {
        var s = new HashSet<char>();
        for (int i = 0; i < 32; i++)
        {
            s.Add((char)i);
        }
        s.Add((char)127);
        return s;
    }

    private static HashSet<char> Without(HashSet<char> s, string remove)
    {
        var o = new HashSet<char>(s);
        foreach (char c in remove)
        {
            o.Remove(c);
        }
        return o;
    }

    /// <summary>tomllib.load: UTF-8 바이트를 읽습니다(BOM 은 떼지 않으므로 Python 처럼 오류가 됩니다).</summary>
    public static OrderedDictionary<string, object?> Load(byte[] bytes)
    {
        string s;
        try
        {
            s = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException e)
        {
            throw new TomlDecodeException($"UTF-8 로 읽을 수 없습니다: {e.Message}");
        }
        return Loads(s);
    }

    /// <summary>tomllib.loads.</summary>
    public static OrderedDictionary<string, object?> Loads(string s)
    {
        // 명세는 "\r\n" 을 "\n" 으로 바꿔도 된다고 합니다(문자열 안 포함).
        string src = s.Replace("\r\n", "\n", StringComparison.Ordinal);
        int pos = 0;
        var output = new Output(new NestedDict(), new Flags());
        string[] header = [];

        while (true)
        {
            // 1. 줄 앞 공백 건너뛰기
            pos = SkipChars(src, pos, TomlWs);
            // 2. 규칙 읽기
            if (pos >= src.Length)
            {
                break;
            }
            char ch = src[pos];
            if (ch == '\n')
            {
                pos++;
                continue;
            }
            if (KeyInitialChars.Contains(ch))
            {
                pos = KeyValueRule(src, pos, output, header);
                pos = SkipChars(src, pos, TomlWs);
            }
            else if (ch == '[')
            {
                char? second = pos + 1 < src.Length ? src[pos + 1] : null;
                output.Flags.FinalizePending();
                if (second == '[')
                {
                    (pos, header) = CreateListRule(src, pos, output);
                }
                else
                {
                    (pos, header) = CreateDictRule(src, pos, output);
                }
                pos = SkipChars(src, pos, TomlWs);
            }
            else if (ch != '#')
            {
                throw SuffixedErr(src, pos, "Invalid statement");
            }
            // 3. 주석 건너뛰기
            pos = SkipComment(src, pos);
            // 4. 줄 끝이나 문서 끝
            if (pos >= src.Length)
            {
                break;
            }
            if (src[pos] != '\n')
            {
                throw SuffixedErr(src, pos, "Expected newline or end of document after a statement");
            }
            pos++;
        }
        return output.Data.Dict;
    }

    private sealed record Output(NestedDict Data, Flags Flags);

    private sealed class FlagNode
    {
        public HashSet<int> Flags { get; } = [];

        public HashSet<int> RecursiveFlags { get; } = [];

        public Dictionary<string, FlagNode> Nested { get; } = [];
    }

    /// <summary>읽은 키·이름 공간에 붙는 표시 (tomllib Flags).</summary>
    private sealed class Flags
    {
        private readonly Dictionary<string, FlagNode> _flags = [];
        private readonly List<(string[] Key, int Flag)> _pending = [];

        public void AddPending(string[] key, int flag) => _pending.Add((key, flag));

        public void FinalizePending()
        {
            foreach ((string[] key, int flag) in _pending)
            {
                Set(key, flag, recursive: false);
            }
            _pending.Clear();
        }

        public void UnsetAll(string[] key)
        {
            Dictionary<string, FlagNode> cont = _flags;
            for (int i = 0; i < key.Length - 1; i++)
            {
                if (!cont.TryGetValue(key[i], out FlagNode? node))
                {
                    return;
                }
                cont = node.Nested;
            }
            cont.Remove(key[^1]);
        }

        public void Set(string[] key, int flag, bool recursive)
        {
            Dictionary<string, FlagNode> cont = _flags;
            for (int i = 0; i < key.Length - 1; i++)
            {
                if (!cont.TryGetValue(key[i], out FlagNode? node))
                {
                    node = new FlagNode();
                    cont[key[i]] = node;
                }
                cont = node.Nested;
            }
            string stem = key[^1];
            if (!cont.TryGetValue(stem, out FlagNode? last))
            {
                last = new FlagNode();
                cont[stem] = last;
            }
            (recursive ? last.RecursiveFlags : last.Flags).Add(flag);
        }

        public bool Is(string[] key, int flag)
        {
            if (key.Length == 0)
            {
                return false; // 문서 뿌리에는 표시가 없음
            }
            Dictionary<string, FlagNode> cont = _flags;
            for (int i = 0; i < key.Length - 1; i++)
            {
                if (!cont.TryGetValue(key[i], out FlagNode? inner))
                {
                    return false;
                }
                if (inner.RecursiveFlags.Contains(flag))
                {
                    return true;
                }
                cont = inner.Nested;
            }
            if (cont.TryGetValue(key[^1], out FlagNode? stem))
            {
                return stem.Flags.Contains(flag) || stem.RecursiveFlags.Contains(flag);
            }
            return false;
        }
    }

    private sealed class NestedDict
    {
        public OrderedDictionary<string, object?> Dict { get; } = [];

        public OrderedDictionary<string, object?> GetOrCreateNest(string[] key, bool accessLists = true)
        {
            object? cont = Dict;
            foreach (string k in key)
            {
                var d = (OrderedDictionary<string, object?>)cont!;
                if (!d.TryGetValue(k, out object? next))
                {
                    next = new OrderedDictionary<string, object?>();
                    d[k] = next;
                }
                cont = next;
                if (accessLists && cont is List<object?> list)
                {
                    cont = list[^1];
                }
                if (cont is not OrderedDictionary<string, object?>)
                {
                    throw new KeyNotFoundException("There is no nest behind this key");
                }
            }
            return (OrderedDictionary<string, object?>)cont!;
        }

        public void AppendNestToList(string[] key)
        {
            OrderedDictionary<string, object?> cont = GetOrCreateNest(key[..^1]);
            string lastKey = key[^1];
            if (cont.TryGetValue(lastKey, out object? existing))
            {
                if (existing is not List<object?> list)
                {
                    throw new KeyNotFoundException("An object other than list found behind this key");
                }
                list.Add(new OrderedDictionary<string, object?>());
            }
            else
            {
                cont[lastKey] = new List<object?> { new OrderedDictionary<string, object?>() };
            }
        }
    }

    private static int SkipChars(string src, int pos, HashSet<char> chars)
    {
        while (pos < src.Length && chars.Contains(src[pos]))
        {
            pos++;
        }
        return pos;
    }

    private static int SkipUntil(string src, int pos, string expect, HashSet<char> errorOn, bool errorOnEof)
    {
        int newPos = src.IndexOf(expect, pos, StringComparison.Ordinal);
        if (newPos < 0)
        {
            newPos = src.Length;
            if (errorOnEof)
            {
                throw SuffixedErr(src, newPos, $"Expected {PyRepr(expect)}");
            }
        }
        for (int i = pos; i < newPos; i++)
        {
            if (errorOn.Contains(src[i]))
            {
                throw SuffixedErr(src, i, $"Found invalid character {PyRepr(src[i].ToString())}");
            }
        }
        return newPos;
    }

    private static int SkipComment(string src, int pos)
    {
        if (pos < src.Length && src[pos] == '#')
        {
            return SkipUntil(src, pos + 1, "\n", IllegalCommentChars, errorOnEof: false);
        }
        return pos;
    }

    private static int SkipCommentsAndArrayWs(string src, int pos)
    {
        while (true)
        {
            int before = pos;
            pos = SkipChars(src, pos, TomlWsAndNewline);
            pos = SkipComment(src, pos);
            if (pos == before)
            {
                return pos;
            }
        }
    }

    private static (int, string[]) CreateDictRule(string src, int pos, Output output)
    {
        pos++; // "[" 건너뛰기
        pos = SkipChars(src, pos, TomlWs);
        (pos, string[] key) = ParseKey(src, pos);
        if (output.Flags.Is(key, FlagExplicitNest) || output.Flags.Is(key, FlagFrozen))
        {
            throw SuffixedErr(src, pos, $"Cannot declare {KeyRepr(key)} twice");
        }
        output.Flags.Set(key, FlagExplicitNest, recursive: false);
        try
        {
            output.Data.GetOrCreateNest(key);
        }
        catch (KeyNotFoundException)
        {
            throw SuffixedErr(src, pos, "Cannot overwrite a value");
        }
        if (!src.AsSpan(pos).StartsWith("]", StringComparison.Ordinal))
        {
            throw SuffixedErr(src, pos, "Expected ']' at the end of a table declaration");
        }
        return (pos + 1, key);
    }

    private static (int, string[]) CreateListRule(string src, int pos, Output output)
    {
        pos += 2; // "[[" 건너뛰기
        pos = SkipChars(src, pos, TomlWs);
        (pos, string[] key) = ParseKey(src, pos);
        if (output.Flags.Is(key, FlagFrozen))
        {
            throw SuffixedErr(src, pos, $"Cannot mutate immutable namespace {KeyRepr(key)}");
        }
        output.Flags.UnsetAll(key);
        output.Flags.Set(key, FlagExplicitNest, recursive: false);
        try
        {
            output.Data.AppendNestToList(key);
        }
        catch (KeyNotFoundException)
        {
            throw SuffixedErr(src, pos, "Cannot overwrite a value");
        }
        if (!src.AsSpan(pos).StartsWith("]]", StringComparison.Ordinal))
        {
            throw SuffixedErr(src, pos, "Expected ']]' at the end of an array declaration");
        }
        return (pos + 2, key);
    }

    private static int KeyValueRule(string src, int pos, Output output, string[] header)
    {
        (pos, string[] key, object? value) = ParseKeyValuePair(src, pos);
        string[] keyParent = key[..^1];
        string keyStem = key[^1];
        string[] absKeyParent = [.. header, .. keyParent];

        for (int i = 1; i < key.Length; i++)
        {
            string[] contKey = [.. header, .. key[..i]];
            if (output.Flags.Is(contKey, FlagExplicitNest))
            {
                throw SuffixedErr(src, pos, $"Cannot redefine namespace {KeyRepr(contKey)}");
            }
            output.Flags.AddPending(contKey, FlagExplicitNest);
        }
        if (output.Flags.Is(absKeyParent, FlagFrozen))
        {
            throw SuffixedErr(src, pos, $"Cannot mutate immutable namespace {KeyRepr(absKeyParent)}");
        }
        OrderedDictionary<string, object?> nest;
        try
        {
            nest = output.Data.GetOrCreateNest(absKeyParent);
        }
        catch (KeyNotFoundException)
        {
            throw SuffixedErr(src, pos, "Cannot overwrite a value");
        }
        if (nest.ContainsKey(keyStem))
        {
            throw SuffixedErr(src, pos, "Cannot overwrite a value");
        }
        if (value is OrderedDictionary<string, object?> or List<object?>)
        {
            output.Flags.Set([.. header, .. key], FlagFrozen, recursive: true);
        }
        nest[keyStem] = value;
        return pos;
    }

    private static (int, string[], object?) ParseKeyValuePair(string src, int pos)
    {
        (pos, string[] key) = ParseKey(src, pos);
        if (pos >= src.Length || src[pos] != '=')
        {
            throw SuffixedErr(src, pos, "Expected '=' after a key in a key/value pair");
        }
        pos++;
        pos = SkipChars(src, pos, TomlWs);
        (pos, object? value) = ParseValue(src, pos);
        return (pos, key, value);
    }

    private static (int, string[]) ParseKey(string src, int pos)
    {
        (pos, string part) = ParseKeyPart(src, pos);
        var key = new List<string> { part };
        pos = SkipChars(src, pos, TomlWs);
        while (true)
        {
            if (pos >= src.Length || src[pos] != '.')
            {
                return (pos, key.ToArray());
            }
            pos++;
            pos = SkipChars(src, pos, TomlWs);
            (pos, part) = ParseKeyPart(src, pos);
            key.Add(part);
            if (key.Count > MaxKeyParts)
            {
                throw new TomlDecodeException($"TOML key has more than the allowed {MaxKeyParts} parts");
            }
            pos = SkipChars(src, pos, TomlWs);
        }
    }

    private static (int, string) ParseKeyPart(string src, int pos)
    {
        char? ch = pos < src.Length ? src[pos] : null;
        if (ch is char c1 && BareKeyChars.Contains(c1))
        {
            int start = pos;
            pos = SkipChars(src, pos, BareKeyChars);
            return (pos, src[start..pos]);
        }
        if (ch == '\'')
        {
            return ParseLiteralStr(src, pos);
        }
        if (ch == '"')
        {
            return ParseOneLineBasicStr(src, pos);
        }
        throw SuffixedErr(src, pos, "Invalid initial character for a key part");
    }

    private static (int, string) ParseOneLineBasicStr(string src, int pos) =>
        ParseBasicStr(src, pos + 1, multiline: false);

    private static (int, object?) ParseArray(string src, int pos)
    {
        pos++;
        var array = new List<object?>();
        pos = SkipCommentsAndArrayWs(src, pos);
        if (src.AsSpan(pos).StartsWith("]", StringComparison.Ordinal))
        {
            return (pos + 1, array);
        }
        while (true)
        {
            (pos, object? val) = ParseValue(src, pos);
            array.Add(val);
            pos = SkipCommentsAndArrayWs(src, pos);
            string c = pos < src.Length ? src[pos].ToString() : "";
            if (c == "]")
            {
                return (pos + 1, array);
            }
            if (c != ",")
            {
                throw SuffixedErr(src, pos, "Unclosed array");
            }
            pos++;
            pos = SkipCommentsAndArrayWs(src, pos);
            if (src.AsSpan(pos).StartsWith("]", StringComparison.Ordinal))
            {
                return (pos + 1, array);
            }
        }
    }

    private static (int, object?) ParseInlineTable(string src, int pos)
    {
        pos++;
        var nested = new NestedDict();
        var flags = new Flags();
        pos = SkipChars(src, pos, TomlWs);
        if (src.AsSpan(pos).StartsWith("}", StringComparison.Ordinal))
        {
            return (pos + 1, nested.Dict);
        }
        while (true)
        {
            (pos, string[] key, object? value) = ParseKeyValuePair(src, pos);
            string[] keyParent = key[..^1];
            string keyStem = key[^1];
            if (flags.Is(key, FlagFrozen))
            {
                throw SuffixedErr(src, pos, $"Cannot mutate immutable namespace {KeyRepr(key)}");
            }
            OrderedDictionary<string, object?> nest;
            try
            {
                nest = nested.GetOrCreateNest(keyParent, accessLists: false);
            }
            catch (KeyNotFoundException)
            {
                throw SuffixedErr(src, pos, "Cannot overwrite a value");
            }
            if (nest.ContainsKey(keyStem))
            {
                throw SuffixedErr(src, pos, $"Duplicate inline table key {PyRepr(keyStem)}");
            }
            nest[keyStem] = value;
            pos = SkipChars(src, pos, TomlWs);
            string c = pos < src.Length ? src[pos].ToString() : "";
            if (c == "}")
            {
                return (pos + 1, nested.Dict);
            }
            if (c != ",")
            {
                throw SuffixedErr(src, pos, "Unclosed inline table");
            }
            if (value is OrderedDictionary<string, object?> or List<object?>)
            {
                flags.Set(key, FlagFrozen, recursive: true);
            }
            pos++;
            pos = SkipChars(src, pos, TomlWs);
        }
    }

    private static (int, string) ParseBasicStrEscape(string src, int pos, bool multiline)
    {
        string escapeId = src.Substring(pos, Math.Min(2, src.Length - pos));
        pos += 2;
        if (multiline && escapeId is "\\ " or "\\\t" or "\\\n")
        {
            // 다음 공백 아닌 글자(또는 문서 끝)까지 공백을 건너뜁니다. 줄바꿈 전에 다른 글자가 있으면 오류.
            if (escapeId != "\\\n")
            {
                pos = SkipChars(src, pos, TomlWs);
                if (pos >= src.Length)
                {
                    return (pos, "");
                }
                if (src[pos] != '\n')
                {
                    throw SuffixedErr(src, pos, "Unescaped '\\' in a string");
                }
                pos++;
            }
            pos = SkipChars(src, pos, TomlWsAndNewline);
            return (pos, "");
        }
        if (escapeId == "\\u")
        {
            return ParseHexChar(src, pos, 4);
        }
        if (escapeId == "\\U")
        {
            return ParseHexChar(src, pos, 8);
        }
        if (BasicStrEscapeReplacements.TryGetValue(escapeId, out string? rep))
        {
            return (pos, rep);
        }
        throw SuffixedErr(src, pos, "Unescaped '\\' in a string");
    }

    private static (int, string) ParseHexChar(string src, int pos, int hexLen)
    {
        string hex = src.Substring(pos, Math.Min(hexLen, Math.Max(0, src.Length - pos)));
        if (hex.Length != hexLen || !hex.All(HexDigitChars.Contains))
        {
            throw SuffixedErr(src, pos, "Invalid hex value");
        }
        pos += hexLen;
        long cp = long.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (!((cp >= 0 && cp <= 55295) || (cp >= 57344 && cp <= 1114111)))
        {
            throw SuffixedErr(src, pos, "Escaped character is not a Unicode scalar value");
        }
        return (pos, char.ConvertFromUtf32((int)cp));
    }

    private static (int, string) ParseLiteralStr(string src, int pos)
    {
        pos++; // 여는 작은따옴표
        int start = pos;
        pos = SkipUntil(src, pos, "'", IllegalLiteralStrChars, errorOnEof: true);
        return (pos + 1, src[start..pos]);
    }

    private static (int, string) ParseMultilineStr(string src, int pos, bool literal)
    {
        pos += 3;
        if (src.AsSpan(pos).StartsWith("\n", StringComparison.Ordinal))
        {
            pos++;
        }
        string delim;
        string result;
        if (literal)
        {
            delim = "'";
            int endPos = SkipUntil(src, pos, "'''", IllegalMultilineLiteralStrChars, errorOnEof: true);
            result = src[pos..endPos];
            pos = endPos + 3;
        }
        else
        {
            delim = "\"";
            (pos, result) = ParseBasicStr(src, pos, multiline: true);
        }
        // 끝 구분자가 3개가 아니라 4·5개면 앞의 1·2개는 내용입니다.
        if (!src.AsSpan(pos).StartsWith(delim, StringComparison.Ordinal))
        {
            return (pos, result);
        }
        pos++;
        if (!src.AsSpan(pos).StartsWith(delim, StringComparison.Ordinal))
        {
            return (pos, result + delim);
        }
        pos++;
        return (pos, result + delim + delim);
    }

    private static (int, string) ParseBasicStr(string src, int pos, bool multiline)
    {
        HashSet<char> errorOn = multiline ? IllegalMultilineBasicStrChars : IllegalBasicStrChars;
        var result = new StringBuilder();
        int start = pos;
        while (true)
        {
            if (pos >= src.Length)
            {
                throw SuffixedErr(src, pos, "Unterminated string");
            }
            char ch = src[pos];
            if (ch == '"')
            {
                if (!multiline)
                {
                    return (pos + 1, result.Append(src, start, pos - start).ToString());
                }
                if (src.AsSpan(pos).StartsWith("\"\"\"", StringComparison.Ordinal))
                {
                    return (pos + 3, result.Append(src, start, pos - start).ToString());
                }
                pos++;
                continue;
            }
            if (ch == '\\')
            {
                result.Append(src, start, pos - start);
                (pos, string parsed) = ParseBasicStrEscape(src, pos, multiline);
                result.Append(parsed);
                start = pos;
                continue;
            }
            if (errorOn.Contains(ch))
            {
                throw SuffixedErr(src, pos, $"Illegal character {PyRepr(ch.ToString())}");
            }
            pos++;
        }
    }

    private static (int, object?) ParseValue(string src, int pos)
    {
        char? ch = pos < src.Length ? src[pos] : null;
        ReadOnlySpan<char> rest = src.AsSpan(Math.Min(pos, src.Length));

        if (ch == '"')
        {
            if (rest.StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                return ParseMultilineStr(src, pos, literal: false);
            }
            return ParseOneLineBasicStr(src, pos);
        }
        if (ch == '\'')
        {
            if (rest.StartsWith("'''", StringComparison.Ordinal))
            {
                return ParseMultilineStr(src, pos, literal: true);
            }
            return ParseLiteralStr(src, pos);
        }
        if (ch == 't' && rest.StartsWith("true", StringComparison.Ordinal))
        {
            return (pos + 4, true);
        }
        if (ch == 'f' && rest.StartsWith("false", StringComparison.Ordinal))
        {
            return (pos + 5, false);
        }
        if (ch == '[')
        {
            return ParseArray(src, pos);
        }
        if (ch == '{')
        {
            return ParseInlineTable(src, pos);
        }

        Match dt = ReDateTime.Match(src, pos);
        if (dt.Success)
        {
            if (!ValidDate(dt))
            {
                throw SuffixedErr(src, pos, "Invalid date or datetime");
            }
            string kind = dt.Groups[4].Success ? "datetime" : "date";
            return (dt.Index + dt.Length, new TomlDateTime(kind, dt.Value));
        }
        Match lt = ReLocalTime.Match(src, pos);
        if (lt.Success)
        {
            return (lt.Index + lt.Length, new TomlDateTime("time", lt.Value));
        }

        Match num = ReNumber.Match(src, pos);
        if (num.Success)
        {
            return (num.Index + num.Length, MatchToNumber(src, pos, num));
        }

        string firstThree = src.Substring(pos, Math.Min(3, src.Length - pos));
        if (firstThree is "inf" or "nan")
        {
            return (pos + 3, ParseSpecialFloat(firstThree));
        }
        string firstFour = src.Substring(pos, Math.Min(4, src.Length - pos));
        if (firstFour is "-inf" or "+inf" or "-nan" or "+nan")
        {
            return (pos + 4, ParseSpecialFloat(firstFour));
        }
        throw SuffixedErr(src, pos, "Invalid value");
    }

    private static bool ValidDate(Match m)
    {
        int year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        // Python datetime.date: 1 ≤ year ≤ 9999
        return year >= 1 && day <= DateTime.DaysInMonth(year, month);
    }

    private static double ParseSpecialFloat(string s) => s switch
    {
        "inf" or "+inf" => double.PositiveInfinity,
        "-inf" => double.NegativeInfinity,
        "nan" or "+nan" => BitConverter.UInt64BitsToDouble(0x7FF8000000000000UL),
        _ => BitConverter.UInt64BitsToDouble(0xFFF8000000000000UL), // -nan: 부호 비트가 선 NaN
    };

    private static object MatchToNumber(string src, int pos, Match m)
    {
        string text = m.Value;
        if (m.Groups["floatpart"].Length > 0)
        {
            // Python float() 는 숫자 사이 밑줄을 받습니다(PEP 515). .NET 도 정확히 반올림합니다.
            string clean = text.Replace("_", "", StringComparison.Ordinal);
            return double.Parse(clean, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        // int(text, 0): 진법 접두어와 밑줄
        string t = text.Replace("_", "", StringComparison.Ordinal);
        bool neg = t.StartsWith('-');
        if (t.StartsWith('+') || neg)
        {
            t = t[1..];
        }
        System.Numerics.BigInteger value;
        if (t.StartsWith("0x", StringComparison.Ordinal))
        {
            value = System.Numerics.BigInteger.Parse("0" + t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else if (t.StartsWith("0o", StringComparison.Ordinal))
        {
            value = ParseRadix(t[2..], 8);
        }
        else if (t.StartsWith("0b", StringComparison.Ordinal))
        {
            value = ParseRadix(t[2..], 2);
        }
        else
        {
            value = System.Numerics.BigInteger.Parse(t, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        if (neg)
        {
            value = -value;
        }
        if (value < long.MinValue || value > long.MaxValue)
        {
            // Python 은 크기 제한 없는 int 로 받지만 C# 설정 값은 long 이라 막습니다(의도한 차이).
            throw SuffixedErr(src, pos, "Integer is outside the int64 range");
        }
        return (long)value;
    }

    private static System.Numerics.BigInteger ParseRadix(string digits, int radix)
    {
        System.Numerics.BigInteger v = 0;
        foreach (char c in digits)
        {
            v = (v * radix) + (c - '0');
        }
        return v;
    }

    private static TomlDecodeException SuffixedErr(string src, int pos, string msg)
    {
        string coord;
        if (pos >= src.Length)
        {
            coord = "end of document";
        }
        else
        {
            int line = 1;
            for (int i = 0; i < pos; i++)
            {
                if (src[i] == '\n')
                {
                    line++;
                }
            }
            int column = line == 1 ? pos + 1 : pos - src.LastIndexOf('\n', pos - 1);
            coord = $"line {line.ToString(CultureInfo.InvariantCulture)}, column {column.ToString(CultureInfo.InvariantCulture)}";
        }
        return new TomlDecodeException($"{msg} (at {coord})");
    }

    private static string KeyRepr(string[] key) =>
        "(" + string.Join(", ", key.Select(PyRepr)) + (key.Length == 1 ? ",)" : ")");

    /// <summary>오류 문구용 Python repr(str) 근사 (작은따옴표, 제어 문자 이스케이프).</summary>
    internal static string PyRepr(string s)
    {
        char quote = s.Contains('\'', StringComparison.Ordinal) && !s.Contains('"', StringComparison.Ordinal)
            ? '"'
            : '\'';
        var sb = new StringBuilder();
        sb.Append(quote);
        foreach (char c in s)
        {
            if (c == quote || c == '\\')
            {
                sb.Append('\\').Append(c);
            }
            else if (c == '\n')
            {
                sb.Append("\\n");
            }
            else if (c == '\r')
            {
                sb.Append("\\r");
            }
            else if (c == '\t')
            {
                sb.Append("\\t");
            }
            else if (c < 0x20 || c == 0x7F)
            {
                sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }
        sb.Append(quote);
        return sb.ToString();
    }
}
