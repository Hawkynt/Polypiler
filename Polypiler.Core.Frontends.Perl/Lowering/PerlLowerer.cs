using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

/// <summary>The context perl gives an expression: void, scalar, list, or the caller's (unknown until run time).</summary>
internal enum PerlWant {
  Void, Scalar, List, Unknown
}

/// <summary>Lowers the syntax tree of a Perl file into a verified <see cref="IrModule"/>.</summary>
internal sealed partial class PerlLowerer {

  private static readonly HashSet<string> _allowedPragmas = new(StringComparer.Ordinal) {
    "strict", "warnings", "integer", "feature", "constant", "Exporter", "List::Util", "POSIX", "Carp", "Test::More",
    "Scalar::Util", "Hash::Util::FieldHash", "Moose", "Moose::Role", "base", "parent", "vars"
  };

  private static readonly HashSet<string> _objectModules = new(StringComparer.Ordinal) {
    "overload", "Moo", "Mouse", "Object::Pad"
  };

  private static readonly HashSet<string> _magicGlobals = new(StringComparer.Ordinal) {
    "ENV", "ARGV", "0", "!", ",", "/", ";", "SIG", "INC", "STDOUT", "STDERR", "STDIN"
  };

  private static readonly HashSet<string> _testAssertions = new(StringComparer.Ordinal) {
    "is", "ok", "cmp_ok", "is_deeply", "done_testing"
  };

  private sealed class PerlPackageInfo(string name) {
    public string Name { get; } = name;
    public SortedDictionary<string, PerlSub> Functions { get; } = new(StringComparer.Ordinal);
    public List<string> Isa { get; } = [];
  }

  private readonly PerlDocument _document;
  private readonly IReadOnlyDictionary<string, PerlExpr?> _constants;
  private readonly PerlContracts _contracts;
  private readonly PerlImportOptions _options;
  private readonly Dictionary<string, PerlPackageInfo> _packages;
  private readonly Dictionary<string, (string Package, string Name, PerlSub Sub, bool IsPublic)> _subs = new(StringComparer.Ordinal);
  private readonly Dictionary<string, IrFunction> _loweredFunctions = new(StringComparer.Ordinal);
  private readonly Dictionary<string, (IrType ReturnType, List<IrType> ParamTypes)> _signatures = new(StringComparer.Ordinal);
  private readonly List<IrTypeDeclaration> _types = [];
  private readonly Dictionary<string, IrClassType> _recordTypes = new(StringComparer.Ordinal);
  private readonly List<IrGlobal> _globals = [];
  private readonly List<string> _exports = [];
  private readonly List<string> _testFunctions = [];

  // Per-subroutine state
  private PerlSub? _currentSub;
  private string _currentPackage = "main";
  private string _currentSubName = "";
  private IrType _currentReturnType = IrType.I64;
  private readonly Dictionary<PerlLexical, IrLocal> _variables = new(ReferenceEqualityComparer.Instance);
  private readonly List<IrLocal> _locals = [];
  private readonly List<IrLocal> _argParams = [];
  private readonly Dictionary<PerlLexical, IrLambda> _closureLocals = new(ReferenceEqualityComparer.Instance);
  private Dictionary<string, IrLocal>? _captured;
  private readonly Stack<LoopContext> _loops = new();
  private readonly PerlBounds _bounds = new();
  private IrLocal? _topicVar;
  private IrLocal? _evalErrorVar;
  private int _line = 1;
  private bool _integer;
  private int _tempCounter;

  private PerlLowerer(PerlDocument document, IReadOnlyDictionary<string, PerlExpr?> constants, Dictionary<string, PerlPackageInfo> packages, PerlContracts contracts, PerlImportOptions options) {
    this._document = document;
    this._constants = constants;
    this._packages = packages;
    this._contracts = contracts;
    this._options = options;
    this._types.AddRange(contracts.Declarations);
  }

