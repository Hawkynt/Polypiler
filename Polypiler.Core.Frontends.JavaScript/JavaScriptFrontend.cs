using System.Collections.Generic;
using System.IO;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>A point where the imported code is known to behave differently from the JavaScript it came from.</summary>
/// <param name="Id">The stable name of the difference, for example <c>js-oob-read</c>.</param>
/// <param name="Description">What JavaScript does and what the imported code does instead.</param>
public sealed record ImportCaveat(string Id, string Description);

/// <summary>Why a parameter, a local or a result has its type.</summary>
/// <param name="Function">The function.</param>
/// <param name="Role"><c>parameter</c>, <c>local</c> or <c>result</c>.</param>
/// <param name="Name">The name of the parameter or local; empty for the result.</param>
/// <param name="Type">The type in the IR.</param>
/// <param name="Evidence">The expression that decided the type - the first one whose value made it what it is - or what did when it was none.</param>
/// <param name="Line">The line of the evidence, 1-based.</param>
/// <param name="Column">The column of the evidence, 1-based.</param>
public sealed record TypeExplanation(string Function, string Role, string Name, IrType Type, string Evidence, int Line, int Column) {

  /// <summary>The explanation as the command line prints it.</summary>
  public override string ToString()
    => $"{this.Function}: {this.Role}{(this.Name.Length == 0 ? "" : " " + this.Name)}: {this.Type} from '{this.Evidence}' at ({this.Line},{this.Column})";

}

/// <summary>What importing a source file produced: a module, or the reasons there is none.</summary>
/// <param name="Module">The imported module, or <see langword="null"/> when there are diagnostics.</param>
/// <param name="Diagnostics">Why the source was refused; empty when <paramref name="Module"/> is set.</param>
/// <param name="Caveats">The known differences of the module, each at most once; empty without a module.</param>
/// <param name="Explanations">Why every parameter, local and result of the module has its type, function by function in source order; empty without a module.</param>
public sealed record ImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<ImportCaveat> Caveats, IReadOnlyList<TypeExplanation> Explanations);

/// <summary>
/// Imports JavaScript without requiring a type anywhere. Every number is a double, computed exactly
/// as ECMA-262 computes it; a variable, a parameter or a result is an integer where the program
/// itself proves it - where every value that reaches it comes from a bitwise operator,
/// <c>&gt;&gt;&gt; 0</c>, <c>Math.imul</c>, a typed array, or a sum under a coercion. JSDoc states the
/// contract of a parameter where there is one. Nothing is taken from a name or from typical usage.
/// <para>
/// Only the functions are read, and the constants that enclose them. What a file does when it is
/// loaded - assigning a function to another name, replacing one - is not looked at, and a function
/// that uses anything but its parameters, its locals, those constants and other functions of the
/// file is refused.
/// </para>
/// </summary>
public static partial class JavaScriptFrontend {

  /// <summary>The code for a syntax error the parser reports.</summary>
  public const string SyntaxError = "PLY1100";

  /// <summary>The registered diagnostic of <see cref="SyntaxError"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor SyntaxErrorDescriptor = new(SyntaxError, DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "javascript.syntax");

  /// <summary>The code for a parameter or a result without a supported JSDoc type.</summary>
  public const string MissingType = "PLY1101";

  /// <summary>The registered diagnostic of <see cref="MissingType"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MissingTypeDescriptor = new(MissingType, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "javascript.types");

  /// <summary>The code for a construct outside the supported subset.</summary>
  public const string Unsupported = "PLY1102";

  /// <summary>The registered diagnostic of <see cref="Unsupported"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedDescriptor = new(Unsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "javascript.subset");

  /// <summary>The code for a <c>BigInt</c> whose value nothing bounds, so that no type of the IR holds it.</summary>
  public const string NotBounded = "PLY1103";

  /// <summary>The registered diagnostic of <see cref="NotBounded"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NotBoundedDescriptor = new(NotBounded, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "javascript.bigint-bounds");

  /// <summary>The code for an export name that no function of the file has.</summary>
  public const string UnknownExport = "PLY1104";

