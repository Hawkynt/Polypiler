using System.Collections.Generic;
using System.IO;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>A point where the imported code is known to behave differently from the JavaScript it came from.</summary>
/// <param name="Id">The stable name of the difference, for example <c>js-oob-read</c>.</param>
/// <param name="Description">What JavaScript does and what the imported code does instead.</param>
public sealed record ImportCaveat(string Id, string Description);

/// <summary>What importing a source file produced: a module, or the reasons there is none.</summary>
/// <param name="Module">The imported module, or <see langword="null"/> when there are diagnostics.</param>
/// <param name="Diagnostics">Why the source was refused; empty when <paramref name="Module"/> is set.</param>
/// <param name="Caveats">The known differences of the module, each at most once; empty without a module.</param>
public sealed record ImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<ImportCaveat> Caveats);

/// <summary>
/// Imports the prototype subset of JavaScript: functions whose parameters and result are typed in
/// JSDoc as <c>int32</c>, <c>uint32</c>, <c>uint8</c>, <c>boolean</c> or a typed array of the three
/// integer types, and whose bodies coerce every arithmetic result explicitly. Types come from JSDoc
/// and from JavaScript's own operator rules, never from how a value is used.
/// <para>
/// Only the functions are read. What a file does when it is loaded - assigning a function to another
/// name, replacing one - is not looked at, and a function that uses anything but its parameters, its
/// locals and other functions of the file is refused.
/// </para>
/// </summary>
public static class JavaScriptFrontend {

  /// <summary>The code for a syntax error the parser reports.</summary>
  public const string SyntaxError = "PLY1100";

  /// <summary>The code for a parameter or a result without a supported JSDoc type.</summary>
  public const string MissingType = "PLY1101";

  /// <summary>The code for a construct outside the supported subset.</summary>
  public const string Unsupported = "PLY1102";

  /// <summary>The code for a value that is not provably a 32-bit integer of the type it is used as.</summary>
  public const string NotAnInteger = "PLY1103";

  /// <summary>The code for an export name that no function of the file has.</summary>
  public const string UnknownExport = "PLY1104";

  /// <summary>
  /// The caveat of a module that accesses array elements: outside the array JavaScript reads
  /// <c>undefined</c> and ignores a write, the imported code traps.
  /// </summary>
  public const string OutOfRangeAccess = "js-oob-read";

  /// <summary>
  /// The caveat of a module that divides: by zero JavaScript yields infinity or <c>NaN</c>, which a
  /// coercion turns into 0, and the one quotient that does not fit wraps; the imported code traps.
  /// </summary>
  public const string DivisionByZero = "js-div-zero";

  /// <summary>
  /// The caveat of a module that steps a variable with <c>++</c> or <c>--</c>: JavaScript counts on
  /// beyond 32 bits, the imported code wraps around like the coerced form it stands for.
  /// </summary>
  public const string StepWraps = "js-step-wrap";

  private static readonly ImportCaveat _outOfRangeAccess = new(
    OutOfRangeAccess,
    "outside its typed array JavaScript reads undefined (0 once coerced) and ignores a write; the imported code traps "
    + "with 'index out of range', and an index computed by an uncoerced + or - is taken modulo 2^32 first");

  private static readonly ImportCaveat _divisionByZero = new(
    DivisionByZero,
    "a division or remainder by zero is Infinity or NaN in JavaScript (0 once coerced) and -2147483648 / -1 wraps; "
    + "the imported code traps with 'divide by zero' and 'integer overflow'");

  private static readonly ImportCaveat _stepWraps = new(
    StepWraps,
    "++ and -- on a variable count on beyond 32 bits in JavaScript; the imported code wraps around, "
    + "as (i + 1) | 0 and (i + 1) >>> 0 do");

  /// <summary>Imports one JavaScript source file, a script or a module.</summary>
  /// <param name="path">The name of the file, used in diagnostics; its stem names the module.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="exports">
  /// The functions to import, together with every function they call; the named ones become the
  /// public functions of the module. Without it, or with no name in it, every function of the file
  /// must import.
  /// </param>
  /// <returns>The module, or the diagnostics that explain why there is none.</returns>
  public static ImportResult Import(string path, string source, IReadOnlyCollection<string>? exports = null) {
    var comments = new List<Comment>();
    if (Parse(source, comments, out var failure) is not { } program)
      return Refused([new Diagnostic(SyntaxError, DiagnosticSeverity.Error, failure!.Description, path, failure.LineNumber, failure.Column + 1)]);

    if (exports is { Count: 0 })
      exports = null;

    var candidates = new FunctionCollector(source, comments).Collect(program);
    var lowering = new Lowering(path, source, candidates);
    var diagnostics = new List<Diagnostic>();
    List<Candidate> chosen;
    if (exports is null) {
      chosen = candidates;
      if (candidates.Count == 0)
        diagnostics.Add(new Diagnostic(Unsupported, DiagnosticSeverity.Error, "'a file without a function' is not supported yet", path, 1, 1));
    } else {
      chosen = [];
      foreach (var name in exports) {
        if (candidates.Find(candidate => candidate.Name == name) is { } candidate)
          chosen.Add(candidate);
        else
          diagnostics.Add(new Diagnostic(UnknownExport, DiagnosticSeverity.Error, $"the file has no function '{name}' to export", path, 1, 1));
      }
    }

    // Lowering a function names the functions it calls; with an export list those are imported too.
    var functions = new Dictionary<Candidate, IrFunction>();
    var pending = new Queue<Candidate>(chosen);
    var visited = new HashSet<Candidate>();
    while (pending.TryDequeue(out var candidate)) {
      if (!visited.Add(candidate))
        continue;

      var isPublic = exports is null ? candidate.IsPublic : exports.Contains(candidate.Name);
      if (lowering.Function(candidate, isPublic, diagnostics) is { } function)
        functions[candidate] = function;

      foreach (var callee in lowering.Callees(candidate))
        pending.Enqueue(callee);
    }

    if (diagnostics.Count > 0)
      return Refused(diagnostics.OrderBy(diagnostic => diagnostic.Line).ThenBy(diagnostic => diagnostic.Column).ToList());

    var module = new IrModule(
      Path.GetFileNameWithoutExtension(path),
      candidates.Where(functions.ContainsKey).Select(candidate => functions[candidate]).ToList());
    var caveats = new List<ImportCaveat>();
    if (lowering.AccessesElements)
      caveats.Add(_outOfRangeAccess);

    if (lowering.Divides)
      caveats.Add(_divisionByZero);

    if (lowering.Steps)
      caveats.Add(_stepWraps);

    return new(module, [], caveats);
  }

  private static ImportResult Refused(IReadOnlyList<Diagnostic> diagnostics) => new(null, diagnostics, []);

  /// <summary>
  /// Parses the source as a script and, when that fails, as a module. When neither parses, the error
  /// of the attempt that got further is the one reported: a script stops at the first
  /// <c>export</c>, a module at the first construct only sloppy mode has.
  /// </summary>
  private static Program? Parse(string source, List<Comment> comments, out ParseErrorException? failure) {
    var parser = new Parser(new ParserOptions {
      EcmaVersion = EcmaVersion.ES2022,
      OnComment = (in Comment comment) => comments.Add(comment)
    });
    failure = null;
    try {
      return parser.ParseScript(source);
    } catch (ParseErrorException asScript) {
      comments.Clear();
      try {
        return parser.ParseModule(source);
      } catch (ParseErrorException asModule) {
        failure = asModule.Error!.Index > asScript.Error!.Index ? asModule : asScript;
        return null;
      }
    }
  }

}
