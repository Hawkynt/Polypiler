using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

internal sealed partial class PerlLowerer {

  private sealed record MooseAttribute(string Name, string Is, string Isa, string Writer, string Reader, string DefaultKind);

  private sealed class PerlClassInfo(string package, IrClassType irClass, PerlClassKind kind) {
    public string Package { get; } = package;
    public IrClassType IrClass { get; } = irClass;
    public PerlClassKind Kind { get; } = kind;
    public Dictionary<string, IrField> FieldsByName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IrField> InsideOutHashes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IrField> MooseWriters { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IrField> MooseReaders { get; } = new(StringComparer.Ordinal);
    public List<string> MooseExtends { get; } = [];
    public List<string> MooseWith { get; } = [];
    public List<MooseAttribute> MooseAttributes { get; } = [];
  }

  private enum PerlClassKind { BlessedHash, InsideOut, Moose }

  private readonly Dictionary<string, PerlClassInfo> _classesByPkg = new(StringComparer.Ordinal);
  private readonly Dictionary<string, IrInterfaceType> _interfacesByPkg = new(StringComparer.Ordinal);
  private readonly Dictionary<IrClassType, PerlClassInfo> _classesByIr = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<string> _classSubKeys = new(StringComparer.Ordinal);
  private readonly Dictionary<string, (IrType ReturnType, List<IrType> ParamTypes)> _classMemberSigs = new(StringComparer.Ordinal);

  private PerlClassInfo? _currentClassInfo;
  private bool _loweringConstructor;
  private IrLocal? _currentSelf;
  private TopLevelDirectives _directives = new();