  /// <summary>The registered diagnostic of <see cref="UnknownExport"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnknownExportDescriptor = new(UnknownExport, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "javascript.export");

  /// <summary>The code for a variable, a parameter or a result that values of two kinds reach.</summary>
  public const string MixedKinds = "PLY1105";

  /// <summary>The registered diagnostic of <see cref="MixedKinds"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MixedKindsDescriptor = new(MixedKinds, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "javascript.mixed-kinds");

  /// <summary>The code for a variable declared without a value that may be read before it is assigned.</summary>
  public const string ReadBeforeAssignment = "PLY1106";

  /// <summary>The registered diagnostic of <see cref="ReadBeforeAssignment"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ReadBeforeAssignmentDescriptor = new(ReadBeforeAssignment, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "javascript.definite-assignment");

  /// <summary>
  /// The code for a thrown value that is no <c>Error</c> where something observes it: a catch that reads
  /// it, or the caller it can leave the function to. A property of the data flow, not of a construct.
  /// </summary>
  public const string NonErrorThrow = "PLY1107";

  /// <summary>The registered diagnostic of <see cref="NonErrorThrow"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NonErrorThrowDescriptor = new(NonErrorThrow, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "javascript.non-error-throw");

  /// <summary>
  /// The caveat of a module that accesses array elements where the imported code does not do what
  /// JavaScript does outside the array: at anything but the index of an element JavaScript reads
  /// <c>undefined</c>, the imported code traps or drops the fraction. A read whose value meets
  /// <c>ToInt32</c>, <c>ToUint32</c> or <c>ToNumber</c> directly and a store into a typed array do what
  /// JavaScript does and need no caveat (#1367).
  /// </summary>
  public const string OutOfRangeAccess = "js-oob-read";

  /// <summary>
  /// The caveat of a module that divides integers where the imported code traps: by zero JavaScript
  /// yields infinity or <c>NaN</c>, and the one quotient that does not fit wraps. A division of doubles
  /// has no such difference, and nor has a quotient or remainder of 32-bit integers under a coercion
  /// whose divisor is evaluated after an operand without an effect (#1367): it is 0 for a divisor of 0.
  /// </summary>
  public const string DivisionByZero = "js-div-zero";

  /// <summary>
  /// The caveat of a module with a parameter that has no JSDoc type and is an integer because the
  /// function reads it under a coercion only: JavaScript coerces whatever number a caller passes,
  /// the imported function takes the integer.
  /// </summary>
  public const string IntegerParameter = "js-param-integer-inferred";

  /// <summary>
  /// The caveat of a module with a parameter that has no JSDoc type and is used as an array: it has
  /// the type of the arrays the file itself passes to it, which is what a caller has to pass as well.
  /// </summary>
  public const string ArrayParameter = "js-param-array-inferred";

  private static readonly ImportCaveat _outOfRangeAccess = new(
    OutOfRangeAccess,
    "at an index that is not the integer of an element JavaScript reads undefined, lets an Array grow, and an element of new Array(n) "
    + "that was never stored is undefined; where such a value is returned, stored into an Array or computed with before a coercion, "
    + "and where an element is stepped or compounded, the imported code traps with 'index out of range' or 'integer overflow', takes "
    + "an index that is a double without its fraction and reads 0 (a read directly under ToInt32, ToUint32 or ToNumber and a store "
    + "into a typed array behave as in JavaScript)");

  /// <summary>The caveat of a module whose sort comparator returns a double, which a NaN makes inconsistent.</summary>
  public const string SortOrder = "js-sort-order";

  /// <summary>The caveat of a module that throws a string out of a function, which its caller receives as an Error of that message (#1365).</summary>
  public const string ThrowsTextCaveat = "js-throw-string";

  private static readonly ImportCaveat _throwsText = new(
    ThrowsTextCaveat,
    "a string thrown out of a function reaches its caller as the string itself in JavaScript; the imported code throws an Error, whose message is the string");

  /// <summary>
  /// The caveat of a module that stores a whole double - <c>parseInt</c> of a short text of proven
  /// digits - where JSDoc states integers (#1413): a NaN, which an Array keeps, traps instead.
  /// </summary>
  public const string NaNStore = "js-nan-store";

