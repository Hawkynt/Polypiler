using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.AstDump;
using Polypiler.Core.Frontends.Python.Lowering;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;
using Polypiler.Core.Frontends.Python.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Python;

/// <summary>A point where the imported code is known to behave differently from the Python it came from.</summary>
/// <param name="Id">The stable name of the difference, for example <c>py-number-widened</c>.</param>
/// <param name="Description">What Python does and what the imported code does instead.</param>
public sealed record PythonCaveat(string Id, string Description);

/// <summary>How a Python source is imported.</summary>
public sealed record PythonImportOptions {

  /// <summary>The interpreter that parses the source, tried before <c>POLYPILER_PYTHON</c>, <c>python3</c> and <c>python</c>; <see langword="null"/> for those.</summary>
  public string? Interpreter { get; init; }

  /// <summary>Whether type hints are recorded as source contracts (the profile key <c>annotations</c>: <c>record</c>, or <c>ignore</c>).</summary>
  public bool RecordAnnotations { get; init; } = true;

  /// <summary>Whether <c>assert</c> statements are left out, as <c>python -O</c> leaves them out (the profile key <c>assertions</c>: <c>keep</c>, or <c>strip</c>).</summary>
  public bool StripAssertions { get; init; }

}

/// <summary>What importing a Python source produced: a module, or the reasons there is none.</summary>
/// <param name="Module">The imported module, or <see langword="null"/> when there are diagnostics.</param>
/// <param name="Diagnostics">Why the source was refused; empty when <paramref name="Module"/> is set.</param>
/// <param name="Caveats">The known differences of the module, each at most once; empty without a module.</param>
/// <param name="Contracts">
/// The type hints as source contracts, <c>f.a: int</c> and <c>f.return: int</c>, in source order:
/// recorded, never trusted - a hint does not change what the imported code does.
/// </param>
public sealed record PythonImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<PythonCaveat> Caveats, IReadOnlyList<string> Contracts) {

  /// <summary>The version of the interpreter that parsed the source; empty when none did.</summary>
  public string Python { get; init; } = "";

}

/// <summary>
/// Imports Python 3: module-level functions with type hints and the calls between them, <c>int</c>,
/// <c>float</c> and <c>bool</c> arithmetic with Python's meaning - <c>/</c> always a correctly
/// rounded <c>float</c>, <c>//</c> and <c>%</c> rounding toward negative infinity, exact comparisons
/// of an <c>int</c> with a <c>float</c>, <c>and</c>/<c>or</c> giving an operand - and <c>if</c>,
/// <c>while</c>, <c>for</c> over <c>range</c> with their <c>else</c> clauses, <c>match</c> on literals
/// and <c>assert</c>. The source is parsed by the standard <c>ast</c> module of the installed CPython
/// through a helper that only parses and prints JSON; it is never imported or executed.
/// <para>
/// A value has the static type its hint states for a parameter, and of the values that reach it for
/// a local and a result. An <c>int</c> is the arbitrary-precision integer of the IR, whose every operation gives Python's exact
/// result: nothing wraps around and nothing overflows. Everything outside the subset is
/// refused with a diagnostic that names the construct.
/// </para>
/// </summary>
public static class PythonFrontend {

  /// <summary>The code for a syntax error python reports, or a helper that fails.</summary>
  public const string SyntaxError = "PLY1800";

  /// <summary>The registered diagnostic of <see cref="SyntaxError"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor SyntaxErrorDescriptor = new(SyntaxError, DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "python.syntax");

  /// <summary>The code for a parameter, local or result whose type is not known, or a hint the import does not know.</summary>
  public const string MissingType = "PLY1801";

  /// <summary>The registered diagnostic of <see cref="MissingType"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MissingTypeDescriptor = new(MissingType, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.types");

  /// <summary>The code for an expression outside the supported subset.</summary>
  public const string Unsupported = "PLY1802";

  /// <summary>The registered diagnostic of <see cref="Unsupported"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedDescriptor = new(Unsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.subset");

  /// <summary>The code for a variable or result that values of types no type of the import holds both of reach.</summary>
  public const string MixedKinds = "PLY1803";

