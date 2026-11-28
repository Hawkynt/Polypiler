using System;
using System.Collections.Generic;

namespace Polypiler.Core.Frontends.Perl.Syntax;

/// <summary>The sets of words and characters the lexer and parser test against.</summary>
internal static class PerlWordSets {

  /// <summary>The words that are operators or statement modifiers where an operator belongs.</summary>
  public static IReadOnlySet<string> Operators { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "x", "lt", "gt", "le", "ge", "eq", "ne", "cmp", "and", "or", "xor", "if", "unless", "while", "until", "for", "foreach"
  };

  /// <summary>The quote-like operators.</summary>
  public static IReadOnlySet<string> Quotes { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "q", "qq", "qw", "qx", "m", "qr", "s", "tr", "y"
  };

  /// <summary>The punctuation that starts a term where a term belongs.</summary>
  public static IReadOnlySet<string> TermOperators { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "(", "[", "{", "\\", "-", "+", "!", "~", "~.", "++", "--", "<<>>"
  };

  /// <summary>The keywords a feature enables, with the feature.</summary>
  public static IReadOnlyDictionary<string, string> FeatureKeywords { get; } = new Dictionary<string, string>(StringComparer.Ordinal) {
    ["say"] = "say", ["state"] = "state", ["fc"] = "fc", ["evalbytes"] = "evalbytes", ["__SUB__"] = "current_sub",
    ["any"] = "keyword_any", ["all"] = "keyword_all", ["try"] = "try", ["catch"] = "try", ["finally"] = "try", ["defer"] = "defer",
    ["given"] = "switch", ["when"] = "switch", ["default"] = "switch", ["break"] = "switch", ["isa"] = "isa",
    ["class"] = "class", ["field"] = "class", ["method"] = "class", ["ADJUST"] = "class", ["__CLASS__"] = "class"
  };

  /// <summary>Whether a word read after a bareword or a variable is an operator or a statement modifier.</summary>
  public static bool IsOperatorWord(string word, bool isaEnabled) => Operators.Contains(word) || isaEnabled && word == "isa";

}
