using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.VisualBasic;

/// <summary>
/// The names of classic Visual Basic: a letter, then letters, digits and underscores, at most 255
/// characters (31 for a module), compared without regard to case, none a keyword or a built-in the
/// emitted code calls ([MS-VBAL] 3.3.5). Names that start with <c>Ply</c> are the helpers'.
/// </summary>
public static class VisualBasicNaming {

  /// <summary>The longest name of a procedure, a variable or a type.</summary>
  public const int MaximumLength = 255;

  /// <summary>The longest name of a module.</summary>
  public const int MaximumModuleLength = 31;

  /// <summary>The prefix of every helper's name, which no other name takes.</summary>
  public const string HelperPrefix = "Ply";

  /// <summary>The keywords of the language and the built-in names the emitted code uses, which no name of a module may take.</summary>
  public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "Abs", "AddressOf", "And", "Any", "Array", "As", "Asc", "AscW", "Attribute", "Base", "Binary", "Boolean", "ByRef", "Byte", "ByVal",
    "Call", "Case", "CBool", "CByte", "CCur", "CDate", "CDbl", "CDec", "CInt", "CLng", "CLngLng", "CLngPtr", "Close", "Compare",
    "Const", "CSng", "CStr", "Currency", "CVar", "CVErr", "Database", "Date", "Debug", "Decimal", "Declare", "DefBool", "DefByte",
    "DefCur", "DefDate", "DefDbl", "DefInt", "DefLng", "DefLngLng", "DefLngPtr", "DefObj", "DefSng", "DefStr", "DefVar", "Dim",
    "Do", "Double", "Each", "Else", "ElseIf", "Empty", "End", "EndIf", "Enum", "Eqv", "Erase", "Err", "Error", "Event", "Exit",
    "Explicit", "False", "Fix", "For", "Friend", "Function", "Get", "Global", "GoSub", "GoTo", "If", "IIf", "Imp", "Implements",
    "In", "Int", "Integer", "Is", "LBound", "Left", "Len", "Let", "Lib", "Like", "Line", "Lock", "Long", "LongLong", "LongPtr",
    "Loop", "LSet", "Me", "Mid", "Mod", "Module", "New", "Next", "Not", "Nothing", "Null", "Object", "On", "Open", "Option",
    "Optional", "Or", "ParamArray", "Preserve", "Print", "Private", "Property", "PtrSafe", "Public", "Put", "RaiseEvent",
    "Randomize", "ReDim", "Rem", "Resume", "Return", "Right", "Round", "RSet", "Seek", "Select", "Set", "Sgn", "Shared",
    "Single", "Sqr", "Static", "Step", "Stop", "Str", "StrComp", "String", "Sub", "Text", "Then", "Time", "To", "True", "Type",
    "TypeOf", "UBound", "Unlock", "Until", "Val", "Variant", "VB_Name", "Wend", "While", "With", "WithEvents", "Write", "Xor",
    "ChrW", "Chr"
  };

  /// <summary>The name of a module: the last segment of the IR's name, made a valid name of at most 31 characters.</summary>
  /// <param name="name">The IR's name, which may be dotted.</param>
  /// <returns>The module name.</returns>
  public static string Module(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var last = name.Contains('.', StringComparison.Ordinal) ? name[(name.LastIndexOf('.') + 1)..] : name;
    var valid = Valid(last, "Module");
    return valid.Length > MaximumModuleLength ? valid[..MaximumModuleLength] : valid;
  }

  /// <summary>
  /// A valid name for an IR name: every character Visual Basic does not allow replaced by an
  /// underscore, a letter in front of one that does not start with a letter, a keyword or a helper's
  /// prefix given a trailing underscore, and the whole cut to the longest length.
  /// </summary>
  /// <param name="name">The IR's name; only its last dotted segment counts.</param>
  /// <param name="fallback">The name when nothing of the given one is left.</param>
  /// <returns>The name.</returns>
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
    if (Reserved.Contains(result) || result.StartsWith(HelperPrefix, StringComparison.OrdinalIgnoreCase))
      result += "_";

    return result.Length > MaximumLength ? result[..MaximumLength] : result;
  }

  /// <summary>The first name of <c>wanted</c>, <c>wanted2</c>, <c>wanted3</c>, … that <paramref name="taken"/> does not hold, compared without case.</summary>
  /// <param name="wanted">The name wanted, valid already.</param>
  /// <param name="taken">Whether a name is taken.</param>
  /// <returns>The name.</returns>
  public static string Free(string wanted, Func<string, bool> taken) {
    ArgumentNullException.ThrowIfNull(wanted);
    ArgumentNullException.ThrowIfNull(taken);
    if (!taken(wanted))
      return wanted;

    return Enumerable.Range(2, int.MaxValue - 2).Select(index => wanted + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).First(candidate => !taken(candidate));
  }

}