  /// <summary>The registered diagnostic of <see cref="MixedKinds"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MixedKindsDescriptor = new(MixedKinds, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.mixed-kinds");

  /// <summary>The code for an integer literal that 64 bits do not hold.</summary>
  public const string IntegerRange = "PLY1804";

  /// <summary>The registered diagnostic of <see cref="IntegerRange"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor IntegerRangeDescriptor = new(IntegerRange, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.int-bounds");

  /// <summary>The code for a statement outside the supported subset.</summary>
  public const string UnsupportedStatement = "PLY1810";

  /// <summary>The registered diagnostic of <see cref="UnsupportedStatement"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedStatementDescriptor = new(UnsupportedStatement, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.statements");

  /// <summary>The code for a local read where it is not assigned on every path, where python would raise <c>UnboundLocalError</c>.</summary>
  public const string MaybeUnbound = "PLY1811";

  /// <summary>The registered diagnostic of <see cref="MaybeUnbound"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MaybeUnboundDescriptor = new(MaybeUnbound, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.definite-assignment");

  /// <summary>The code for a <c>match</c> pattern outside the supported ones.</summary>
  public const string UnsupportedPattern = "PLY1812";

  /// <summary>The registered diagnostic of <see cref="UnsupportedPattern"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedPatternDescriptor = new(UnsupportedPattern, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.match");

  /// <summary>The code for a <c>for</c> over anything but <c>range</c>.</summary>
  public const string UnsupportedIteration = "PLY1813";

  /// <summary>The registered diagnostic of <see cref="UnsupportedIteration"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedIterationDescriptor = new(UnsupportedIteration, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.iteration");

  /// <summary>The code for a feature of functions outside the subset: defaults, keyword arguments, decorators, nested functions.</summary>
  public const string UnsupportedFunction = "PLY1830";

  /// <summary>The registered diagnostic of <see cref="UnsupportedFunction"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedFunctionDescriptor = new(UnsupportedFunction, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.functions");

  /// <summary>The code for a function value that escapes, a lambda, a generator or a coroutine.</summary>
  public const string EscapingFunction = "PLY1832";

  /// <summary>The registered diagnostic of <see cref="EscapingFunction"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor EscapingFunctionDescriptor = new(EscapingFunction, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.function-values");

  /// <summary>The code for a name that is neither part of the import nor a supported built-in, and for an import.</summary>
  public const string Unresolved = "PLY1833";

  /// <summary>The registered diagnostic of <see cref="Unresolved"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnresolvedDescriptor = new(Unresolved, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.names");

  /// <summary>The caveat of a module that stores a narrower number where a wider one is held.</summary>
  public const string Widened = "py-number-widened";

  /// <summary>The caveat of a module that raises an <c>int</c> to an <c>int</c> power whose sign is not known.</summary>
  public const string PowerSign = "py-int-power-sign";

  /// <summary>The caveat of a module that raises a <c>float</c> to a power.</summary>
  public const string ComplexPower = "py-complex-power";

  private static readonly Dictionary<string, string> _caveats = new() {
    [Widened] = "a bool or int stored where a variable, an argument or a result also holds wider numbers is converted to the wider type "
      + "when it is stored; Python keeps the narrower value, which compares equal but has its own type",
    [PowerSign] = "an int raised to an int power whose sign the import does not know is an int, and a negative power raises ValueError, "
      + "where Python gives a float",
    [ComplexPower] = "a negative float raised to a fractional power raises ArithmeticError, where Python gives a complex number"
  };

  /// <summary>Imports one Python source file, parsed by the installed CPython.</summary>
  /// <param name="path">The name of the file, used in diagnostics; its stem names the module.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="options">How to import; <see langword="null"/> for the defaults.</param>
  /// <returns>The module, or the diagnostics that explain why there is none.</returns>
  public static PythonImportResult Import(string path, string source, PythonImportOptions? options = null) {
    options ??= new();
    var (json, failure) = AstHelper.Run(path, source, options.Interpreter);
    return json is null
      ? Refused(path, failure!, 1, 1, "")
      : ImportDump(path, source, json, options);
  }