  private sealed class TopLevelDirectives {
    public Dictionary<string, List<string>> Isa { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Extends { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> With { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Requires { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<MooseAttribute>> Attributes { get; } = new(StringComparer.Ordinal);
  }

  private static void AppendStrings(Dictionary<string, List<string>> map, string pkg, IEnumerable<PerlExpr> args) {
    if (!map.TryGetValue(pkg, out var list))
      map[pkg] = list = [];
    list.AddRange(args.SelectMany(a => ConstantStrings(a)));
  }

  private static IEnumerable<PerlStatement> TopLevelOf(PerlDocument document) {
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
    return Flatten(document.Statements);
  }

  /// <summary>The class directives of the program's top level: <c>our @ISA = ...</c>, and Moose's <c>extends</c>, <c>with</c>, <c>requires</c>, <c>has</c>.</summary>
  private static TopLevelDirectives CollectTopLevelClassDirectives(PerlDocument document) {
    var d = new TopLevelDirectives();
    foreach (var statement in TopLevelOf(document)) {
      if (statement is not PerlExpressionStatement { Modifier: null, Expression: var expression })
        continue;
      var currentPkg = statement.Package;
      if (expression is PerlAssign { Operator: "=", Target: var target, Value: var value }) {
        var isa = Unparen(target) switch {
          PerlDeclaration { Variables: [{ Sigil: '@', Name: "ISA" }] } => currentPkg,
          PerlVariable { Sigil: '@' } v when ShortName(v.Name) == "ISA" => v.Name.Contains("::", StringComparison.Ordinal) ? v.Name[..v.Name.LastIndexOf("::", StringComparison.Ordinal)] : currentPkg,
          _ => null
        };
        if (isa is not null)
          AppendStrings(d.Isa, isa, [value]);
        continue;
      }
      if (expression is not PerlCall { Builtin: false } call)
        continue;
      var shortFn = ShortName(call.Name);
      var args = FlattenDirectiveArgs(call.Arguments ?? []);
      switch (shortFn) {
        case "extends": AppendStrings(d.Extends, currentPkg, args); break;
        case "with": AppendStrings(d.With, currentPkg, args); break;
        case "requires": AppendStrings(d.Requires, currentPkg, args); break;
        case "has" when args.Count > 0 && ConstantStrings(args[0]).FirstOrDefault() is { } attrName:
          if (!d.Attributes.TryGetValue(currentPkg, out var attributes))
            d.Attributes[currentPkg] = attributes = [];
          attributes.Add(ParseMooseAttribute(attrName, args));
          break;
      }
    }
    return d;
  }

  private static MooseAttribute ParseMooseAttribute(string attrName, List<PerlExpr> flatArgs) {
    string isMode = "rw", isa = "", writer = "", reader = "", defKind = "none";
    for (var i = 1; i + 1 < flatArgs.Count; i += 2) {
      var key = ConstantStrings(flatArgs[i]).FirstOrDefault();
      var valOp = flatArgs[i + 1];
      var text = ConstantStrings(valOp).FirstOrDefault();
      switch (key) {
        case "is": isMode = text!; break;
        case "isa": isa = text!; break;
        case "writer": writer = text!; break;
        case "reader": reader = text!; break;
        case "default":
          defKind = valOp switch {
            PerlNumber { Value.IsIntegral: true } => "integer",
            PerlNumber => "float",
            PerlString => "string",
            _ => defKind
          };
          break;
      }
    }
    return new MooseAttribute(attrName, isMode, isa, writer, reader, defKind);
  }

  private static List<PerlExpr> FlattenDirectiveArgs(IEnumerable<PerlExpr> args) {
    var result = new List<PerlExpr>();
    void Walk(PerlExpr op) {
      if (op is PerlList list) {
        foreach (var c in list.Items)
          Walk(c);
        return;
      }
      if (op is PerlWords words) {
        result.AddRange(words.Words.Select(w => new PerlString(w) { Line = op.Line }));
        return;
      }
      result.Add(op);
    }
    foreach (var a in args)
      Walk(a);
    return result;
  }

  /// <summary>The packages that <c>use Moose</c> (classes) or <c>use Moose::Role</c> (roles), in the order they first do.</summary>
  private static Dictionary<string, string> MoosePackages(PerlDocument document) {
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var use in PerlWalk.Descendants(document).OfType<PerlUse>())
      if (use is { No: false, Module: "Moose" or "Moose::Role", EmptyImports: false } && !result.ContainsKey(use.Package))
        result[use.Package] = use.Module == "Moose" ? "class" : "role";
    return result;
  }

  private static Dictionary<string, IrTypeDeclaration> PreScanClassShells(PerlDocument document, Dictionary<string, PerlPackageInfo> packages) {
    var directives = CollectTopLevelClassDirectives(document);
    var shells = new Dictionary<string, IrTypeDeclaration>(StringComparer.Ordinal);
    foreach (var (pkg, kind) in MoosePackages(document)) {
      var name = CleanTypeName(pkg);
      shells[pkg] = kind == "role" ? new IrInterfaceType(name) : new IrClassType(name);
    }

    foreach (var (pkg, p) in packages) {
      if (pkg == "main" || shells.ContainsKey(pkg))
        continue;
      if (DetectNonMooseClassKind(p, directives.Isa.GetValueOrDefault(pkg)) is not null)
        shells[pkg] = new IrClassType(CleanTypeName(pkg));
    }

    return shells;
  }

  private static string CleanTypeName(string pkg)
    => pkg.Replace("::", "_", StringComparison.Ordinal);

  private static PerlClassKind? DetectNonMooseClassKind(PerlPackageInfo pkg, List<string>? runtimeIsa = null) {
    if (pkg.Functions.TryGetValue("new", out var ctor)) {
      foreach (var node in PerlWalk.Descendants(ctor.Body!, intoSubs: false)) {
        if (node is not PerlCall { Builtin: true, Name: "bless" } bless)
          continue;
        var target = bless.Arguments is [var first, ..] ? first : null;
        if (target is PerlUnary { Operator: "\\" } reference && PerlWalk.Descendants(reference).Any(c => c is PerlVariable { Sigil: '$', Lexical: not null }))
          return PerlClassKind.InsideOut;
        return PerlClassKind.BlessedHash;
      }
    }
    if (pkg.Isa.Any(i => i != "Exporter") || (runtimeIsa?.Any(i => i != "Exporter") ?? false))
      return PerlClassKind.BlessedHash;
    return null;
  }

  private void InitializeClassesAndInterfaces(Dictionary<string, IrTypeDeclaration> shells, Dictionary<string, PerlPackageInfo> packages, List<IrFunction> functions) {
    this._directives = CollectTopLevelClassDirectives(this._document);
    var moose = MoosePackages(this._document);
    foreach (var (pkg, decl) in shells) {
      if (decl is IrInterfaceType iface) {
        this._interfacesByPkg[pkg] = iface;
        this._types.Add(iface);
      } else {
        var cls = (IrClassType)decl;
        var isMoose = moose.ContainsKey(pkg);
        var kind = isMoose ? PerlClassKind.Moose : DetectNonMooseClassKind(packages[pkg], this._directives.Isa.GetValueOrDefault(pkg))!.Value;
        var info = new PerlClassInfo(pkg, cls, kind);
        if (isMoose) {
          info.MooseExtends.AddRange(this._directives.Extends.GetValueOrDefault(pkg) ?? []);
          info.MooseWith.AddRange(this._directives.With.GetValueOrDefault(pkg) ?? []);
          info.MooseAttributes.AddRange(this._directives.Attributes.GetValueOrDefault(pkg) ?? []);
        }
        this._classesByPkg[pkg] = info;
        this._classesByIr[cls] = info;
        this._types.Add(cls);
      }
    }

    // Define Moose roles (interfaces)
    foreach (var (pkg, iface) in this._interfacesByPkg) {
      var allWith = this._directives.With.GetValueOrDefault(pkg) ?? [];
      var allReqs = this._directives.Requires.GetValueOrDefault(pkg) ?? [];
      var baseIfaces = new List<IrInterfaceType>();
      foreach (var w in allWith) {
        if (!this._interfacesByPkg.TryGetValue(w, out var baseIface))
          throw new Refusal(PerlFrontend.ObjectSystemUnsupported, $"role {w}", 1, cause: "OBJ-INTERFACES");
        baseIfaces.Add(baseIface);
      }
      var methods = new List<IrMethod>();
      foreach (var req in allReqs) {
        var fnName = $"{iface.Name}__{req}";
        var retType = this._contracts.TryGetType(pkg, req, "return", out var r) ? r : IrType.I64;
        var selfLocal = new IrLocal("self", IrType.Object(iface));
        functions.Add(new IrFunction(fnName, false, retType, [selfLocal], [], new([])));
        methods.Add(new IrMethod(req, fnName, false) { Dispatch = IrDispatch.Abstract });
      }
      iface.DefineMembers(methods, [], baseIfaces);
    }

    // Validate & wire class inheritance + roles
    foreach (var (pkg, info) in this._classesByPkg) {
      var bases = new List<string>();
      if (info.Kind == PerlClassKind.Moose)
        bases.AddRange(info.MooseExtends);
      if (packages.TryGetValue(pkg, out var p))
        bases.AddRange(p.Isa.Where(i => i != "Exporter"));
      if (this._directives.Isa.TryGetValue(pkg, out var rIsa))
        bases.AddRange(rIsa.Where(i => i != "Exporter"));
      bases = [.. bases.Distinct(StringComparer.Ordinal)];

      IrClassType? baseCls = null;
      if (bases.Count > 1)
        throw new Refusal(PerlFrontend.ObjectSystemUnsupported, $"multiple inheritance in {pkg}", 1, cause: "OBJ-VIRTUAL");
      if (bases.Count == 1) {
        if (!this._classesByPkg.TryGetValue(bases[0], out var baseInfo))
          throw new Refusal(PerlFrontend.ObjectSystemUnsupported, $"@{pkg}::ISA ({bases[0]})", 1, cause: "OBJ-VIRTUAL");
        baseCls = baseInfo.IrClass;
      }

      var ifaces = new List<IrInterfaceType>();
      foreach (var w in info.MooseWith) {
        if (!this._interfacesByPkg.TryGetValue(w, out var iface))
          throw new Refusal(PerlFrontend.ObjectSystemUnsupported, $"with '{w}'", 1, cause: "OBJ-INTERFACES");
        ifaces.Add(iface);
      }
      info.IrClass.Inherit(baseCls, ifaces);
    }

    // Order base classes before derived classes so base fields/methods exist first
    var ordered = new List<PerlClassInfo>(this._classesByPkg.Count);
    var visited = new HashSet<PerlClassInfo>(ReferenceEqualityComparer.Instance);
    void Visit(PerlClassInfo info) {
      if (!visited.Add(info))
        return;
      if (info.IrClass.Base is { } b)
        Visit(this._classesByIr[b]);
      ordered.Add(info);
    }
    foreach (var info in this._classesByPkg.Values)
      Visit(info);

    var pendingCtors = new List<(PerlClassInfo Info, IrConstructor Ctor, PerlSub? Sub)>();
    var pendingMethods = new List<(PerlClassInfo Info, IrMethod Method, string Name, PerlSub Sub)>();
    foreach (var info in ordered) {
      packages.TryGetValue(info.Package, out var pkg);
      this.DeclareClassMembers(info, pkg, pendingCtors, pendingMethods);
    }

    foreach (var (info, ctor, sub) in pendingCtors)
      this._classMemberSigs[ctor.Function] = (IrType.Void, this.PredictCtorParamTypes(info, sub));
    foreach (var (info, method, subName, sub) in pendingMethods)
      this._classMemberSigs[method.Function] = (
        this._contracts.TryGetType(info.Package, subName, "return", out var r) ? r : IrType.I64,
        this.PredictMethodParamTypes(info, subName, sub));
    foreach (var (pkg, iface) in this._interfacesByPkg)
      foreach (var m in iface.Methods)
        this._classMemberSigs[m.Function] = (
          this._contracts.TryGetType(pkg, m.Name, "return", out var r) ? r : IrType.I64,
          [IrType.Object(iface)]);

    foreach (var (info, ctor, sub) in pendingCtors) {
      var fn = sub is not null
        ? this.LowerClassConstructor(info, ctor, sub)
        : this.SynthesizeClassConstructor(info, ctor);
      functions.Add(fn);
      this._loweredFunctions[fn.Name] = fn;
    }
    foreach (var (info, method, subName, sub) in pendingMethods) {
      var fn = this.LowerClassMethod(info, method, subName, sub);
      functions.Add(fn);
      this._loweredFunctions[fn.Name] = fn;
    }
  }

  private List<IrType> PredictCtorParamTypes(PerlClassInfo info, PerlSub? sub) {
    var list = new List<IrType> { IrType.Object(info.IrClass) };
    if (sub is null) {
      if (info.IrClass.Base is { Constructors: [var bc, ..] })
        list.AddRange(this._classMemberSigs[bc.Function].ParamTypes.Skip(1));
      foreach (var f in info.IrClass.Fields)
        list.Add(f.Type);
      return list;
    }
    this.CollectPredictedParams(info.Package, "new", sub, list);
    return list;
  }

  private List<IrType> PredictMethodParamTypes(PerlClassInfo info, string subName, PerlSub sub) {
    var list = new List<IrType> { IrType.Object(info.IrClass) };
    this.CollectPredictedParams(info.Package, subName, sub, list);
    return list;
  }

  private void CollectPredictedParams(string pkg, string subName, PerlSub sub, List<IrType> list) {
    var targets = MatchParamUnpack(sub.Body!.Statements, out _);
    if (targets is null)
      return;
    foreach (var target in targets.Variables.Skip(1))
      list.Add(this.ResolveParamType(pkg, subName, target.Name, target.Sigil == '@'));
  }

  private void DeclareClassMembers(
    PerlClassInfo info,
    PerlPackageInfo? pkg,
    List<(PerlClassInfo, IrConstructor, PerlSub?)> pendingCtors,
    List<(PerlClassInfo, IrMethod, string, PerlSub)> pendingMethods) {
    var fields = new List<IrField>();
    if (info.Kind == PerlClassKind.Moose) {
      foreach (var attr in info.MooseAttributes) {
        var ftype = this.ResolveMooseAttrType(info.Package, attr);
        var isReadOnly = attr.Is == "ro" && string.IsNullOrEmpty(attr.Writer);
        var visibility = attr.Name.StartsWith('_') || attr.Is == "bare" ? IrVisibility.Private : IrVisibility.Public;
        var field = new IrField(attr.Name, ftype, isReadOnly) { Visibility = visibility };
        fields.Add(field);
        info.FieldsByName[attr.Name] = field;
        var reader = string.IsNullOrEmpty(attr.Reader) ? attr.Name : attr.Reader;
        info.MooseReaders[reader] = field;
        if (attr.Is != "ro")
          info.MooseWriters[attr.Name] = field;
        if (!string.IsNullOrEmpty(attr.Writer))
          info.MooseWriters[attr.Writer] = field;
      }
    } else if (info.Kind == PerlClassKind.InsideOut) {
      this.DiscoverInsideOutFields(info, pkg!, fields);
    } else {
      this.DiscoverBlessedHashFields(info, pkg!, fields);
    }
    info.IrClass.Define(fields);

    var ctors = new List<IrConstructor>();
    var methods = new List<IrMethod>();

    if (pkg is not null && pkg.Functions.TryGetValue("new", out var newSub)) {
      var ctorFn = $"{info.IrClass.Name}__ctor";
      var ctor = new IrConstructor(ctorFn) { SkipsBase = info.IrClass.Base is not null };
      ctors.Add(ctor);
      pendingCtors.Add((info, ctor, newSub));
      this._classSubKeys.Add($"{info.Package}::new");
    } else {
      var ctorFn = $"{info.IrClass.Name}__ctor";
      var ctor = new IrConstructor(ctorFn);
      ctors.Add(ctor);
      pendingCtors.Add((info, ctor, null));
    }

    var baseMethods = new Dictionary<string, IrMethod>(StringComparer.Ordinal);
    for (var b = info.IrClass.Base; b is not null; b = b.Base)
      foreach (var m in b.Methods)
        baseMethods.TryAdd(m.Name, m);

    var ifaceMethods = new Dictionary<string, List<IrMethod>>(StringComparer.Ordinal);
    foreach (var iface in info.IrClass.AllInterfaces)
      foreach (var m in iface.Methods) {
        if (!ifaceMethods.TryGetValue(m.Name, out var list))
          ifaceMethods[m.Name] = list = [];
        list.Add(m);
      }

    if (pkg is not null) {
      foreach (var (subName, sub) in pkg.Functions) {
        this._classSubKeys.Add($"{info.Package}::{subName}");
        if (subName is "new" or "DESTROY")
          continue;
        var fnName = $"{info.IrClass.Name}__{subName}";
        var overrides = baseMethods.GetValueOrDefault(subName);
        var impls = ifaceMethods.GetValueOrDefault(subName) ?? [];
        var dispatch = overrides is not null
          ? IrDispatch.Override
          : (impls.Count > 0 || this.IsOverriddenInDerived(info.IrClass, subName))
            ? IrDispatch.Virtual
            : IrDispatch.Instance;
        var vis = subName.StartsWith('_') ? IrVisibility.Private : IrVisibility.Public;
        var method = new IrMethod(subName, fnName, false) {
          Visibility = vis,
          Dispatch = dispatch,
          Overrides = overrides,
          Implements = impls
        };
        methods.Add(method);
        pendingMethods.Add((info, method, subName, sub));
      }
    }

    info.IrClass.DefineMembers(ctors, methods, [], []);
  }

  private bool IsOverriddenInDerived(IrClassType baseCls, string methodName) {
    foreach (var other in this._classesByPkg.Values)
      if (!ReferenceEquals(other.IrClass, baseCls) && other.IrClass.DerivesFrom(baseCls)
          && this._packages.TryGetValue(other.Package, out var p) && p.Functions.ContainsKey(methodName))
        return true;
    return false;
  }

  private IrType ResolveMooseAttrType(string pkg, MooseAttribute attr) {
    if (this._contracts.TryGetType(pkg, "field", attr.Name, out var ct))
      return ct;
    return attr.Isa switch {
      "Int" => IrType.I64,
      "Num" => IrType.F64,
      "Str" => IrType.String,
      "Bool" => IrType.Bool,
      _ => attr.DefaultKind switch {
        "float" => IrType.F64,
        "string" => IrType.String,
        _ => IrType.I64
      }
    };
  }

  private IrType ResolveDiscoveredFieldType(string pkg, string name)
    => this._contracts.TryGetType(pkg, "field", name, out var ct) ? ct
      : this._contracts.TryGetType(pkg, "new", name.TrimStart('_'), out var pt) ? pt
      : IrType.I64;

  private void DiscoverInsideOutFields(PerlClassInfo info, PerlPackageInfo pkg, List<IrField> fields) {
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (_, sub) in pkg.Functions) {
      foreach (var node in PerlWalk.Descendants(sub.Body!, intoSubs: false)) {
        if (node is not PerlVariable { Sigil: '%', Lexical: { Kind: not PerlDeclarator.Our } } hash)
          continue;
        var rawName = hash.Name;
        if (!seen.Add(rawName))
          continue;
        var cleanName = rawName.StartsWith("_f_", StringComparison.Ordinal) ? rawName[3..] : rawName;
        var ftype = this.ResolveDiscoveredFieldType(info.Package, cleanName);
        var visibility = rawName.StartsWith('_') && !pkg.Functions.ContainsKey(cleanName.TrimStart('_'))
          ? IrVisibility.Private
          : IrVisibility.Public;
        var field = new IrField(cleanName, ftype, false) { Visibility = visibility };
        fields.Add(field);
        info.FieldsByName[cleanName] = field;
        info.InsideOutHashes[rawName] = field;
      }
    }
  }

  /// <summary>Whether an element is one link of perl's multideref op (a constant or plain-variable key on a variable or a reference held in one).</summary>
  private static bool IsSingleDeref(PerlElement element) => element.Aggregate is PerlVariable or PerlDeref { Reference: PerlVariable { Sigil: '$' } };

  private void DiscoverBlessedHashFields(PerlClassInfo info, PerlPackageInfo pkg, List<IrField> fields) {
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var baseField in info.IrClass.Base?.AllFields ?? [])
      seen.Add(baseField.Name);

    void AddKey(string key) {
      if (!seen.Add(key))
        return;
      var ftype = this.ResolveDiscoveredFieldType(info.Package, key);
      var vis = key.StartsWith('_') ? IrVisibility.Private : IrVisibility.Public;
      var field = new IrField(key, ftype, false) { Visibility = vis };
      fields.Add(field);
      info.FieldsByName[key] = field;
    }

    foreach (var (_, sub) in pkg.Functions) {
      var chained = new HashSet<PerlNode>(ReferenceEqualityComparer.Instance);
      foreach (var node in PerlWalk.Descendants(sub.Body!, intoSubs: false)) {
        if (node is PerlElement { Aggregate: PerlDeref { Reference: PerlElement inner } })
          chained.Add(inner);
        if (node is PerlAnonHash anon) {
          for (var i = 0; i + 1 < anon.Items.Count; i += 2)
            if (anon.Items[i] is PerlString key)
              AddKey(key.Value);
        } else if (node is PerlElement { Hash: true } element && !chained.Contains(element) && IsSingleDeref(element) && element.Index is PerlString or PerlNumber) {
          AddKey(element.Index is PerlString s ? s.Value : ((PerlNumber)element.Index).Value.ToString());
        }
      }
    }
  }