  private static readonly ImportCaveat _nanStore = new(
    NaNStore,
    "a value proven a NaN or an integer, such as parseInt of an empty text of digits, is stored where JSDoc states integers: JavaScript "
    + "keeps the NaN, the imported code traps with 'integer overflow'");

  private static readonly ImportCaveat _sortOrder = new(
    SortOrder,
    "a sort comparator returns a double: for inputs that make it inconsistent, such as a NaN, ECMA-262 leaves the order to the engine, the translation fixes the one of its stable merge sort");

  private static readonly ImportCaveat _divisionByZero = new(
    DivisionByZero,
    "an integer division or remainder by zero is Infinity or NaN in JavaScript, -2147483648 / -1 wraps and a BigInt division by zero "
    + "throws a RangeError; where the remainder of two non-negative integers is no operand of a coercion, where the dividend under a "
    + "coercion has an effect, and for BigInts, the imported code traps with 'divide by zero' and 'integer overflow'");

  /// <summary>Imports one JavaScript source file, a script or a module.</summary>
  /// <param name="path">The name of the file, used in diagnostics; its stem names the module.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="exports">
  /// The functions to import, together with every function they call; the named ones become the
  /// public functions of the module. Without it, or with no name in it, every function of the file
  /// must import.
  /// </param>
  /// <param name="read">
  /// Reads a file the source requires - its text, or <see langword="null"/> when there is none - or
  /// <see langword="null"/> to import the file alone. With it a module that requires others, a UMD
  /// factory, a wrapper that assigns <c>module.exports</c> or a CommonJS file, is imported together
  /// with the files it requires (<see cref="Modules.ModuleGraph"/>), and diagnostics point into the
  /// file they are about.
  /// </param>
  /// <returns>The module, or the diagnostics that explain why there is none.</returns>
  public static ImportResult Import(string path, string source, IReadOnlyCollection<string>? exports = null, System.Func<string, string?>? read = null) {
    if (read is null)
      return Imported(path, source, exports, 0);

    var linked = Modules.ModuleGraph.Link(path, source, read, out var problems);
    if (problems.Count > 0)
      return Refused(problems);
    if (linked is null)
      return Imported(path, source, exports, 0);

    var result = Imported(path, linked.Text, exports, linked.Files[^1].FirstLine);
    return result with { Diagnostics = [.. result.Diagnostics.Select(linked.Locate)] };
  }

  private static ImportResult Imported(string path, string source, IReadOnlyCollection<string>? exports, int entryLine) {
    var comments = new List<Comment>();
    if (Parse(source, comments, out var failure) is not { } program)
      return Refused([SyntaxErrorDescriptor.At(failure!.Description, path, failure.LineNumber, failure.Column + 1)]);

    if (exports is { Count: 0 })
      exports = null;

    // A function the file does not export that its calls pass values of two kinds is imported once more with a copy per kind.
    var result = Imported(path, source, program, comments, exports, entryLine, [], out var copies);
    return copies.Count == 0 ? result : Imported(path, source, program, comments, exports, entryLine, copies, out _);
  }

