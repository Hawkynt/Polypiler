using System;
using System.Collections.Generic;
using System.Text;

namespace Polypiler.Core.Backends.VisualBasic.Net;

/// <summary>
/// The names of Visual Basic .NET: letters, digits and underscores, starting with a letter, never a
/// reserved keyword of the language reference ("Keywords (Visual Basic)") unless in brackets, and never
/// starting with <c>Ply</c>, which the backend's helpers start with. Names are compared without case,
/// as the language compares them.
/// </summary>
public static class VbNetNaming {

  /// <summary>
  /// The reserved keywords, which a name is written in brackets as, <c>[Next]</c>, so that the name
  /// of the compiled member stays the IR's. The unreserved keywords (<c>Order</c>, <c>Key</c>,
  /// <c>Text</c>, …) are names like any other.
  /// </summary>
  public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "AddHandler", "AddressOf", "Alias", "And", "AndAlso", "As", "Boolean", "ByRef", "Byte", "ByVal", "Call", "Case", "Catch",
    "CBool", "CByte", "CChar", "CDate", "CDbl", "CDec", "Char", "CInt", "Class", "CLng", "CObj", "Const", "Continue", "CSByte",
    "CShort", "CSng", "CStr", "CType", "CUInt", "CULng", "CUShort", "Date", "Decimal", "Declare", "Default", "Delegate", "Dim",
    "DirectCast", "Do", "Double", "Each", "Else", "ElseIf", "End", "EndIf", "Enum", "Erase", "Error", "Event", "Exit", "False",
    "Finally", "For", "Friend", "Function", "Get", "GetType", "GetXMLNamespace", "Global", "GoSub", "GoTo", "Handles", "If",
    "Implements", "Imports", "In", "Inherits", "Integer", "Interface", "Is", "IsNot", "Let", "Lib", "Like", "Long", "Loop", "Me",
    "Mod", "Module", "MustInherit", "MustOverride", "MyBase", "MyClass", "NameOf", "Namespace", "Narrowing", "New", "Next", "Not",
    "Nothing", "NotInheritable", "NotOverridable", "Object", "Of", "On", "Operator", "Option", "Optional", "Or", "OrElse", "Out",
    "Overloads", "Overridable", "Overrides", "ParamArray", "Partial", "Private", "Property", "Protected", "Public", "RaiseEvent",
    "ReadOnly", "ReDim", "REM", "RemoveHandler", "Resume", "Return", "SByte", "Select", "Set", "Shadows", "Shared", "Short",
    "Single", "Static", "Step", "Stop", "String", "Structure", "Sub", "SyncLock", "Then", "Throw", "To", "True", "Try", "TryCast",
    "TypeOf", "UInteger", "ULong", "UShort", "Using", "Variant", "Wend", "When", "While", "Widening", "With", "WithEvents",
    "WriteOnly", "Xor"
  };

  /// <summary>The names that are no keywords but that a module name would shadow: the members of <c>Object</c>, and <c>System</c> and <c>Microsoft</c>, which the generated code names its types under.</summary>
  public static IReadOnlySet<string> Shadowing { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Equals", "GetHashCode", "ToString", "System", "Microsoft" };

  /// <summary>The prefix of every helper the backend writes.</summary>
  public const string HelperPrefix = "Ply";

  /// <summary>A valid name for a name of the IR: the last part after a dot, its other characters made underscores.</summary>
  /// <param name="name">The name in the IR.</param>
  /// <param name="fallback">The name when nothing of it is left.</param>
  /// <returns>The name, in brackets where it is a keyword, with an underscore after it where it would shadow a name the code needs or be a helper's.</returns>
  public static string Valid(string name, string fallback) {
    ArgumentNullException.ThrowIfNull(name);
    var segment = name.Contains('.', StringComparison.Ordinal) ? name[(name.LastIndexOf('.') + 1)..] : name;
    var text = new StringBuilder();
    foreach (var c in segment)
      text.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');

    var result = text.ToString().Trim('_');
    if (result.Length == 0)
      result = fallback;
    if (!char.IsAsciiLetter(result[0]))
      result = "x" + result;
    if (Shadowing.Contains(result) || result.StartsWith(HelperPrefix, StringComparison.OrdinalIgnoreCase))
      result += "_";

    return Reserved.Contains(result) ? "[" + result + "]" : result;
  }

  /// <summary>A name like the one wanted that is not taken: the name, or the first one with a number after it that is free; a keyword in brackets is left for its bare form with an underscore.</summary>
  /// <param name="wanted">The name wanted, perhaps in brackets.</param>
  /// <param name="taken">Whether a name is taken.</param>
  /// <returns>The name.</returns>
  public static string Free(string wanted, Func<string, bool> taken) {
    ArgumentNullException.ThrowIfNull(wanted);
    ArgumentNullException.ThrowIfNull(taken);
    if (!taken(wanted))
      return wanted;

    return VisualBasicNaming.Free(wanted.StartsWith('[') ? Bare(wanted) + "_" : wanted, taken);
  }

  /// <summary>A name without the brackets of a keyword.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The name inside the brackets.</returns>
  public static string Bare(string name) {
    ArgumentNullException.ThrowIfNull(name);
    return name.StartsWith('[') ? name[1..^1] : name;
  }

}