  private IrFunction SynthesizeClassConstructor(PerlClassInfo info, IrConstructor ctor) {
    var selfLocal = new IrLocal("self", IrType.Object(info.IrClass));
    var parameters = new List<IrLocal> { selfLocal };
    var stmts = new List<IrStmt>();
    if (info.IrClass.Base is { Constructors: [var baseCtor, ..] }) {
      var baseParams = this._loweredFunctions[baseCtor.Function].Parameters;
      var baseArgs = new List<IrExpr> { new IrLocalRef(selfLocal) };
      for (var i = 1; i < baseParams.Count; ++i) {
        var bp = new IrLocal(baseParams[i].Name, baseParams[i].Type);
        parameters.Add(bp);
        baseArgs.Add(new IrLocalRef(bp));
      }
      stmts.Add(new IrExprStmt(new IrCall(baseCtor.Function, IrType.Void, baseArgs)));
    }
    foreach (var field in info.IrClass.Fields) {
      var p = new IrLocal(field.Name.TrimStart('_'), field.Type);
      parameters.Add(p);
      stmts.Add(new IrFieldStore(new IrLocalRef(selfLocal), field, new IrLocalRef(p)));
    }
    stmts.Add(new IrReturn(null));
    return new(ctor.Function, false, IrType.Void, parameters, [], new(stmts));
  }