  public static PerlImportResult LowerTree(string file, PerlDocument document, IReadOnlyDictionary<string, PerlExpr?> constants, PerlImportOptions options) {
    try {
      var packages = CollectPackageInfo(document);
      var shells = PreScanClassShells(document, packages);
      IrType? ResolveNamed(string name) {
        if (shells.TryGetValue(name, out var decl))
          return decl is IrClassType c ? IrType.Object(c) : IrType.Object((IrInterfaceType)decl);
        return null;
      }
      var contracts = PerlContracts.Parse(options.Contracts, msg => new Refusal(PerlFrontend.CompileFailed, "compilation failed: " + msg, 1), ResolveNamed);
      var lowerer = new PerlLowerer(document, constants, packages, contracts, options);
      var module = lowerer.BuildModule(file, shells);
      var problems = IrVerifier.Verify(module);
      return problems.Count == 0
        ? new(module, [], contracts.Recorded, lowerer._exports, lowerer._testFunctions)
        : new(null, problems, [], [], []);
    } catch (Refusal error) {
      var diag = error.Extra is not null
        ? Diagnostic.Create(error.Descriptor, new(file, error.Line, 1, error.Line, 1), error.Detail, error.Extra)
        : Diagnostic.Create(error.Descriptor, new(file, error.Line, 1, error.Line, 1), error.Detail);
      if (error.Cause is not null)
        diag = diag with { Cause = error.Cause };
      return new(null, [diag], [], [], []);
    }
  }

  /// <summary>
  /// The packages of the file as perl's symbol table holds them after compiling it: those that define
  /// subs, and those whose inheritance <c>use parent</c>/<c>use base</c> sets, with their subs by name.
  /// </summary>
  private static Dictionary<string, PerlPackageInfo> CollectPackageInfo(PerlDocument document) {
    var packages = new Dictionary<string, PerlPackageInfo>(StringComparer.Ordinal);
    PerlPackageInfo Get(string name) {
      if (!packages.TryGetValue(name, out var info))
        packages[name] = info = new PerlPackageInfo(name);
      return info;
    }
    foreach (var declaration in document.Subs) {
      var split = declaration.Name.LastIndexOf("::", StringComparison.Ordinal);
      Get(declaration.Name[..split]).Functions[declaration.Name[(split + 2)..]] = declaration.Sub;
    }
    foreach (var statement in PerlWalk.Descendants(document).OfType<PerlUse>()) {
      if (statement is not { No: false, Module: "base" or "parent", EmptyImports: false })
        continue;
      var parents = ConstantStrings(statement.Imports).Where(s => s != "-norequire").ToList();
      if (parents.Count > 0)
        Get(statement.Package).Isa.AddRange(parents);
    }
    return packages;
  }

  private static IEnumerable<string> ConstantStrings(PerlExpr? expression) => expression switch {
    PerlString s => [s.Value],
    PerlWords w => w.Words,
    PerlList l => l.Items.SelectMany(ConstantStrings),
    _ => []
  };

  private IrModule BuildModule(string file, Dictionary<string, IrTypeDeclaration> shells) {
    this.ValidateBeginBlocks();
    var packages = this._packages.Count > 0
      ? this._packages
      : new Dictionary<string, PerlPackageInfo> { ["main"] = new PerlPackageInfo("main") };
    var functions = new List<IrFunction>();
    this.InitializeClassesAndInterfaces(shells, packages, functions);
    this.CollectPackages(packages);
    this.ValidateAndLowerTopLevel(file);

    if (this._subs.Count == 0 && this._testFunctions.Count == 0 && functions.Count == 0)
      throw new Refusal(PerlFrontend.SubUnsupported, "a file without subroutines", 1);

    foreach (var (key, entry) in this._subs) {
      var ret = this._contracts.TryGetType(entry.Package, entry.Name, "return", out var r) ? r : IrType.I64;
      this._signatures[key] = (ret, []);
    }

    foreach (var (_, entry) in this._subs.OrderBy(e => e.Key, StringComparer.Ordinal)) {
      var fn = this.LowerSubroutine(entry.Package, entry.Name, entry.Sub, entry.IsPublic);
      this._loweredFunctions[entry.Name] = fn;
      functions.Add(fn);
    }

    foreach (var extra in this._loweredFunctions.Values) {
      if (!functions.Contains(extra))
        functions.Add(extra);
    }

    var moduleName = this.ChooseModuleName(file);
    return new IrModule(moduleName, functions) {
      Types = this._types,
      Globals = this._globals
    };
  }

