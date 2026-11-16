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

  /// <summary>
  /// How a module the source imports is found, by its name such as <c>pkg.helper</c>: its path, its
  /// text and, where one is recorded, the tree the helper printed for it; <see langword="null"/> where
  /// there is no such module. Without a locator modules are files under the directory of the source
  /// (the profile key <c>searchPath</c>).
  /// </summary>
  public System.Func<string, (string Path, string Source, string? Dump)?>? Modules { get; init; }

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

  /// <summary>The code for a sequence operation outside the subset: a list method such as <c>sort</c>, <c>del a[i]</c>, <c>is</c> between lists.</summary>
  public const string UnsupportedSequence = "PLY1820";

  /// <summary>The registered diagnostic of <see cref="UnsupportedSequence"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedSequenceDescriptor = new(UnsupportedSequence, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.sequences");

  /// <summary>The code for a slice that is no value: slice assignment and deletion, a stored slice object, several dimensions.</summary>
  public const string UnsupportedSlice = "PLY1821";

  /// <summary>The registered diagnostic of <see cref="UnsupportedSlice"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedSliceDescriptor = new(UnsupportedSlice, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.slices");

  /// <summary>The code for a string operation outside the subset: f-strings, formatting, <c>join</c>, <c>split</c>, <c>decode</c>.</summary>
  public const string UnsupportedString = "PLY1822";

  /// <summary>The registered diagnostic of <see cref="UnsupportedString"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedStringDescriptor = new(UnsupportedString, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.strings");

  /// <summary>The code for a <c>struct</c> format or an <c>array</c> typecode outside the portable subset.</summary>
  public const string UnsupportedLayout = "PLY1823";

  /// <summary>The registered diagnostic of <see cref="UnsupportedLayout"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedLayoutDescriptor = new(UnsupportedLayout, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.layouts");

  /// <summary>The code for a parameter default of a mutable type, one object shared across calls.</summary>
  public const string MutableDefault = "PLY1834";

  /// <summary>The registered diagnostic of <see cref="MutableDefault"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MutableDefaultDescriptor = new(MutableDefault, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.defaults");

  /// <summary>The code for a class feature outside dataclasses, simple final classes and enums: bases, <c>super()</c>, <c>@classmethod</c>, operator methods.</summary>
  public const string UnsupportedClass = "PLY1840";

  /// <summary>The registered diagnostic of <see cref="UnsupportedClass"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedClassDescriptor = new(UnsupportedClass, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.classes");

  /// <summary>The code for a generic declaration: <c>Generic[T]</c>, <c>TypeVar</c>, type parameters.</summary>
  public const string UnsupportedGeneric = "PLY1841";

  /// <summary>The registered diagnostic of <see cref="UnsupportedGeneric"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedGenericDescriptor = new(UnsupportedGeneric, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.generics");

  /// <summary>The code for a <c>dict</c> or <c>set</c> operation.</summary>
  public const string UnsupportedDict = "PLY1842";

  /// <summary>The registered diagnostic of <see cref="UnsupportedDict"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedDictDescriptor = new(UnsupportedDict, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.dicts");

  /// <summary>The code for an exception construct outside the subset: <c>raise … from</c>, <c>with</c>, exception groups, exception classes with members.</summary>
  public const string UnsupportedException = "PLY1843";

  /// <summary>The registered diagnostic of <see cref="UnsupportedException"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedExceptionDescriptor = new(UnsupportedException, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.exceptions");

  /// <summary>The code for an attribute created outside <c>__init__</c>, and for <c>setattr</c>, <c>getattr</c> and their kin.</summary>
  public const string DynamicAttribute = "PLY1844";

  /// <summary>The registered diagnostic of <see cref="DynamicAttribute"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor DynamicAttributeDescriptor = new(DynamicAttribute, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "python.attributes");

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

  /// <summary>The caveat of a module that compares floats inside lists and tuples, or searches for one.</summary>
  public const string FloatIdentity = "py-float-identity";

  /// <summary>The caveat of a module that sorts floats.</summary>
  public const string SortNaN = "py-sort-nan";

  /// <summary>The caveat of a module that makes characters with <c>chr</c>.</summary>
  public const string Surrogates = "py-str-surrogates";

  /// <summary>The caveat of a module that keeps an <c>int</c> where the IR holds one in 64 bits: a field, a tuple, an optional, a table, an index.</summary>
  public const string HeldInt = "py-int-held-64";

  private static readonly Dictionary<string, string> _caveats = new() {
    [Widened] = "a bool or int stored where a variable, an argument or a result also holds wider numbers is converted to the wider type "
      + "when it is stored; Python keeps the narrower value, which compares equal but has its own type",
    [PowerSign] = "an int raised to an int power whose sign the import does not know is an int, and a negative power raises ValueError, "
      + "where Python gives a float",
    [ComplexPower] = "a negative float raised to a fractional power raises ArithmeticError, where Python gives a complex number",
    [FloatIdentity] = "a NaN inside a list or tuple, or searched for with in, is unequal to itself; CPython finds the very same NaN object equal to itself",
    [SortNaN] = "floats sorted with a NaN among them are ordered by a stable merge sort; CPython's timsort may order them otherwise",
    [Surrogates] = "a str is held as UTF-16: a lone high surrogate followed by a lone low surrogate made with chr is one code point, where Python has two",
    [HeldInt] = "an int kept in an attribute, a tuple, an optional, a module table or variable, a key of a dict or an element of a set, or used as an index, a count or an argument of the "
      + "byte and struct operations, is a 64-bit integer there: one outside -2**63 .. 2**63 - 1 raises OverflowError, where Python keeps it exact"
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

    var previous = (Tuples.Current, Classes.Current);
    Tuples.Current = new();
    Classes.Current = new();
    try {
      return Run(path, source, parsed, options);
    } finally {
      (Tuples.Current, Classes.Current) = previous;
    }
  }

  private static PythonImportResult Run(string path, string source, ParsedSource parsed, PythonImportOptions options) {
    var diagnostics = new List<Diagnostic>();
    var bound = new Dictionary<PyNode, Variable>(ReferenceEqualityComparer.Instance);
    var name = Path.GetFileNameWithoutExtension(path);
    var locate = Locator(path, options, diagnostics);
    var collector = new Collector(locate, bound, diagnostics);
    var main = collector.Collect(new ModuleSource(name, new SourceFile(path, source), parsed.Module!), true);
    var modules = collector.Modules;
    if (main.Functions.Count == 0 && main.Classes.Count == 0 && diagnostics.Count == 0)
      diagnostics.Add(main.File.At(main.Node, UnsupportedStatementDescriptor, "statement 'a module without a function' is not supported by the Python frontend"));

    foreach (var module in modules) {
      ScopeResolver.ModuleScopes(module, bound);
      var check = new SubsetCheck(module, bound, diagnostics);
      check.CheckModule();
      foreach (var function in module.Functions.Values.Concat(module.Classes.Values.SelectMany(type => type.Methods.Values)))
        check.Check(function);
    }

    var notes = new LoweringNotes();
    var helpers = new PythonHelpers(modules.SelectMany(module => module.AllFunctions.Select(function => function.Name).Concat(module.Classes.Keys)).ToHashSet());
    var context = new LoweringContext(bound, helpers, notes, options);
    if (diagnostics.Count == 0) {
      foreach (var module in modules) {
        Names(module);
        ClassDeclarations.Signatures(module);
        ClassDeclarations.Enums(module, context);
      }

      var evaluation = new ModuleEvaluation(context, new TypeInference(modules, bound, diagnostics), diagnostics);
      foreach (var module in modules)
        evaluation.Run(module);
    }

    if (diagnostics.Count == 0)
      foreach (var module in modules)
        foreach (var function in module.AllFunctions)
          new DefiniteAssignment(module.File, function, bound, diagnostics).Run();

    if (diagnostics.Count == 0)
      new TypeInference(modules, bound, diagnostics).Run();

    var lowered = new List<IrFunction>();
    var globals = new List<IrGlobal>();
    if (diagnostics.Count == 0) {
      foreach (var module in modules) {
        ClassDeclarations.Records(module, context, diagnostics);
        foreach (var global in module.GlobalVariables.Values) {
          var initial = global.Initial!;
          // A variable of the module holds an int as a field does (PyTypes.Held); a bool or an int a float variable starts with is that float.
          var value = initial.Type == global.Type ? initial.Value! : initial.Value is IrBigConst integer ? Ir.Float((double)integer.Value) : Ir.Float(((IrConst)initial.Value!).Value);
          global.Ir = new IrGlobal(Unique(module, global.Name, globals), PyTypes.Held(global.Type), Ir.Fit(value, PyTypes.Held(global.Type)));
          globals.Add(global.Ir);
        }
      }
    }

    if (diagnostics.Count == 0) {
      foreach (var module in modules) {
        ClassDeclarations.Members(module, context, new FunctionLowering(context, module, null));
        foreach (var function in module.Functions.Values.Concat(module.Classes.Values.Where(type => type.Ir is not null).SelectMany(type => type.Methods.Values))) {
          try {
            lowered.Add(new FunctionLowering(context, module, function).Lower());
          } catch (Refusal refusal) {
            diagnostics.Add(refusal.Diagnostic);
          }
        }
      }
    }

    if (diagnostics.Count > 0)
      return new(null, [.. diagnostics.OrderBy(diagnostic => diagnostic.File == path ? 0 : 1).ThenBy(diagnostic => diagnostic.Line).ThenBy(diagnostic => diagnostic.Column)], [], []) { Python = parsed.Python };

    var caveats = new List<string>();

    if (notes.Widens || modules.SelectMany(module => module.AllFunctions).Any(function => function.Parameters.Concat(function.Locals.Values).Any(variable => variable.Widened)))
      caveats.Add(Widened);

    if (notes.PowersBySignUnknown)
      caveats.Add(PowerSign);

    if (notes.PowersFloats)
      caveats.Add(ComplexPower);

    if (notes.SearchesFloats)
      caveats.Add(FloatIdentity);

    if (notes.SortsFloats)
      caveats.Add(SortNaN);

    if (notes.Characters)
      caveats.Add(Surrogates);

    IrFunction[] functions = [.. lowered, .. context.Made, .. helpers.Functions];
    if (functions.SelectMany(function => IrTree.Nodes(function.Body)).Any(node => node is IrConvert { Contract: IrConversion.Trap, Operand.Type.IsBigInt: true }))
      caveats.Add(HeldInt);

    var imported = new IrModule(name, functions) {
      Caveats = caveats,
      Types = [.. Tuples.Current.Structs, .. context.Types],
      Globals = [.. context.Tables, .. globals]
    };
    var contracts = options.RecordAnnotations ? Contracts(modules, notes) : [];
    return new(imported, [], [.. caveats.Select(id => new PythonCaveat(id, _caveats[id]))], contracts) { Python = parsed.Python };
  }

  /// <summary>The names the functions of a module have in the IR: their own in the module the import started from, the module's before them in another.</summary>
  private static void Names(ModuleModel module) {
    var prefix = module.IsMain ? "" : module.Name.Replace('.', '_') + "_";
    foreach (var function in module.Functions.Values)
      function.IrName = prefix + function.Name;
  }

  private static string Unique(ModuleModel module, string name, List<IrGlobal> globals) {
    var candidate = (module.IsMain ? "" : module.Name.Replace('.', '_') + "_") + name;
    while (globals.Any(global => global.Name == candidate) || module.Functions.ContainsKey(candidate))
      candidate += "_";

    return candidate;
  }

  /// <summary>
  /// How the modules a source imports are found: by the options' locator, or as files under the
  /// directory of the source - <c>a/b.py</c> for <c>a.b</c>, or <c>a/b/__init__.py</c> for a package -
  /// parsed by the same interpreter, never imported or run.
  /// </summary>
  private static System.Func<string, ModuleSource?> Locator(string path, PythonImportOptions options, List<Diagnostic> diagnostics) {
    var cache = new Dictionary<string, ModuleSource?>();
    return name => {
      if (cache.TryGetValue(name, out var known))
        return known;

      ModuleSource? found = null;
      if (options.Modules?.Invoke(name) is { } given) {
        found = Parse(name, given.Path, given.Source, given.Dump ?? AstHelper.Run(given.Path, given.Source, options.Interpreter).Json, given.Path.EndsWith("__init__.py", System.StringComparison.Ordinal), diagnostics);
      } else if (options.Modules is null) {
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var relative = Path.Combine([root, .. name.Split('.')]);
        foreach (var (file, package) in new[] { (relative + ".py", false), (Path.Combine(relative, "__init__.py"), true) }) {
          if (!File.Exists(file))
            continue;

          var text = File.ReadAllText(file);
          found = Parse(name, file, text, AstHelper.Run(file, text, options.Interpreter).Json, package, diagnostics);
          break;
        }
      }

      cache[name] = found;
      return found;
    };
  }

  private static ModuleSource? Parse(string name, string path, string source, string? dump, bool package, List<Diagnostic> diagnostics) {
    var parsed = dump is null ? null : AstReader.Read(dump);
    if (parsed?.Module is null) {
      var (message, line, column) = parsed?.Error ?? ($"the Python helper could not parse {path}", 1, 1);
      diagnostics.Add(SyntaxErrorDescriptor.At(message, path, line, column));
      return null;
    }

    return new ModuleSource(name, new SourceFile(path, source), parsed.Module, package);
  }

  /// <summary>The description of a caveat, by its name.</summary>
  /// <param name="id">The name of the caveat, such as <see cref="Widened"/>.</param>
  /// <returns>What Python does and what the imported code does instead.</returns>
  public static string Describe(string id) => _caveats[id];

  private static PythonImportResult Refused(string path, string message, int line, int column, string python)
    => new(null, [SyntaxErrorDescriptor.At(message, path, line, column)], [], []) { Python = python };

  /// <summary>The contracts the hints of parameters, results and locals state, function by function.</summary>
  private static List<string> Contracts(IEnumerable<ModuleModel> modules, LoweringNotes notes) {
    var contracts = new List<string>();
    foreach (var module in modules)
      foreach (var function in module.AllFunctions) {
        foreach (var parameter in function.Parameters.Where(parameter => parameter.Hint is not null))
          contracts.Add($"{function.Qualified}.{parameter.Name}: {module.File.Quote(parameter.Hint!)}");

        if (function.ReturnHint is { } returns)
          contracts.Add($"{function.Qualified}.return: {module.File.Quote(returns)}");

        contracts.AddRange(notes.Contracts.Where(contract => contract.StartsWith(function.Qualified + ".", System.StringComparison.Ordinal)));
      }

    return contracts;
  }

}