  private IrFunction LowerClassConstructor(PerlClassInfo info, IrConstructor ctor, PerlSub sub) {
    this.ResetSubState(info.Package, "new", sub, IrType.Void);
    this._line = 1;
    this._currentClassInfo = info;
    this._loweringConstructor = true;
    var selfLocal = new IrLocal("self", IrType.Object(info.IrClass));
    this._currentSelf = selfLocal;

    var nodes = sub.Body!.Statements;
    var parameters = new List<IrLocal> { selfLocal };
    var consumed = new HashSet<PerlStatement>(ReferenceEqualityComparer.Instance);
    var statements = new List<IrStmt>();

    this.ExtractClassParameters(nodes, info.Package, "new", skipFirstAsClass: true, selfLocal, parameters, consumed);
    this.LowerStatements(nodes, statements, consumed, PerlWant.Unknown, allowImplicitReturn: false);
    if (statements.Count == 0 || statements[^1] is not IrReturn)
      statements.Add(new IrReturn(null));

    this._currentClassInfo = null;
    this._loweringConstructor = false;
    this._currentSelf = null;
    return new(ctor.Function, false, IrType.Void, parameters, [.. this._locals], new(statements));
  }

  private IrFunction LowerClassMethod(PerlClassInfo info, IrMethod method, string subName, PerlSub sub) {
    var retType = this._contracts.TryGetType(info.Package, subName, "return", out var r) ? r : IrType.I64;
    this.ResetSubState(info.Package, subName, sub, retType);
    this._line = 1;
    this._currentClassInfo = info;
    var selfLocal = new IrLocal("self", IrType.Object(info.IrClass));
    this._currentSelf = selfLocal;

    var nodes = sub.Body!.Statements;
    var parameters = new List<IrLocal> { selfLocal };
    var consumed = new HashSet<PerlStatement>(ReferenceEqualityComparer.Instance);
    var statements = new List<IrStmt>();

    this.ExtractClassParameters(nodes, info.Package, subName, skipFirstAsClass: false, selfLocal, parameters, consumed);
    this.LowerStatements(nodes, statements, consumed, PerlWant.Unknown, allowImplicitReturn: true);
    if (statements.Count == 0 || statements[^1] is not IrReturn)
      statements.Add(new IrReturn(retType == IrType.Void ? null : DefaultExpr(retType)));

    this._currentClassInfo = null;
    this._currentSelf = null;
    return new(method.Function, false, retType, parameters, [.. this._locals], new(statements));
  }