  /// <summary>
  /// Imports a parsed file, with a copy of a function for every call <paramref name="copies"/> names;
  /// without copies, says which calls of a function that is no member of a class and that the file
  /// does not export pass values of other kinds than its other calls - each kind then gets a copy,
  /// and the file has to be imported again.
  /// </summary>
  private static ImportResult Imported(string path, string source, Program program, List<Comment> comments, IReadOnlyCollection<string>? exports, int entryLine,
      Dictionary<Node, (IFunction Function, int Copy)> copies, out Dictionary<Node, (IFunction Function, int Copy)> wanted) {
    wanted = [];
    var errors = new ErrorLibrary(program);
    var collector = new FunctionCollector(source, comments) { Errors = errors, EntryLine = entryLine };
    var candidates = collector.Collect(program);
    var redirects = Copies(candidates, copies);
    var constants = collector.Constants;
    var classes = collector.Classes;
    var enums = collector.EnumObjects;
    var inference = new TypeInference(path, source, candidates, constants.ToDictionary(constant => constant.Name)) {
      // A native error is a class wherever no class of the file has its name.
      Classes = classes.GroupBy(owner => owner.Name).Select(group => group.First())
        .Concat(errors.ByName.Values.Where(error => classes.All(owner => owner.Name != error.Name))).ToDictionary(owner => owner.Name),
      Redirects = redirects,
      Enums = enums.ToDictionary(named => named.Name),
      Typedefs = Typedefs.Read(program, source, comments)
    };
    var globals = collector.Globals;
    inference.DeclareGlobals(globals, program);
    var diagnostics = new List<Diagnostic>();
    List<Candidate> chosen;
    if (exports is null) {
      chosen = candidates;
      if (candidates.Count == 0)
        diagnostics.Add(UnsupportedDescriptor.At("'a file without a function' is not supported yet", path, 1, 1));
    } else {
      chosen = [];
      foreach (var name in exports) {
        if (candidates.Find(candidate => candidate.Name == name) is { } candidate)
          chosen.Add(candidate);
        else
          diagnostics.Add(UnknownExportDescriptor.At($"the file has no function '{name}' to export", path, 1, 1));
      }

      // A class is imported whole, whichever of its members the functions exported use; so is what the module does when it is loaded.
      chosen.AddRange(candidates.Where(candidate => (candidate.Class is not null || candidate == collector.Initializer) && !chosen.Contains(candidate)));
    }

    // With an export list the functions the named ones call are imported too; the types are found for all of them together.
    var models = inference.Solve(chosen, candidate => candidate != collector.Initializer && (exports is null ? candidate.IsPublic : exports.Contains(candidate.Name)));
    if (copies.Count == 0 && Wanted(inference, models) is { Count: > 0 } split) {
      wanted = split;
      return Refused([]);
    }

    // A struct is named after the first variable, in source order, that holds a value of it, and
    // only where none does after the first array of it.
    var variables = models.SelectMany(model => model.Parameters.Concat(model.Locals)).OrderBy(variable => variable.Declaration.Start).ToList();
    foreach (var elements in new[] { false, true })
      foreach (var variable in variables)
        inference.Shapes.Prefer(variable.Type, variable.Name, elements);

    inference.Shapes.Seal();
    // The classes synthesized for literals and extended objects are classes of the module (#1388).
    classes = [.. classes, .. inference.Shapes.Classes.Made];
    // The signatures of the function values name the structs of their records, which exist from here on (#1377).
    inference.Sign(models);
    // The class of every typedef JSDoc names is a class of the module (#1409).
    classes = [.. classes, .. inference.Typedefs.Used];
    Classes.Declare(classes, inference, diagnostics);
    var lowering = new Lowering(inference) { ModuleInitializer = collector.Initializer };
    // What the module does when it is loaded runs only where its functions have state it could change; otherwise it is not looked at.
    var initializer = collector.Initializer;
    if (initializer is not null && !classes.Any(owner => owner.Statics.Count > 0)
        && !models.Where(model => model.Candidate != initializer).SelectMany(model => model.Bindings.Values).OfType<Variable>().Any(variable => variable.IsGlobal)) {
      models = [.. models.Where(model => model.Candidate != initializer)];
      initializer = null;
    }

    var state = ModuleState.Lower(program, globals, models, inference.Initializers, lowering, diagnostics, classes.SelectMany(owner => owner.Statics.Values),
      initializer is null ? collector.ClassStatements : [.. collector.ClassStatements, .. collector.Effects]);
    if (diagnostics.Count > 0)
      return Refused(diagnostics.OrderBy(diagnostic => diagnostic.Line).ThenBy(diagnostic => diagnostic.Column).ToList());

    var functions = new Dictionary<Candidate, IrFunction>();
    // A nested function is lowered inside the function it stands in.
    foreach (var model in models.Where(model => !model.Candidate.IsNested))
      if (lowering.Function(model, diagnostics) is { } function)
        functions[model.Candidate] = function;

    if (diagnostics.Count > 0)
      return Refused(diagnostics.OrderBy(diagnostic => diagnostic.Line).ThenBy(diagnostic => diagnostic.Column).ToList());

    var lowered = candidates.Where(functions.ContainsKey).Select(candidate => functions[candidate]).ToList();
    // The functions that run the array callbacks (#1329).
    lowered.AddRange(lowering.Callbacks);

    lowered.AddRange(Classes.Define(classes, inference));
    ModuleState.CheckInitializers(state, lowered, lowering, diagnostics);
    ModuleState.CheckOrder(state, initializer is null ? null : functions.GetValueOrDefault(initializer), lowered, classes.Select(owner => owner.Ir), lowering.ModuleSites, lowering, diagnostics);
    if (diagnostics.Count > 0)
      return Refused(diagnostics);

    var module = new IrModule(
      Path.GetFileNameWithoutExtension(path),
      lowered,
      constants.Where(lowering.Constants.Contains).Select(constant => constant.Constant).ToList()) {
      Types = [.. enums.Select(named => named.Type).OfType<IrEnumType>(), .. lowering.Structs(lowered, inference.Shapes), .. classes.Select(owner => owner.Ir)],
      Globals = [.. state.Select(global => global.Global!)],
      Initializer = initializer?.Name
    };

    // An overridden method called on the object a constructor is making would run before the fields of the derived class have their values in C#'s order and after it in JavaScript's.
    foreach (var (_, call) in IrConstruction.VirtualCalls(module))
      diagnostics.Add(inference.Diagnose(lowering.Sites[call], UnsupportedDescriptor, "'a call of an overridden method on 'this' during construction' is not supported yet"));

    // A Promise.all whose order or whose rejection would show how JavaScript and .NET interleave its calls (#1330).
    diagnostics.AddRange(lowering.WhenAllProblems(module));

    if (diagnostics.Count > 0)
      return Refused(diagnostics);

    // The copies are named after their types last: the checks above find their calls in the module as lowered.
    module = Named(module, redirects.Values);
    var caveats = new List<ImportCaveat>();
    if (lowering.AccessesElements)
      caveats.Add(_outOfRangeAccess);

    if (lowering.Divides)
      caveats.Add(_divisionByZero);

    if (lowering.SortsByDifference)
      caveats.Add(_sortOrder);

    if (lowering.ThrowsText)
      caveats.Add(_throwsText);

    if (lowering.TrapsNaN)
      caveats.Add(_nanStore);

    var ordered = models.Where(model => !model.Candidate.IsNested).OrderBy(model => model.Candidate.Anchor.Start).ToList();
    var integers = Signatures(ordered, parameter => parameter.IsIntegerByUse && parameter.Parameter!.Type.IsWord);
    if (integers.Length > 0)
      caveats.Add(new ImportCaveat(
        IntegerParameter,
        "a parameter without a JSDoc type that its function reads only under ToInt32 or ToUint32 is that 32-bit integer; JavaScript "
        + $"coerces whatever number is passed, the imported function takes the integer: {integers}"));

    var arrays = Signatures(ordered.Where(model => model.IsPublic), parameter => parameter.IsArrayByCall);
    if (arrays.Length > 0)
      caveats.Add(new ImportCaveat(
        ArrayParameter,
        "a parameter without a JSDoc type that is used as an array has the type of the arrays the file itself passes to it; "
        + $"JavaScript takes any array, the imported function that one: {arrays}"));

    return new(module, [], caveats, ordered.SelectMany(inference.Explain).ToList());
  }

  /// <summary>The functions that have parameters of a kind, each as its name and those parameters.</summary>
  private static string Signatures(IEnumerable<FunctionModel> models, System.Func<Variable, bool> matches)
    => string.Join(", ", models
      .Select(model => (model.Candidate.Name, Parameters: model.Parameters.Where(matches).Select(parameter => parameter.Name).ToList()))
      .Where(function => function.Parameters.Count > 0)
      .Select(function => $"{function.Name}({string.Join(", ", function.Parameters)})"));

  private static ImportResult Refused(IReadOnlyList<Diagnostic> diagnostics) => new(null, diagnostics, [], []);

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
