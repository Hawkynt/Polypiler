using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Polypiler.Core.Backends.VBScript;

/// <summary>
/// Maps IR names to valid, collision-free, case-insensitive VBScript 5.8 identifiers, script
/// paths and string literals. Every VBScript identifier must start with an ASCII letter.
/// </summary>
internal static class VBScriptNaming {

  /// <summary>VBScript keywords, future-reserved words, operators, constants and built-in functions (case-insensitive).</summary>
  public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "Abs", "And", "Array", "As", "Asc", "AscB", "AscW", "Atn", "Attribute", "Base", "ByRef", "ByVal",
    "Call", "Case", "CBool", "CByte", "CCur", "CDate", "CDbl", "Chr", "ChrB", "ChrW", "CInt", "Class",
    "CLng", "Compare", "Const", "Cos", "CreateObject", "CSng", "CStr", "Currency", "Date", "DateAdd",
    "DateDiff", "DatePart", "DateSerial", "DateValue", "Day", "Debug", "Decimal", "Declare", "Default",
    "Dim", "Do", "Double", "Each", "Else", "ElseIf", "Empty", "End", "Enum", "Eqv", "Erase", "Err",
    "Error", "Eval", "Event", "Execute", "ExecuteGlobal", "Exit", "Exp", "Explicit", "False", "Filter",
    "Fix", "For", "FormatCurrency", "FormatDateTime", "FormatNumber", "FormatPercent", "Friend",
    "Function", "Get", "GetLocale", "GetObject", "GetRef", "Global", "GoSub", "GoTo", "Hex", "Hour",
    "If", "Imp", "Implements", "In", "InputBox", "InStr", "InStrB", "InStrRev", "Int", "Integer",
    "Interface", "Is", "IsArray", "IsDate", "IsEmpty", "IsNull", "IsNumeric", "IsObject", "Join",
    "LBound", "LCase", "Left", "LeftB", "Len", "LenB", "Let", "Like", "Line", "LoadPicture", "Lock",
    "Log", "Long", "Loop", "LSet", "LTrim", "Me", "Mid", "MidB", "Minute", "Mod", "Month", "MonthName",
    "MsgBox", "New", "Next", "Not", "Nothing", "Now", "Null", "Object", "Oct", "On", "Option",
    "Optional", "Or", "ParamArray", "Preserve", "Private", "Property", "Public", "RaiseEvent",
    "Randomize", "ReDim", "Rem", "Replace", "Resume", "Return", "RGB", "Right", "RightB", "Rnd",
    "Round", "RSet", "RTrim", "ScriptEngine", "Second", "Select", "Set", "SetLocale", "Sgn", "Sin",
    "Single", "Space", "Split", "Sqr", "Static", "Step", "Stop", "StrComp", "String", "StrReverse",
    "Sub", "Tan", "Then", "Time", "Timer", "TimeSerial", "TimeValue", "To", "Trim", "True", "Type",
    "TypeName", "TypeOf", "UBound", "UCase", "Until", "Variant", "VarType", "Weekday", "WeekdayName",
    "Wend", "While", "With", "WithEvents", "Write", "WScript", "Xor", "Year",
    "vbCr", "vbCrLf", "vbLf", "vbTab", "vbNullChar", "vbNullString", "vbObjectError",
    "vbBinaryCompare", "vbTextCompare"
  };

  /// <summary>Converts an IR name into a valid VBScript identifier starting with an ASCII letter.</summary>
  public static string Identifier(string name, string prefix = "v_") {
    var text = new StringBuilder(name.Length);
    foreach (var ch in name)
      text.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');

    var raw = text.ToString();
    if (raw.Length == 0)
      return prefix + "id";
    if (!char.IsAsciiLetter(raw[0]))
      raw = prefix + raw;
    if (Reserved.Contains(raw) || raw.StartsWith("ply_", StringComparison.OrdinalIgnoreCase))
      raw += "_v";
    return raw;
  }

  /// <summary>Returns <paramref name="name"/> or <c>name_2</c>, <c>name_3</c>, ... until not taken (case-insensitively).</summary>
  public static string Free(string name, ISet<string> taken) {
    if (taken.Add(name))
      return name;

    for (var suffix = 2; ; ++suffix) {
      var candidate = name + "_" + suffix.ToString(CultureInfo.InvariantCulture);
      if (taken.Add(candidate))
        return candidate;
    }
  }

  /// <summary>Derives the output <c>.vbs</c> file name from the module name and optional override.</summary>
  public static string ScriptFileName(string moduleName, string? scriptName) {
    var raw = !string.IsNullOrWhiteSpace(scriptName) ? scriptName.Trim() : moduleName;
    if (raw.EndsWith(".vbs", StringComparison.OrdinalIgnoreCase))
      raw = raw[..^4];
    var clean = new StringBuilder(raw.Length);
    foreach (var ch in raw)
      clean.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
    var stem = clean.ToString().Trim('.');
    return (stem.Length > 0 ? stem : "module") + ".vbs";
  }

  /// <summary>Formats a VBScript string expression for arbitrary UTF-16 code units.</summary>
  public static string StringLiteral(string value) {
    if (value.Length == 0)
      return "\"\"";

    var parts = new List<string>();
    var current = new StringBuilder();
    void Flush() {
      if (current.Length == 0)
        return;
      parts.Add("\"" + current + "\"");
      current.Clear();
    }

    foreach (var ch in value) {
      switch (ch) {
        case '"':
          current.Append("\"\"");
          break;
        case '\n':
          Flush();
          parts.Add("Chr(10)");
          break;
        case '\r':
          Flush();
          parts.Add("Chr(13)");
          break;
        case '\t':
          Flush();
          parts.Add("Chr(9)");
          break;
        default:
          if (ch >= 0x20 && ch <= 0x7E) {
            current.Append(ch);
          } else {
            Flush();
            parts.Add($"ChrW({((int)ch).ToString(CultureInfo.InvariantCulture)})");
          }
          break;
      }
    }

    Flush();
    return string.Join(" & ", parts);
  }

}