  private void ExtractClassParameters(IReadOnlyList<PerlStatement> nodes, string package, string name, bool skipFirstAsClass, IrLocal selfLocal, List<IrLocal> parameters, HashSet<PerlStatement> consumed) {
    var targets = MatchParamUnpack(nodes, out var assignOp);
    if (targets is null)
      return;
    consumed.Add(assignOp);
    if (!skipFirstAsClass)
      this._variables[targets.Variables[0].Lexical!] = selfLocal;
    foreach (var target in targets.Variables.Skip(1)) {
      var ptype = this.ResolveParamType(package, name, target.Name, target.Sigil == '@');
      var param = new IrLocal(target.Name, ptype);
      this._variables[target.Lexical!] = param;
      parameters.Add(param);
    }
  }

  private bool TryLowerBlessStatement(PerlExpr node, List<IrStmt> statements) {
    if (!this._loweringConstructor)
      return false;

    if (node is PerlAssign { Operator: "=", Target: PerlDeclaration { Parenthesized: false, Variables: [{ Lexical: { } slot }] }, Value: PerlCall { Builtin: true, Name: "bless" } bless }) {
      this._variables[slot] = this._currentSelf!;
      this.LowerBlessInit(bless, statements);
      return true;
    }
    if (node is PerlJump { Keyword: "return", Value: var value }) {
      var vals = value is PerlList { Parenthesized: false } list ? list.Items : value is null ? [] : [value];
      if (vals is [PerlCall { Builtin: true, Name: "bless" } returned]) {
        this.LowerBlessInit(returned, statements);
        statements.Add(new IrReturn(null));
        return true;
      }
    }
    return false;
  }

