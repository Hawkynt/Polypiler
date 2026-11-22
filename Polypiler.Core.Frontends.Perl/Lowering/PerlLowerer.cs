using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

/// <summary>Lowers a compiled <see cref="PerlTree"/> into a verified <see cref="IrModule"/>.</summary>
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

  private readonly PerlTree _tree;
  private readonly PerlContracts _contracts;
  private readonly PerlImportOptions _options;
  private readonly Dictionary<string, (string Package, string Name, PerlSubroutine Sub, bool IsPublic)> _subs = new(StringComparer.Ordinal);
  private readonly Dictionary<string, IrFunction> _loweredFunctions = new(StringComparer.Ordinal);
  private readonly Dictionary<string, (IrType ReturnType, List<IrType> ParamTypes)> _signatures = new(StringComparer.Ordinal);
  private readonly List<IrTypeDeclaration> _types = [];
  private readonly Dictionary<string, IrClassType> _recordTypes = new(StringComparer.Ordinal);
  private readonly List<IrGlobal> _globals = [];
  private readonly Dictionary<string, IrGlobal> _globalsByName = new(StringComparer.Ordinal);
  private readonly List<string> _exports = [];
  private readonly List<string> _testFunctions = [];

  // Per-subroutine state
  private PerlSubroutine _currentSub = null!;
  private string _currentPackage = "main";
  private string _currentSubName = "";
  private IrType _currentReturnType = IrType.I64;
  private readonly Dictionary<int, IrLocal> _variables = [];
  private readonly List<IrLocal> _locals = [];
  private readonly List<IrLocal> _argParams = [];
  private readonly Dictionary<int, IrLambda> _closureLocals = [];
  private readonly Stack<LoopContext> _loops = new();
  private readonly PerlBounds _bounds = new();
  private IrLocal? _topicVar;
  private IrLocal? _evalErrorVar;
  private int _line = 1;
  private bool _integer;
  private int _tempCounter;

  private PerlLowerer(PerlTree tree, PerlContracts contracts, PerlImportOptions options) {
    this._tree = tree;
    this._contracts = contracts;
    this._options = options;
    this._types.AddRange(contracts.Declarations);
  }

  public static PerlImportResult LowerTree(string file, PerlTree tree, PerlImportOptions options) {
    try {
      var shells = PreScanClassShells(tree);
      IrType? ResolveNamed(string name) {
        if (shells.TryGetValue(name, out var decl))
          return decl is IrClassType c ? IrType.Object(c) : IrType.Object((IrInterfaceType)decl);
        return null;
      }
      var contracts = PerlContracts.Parse(options.Contracts, msg => new Refusal(PerlFrontend.CompileFailed, msg, 1), ResolveNamed);
      var lowerer = new PerlLowerer(tree, contracts, options);
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

  private IrModule BuildModule(string file, Dictionary<string, IrTypeDeclaration> shells) {
    this.ValidateBeginBlocks();
    var packages = this._tree.Packages.Count > 0
      ? this._tree.Packages
      : new Dictionary<string, PerlPackage> {
          ["main"] = new PerlPackage { Functions = this._tree.Functions }
        };
    var functions = new List<IrFunction>();
    this.InitializeClassesAndInterfaces(shells, packages, functions);
    this.CollectPackages(packages);
    this.ValidateAndLowerTopLevel(file);

    if (this._subs.Count == 0 && this._testFunctions.Count == 0 && functions.Count == 0)
      throw new Refusal(PerlFrontend.SubUnsupported, "a file without subroutines", 1);

    // Pre-register signatures so mutual recursion and forward calls resolve cleanly.
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
    var nonMain = this._tree.Packages.Keys.Where(p => p != "main").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
    if (nonMain is not null)
      return nonMain.Replace("::", ".", StringComparison.Ordinal);
    var stem = Path.GetFileNameWithoutExtension(file);
    return string.IsNullOrWhiteSpace(stem) ? "PerlModule" : stem;
  }

  private void ValidateBeginBlocks() {
    foreach (var begin in this._tree.Begin) {
      var stmts = begin.Body!.Children[0].Children;

      var significant = stmts.Where(s => s.Name is not ("nextstate" or "null")).ToArray();
      if (significant is [
        { Name: "require", Children: [{ Name: "const", Constant: { Kind: "string", Value: var reqFile } }] },
        { Name: "lineseq", Children: [.., { Name: "entersub" or "stub" } tailOp] }
      ]) {
        var modName = reqFile.EndsWith(".pm", StringComparison.Ordinal)
          ? reqFile[..^3].Replace("/", "::", StringComparison.Ordinal)
          : reqFile;
        if (modName is "bigint" or "bignum" or "bigrat" or "Math::BigInt")
          throw new Refusal(PerlFrontend.ScalarUnsupported, $"use {modName}", 1);
        if (_objectModules.Contains(modName))
          throw new Refusal(PerlFrontend.ObjectSystemUnsupported, $"use {modName}", 1, cause: "OBJ-CLASSES");
        if (modName == "utf8")
          continue;
        if (!_allowedPragmas.Contains(modName))
          throw new Refusal(PerlFrontend.UnresolvedModule, modName, 1);

        if (modName is "base" or "parent" && tailOp.Name == "entersub") {
          var targets = tailOp.Children.SelectMany(Descendants)
            .Where(o => o is { Name: "const", Constant: { Kind: "string", Value: var s } } && s != modName && s != "-norequire")
            .Select(o => o.Constant!.Value);
          foreach (var parentPkg in targets)
            if (parentPkg != "Exporter" && !this._tree.Packages.ContainsKey(parentPkg))
              throw new Refusal(PerlFrontend.ObjectSystemUnsupported, $"use {modName} '{parentPkg}'", 1, cause: "OBJ-CLASSES");
        }
        continue;
      }

      // Check version pragma like `use v5.36;` or `use 5.036;`
      if (significant is [{ Name: "require", Children: [{ Name: "const" }] }])
        continue;

      throw new Refusal(PerlFrontend.SubUnsupported, "user BEGIN block", 1);
    }
  }

  private void CollectPackages(Dictionary<string, PerlPackage> packages) {
    var runtimeExports = this.CollectRuntimeExports();

    foreach (var (pkgName, pkg) in packages) {
      var nonExporterIsa = pkg.Isa.Concat(this._directives.Isa.GetValueOrDefault(pkgName) ?? []).Any(i => i != "Exporter");
      if (!this._classesByPkg.ContainsKey(pkgName) && nonExporterIsa)
        throw new Refusal(PerlFrontend.ObjectSystemUnsupported, $"@{pkgName}::ISA", 1, cause: "OBJ-VIRTUAL");

      var explicitExports = pkg.Export.Concat(pkg.ExportOk).ToHashSet(StringComparer.Ordinal);
      if (runtimeExports.TryGetValue(pkgName, out var extra))
        explicitExports.UnionWith(extra);
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

  private void ValidateAndLowerTopLevel(string file) {
    if (this._tree.Program is null)
      return;

    var allOps = Descendants(this._tree.Program).ToList();
    var hasTestMore = allOps.Any(o => o.Name == "gv" && o.Symbol is "is" or "ok" or "cmp_ok" or "is_deeply" or "done_testing");
    if (hasTestMore) {
      this.LowerTestFile(file, this._tree.Program);
      return;
    }

    // Top-level statements allowed: `package`, `our @EXPORT...`, `our @ISA = ...` (validated by package/class check), `our $X = CONST`, trailing `1;`, `nextstate`, `enter`, `leave`.
    foreach (var op in allOps) {
      if (op.Name is "print" or "say" or "system" or "exec" or "open")
        throw new Refusal(PerlFrontend.SubUnsupported, $"top-level {op.Name}", Math.Max(1, op.Line));
      if (op.Name == "tie")
        throw new Refusal(PerlFrontend.MagicUnsupported, "tie", Math.Max(1, op.Line));
    }
  }

  private void LowerTestFile(string file, PerlOp program) {
    var testName = "test_" + Path.GetFileNameWithoutExtension(file);
    this._currentSub = new PerlSubroutine { Lexicals = this._tree.ProgramLexicals, Body = program };
    this._currentPackage = "main";
    this._currentSubName = testName;
    this._currentReturnType = IrType.Void;
    this._variables.Clear();
    this._locals.Clear();
    this._argParams.Clear();
    this._closureLocals.Clear();
    this._loops.Clear();
    this._tempCounter = 0;
    this._line = 1;
    this._integer = false;

    var stmts = new List<IrStmt>();
    var nodes = program.Name == "leave" ? program.Children : [program];
    foreach (var node in nodes) {
      if (node.Name is "enter" or "nextstate" or "null" or "const") {
        if (node.Name == "nextstate") {
          this._line = node.Line;
          this._integer = node.Integer != 0;
        }
        continue;
      }
      if (node.Name == "entersub" && this.TryLowerTestAssertion(node, stmts))
        continue;
    }
    stmts.Add(new IrReturn(null));
    var fn = new IrFunction(testName, true, IrType.Void, [], [.. this._locals], new(stmts));
    this._loweredFunctions[testName] = fn;
    this._testFunctions.Add(testName);
  }

  private bool TryLowerTestAssertion(PerlOp call, List<IrStmt> stmts) {
    if (!TryExtractCallTarget(call, out var name, out var argOps))
      return false;

    if (name is "done_testing" or "plan")
      return true;
    if (name == "ok" && argOps.Length >= 1) {
      var cond = this.Truth(this.LiftExpr(argOps[0]));
      stmts.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, cond),
        new([new IrThrow(new IrNew(IrType.Object(IrExceptionClass.AssertionException),
          IrExceptionClass.Constructor(IrExceptionClass.AssertionException, 1), [new IrStrConst("ok assertion failed")]))]), null));
      return true;
    }
    if (name == "is" && argOps.Length >= 2) {
      var actual = this.LiftExpr(argOps[0]);
      var expected = this.LiftExpr(argOps[1]);
      var eq = actual.Type == IrType.String && expected.Type == IrType.String
        ? (IrExpr)new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [actual, expected])
        : new IrBinary(IrBinaryOp.Equal, actual, this.ToType(expected, actual.Type));
      stmts.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, eq),
        new([new IrThrow(new IrNew(IrType.Object(IrExceptionClass.AssertionException),
          IrExceptionClass.Constructor(IrExceptionClass.AssertionException, 1), [new IrStrConst("is assertion failed")]))]), null));
      return true;
    }
    if (name == "cmp_ok") {
      var actual = this.LiftExpr(argOps[0]);
      var expected = this.LiftExpr(argOps[2]);
      var eq = new IrBinary(IrBinaryOp.Equal, actual, this.ToType(expected, actual.Type));
      stmts.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, eq),
        new([new IrThrow(new IrNew(IrType.Object(IrExceptionClass.AssertionException),
          IrExceptionClass.Constructor(IrExceptionClass.AssertionException, 1), [new IrStrConst("cmp_ok assertion failed")]))]), null));
      return true;
    }
    return false;
  }

  private IrFunction LowerSubroutine(string package, string name, PerlSubroutine sub, bool isPublic) {
    this._currentSub = sub;
    this._currentPackage = package;
    this._currentSubName = name;
    this._variables.Clear();
    this._locals.Clear();
    this._argParams.Clear();
    this._closureLocals.Clear();
    this._loops.Clear();
    this._topicVar = null;
    this._evalErrorVar = null;
    this._line = 1;
    this._integer = false;
    this._tempCounter = 0;

    var nodes = sub.Body!.Children is [{ Name: "lineseq", Children: var n }] ? n : sub.Body.Children;

    this._currentReturnType = this._contracts.TryGetType(package, name, "return", out var retType)
      ? retType : IrType.I64;

    var parameters = new List<IrLocal>();
    var consumed = new HashSet<PerlOp>(ReferenceEqualityComparer.Instance);
    var statements = new List<IrStmt>();

    this.ExtractParameters(nodes, package, name, parameters, consumed, statements);
    this.LowerStatements(nodes, statements, consumed, allowImplicitReturn: true);

    if (statements.Count == 0 || statements[^1] is not IrReturn)
      statements.Add(new IrReturn(this._currentReturnType == IrType.Void ? null : DefaultExpr(this._currentReturnType)));

    var irName = this.IrFunctionName(package, name);
    return new(irName, isPublic, this._currentReturnType, parameters, [.. this._locals], new(statements));
  }

  private Dictionary<string, HashSet<string>> CollectRuntimeExports() {
    var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    if (this._tree.Program is null)
      return result;

    var currentPkg = "main";
    var nodes = this._tree.Program.Name == "leave" ? this._tree.Program.Children : [this._tree.Program];
    foreach (var node in nodes) {
      if (node.Name == "nextstate" && !string.IsNullOrEmpty(node.Package)) {
        currentPkg = node.Package;
        continue;
      }
      if (node is { Name: "aassign", Children: [var rhs, var lhs] }) {
        var exportGv = Descendants(lhs).FirstOrDefault(o => o.Name == "gv" && o.Symbol is "EXPORT" or "EXPORT_OK");
        if (exportGv is null)
          continue;
        var targetPkg = exportGv.Stash ?? currentPkg;
        if (!result.TryGetValue(targetPkg, out var set))
          result[targetPkg] = set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Descendants(rhs).Where(o => o.Name == "const" && o.Constant?.Kind == "string"))
          set.Add(c.Constant!.Value);
      }
    }
    return result;
  }

  private string IrFunctionName(string package, string name) {
    if (package == "main" || this._subs.Values.Count(e => e.Name == name) == 1)
      return name;
    return $"{package.Replace("::", "_", StringComparison.Ordinal)}_{name}";
  }

  private void ExtractParameters(PerlOp[] nodes, string package, string name, List<IrLocal> parameters, HashSet<PerlOp> consumed, List<IrStmt> statements) {
    var nonState = nodes.Where(n => n.Name != "nextstate").ToList();
    if (nonState.Count == 0)
      return;

    var first = nonState[0];

    // Form 1: `my ($a, $b) = @_;`
    if (first is { Name: "aassign", Children: [
      { Name: "null", Children: [{ Name: "padrange" }, { Name: "rv2av", Children: [{ Name: "gv", Symbol: "_" }] }] },
      { Name: "null", Children: var targets }
    ] } && targets.Length >= 2 && targets[0].Name == "pushmark") {
      consumed.Add(first);
      foreach (var target in targets.Skip(1)) {
        if (target.Name is "padsv" or "padav") {
          var lex = this.LexicalName(target.Target);
          var ptype = this.ResolveParamType(package, name, lex, target.Name == "padav");
          var param = new IrLocal(lex, ptype);
          if (!this._variables.TryAdd(target.Target, param))
            throw this.Fail(PerlFrontend.SubUnsupported, $"duplicate parameter {lex}");
          parameters.Add(param);
        }
      }
      return;
    }

    // Form 2: `use feature 'signatures'` (`argcheck` / `null` wrapping `lineseq` with `multiparam` / `argelem`)
    if (first is { Name: "null", Children: [{ Name: "lineseq", Children: var sigOps }] } &&
        sigOps.Any(o => o.Name is "multiparam" or "argcheck" or "argelem")) {
      consumed.Add(first);
      foreach (var op in sigOps) {
        if (op.Name == "multiparam" || op.Name == "argelem") {
          // Bind parameters from pad names in lexical order of this sub's initial pad slots.
          for (var slot = 1; slot < this._currentSub.Lexicals.Length; ++slot) {
            var raw = this._currentSub.Lexicals[slot];
            if (raw is { Length: > 1 } && raw[0] == '$' && this._contracts.TryGetType(package, name, raw[1..], out var ct)) {
              var param = new IrLocal(raw[1..], ct);
              if (this._variables.TryAdd(slot, param))
                parameters.Add(param);
            }
          }
        }
      }
      return;
    }

    // Form 3: Leading `my $a = shift;` / `my $a = shift @_;` statements
    foreach (var stmt in nonState) {
      if (stmt is { Name: "padsv_store", Children: [{ Name: "shift" }, ..] }) {
        consumed.Add(stmt);
        var lex = this.LexicalName(stmt.Target);
        var ptype = this.ResolveParamType(package, name, lex, false);
        var param = new IrLocal(lex, ptype);
        this._variables[stmt.Target] = param;
        parameters.Add(param);
      } else {
        break;
      }
    }
    if (parameters.Count > 0)
      return;

    // Form 4: Direct `$_[N]` reads (synthesize parameters $arg0, $arg1, ... from contracts or max index)
    var argIndices = new SortedSet<int>();
    foreach (var op in Descendants(this._currentSub.Body!)) {
      if (op.Name == "aelemfast" && op.Symbol == "_")
        argIndices.Add(op.Private);
    }
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

  private IrLocal DeclareLocal(string name, IrType type, int? padSlot = null) {
    var unique = name;
    var suffix = 2;
    while (this._locals.Any(l => l.Name == unique) || this._variables.Values.Any(l => l.Name == unique))
      unique = $"{name}_{suffix++}";
    var local = new IrLocal(unique, type);
    this._locals.Add(local);
    if (padSlot is { } slot)
      this._variables[slot] = local;
    return local;
  }

  private IrLocal TempLocal(IrType type, string prefix = "_t")
    => this.DeclareLocal($"{prefix}{++this._tempCounter}", type);

  private string LexicalName(int slot)
    => this._currentSub.Lexicals[slot]![1..];

  private char LexicalSigil(int slot)
    => slot > 0 && slot < this._currentSub.Lexicals.Length && this._currentSub.Lexicals[slot] is { Length: > 0 } raw ? raw[0] : '\0';

  private IrExpr LowerCall(PerlOp call) {
    // Method call (`method_named` / `method_super` / `method`)
    var children = UnwrapList(call.Children);
    if (children.LastOrDefault() is { Name: "method_named" or "method_super" or "method" } methodOp)
      return this.LowerMethodCall(children, methodOp);

    // Closure invocation `$f->(ARGS)` where last child is `null` -> `padsv` holding an anonymous sub.
    if (children.Length >= 1 && children[^1] is { Name: "null", Children: [{ Name: "padsv", Target: var fnSlot }] } &&
        this._variables.TryGetValue(fnSlot, out var fnLocal) && fnLocal.Type.IsFunction) {
      var sig = fnLocal.Type.Signature!;
      var rawArgs = StripMarks(children[..^1]).Select(this.LiftExpr).ToArray();
      var argExprs = rawArgs.Select((a, idx) => idx < sig.Parameters.Count ? this.ToType(a, sig.Parameters[idx]) : a).ToArray();
      return new IrInvoke(sig.Result, new IrLocalRef(fnLocal), argExprs);
    }

    if (!TryExtractCallTarget(call, out var calleeName, out var argOps))
      throw this.Fail(PerlFrontend.SubUnsupported, "dynamic subroutine call");

    // Core-module call map (#436): List::Util, POSIX, Carp
    var shortName = calleeName.Contains("::", StringComparison.Ordinal)
      ? calleeName[(calleeName.LastIndexOf("::", StringComparison.Ordinal) + 2)..]
      : calleeName;

    if (shortName is "croak" or "confess") {
      var msg = argOps.Length > 0 ? this.LiftExpr(argOps[0]) : new IrStrConst("error");
      return new IrThrowExpression(IrType.I64, new IrNew(IrType.Object(IrExceptionClass.Exception),
        IrExceptionClass.Constructor(IrExceptionClass.Exception, 1), [msg.Type == IrType.String ? msg : new IrStrConst(shortName)]));
    }
    if (shortName is "floor" or "ceil") {
      var arg = this.ToType(this.LiftExpr(argOps[0]), IrType.F64);
      return new IrIntrinsic(shortName == "floor" ? IrIntrinsicOp.Floor : IrIntrinsicOp.Ceiling, IrType.F64, [arg]);
    }
    if (shortName is "min" or "max" && argOps.Length >= 2) {
      var acc = this.LiftExpr(argOps[0]);
      var op = shortName == "min" ? IrIntrinsicOp.Min : IrIntrinsicOp.Max;
      for (var i = 1; i < argOps.Length; ++i) {
        var next = this.LiftExpr(argOps[i]);
        var common = this._bounds.CommonType(acc, next, m => this.Fail(PerlFrontend.ScalarUnsupported, m));
        acc = new IrIntrinsic(op, common, [PerlBounds.Promote(acc, common), PerlBounds.Promote(next, common)]);
      }
      return acc;
    }
    if (shortName is "sum" or "sum0") {
      if (argOps.Length == 0)
        return new IrConst(IrType.I64, 0);
      var acc = this.LiftExpr(argOps[0]);
      for (var i = 1; i < argOps.Length; ++i) {
        var next = this.LiftExpr(argOps[i]);
        var common = this._bounds.CommonType(acc, next, m => this.Fail(PerlFrontend.ScalarUnsupported, m));
        acc = new IrBinary(IrBinaryOp.Add, PerlBounds.Promote(acc, common), PerlBounds.Promote(next, common));
      }
      return acc;
    }

    // Call to another subroutine in this module.
    var lookupKey = this._subs.ContainsKey(calleeName) ? calleeName
      : this._subs.ContainsKey($"{this._currentPackage}::{shortName}") ? $"{this._currentPackage}::{shortName}"
      : null;

    if (lookupKey is null)
      throw this.Fail(PerlFrontend.UnresolvedModule, calleeName);

    var entry = this._subs[lookupKey];
    var irTarget = this.IrFunctionName(entry.Package, entry.Name);
    var retType = this._signatures[lookupKey].ReturnType;
    var args = argOps.Select(this.LiftExpr).ToArray();
    return new IrCall(irTarget, retType, args);
  }

  private static bool TryExtractCallTarget(PerlOp call, out string name, out PerlOp[] args) {
    name = "";
    args = [];
    var kids = UnwrapList(call.Children);
    if (kids.Length == 0)
      return false;

    var last = kids[^1];
    var gv = last.Children.Length == 1 ? last.Children[0] : last;
    if (gv is { Name: "gv", Symbol: { } sym }) {
      var stash = gv.Stash;
      name = string.IsNullOrEmpty(stash) || stash == "main" ? sym : $"{stash}::{sym}";
      args = StripMarks(kids[..^1]);
      return true;
    }
    return false;
  }

  private static PerlOp[] UnwrapList(PerlOp[] children)
    => children is [{ Name: "null", Children: var inner }] ? inner : children;

  private static PerlOp[] StripMarks(PerlOp[] children)
    => children.Where(c => c.Name is not ("pushmark" or "padrange" or "stub") && !(c.Name == "null" && c.Children.Length == 0)).ToArray();

  private static IEnumerable<PerlOp> Descendants(PerlOp op)
    => op.Children.SelectMany(Descendants).Prepend(op);

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