  /// <summary>
  /// The tree the helper prints for a source, in wire format 1: what <see cref="Import"/> reads. A
  /// recorded tree imports again without an interpreter (<see cref="ImportDump"/>).
  /// </summary>
  /// <param name="path">The name of the file, which python's messages carry.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="interpreter">An interpreter to try first, or <see langword="null"/>.</param>
  /// <returns>The JSON the helper printed, or the reason it printed none.</returns>
  public static (string? Json, string? Failure) Dump(string path, string source, string? interpreter = null) => AstHelper.Run(path, source, interpreter);

  /// <summary>Imports a source from the tree the helper printed for it.</summary>
  /// <param name="path">The name of the file, used in diagnostics; its stem names the module.</param>
  /// <param name="source">The text of the file, which diagnostics quote.</param>
  /// <param name="dump">What the helper printed for the text (<see cref="Dump"/>).</param>
  /// <param name="options">How to import; <see langword="null"/> for the defaults.</param>
  /// <returns>The module, or the diagnostics that explain why there is none.</returns>
  public static PythonImportResult ImportDump(string path, string source, string dump, PythonImportOptions? options = null) {
    options ??= new();
    var parsed = AstReader.Read(dump);
    if (parsed.Error is var (message, line, column))
      return Refused(path, message, line, column, parsed.Python);

    var file = new SourceFile(path, source);
    var diagnostics = new List<Diagnostic>();
    var functions = Collect(file, parsed.Module!, diagnostics);
    var check = new SubsetCheck(file, functions, diagnostics);
    foreach (var function in functions.Values)
      check.Check(function);

    if (diagnostics.Count == 0)
      foreach (var function in functions.Values)
        new DefiniteAssignment(file, function, diagnostics).Run();

    if (diagnostics.Count == 0)
      new TypeInference(file, functions, diagnostics).Run();

    var notes = new LoweringNotes();
    var helpers = new PythonHelpers(functions.Keys.ToHashSet());
    var lowered = new List<IrFunction>();
    if (diagnostics.Count == 0) {
      foreach (var function in functions.Values) {
        try {
          lowered.Add(new FunctionLowering(file, function, functions, helpers, notes, options).Lower());
        } catch (Refusal refusal) {
          diagnostics.Add(refusal.Diagnostic);
        }
      }
    }

    if (diagnostics.Count > 0)
      return new(null, [.. diagnostics.OrderBy(diagnostic => diagnostic.Line).ThenBy(diagnostic => diagnostic.Column)], [], []) { Python = parsed.Python };

    var caveats = new List<string>();

    if (notes.Widens || functions.Values.Any(function => function.Parameters.Concat(function.Locals.Values).Any(variable => variable.Widened)))
      caveats.Add(Widened);

    if (notes.PowersBySignUnknown)
      caveats.Add(PowerSign);

    if (notes.PowersFloats)
      caveats.Add(ComplexPower);

    var module = new IrModule(Path.GetFileNameWithoutExtension(path), [.. lowered, .. helpers.Functions]) { Caveats = caveats };
    var contracts = options.RecordAnnotations ? Contracts(file, functions.Values, notes) : [];
    return new(module, [], [.. caveats.Select(id => new PythonCaveat(id, _caveats[id]))], contracts) { Python = parsed.Python };
  }

  /// <summary>The description of a caveat, by its name.</summary>
  /// <param name="id">The name of the caveat, such as <see cref="Widened"/>.</param>
  /// <returns>What Python does and what the imported code does instead.</returns>
  public static string Describe(string id) => _caveats[id];

  private static PythonImportResult Refused(string path, string message, int line, int column, string python)
    => new(null, [SyntaxErrorDescriptor.At(message, path, line, column)], [], []) { Python = python };

  /// <summary>The contracts the hints of parameters, results and locals state, function by function.</summary>
  private static List<string> Contracts(SourceFile file, IEnumerable<FunctionModel> functions, LoweringNotes notes) {
    var contracts = new List<string>();
    foreach (var function in functions) {
      foreach (var parameter in function.Parameters.Where(parameter => parameter.Hint is not null))
        contracts.Add($"{function.Name}.{parameter.Name}: {file.Quote(parameter.Hint!)}");

      if (function.ReturnHint is { } returns)
        contracts.Add($"{function.Name}.return: {file.Quote(returns)}");

      contracts.AddRange(notes.Contracts.Where(contract => contract.StartsWith(function.Name + ".", System.StringComparison.Ordinal)));
    }

    return contracts;
  }