  private void LowerBlessInit(PerlCall blessOp, List<IrStmt> statements) {
    if (blessOp.Arguments is not [PerlAnonHash target, ..])
      return;
    var pairs = target.Items;
    for (var i = 0; i + 1 < pairs.Count; i += 2) {
      if (pairs[i] is PerlString key) {
        var field = this._currentClassInfo!.IrClass.AllFields.First(f => f.Name == key.Value);
        var val = this.ToType(this.LiftExpr(pairs[i + 1], PerlWant.List), field.Type);
        statements.Add(new IrFieldStore(new IrLocalRef(this._currentSelf!), field, val));
      }
    }
  }

  private bool TryResolveInsideOutAccess(PerlElement op, out IrExpr objExpr, out IrField field) {
    objExpr = null!;
    field = null!;
    if (this._currentClassInfo?.Kind != PerlClassKind.InsideOut)
      return false;
    if (op is not { Hash: true, Aggregate: PerlVariable { Sigil: '%', Lexical: not null } hash } || !this._currentClassInfo.InsideOutHashes.TryGetValue(hash.Name, out field!))
      return false;
    var key = op.Index;
    var inner = key switch {
      PerlInterpolation { Parts: [var only] } => only,
      PerlCall { Arguments: [var argument, ..] } => argument,
      _ => key
    };
    objExpr = this.LiftExpr(inner, PerlWant.Scalar);
    return objExpr.Type.IsObject;
  }