  private string ChooseModuleName(string file) {
    var nonMain = this._packages.Keys.Where(p => p != "main").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
    if (nonMain is not null)
      return nonMain.Replace("::", ".", StringComparison.Ordinal);
    var stem = Path.GetFileNameWithoutExtension(file);
    return string.IsNullOrWhiteSpace(stem) ? "PerlModule" : stem;
  }

  /// <summary>
  /// The compile-time code of the file, in the order perl runs it: every <c>use</c> must name a module
  /// of the subset, and a <c>BEGIN</c> block of the file's own is refused.
  /// </summary>
  private void ValidateBeginBlocks() {
    foreach (var node in PerlWalk.Descendants(this._document)) {
      if (node is PerlPhaseBlock { Phase: "BEGIN" })
        throw new Refusal(PerlFrontend.SubUnsupported, "user BEGIN block", 1);
      if (node is not PerlUse { Module: { } module } use || use.EmptyImports)
        continue;
      if (module is "bigint" or "bignum" or "bigrat" or "Math::BigInt")
        throw new Refusal(PerlFrontend.ScalarUnsupported, $"use {module}", 1);
      if (_objectModules.Contains(module))
        throw new Refusal(PerlFrontend.ObjectSystemUnsupported, $"use {module}", 1, cause: "OBJ-CLASSES");
      if (module == "utf8")
        continue;
      if (!_allowedPragmas.Contains(module))
        throw new Refusal(PerlFrontend.UnresolvedModule, module, 1);
      if (module is "base" or "parent")
        foreach (var parentPkg in ConstantStrings(use.Imports).Where(s => s != module && s != "-norequire"))
          if (parentPkg != "Exporter" && !this._packages.ContainsKey(parentPkg))
            throw new Refusal(PerlFrontend.ObjectSystemUnsupported, $"use {module} '{parentPkg}'", 1, cause: "OBJ-CLASSES");
    }
  }

  private void CollectPackages(Dictionary<string, PerlPackageInfo> packages) {
    var runtimeExports = this.CollectRuntimeExports();

    foreach (var (pkgName, pkg) in packages) {
      var nonExporterIsa = pkg.Isa.Concat(this._directives.Isa.GetValueOrDefault(pkgName) ?? []).Any(i => i != "Exporter");
      if (!this._classesByPkg.ContainsKey(pkgName) && nonExporterIsa)
        throw new Refusal(PerlFrontend.ObjectSystemUnsupported, $"@{pkgName}::ISA", 1, cause: "OBJ-VIRTUAL");

      var explicitExports = runtimeExports.GetValueOrDefault(pkgName) ?? [];
      foreach (var (subName, sub) in pkg.Functions) {
        if (this._classSubKeys.Contains($"{pkgName}::{subName}"))
          continue;
        if (subName is "AUTOLOAD" or "DESTROY")
          throw new Refusal(PerlFrontend.SubUnsupported, subName, 1);
        if (sub.Prototype is { Length: > 0 })
          throw new Refusal(PerlFrontend.SubUnsupported, $"prototype ({sub.Prototype}) on sub {subName}", 1);

        var isPublic = pkgName == "main"
          ? (this._options.ExportFilter is null ? !subName.StartsWith('_') : this._options.ExportFilter.Contains(subName))
          : (explicitExports.Contains(subName) && (this._options.ExportFilter is null || this._options.ExportFilter.Contains(subName)));
        var key = pkgName == "main" ? subName : $"{pkgName}::{subName}";
        this._subs[key] = (pkgName, subName, sub, isPublic);
        if (isPublic)
          this._exports.Add(pkgName == "main" ? $"main::{subName}" : key);
      }
    }
  }

