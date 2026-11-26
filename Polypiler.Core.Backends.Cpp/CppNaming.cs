using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.Cpp;

/// <summary>Maps IR names to idiomatic, collision-free ISO C++ identifiers, namespaces, file stems and string literals.</summary>
internal static class CppNaming {

  private static readonly HashSet<string> _reserved = new(StringComparer.Ordinal) {
    "alignas", "alignof", "and", "and_eq", "asm", "auto", "bitand", "bitor", "bool", "break",
    "case", "catch", "char", "char8_t", "char16_t", "char32_t", "class", "compl", "concept", "const",
    "consteval", "constexpr", "constinit", "const_cast", "continue", "co_await", "co_return", "co_yield",
    "decltype", "default", "delete", "do", "double", "dynamic_cast", "else", "enum", "explicit", "export",
    "extern", "false", "float", "for", "friend", "goto", "if", "inline", "int", "long", "mutable",
    "namespace", "new", "noexcept", "not", "not_eq", "nullptr", "operator", "or", "or_eq", "private",
    "protected", "public", "register", "reinterpret_cast", "requires", "return", "short", "signed",
    "sizeof", "static", "static_assert", "static_cast", "struct", "switch", "template", "this",
    "thread_local", "throw", "true", "try", "typedef", "typeid", "typename", "union", "unsigned",
    "using", "virtual", "void", "volatile", "wchar_t", "while", "xor", "xor_eq",
    "override", "final", "NULL", "EOF", "std", "main"
  };

  /// <summary>Converts an identifier to <c>snake_case</c>.</summary>
  public static string Snake(string name) {
    var text = new StringBuilder();
    for (var i = 0; i < name.Length; ++i) {
      var ch = name[i];
      if (char.IsAsciiLetterUpper(ch)) {
        var prev = i > 0 ? name[i - 1] : '\0';
        var next = i + 1 < name.Length ? name[i + 1] : '\0';
        if (i > 0 && (char.IsAsciiLetterLower(prev) || char.IsAsciiDigit(prev) || (char.IsAsciiLetterUpper(prev) && char.IsAsciiLetterLower(next))))
          text.Append('_');
        text.Append(char.ToLowerInvariant(ch));
      } else if (char.IsAsciiLetterOrDigit(ch)) {
        text.Append(ch);
      } else if (text.Length > 0 && text[^1] != '_') {
        text.Append('_');
      }
    }

    return text.ToString().Trim('_');
  }

  /// <summary>Converts an identifier to <c>PascalCase</c> for C++ types and enum values.</summary>
  public static string Pascal(string name) {
    var text = new StringBuilder();
    var cap = true;
    foreach (var ch in name) {
      if (char.IsAsciiLetterOrDigit(ch)) {
        text.Append(cap ? char.ToUpperInvariant(ch) : ch);
        cap = false;
      } else {
        cap = true;
      }
    }

    var result = text.ToString();
    if (result.Length == 0)
      return "Type";
    if (char.IsAsciiDigit(result[0]))
      result = "T" + result;
    return _reserved.Contains(result) ? result + "_" : result;
  }

  /// <summary>Maps an identifier to a safe <c>snake_case</c> C++ identifier.</summary>
  public static string Identifier(string name, string fallback = "v") {
    var snake = Snake(name.Replace("<", "_of_", StringComparison.Ordinal).Replace(",", "_and_", StringComparison.Ordinal));
    if (snake.Length == 0)
      snake = fallback;
    if (char.IsAsciiDigit(snake[0]))
      snake = "n_" + snake;
    return _reserved.Contains(snake) ? snake + "_" : snake;
  }

  /// <summary>Derives the file stem (without extension) from an IR module name.</summary>
  public static string FileStem(string moduleName) {
    var segments = moduleName.Replace("::", ".", StringComparison.Ordinal)
      .Split('.', StringSplitOptions.RemoveEmptyEntries);
    var last = segments.Length > 0 ? segments[^1] : moduleName;
    var stem = Snake(last);
    return stem.Length > 0 ? stem : "module";
  }

  /// <summary>Derives a valid C++17 nested namespace from an override or an IR module name.</summary>
  public static string Namespace(string moduleName, string? overrideNamespace) {
    var raw = !string.IsNullOrWhiteSpace(overrideNamespace) ? overrideNamespace : moduleName;
    var segments = raw.Replace("::", ".", StringComparison.Ordinal)
      .Split('.', StringSplitOptions.RemoveEmptyEntries)
      .Select(s => Snake(s))
      .Where(s => s.Length > 0)
      .Select(s => Identifier(s, "ns"))
      .ToArray();
    return segments.Length > 0 ? string.Join("::", segments) : "module_";
  }

  /// <summary>Returns <paramref name="name"/> or <c>name_2</c>, <c>name_3</c>, … until <paramref name="taken"/> is false.</summary>
  public static string Free(string name, Func<string, bool> taken) {
    if (!taken(name))
      return name;

    for (var suffix = 2; ; ++suffix) {
      var candidate = name + "_" + suffix.ToString(CultureInfo.InvariantCulture);
      if (!taken(candidate))
        return candidate;
    }
  }

  /// <summary>Formats a C++ <c>std::string</c> literal preserving embedded NULs and non-ASCII bytes safely.</summary>
  public static string StringLiteral(string value) {
    var hasNul = value.Contains('\0', StringComparison.Ordinal);
    var sb = new StringBuilder("std::string(\"");

    foreach (var ch in value) {
      switch (ch) {
        case '\\': sb.Append("\\\\"); break;
        case '"': sb.Append("\\\""); break;
        case '\n': sb.Append("\\n"); break;
        case '\r': sb.Append("\\r"); break;
        case '\t': sb.Append("\\t"); break;
        default:
          if (ch >= 0x20 && ch <= 0x7E) {
            sb.Append(ch);
          } else {
            var b = (int)ch & 0xFF;
            sb.Append('\\');
            sb.Append((char)('0' + ((b >> 6) & 7)));
            sb.Append((char)('0' + ((b >> 3) & 7)));
            sb.Append((char)('0' + (b & 7)));
          }
          break;
      }
    }

    sb.Append('"');
    if (hasNul)
      sb.Append(", ").Append(value.Length.ToString(CultureInfo.InvariantCulture));
    sb.Append(')');
    return sb.ToString();
  }

}