  private bool TryLowerInsideOutRead(PerlElement op, out IrExpr expr) {
    if (this.TryResolveInsideOutAccess(op, out var objExpr, out var field)) {
      expr = new IrFieldLoad(objExpr, field);
      return true;
    }
    expr = null!;
    return false;
  }

  private bool TryLowerInsideOutStore(PerlElement lhsOp, IrExpr value, List<IrStmt> statements) {
    if (!this.TryResolveInsideOutAccess(lhsOp, out var objExpr, out var field))
      return false;
    statements.Add(new IrFieldStore(objExpr, field, this.ToType(value, field.Type)));
    return true;
  }

  private IrExpr LowerMethodCall(PerlMethodCall call) {
    var methodName = call.Name;
    if (call.DynamicName is not null || string.IsNullOrEmpty(methodName))
      throw this.Fail(PerlFrontend.ObjectSystemUnsupported, "->method", cause: "OBJ-CLASSES");
    var isSuper = methodName.StartsWith("SUPER::", StringComparison.Ordinal);
    if (isSuper)
      methodName = methodName[7..];
    var argOps = call.Arguments;

    // 1. Constructor call: `ClassName->new(args...)`
    if (methodName == "new" && call.Invocant is PerlString { } recvName && this._classesByPkg.TryGetValue(recvName.Value, out var newInfo)) {
      var ctor = newInfo.IrClass.Constructors[0];
      var ctorParamTypes = this._classMemberSigs[ctor.Function].ParamTypes;
      var ctorArgs = this.BindConstructorArgs(newInfo, argOps, ctorParamTypes);
      return new IrNew(IrType.Object(newInfo.IrClass), ctor, ctorArgs);
    }

    var receiver = this.LiftExpr(call.Invocant, PerlWant.Scalar);

    // 2. `$obj->isa('ClassName')` / `$obj->does('RoleName')`
    if (methodName == "isa" && argOps.Count == 1 && this.Fold(argOps[0])?.Text is { } isaPkg && this._classesByPkg.TryGetValue(isaPkg, out var isaInfo))
      return new IrTypeTest(receiver, IrType.Object(isaInfo.IrClass));

    if (methodName == "does" && argOps.Count == 1 && this.Fold(argOps[0])?.Text is { } rolePkg && this._interfacesByPkg.TryGetValue(rolePkg, out var roleIface))
      return new IrTypeTest(receiver, IrType.Object(roleIface));

    // 3. `$self->SUPER::method(...)`
    if (isSuper && receiver.Type.ClassType is { Base: { } baseCls }) {
      var baseMethod = FindMethodInLineage(baseCls, methodName)
        ?? throw this.Fail(PerlFrontend.ObjectSystemUnsupported, $"->SUPER::{methodName}", cause: "OBJ-VIRTUAL");
      var (baseRet, baseParams) = this._classMemberSigs[baseMethod.Function];
      var baseArgs = argOps.Select((a, idx) => this.ToType(this.LiftExpr(a, PerlWant.List), baseParams[idx + 1])).ToArray();
      return new IrMethodCall(baseRet, baseMethod, receiver, baseArgs) { Kind = IrMethodCallKind.Base };
    }

    // 4. Interface call
    if (receiver.Type.InterfaceType is { } iface) {
      var im = iface.AllMethods.FirstOrDefault(m => m.Name == methodName)
        ?? throw this.Fail(PerlFrontend.ObjectSystemUnsupported, $"->{methodName}", cause: "OBJ-INTERFACES");
      var (imRet, imParams) = this._classMemberSigs[im.Function];
      var imArgs = argOps.Select((a, idx) => this.ToType(this.LiftExpr(a, PerlWant.List), imParams[idx + 1])).ToArray();
      return new IrMethodCall(imRet, im, receiver, imArgs) { Kind = IrMethodCallKind.Interface };
    }

    // 5. Class method or Moose attribute reader
    if (receiver.Type.ClassType is { } recvCls) {
      if (argOps.Count == 0 && this.FindMooseReader(recvCls, methodName) is { } readField)
        return new IrFieldLoad(receiver, readField);

      if (FindMethodInLineage(recvCls, methodName) is { } m) {
        var (mRet, mParams) = this._classMemberSigs[m.Function];
        var mArgs = argOps.Select((a, idx) => this.ToType(this.LiftExpr(a, PerlWant.List), mParams[idx + 1])).ToArray();
        var kind = m.IsVirtual ? IrMethodCallKind.Virtual : IrMethodCallKind.Direct;
        return new IrMethodCall(mRet, m, receiver, mArgs) { Kind = kind };
      }
    }

    throw this.Fail(PerlFrontend.ObjectSystemUnsupported, isSuper ? $"->SUPER::{methodName}" : $"->{methodName}", cause: "OBJ-CLASSES");
  }