  /// <summary>The statements of the program outside any sub, with those of <c>package NAME BLOCK</c> in place.</summary>
  private IEnumerable<PerlStatement> TopLevel() {
    IEnumerable<PerlStatement> Flatten(IEnumerable<PerlStatement> statements) {
      foreach (var statement in statements) {
        if (statement is PerlPackage { Body: { } body }) {
          foreach (var inner in Flatten(body.Statements))
            yield return inner;
          continue;
        }
        yield return statement;
      }
    }
    return Flatten(this._document.Statements);
  }

  /// <summary>Every node of the program's own code: the top-level statements, without the subs and the compile-time code.</summary>
  private IEnumerable<PerlNode> ProgramNodes()
    => this.TopLevel().Where(s => s is not (PerlSubDeclaration or PerlUse or PerlPhaseBlock)).SelectMany(s => PerlWalk.Descendants(s, intoSubs: false));

  private void ValidateAndLowerTopLevel(string file) {
    var nodes = this.ProgramNodes().ToList();
    var hasTestMore = nodes.Any(n => n is PerlCall { Builtin: false } call && _testAssertions.Contains(call.Name));
    if (hasTestMore) {
      this.LowerTestFile(file);
      return;
    }

    foreach (var node in nodes) {
      if (node is PerlCall { Builtin: true, Name: "print" or "say" or "system" or "exec" or "open" } call)
        throw new Refusal(PerlFrontend.SubUnsupported, $"top-level {call.Name}", 1);
      if (node is PerlCall { Builtin: true, Name: "tie" })
        throw new Refusal(PerlFrontend.MagicUnsupported, "tie", 1);
    }
  }

  private void LowerTestFile(string file) {
    var testName = "test_" + Path.GetFileNameWithoutExtension(file);
    this.ResetSubState("main", testName, null, IrType.Void);

    var stmts = new List<IrStmt>();
    foreach (var statement in this.TopLevel()) {
      this._line = statement.Line;
      this._integer = statement.Integer;
      if (statement is PerlExpressionStatement { Modifier: null, Expression: PerlCall { Builtin: false } call })
        this.TryLowerTestAssertion(call, stmts);
    }
    stmts.Add(new IrReturn(null));
    var fn = new IrFunction(testName, true, IrType.Void, [], [.. this._locals], new(stmts));
    this._loweredFunctions[testName] = fn;
    this._testFunctions.Add(testName);
  }