  /// <summary>
  /// The functions of the module, by name in source order. A module-level statement other than a
  /// <c>def</c>, a docstring, <c>from __future__ import</c> and the block under
  /// <c>if __name__ == "__main__":</c>, which a translation does not run, is refused.
  /// </summary>
  private static Dictionary<string, FunctionModel> Collect(SourceFile file, PyNode module, List<Diagnostic> diagnostics) {
    var functions = new Dictionary<string, FunctionModel>();
    foreach (var statement in module.Nodes("body")) {
      switch (statement.Kind) {
        case "FunctionDef":
          if (Feature(statement) is { } feature) {
            diagnostics.Add(file.At(statement, UnsupportedFunctionDescriptor, $"'{feature}' is not supported for functions by the Python frontend"));
            break;
          }

          var function = new FunctionModel(statement);
          if (!functions.TryAdd(function.Name, function)) {
            diagnostics.Add(file.At(statement, UnsupportedFunctionDescriptor, $"'redefinition of {function.Name}' is not supported for functions by the Python frontend"));
            break;
          }

          ScopeResolver.CollectLocals(function);
          break;
        case "AsyncFunctionDef":
          diagnostics.Add(file.At(statement, EscapingFunctionDescriptor,
            $"the function value '{statement.Text("name")}' escapes, or '{statement.Text("name")}' is a generator or coroutine; only direct calls of the module's functions are imported"));
          break;
        case "Expr" when statement.Node("value") is { Kind: "Constant" } docstring && docstring.Text("kind") == "str":
        case "ImportFrom" when statement.Text("module") == "__future__":
        case "If" when IsMainGuard(statement):
          break;
        case "Import" or "ImportFrom":
          var names = statement.Text("module") ?? string.Join(", ", statement.Nodes("names").Select(alias => alias.Text("name")));
          diagnostics.Add(file.At(statement, UnresolvedDescriptor, $"'{names}' is neither part of this import nor in the Python call map"));
          break;
        default:
          diagnostics.Add(file.At(statement, UnsupportedStatementDescriptor, $"statement 'module-level {file.Quote(statement)}' is not supported by the Python frontend"));
          break;
      }
    }

    if (functions.Count == 0 && diagnostics.Count == 0)
      diagnostics.Add(file.At(module, UnsupportedStatementDescriptor, "statement 'a module without a function' is not supported by the Python frontend"));

    return functions;
  }

  /// <summary>The first feature of a <c>def</c> outside the subset, or <see langword="null"/>.</summary>
  private static string? Feature(PyNode function) {
    var arguments = function.Node("args")!;
    var name = function.Text("name");
    return function.Nodes("decorator_list").Count > 0 ? $"a decorator of {name}"
      : arguments.Nodes("defaults").Count > 0 ? $"a default value of a parameter of {name}"
      : arguments.Nodes("posonlyargs").Count > 0 ? $"a positional-only parameter of {name}"
      : arguments.Nodes("kwonlyargs").Count > 0 ? $"a keyword-only parameter of {name}"
      : arguments.Node("vararg") is not null ? $"*{arguments.Node("vararg")!.Text("arg")} of {name}"
      : arguments.Node("kwarg") is not null ? $"**{arguments.Node("kwarg")!.Text("arg")} of {name}"
      : function.Nodes("type_params").Count > 0 ? $"a type parameter of {name}"
      : null;
  }

  /// <summary>Whether a statement is <c>if __name__ == "__main__":</c>.</summary>
  private static bool IsMainGuard(PyNode statement)
    => statement.Node("test") is { Kind: "Compare" } test && test.Nodes("ops") is [{ Kind: "Eq" }]
      && test.Node("left") is { Kind: "Name" } left && left.Text("id") == "__name__"
      && test.Nodes("comparators") is [{ Kind: "Constant" } main] && main.Text("value") == "__main__";

}