  private bool TryLowerMooseSetterStmt(PerlExpr node, List<IrStmt> statements) {
    if (node is not PerlMethodCall { Name: { } methodName, DynamicName: null, Arguments: [var argument] } call)
      return false;
    var receiver = this.LiftExpr(call.Invocant, PerlWant.Scalar);
    if (receiver.Type.ClassType is { } recvCls && this.FindMooseWriter(recvCls, methodName) is { } field) {
      var val = this.ToType(this.LiftExpr(argument, PerlWant.List), field.Type);
      statements.Add(new IrFieldStore(receiver, field, val));
      return true;
    }
    return false;
  }

  private IrExpr[] BindConstructorArgs(PerlClassInfo info, IReadOnlyList<PerlExpr> argOps, List<IrType> ctorParamTypes) {
    var expectedArgs = ctorParamTypes.Count - 1;
    // Support Moose `Class->new(field => val, ...)` named arguments as well as positional arguments
    if (info.Kind == PerlClassKind.Moose && argOps.Count > 0 && argOps.Count % 2 == 0
        && argOps[0] is PerlString { Value: var firstKey }
        && info.IrClass.AllFields.Any(f => f.Name.TrimStart('_') == firstKey || f.Name == firstKey)) {
      var named = new Dictionary<string, PerlExpr>(StringComparer.Ordinal);
      for (var i = 0; i + 1 < argOps.Count; i += 2)
        if (argOps[i] is PerlString { Value: var k })
          named[k] = argOps[i + 1];

      var allFields = info.IrClass.AllFields;
      var result = new IrExpr[expectedArgs];
      for (var i = 0; i < expectedArgs; ++i) {
        var f = allFields[i];
        var clean = f.Name.TrimStart('_');
        result[i] = named.TryGetValue(f.Name, out var op) || named.TryGetValue(clean, out op)
          ? this.ToType(this.LiftExpr(op, PerlWant.List), ctorParamTypes[i + 1])
          : DefaultExpr(ctorParamTypes[i + 1]);
      }
      return result;
    }

    var positional = new IrExpr[expectedArgs];
    for (var i = 0; i < expectedArgs; ++i)
      positional[i] = i < argOps.Count
        ? this.ToType(this.LiftExpr(argOps[i], PerlWant.List), ctorParamTypes[i + 1])
        : DefaultExpr(ctorParamTypes[i + 1]);
    return positional;
  }

  private static IrMethod? FindMethodInLineage(IrClassType cls, string name) {
    foreach (var type in cls.Lineage)
      if (type.Methods.FirstOrDefault(m => m.Name == name) is { } m)
        return m;
    return null;
  }

  private IrField? FindMooseReader(IrClassType cls, string name) {
    foreach (var type in cls.Lineage)
      if (this._classesByIr[type].MooseReaders.TryGetValue(name, out var f))
        return f;
    return null;
  }

  private IrField? FindMooseWriter(IrClassType cls, string name) {
    foreach (var type in cls.Lineage)
      if (this._classesByIr[type].MooseWriters.TryGetValue(name, out var f))
        return f;
    return null;
  }

}