  private void TryLowerTestAssertion(PerlCall call, List<IrStmt> stmts) {
    var name = call.Name;
    var argOps = call.Arguments ?? [];
    if (name is "done_testing" or "plan")
      return;
    if (name == "ok" && argOps.Count >= 1) {
      var cond = this.Truth(this.LiftExpr(argOps[0], PerlWant.Scalar));
      stmts.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, cond),
        new([new IrThrow(new IrNew(IrType.Object(IrExceptionClass.AssertionException),
          IrExceptionClass.Constructor(IrExceptionClass.AssertionException, 1), [new IrStrConst("ok assertion failed")]))]), null));
      return;
    }
    if (name == "is" && argOps.Count >= 2) {
      var actual = this.LiftExpr(argOps[0], PerlWant.Scalar);
      var expected = this.LiftExpr(argOps[1], PerlWant.Scalar);
      var eq = actual.Type == IrType.String && expected.Type == IrType.String
        ? (IrExpr)new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [actual, expected])
        : new IrBinary(IrBinaryOp.Equal, actual, this.ToType(expected, actual.Type));
      stmts.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, eq),
        new([new IrThrow(new IrNew(IrType.Object(IrExceptionClass.AssertionException),
          IrExceptionClass.Constructor(IrExceptionClass.AssertionException, 1), [new IrStrConst("is assertion failed")]))]), null));
      return;
    }
    if (name == "cmp_ok" && argOps.Count >= 3) {
      var actual = this.LiftExpr(argOps[0], PerlWant.Scalar);
      var expected = this.LiftExpr(argOps[2], PerlWant.Scalar);
      var eq = new IrBinary(IrBinaryOp.Equal, actual, this.ToType(expected, actual.Type));
      stmts.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, eq),
        new([new IrThrow(new IrNew(IrType.Object(IrExceptionClass.AssertionException),
          IrExceptionClass.Constructor(IrExceptionClass.AssertionException, 1), [new IrStrConst("cmp_ok assertion failed")]))]), null));
    }
  }

  private void ResetSubState(string package, string name, PerlSub? sub, IrType retType) {
    this._currentSub = sub;
    this._currentPackage = package;
    this._currentSubName = name;
    this._currentReturnType = retType;
    this._variables.Clear();
    this._locals.Clear();
    this._argParams.Clear();
    this._closureLocals.Clear();
    this._captured = null;
    this._loops.Clear();
    this._topicVar = null;
    this._evalErrorVar = null;
    this._line = sub?.Line ?? 1;
    this._integer = false;
    this._tempCounter = 0;
  }

  private IrFunction LowerSubroutine(string package, string name, PerlSub sub, bool isPublic) {
    var retType = this._contracts.TryGetType(package, name, "return", out var r) ? r : IrType.I64;
    this.ResetSubState(package, name, sub, retType);
    this._line = 1;

    var nodes = sub.Body!.Statements;
    var parameters = new List<IrLocal>();
    var consumed = new HashSet<PerlStatement>(ReferenceEqualityComparer.Instance);
    var statements = new List<IrStmt>();

    this.ExtractParameters(sub, nodes, package, name, parameters, consumed);
    this.LowerStatements(nodes, statements, consumed, PerlWant.Unknown, allowImplicitReturn: true);

    if (statements.Count == 0 || statements[^1] is not IrReturn)
      statements.Add(new IrReturn(this._currentReturnType == IrType.Void ? null : DefaultExpr(this._currentReturnType)));

    var irName = this.IrFunctionName(package, name);
    return new(irName, isPublic, this._currentReturnType, parameters, [.. this._locals], new(statements));
  }

  /// <summary>The names <c>our @EXPORT = ...</c> and <c>our @EXPORT_OK = ...</c> at the top level give each package.</summary>
  private Dictionary<string, HashSet<string>> CollectRuntimeExports() {
    var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    foreach (var statement in this.TopLevel()) {
      if (statement is not PerlExpressionStatement { Modifier: null, Expression: PerlAssign { Operator: "=", Target: var target, Value: var value } })
        continue;
      var array = Unparen(target) switch {
        PerlDeclaration { Kind: PerlDeclarator.Our, Variables: [{ Sigil: '@', Name: "EXPORT" or "EXPORT_OK" } v] } => v,
        PerlVariable { Sigil: '@' } v when ShortName(v.Name) is "EXPORT" or "EXPORT_OK" => v,
        _ => null
      };
      if (array is null)
        continue;
      var targetPkg = array.Name.Contains("::", StringComparison.Ordinal) ? array.Name[..array.Name.LastIndexOf("::", StringComparison.Ordinal)] : statement.Package;
      if (!result.TryGetValue(targetPkg, out var set))
        result[targetPkg] = set = new HashSet<string>(StringComparer.Ordinal);
      foreach (var s in ConstantStrings(value))
        set.Add(s);
    }
    return result;
  }

  private static string ShortName(string name) {
    var split = name.LastIndexOf("::", StringComparison.Ordinal);
    return split < 0 ? name : name[(split + 2)..];
  }

  private static PerlExpr Unparen(PerlExpr expression)
    => expression is PerlList { Parenthesized: true, Items: [var only] } && only is PerlDeclaration or PerlVariable ? only : expression;

  private string IrFunctionName(string package, string name) {
    if (package == "main" || this._subs.Values.Count(e => e.Name == name) == 1)
      return name;
    return $"{package.Replace("::", "_", StringComparison.Ordinal)}_{name}";
  }

  // ---- parameters ----

  /// <summary>The statements of a body that perl compiles into code (a <c>use</c>, a <c>package</c> or a nested <c>sub</c> compile to none).</summary>
  private static bool IsOpless(PerlStatement statement)
    => statement is PerlUse or PerlPackage { Body: null } or PerlSubDeclaration or PerlPhaseBlock or PerlEmpty;

  /// <summary><c>my ($a, $b) = @_;</c> as the first statement of a body: the declared variables.</summary>
  private static PerlDeclaration? MatchParamUnpack(IReadOnlyList<PerlStatement> nodes, out PerlStatement statement) {
    statement = null!;
    var first = nodes.FirstOrDefault(s => !IsOpless(s));
    if (first is PerlExpressionStatement { Modifier: null, Expression: PerlAssign { Operator: "=", Target: PerlDeclaration { Kind: PerlDeclarator.My, Parenthesized: true } declaration, Value: PerlVariable { Sigil: '@', Name: "_" } } }
        && declaration.Variables.All(v => v.Lexical is not null)) {
      statement = first;
      return declaration;
    }
    return null;
  }

  private void ExtractParameters(PerlSub sub, IReadOnlyList<PerlStatement> nodes, string package, string name, List<IrLocal> parameters, HashSet<PerlStatement> consumed) {
    var nonState = nodes.Where(n => !IsOpless(n)).ToList();
    if (nonState.Count == 0 && sub.Signature is null)
      return;

    // Form 1: `my ($a, $b) = @_;`
    if (MatchParamUnpack(nodes, out var assign) is { } unpack) {
      consumed.Add(assign);
      foreach (var target in unpack.Variables) {
        if (target.Sigil is '$' or '@') {
          var lex = target.Name;
          var ptype = this.ResolveParamType(package, name, lex, target.Sigil == '@');
          var param = new IrLocal(lex, ptype);
          if (!this._variables.TryAdd(target.Lexical!, param))
            throw this.Fail(PerlFrontend.SubUnsupported, $"duplicate parameter {lex}");
          parameters.Add(param);
        }
      }
      return;
    }

    // Form 2: a signature binds the scalars of the pad that have contract types, in pad order.
    if (sub.Signature is not null) {
      foreach (var lexical in sub.Unit.Lexicals) {
        if (lexical.Sigil == '$' && this._contracts.TryGetType(package, name, lexical.Name, out var ct)) {
          var param = new IrLocal(lexical.Name, ct);
          if (this._variables.TryAdd(lexical, param))
            parameters.Add(param);
        }
      }
      return;
    }

    // Form 3: leading `my $a = shift;` statements.
    foreach (var stmt in nonState) {
      if (stmt is PerlExpressionStatement { Modifier: null, Expression: PerlAssign { Operator: "=", Target: PerlDeclaration { Kind: PerlDeclarator.My, Parenthesized: false, Variables: [{ Sigil: '$' } variable] }, Value: PerlCall { Builtin: true, Name: "shift" } } }) {
        consumed.Add(stmt);
        var ptype = this.ResolveParamType(package, name, variable.Name, false);
        var param = new IrLocal(variable.Name, ptype);
        this._variables[variable.Lexical!] = param;
        parameters.Add(param);
      } else {
        break;
      }
    }
    if (parameters.Count > 0)
      return;

    // Form 4: direct `$_[N]` reads synthesize parameters $arg0, $arg1, ...
    var argIndices = new SortedSet<int>();
    foreach (var node in PerlWalk.Descendants(sub.Body!, intoSubs: false))
      if (node is PerlElement { Hash: false, Aggregate: PerlVariable { Sigil: '@', Name: "_" }, Index: PerlNumber { Value.IsIntegral: true } index } && index.Value.Integer is >= 0 and < 256)
        argIndices.Add((int)index.Value.Integer);
    if (argIndices.Count > 0) {
      var max = argIndices.Max;
      for (var i = 0; i <= max; ++i) {
        var pname = $"arg{i}";
        var ptype = this._contracts.TryGetType(package, name, pname, out var t) ? t : IrType.I64;
        var param = new IrLocal(pname, ptype);
        parameters.Add(param);
        this._argParams.Add(param);
      }
    }
  }

  private IrType ResolveParamType(string package, string func, string paramName, bool isArray) {
    if (this._contracts.TryGetType(package, func, paramName, out var type))
      return type;
    return isArray ? IrType.List(IrType.I64) : IrType.I64;
  }

  private IrLocal DeclareLocal(string name, IrType type, PerlLexical? lexical = null) {
    var unique = name;
    var suffix = 2;
    while (this._locals.Any(l => l.Name == unique) || this._variables.Values.Any(l => l.Name == unique))
      unique = $"{name}_{suffix++}";
    var local = new IrLocal(unique, type);
    this._locals.Add(local);
    if (lexical is not null)
      this._variables[lexical] = local;
    return local;
  }

  private IrLocal TempLocal(IrType type, string prefix = "_t")
    => this.DeclareLocal($"{prefix}{++this._tempCounter}", type);

  /// <summary>The local a lexical reads: its own, or inside an anonymous sub the outer local of the same name.</summary>
  private bool TryLocal(PerlLexical lexical, out IrLocal local) {
    if (this._variables.TryGetValue(lexical, out local!))
      return true;
    if (this._captured is not null && lexical.Sigil == '$' && this._captured.TryGetValue(lexical.Name, out local!))
      return true;
    return false;
  }

  // ---- calls ----

  private IrExpr LowerCall(PerlCall call) {
    var calleeName = this.QualifiedCallee(call.Name);
    var argOps = call.Arguments ?? [];

    var shortName = ShortName(calleeName);
    if (shortName is "croak" or "confess") {
      var msg = argOps.Count > 0 ? this.LiftExpr(argOps[0], PerlWant.List) : new IrStrConst("error");
      return new IrThrowExpression(IrType.I64, new IrNew(IrType.Object(IrExceptionClass.Exception),
        IrExceptionClass.Constructor(IrExceptionClass.Exception, 1), [msg.Type == IrType.String ? msg : new IrStrConst(shortName)]));
    }
    if (shortName is "floor" or "ceil") {
      var arg = this.ToType(this.LiftExpr(argOps[0], PerlWant.List), IrType.F64);
      return new IrIntrinsic(shortName == "floor" ? IrIntrinsicOp.Floor : IrIntrinsicOp.Ceiling, IrType.F64, [arg]);
    }
    if (shortName is "min" or "max" && argOps.Count >= 2) {
      var acc = this.LiftExpr(argOps[0], PerlWant.List);
      var op = shortName == "min" ? IrIntrinsicOp.Min : IrIntrinsicOp.Max;
      for (var i = 1; i < argOps.Count; ++i) {
        var next = this.LiftExpr(argOps[i], PerlWant.List);
        var common = this._bounds.CommonType(acc, next, m => this.Fail(PerlFrontend.ScalarUnsupported, m));
        acc = new IrIntrinsic(op, common, [PerlBounds.Promote(acc, common), PerlBounds.Promote(next, common)]);
      }
      return acc;
    }
    if (shortName is "sum" or "sum0") {
      if (argOps.Count == 0)
        return new IrConst(IrType.I64, 0);
      var acc = this.LiftExpr(argOps[0], PerlWant.List);
      for (var i = 1; i < argOps.Count; ++i) {
        var next = this.LiftExpr(argOps[i], PerlWant.List);
        var common = this._bounds.CommonType(acc, next, m => this.Fail(PerlFrontend.ScalarUnsupported, m));
        acc = new IrBinary(IrBinaryOp.Add, PerlBounds.Promote(acc, common), PerlBounds.Promote(next, common));
      }
      return acc;
    }

    var lookupKey = this._subs.ContainsKey(calleeName) ? calleeName
      : this._subs.ContainsKey($"{this._currentPackage}::{shortName}") ? $"{this._currentPackage}::{shortName}"
      : null;

    if (lookupKey is null)
      throw this.Fail(PerlFrontend.UnresolvedModule, calleeName);

    var entry = this._subs[lookupKey];
    var irTarget = this.IrFunctionName(entry.Package, entry.Name);
    var retType = this._signatures[lookupKey].ReturnType;
    var args = argOps.Select(a => this.LiftExpr(a, PerlWant.List)).ToArray();
    return new IrCall(irTarget, retType, args);
  }

  /// <summary>The name of a called sub as perl's symbol table holds it: qualified by the current package, <c>main</c> left out.</summary>
  private string QualifiedCallee(string name) {
    if (name.StartsWith("main::", StringComparison.Ordinal))
      return name[6..];
    if (name.Contains("::", StringComparison.Ordinal))
      return name;
    return this._currentPackage == "main" ? name : $"{this._currentPackage}::{name}";
  }

  private static IrExpr DefaultExpr(IrType type) {
    if (type == IrType.Bool) return new IrConst(IrType.Bool, 0);
    if (type.IsFloat) return new IrConst(type, 0);
    if (type.IsInteger) return new IrConst(type, 0);
    if (type == IrType.String) return new IrStrConst("");
    if (type.IsOptional) return new IrNone(type);
    if (type.IsNullable) return new IrNull(type);
    if (type.IsList) return new IrCollection(IrCollectionOp.ListNew, type, []);
    return new IrCollection(IrCollectionOp.MapNew, type, []);
  }

  private IrExpr ToType(IrExpr value, IrType target) {
    if (value.Type == target)
      return value;
    if (target.IsOptional && value.Type == target.Underlying)
      return new IrSome(value);
    if (value.Type.IsOptional && value.Type.Underlying == target)
      return new IrCoalesce(value, DefaultExpr(target));
    if (value.Type == IrType.Bool && target.IsInteger)
      return new IrConditional(value, new IrConst(target, 1), new IrConst(target, 0));
    if (target == IrType.Bool)
      return this.Truth(value);
    if (value.Type.IsNumeric && target.IsNumeric)
      return PerlBounds.Promote(value, target);
    throw this.Fail(PerlFrontend.ScalarUnsupported, $"converting {value.Type} to {target}");
  }

  private IrExpr Truth(IrExpr value) {
    if (value.Type == IrType.Bool)
      return value;
    if (value.Type.IsOptional)
      return new IrUnary(IrUnaryOp.LogicalNot, new IrIsNone(value));
    if (value.Type.IsNullable && value.Type != IrType.String)
      return new IrUnary(IrUnaryOp.LogicalNot, new IrIsNull(value));
    if (value.Type == IrType.String) {
      var len = new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [value]);
      var notEmpty = new IrBinary(IrBinaryOp.NotEqual, len, new IrConst(IrType.I32, 0));
      var notZero = new IrUnary(IrUnaryOp.LogicalNot, new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [value, new IrStrConst("0")]));
      return new IrBinary(IrBinaryOp.LogicalAnd, notEmpty, notZero);
    }
    if (value.Type.IsList)
      return new IrBinary(IrBinaryOp.NotEqual, new IrCollection(IrCollectionOp.ListCount, IrType.I32, [value]), new IrConst(IrType.I32, 0));
    if (value.Type.IsArray)
      return new IrBinary(IrBinaryOp.NotEqual, new IrArrayLength(value), new IrConst(IrType.I32, 0));
    return new IrBinary(IrBinaryOp.NotEqual, value, new IrConst(value.Type, 0));
  }

  private Refusal Fail(DiagnosticDescriptor descriptor, string detail, string? extra = null, string? cause = null)
    => new(descriptor, detail, this._line, extra, cause);

  private sealed class LoopContext(string? label, bool isDoWhile) {
    public string? Label { get; } = label;
    public bool IsDoWhile { get; } = isDoWhile;
    public IrLocal? BreakFlag { get; set; }
    public IrLocal? ContinueFlag { get; set; }
  }

  private sealed class Refusal(DiagnosticDescriptor descriptor, string detail, int line, string? extra = null, string? cause = null) : Exception(detail) {
    public DiagnosticDescriptor Descriptor { get; } = descriptor;
    public string Detail { get; } = detail;
    public int Line { get; } = Math.Max(1, line);
    public string? Extra { get; } = extra;
    public string? Cause { get; } = cause;
  }

}
