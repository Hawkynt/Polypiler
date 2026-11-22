using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

internal sealed partial class PerlLowerer {

  private sealed class PerlClassInfo(string package, IrClassType irClass, PerlClassKind kind) {
    public string Package { get; } = package;
    public IrClassType IrClass { get; } = irClass;
    public PerlClassKind Kind { get; } = kind;
    public Dictionary<string, IrField> FieldsByName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IrField> InsideOutHashes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IrField> MooseWriters { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IrField> MooseReaders { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IrMethod> MethodsByName { get; } = new(StringComparer.Ordinal);
    public PerlMoosePackage? MooseSpec { get; init; }
  }

  private enum PerlClassKind { BlessedHash, InsideOut, Moose }

  private readonly Dictionary<string, PerlClassInfo> _classesByPkg = new(StringComparer.Ordinal);
  private readonly Dictionary<string, IrInterfaceType> _interfacesByPkg = new(StringComparer.Ordinal);
  private readonly Dictionary<IrClassType, PerlClassInfo> _classesByIr = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<string> _classSubKeys = new(StringComparer.Ordinal);
  private readonly Dictionary<string, (IrType ReturnType, List<IrType> ParamTypes)> _classMemberSigs = new(StringComparer.Ordinal);

  // Per-class-member lowering state
  private PerlClassInfo? _currentClassInfo;
  private bool _loweringConstructor;
  private IrLocal? _currentSelf;
  private TopLevelDirectives _directives = new();

  private sealed class TopLevelDirectives {
    public Dictionary<string, List<string>> Isa { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Extends { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> With { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Requires { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<PerlMooseAttribute>> Attributes { get; } = new(StringComparer.Ordinal);
  }

  private static string? StringConst(PerlOp op)
    => op.Constant?.Kind == "string" ? op.Constant.Value : null;

  private static PerlOp[] BodyNodes(PerlSubroutine sub) {
    var ch = sub.Body!.Children;
    return ch[0].Name == "lineseq" ? ch[0].Children : ch;
  }

  private static void AppendStringArgs(Dictionary<string, List<string>> map, string pkg, List<PerlOp> args) {
    if (!map.TryGetValue(pkg, out var list))
      map[pkg] = list = [];
    foreach (var a in args)
      if (StringConst(a) is { } s)
        list.Add(s);
  }

  private static TopLevelDirectives CollectTopLevelClassDirectives(PerlTree tree) {
    var d = new TopLevelDirectives();
    var currentPkg = "main";
    var nodes = tree.Program!.Children;
    foreach (var node in nodes) {
      if (node.Name == "nextstate") {
        if (!string.IsNullOrEmpty(node.Package))
          currentPkg = node.Package;
        continue;
      }
      if (node.Name == "aassign" && node.Children.Length == 2) {
        var isaGv = Descendants(node.Children[1]).FirstOrDefault(o => o.Name == "gv" && o.Symbol == "ISA");
        if (isaGv is not null) {
          var targetPkg = isaGv.Stash!;
          if (!d.Isa.TryGetValue(targetPkg, out var list))
            d.Isa[targetPkg] = list = [];
          foreach (var o in Descendants(node.Children[0]))
            if (StringConst(o) is { } s)
              list.Add(s);
        }
        continue;
      }
      if (node.Name == "entersub" && TryExtractCallTarget(node, out var fn, out var args)) {
        var idx = fn.LastIndexOf("::", StringComparison.Ordinal);
        var shortFn = idx >= 0 ? fn[(idx + 2)..] : fn;
        if (shortFn is not ("extends" or "with" or "requires" or "has"))
          continue;
        var flatArgs = FlattenDirectiveArgs(args);
        switch (shortFn) {
          case "extends": AppendStringArgs(d.Extends, currentPkg, flatArgs); break;
          case "with": AppendStringArgs(d.With, currentPkg, flatArgs); break;
          case "requires": AppendStringArgs(d.Requires, currentPkg, flatArgs); break;
          case "has":
            if (StringConst(flatArgs[0]) is { } attrName) {
              if (!d.Attributes.TryGetValue(currentPkg, out var list))
                d.Attributes[currentPkg] = list = [];
              list.Add(ParseMooseAttribute(attrName, flatArgs));
            }
            break;
        }
      }
    }
    return d;
  }

  private static PerlMooseAttribute ParseMooseAttribute(string attrName, List<PerlOp> flatArgs) {
    string isMode = "rw", isa = "", writer = "", reader = "", defKind = "none", defVal = "";
    for (var i = 1; i + 1 < flatArgs.Count; i += 2) {
      var key = StringConst(flatArgs[i]);
      var valOp = flatArgs[i + 1];
      switch (key) {
        case "is": isMode = StringConst(valOp)!; break;
        case "isa": isa = StringConst(valOp)!; break;
        case "writer": writer = StringConst(valOp)!; break;
        case "reader": reader = StringConst(valOp)!; break;
        case "default":
          if (valOp.Constant is { } defC) {
            defKind = defC.Kind;
            defVal = defC.Value;
          }
          break;
      }
    }
    return new PerlMooseAttribute {
      Name = attrName, Is = isMode, Isa = isa, Writer = writer, Reader = reader, DefaultKind = defKind, DefaultValue = defVal
    };
  }

  private static List<PerlOp> FlattenDirectiveArgs(PerlOp[] args) {
    var result = new List<PerlOp>();
    void Walk(PerlOp op) {
      if (op.Name is "list" or "null") {
        foreach (var c in op.Children)
          Walk(c);
        return;
      }
      result.Add(op);
    }
    foreach (var a in args)
      Walk(a);
    return result;
  }

  private static Dictionary<string, IrTypeDeclaration> PreScanClassShells(PerlTree tree) {
    var directives = CollectTopLevelClassDirectives(tree);
    var shells = new Dictionary<string, IrTypeDeclaration>(StringComparer.Ordinal);
    foreach (var (pkg, moose) in tree.Moose) {
      var name = CleanTypeName(pkg);
      shells[pkg] = moose.Kind == "role" ? new IrInterfaceType(name) : new IrClassType(name);
    }

    foreach (var (pkg, p) in tree.Packages) {
      if (pkg == "main" || shells.ContainsKey(pkg))
        continue;
      if (DetectNonMooseClassKind(p, directives.Isa.GetValueOrDefault(pkg)) is not null)
        shells[pkg] = new IrClassType(CleanTypeName(pkg));
    }

    return shells;
  }

  private static string CleanTypeName(string pkg)
    => pkg.Replace("::", "_", StringComparison.Ordinal);

  private static PerlClassKind? DetectNonMooseClassKind(PerlPackage pkg, List<string>? runtimeIsa = null) {
    if (pkg.Functions.TryGetValue("new", out var ctor)) {
      foreach (var op in Descendants(ctor.Body!)) {
        if (op.Name != "bless")
          continue;
        var target = UnwrapSingleNull(StripMarks(op.Children)[0]);
        if (target.Name is "srefgen" or "refgen" && Descendants(target).Any(c => c.Name == "padsv"))
          return PerlClassKind.InsideOut;
        return PerlClassKind.BlessedHash;
      }
    }
    // Derived blessed-hash / inside-out subclass that inherits `new` from a parent in the same file
    if (pkg.Isa.Any(i => i != "Exporter") || (runtimeIsa?.Any(i => i != "Exporter") ?? false))
      return PerlClassKind.BlessedHash;
    return null;
  }

  private void InitializeClassesAndInterfaces(Dictionary<string, IrTypeDeclaration> shells, Dictionary<string, PerlPackage> packages, List<IrFunction> functions) {
    this._directives = CollectTopLevelClassDirectives(this._tree);
    foreach (var (pkg, decl) in shells) {
      if (decl is IrInterfaceType iface) {
        this._interfacesByPkg[pkg] = iface;
        this._types.Add(iface);
      } else {
        var cls = (IrClassType)decl;
        var isMoose = this._tree.Moose.TryGetValue(pkg, out var mooseSpec);
        if (isMoose) {
          mooseSpec = mooseSpec! with {
            Extends = [.. mooseSpec.Extends, .. this._directives.Extends.GetValueOrDefault(pkg) ?? []],
            With = [.. mooseSpec.With, .. this._directives.With.GetValueOrDefault(pkg) ?? []],
            Requires = [.. mooseSpec.Requires, .. this._directives.Requires.GetValueOrDefault(pkg) ?? []],
            Attributes = [.. mooseSpec.Attributes, .. this._directives.Attributes.GetValueOrDefault(pkg) ?? []]
          };
        }
        var kind = isMoose ? PerlClassKind.Moose : DetectNonMooseClassKind(packages[pkg], this._directives.Isa.GetValueOrDefault(pkg))!.Value;
        var info = new PerlClassInfo(pkg, cls, kind) { MooseSpec = mooseSpec };
        this._classesByPkg[pkg] = info;
        this._classesByIr[cls] = info;
        this._types.Add(cls);
      }
    }

    // Define Moose roles (interfaces)
    foreach (var (pkg, iface) in this._interfacesByPkg) {
      var moose = this._tree.Moose[pkg];
      var allWith = moose.With.Concat(this._directives.With.GetValueOrDefault(pkg) ?? []).ToList();
      var allReqs = moose.Requires.Concat(this._directives.Requires.GetValueOrDefault(pkg) ?? []).ToList();
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
      if (info.MooseSpec is not null)
        bases.AddRange(info.MooseSpec.Extends);
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
      if (info.MooseSpec is not null) {
        foreach (var w in info.MooseSpec.With) {
          if (!this._interfacesByPkg.TryGetValue(w, out var iface))
            throw new Refusal(PerlFrontend.ObjectSystemUnsupported, $"with '{w}'", 1, cause: "OBJ-INTERFACES");
          ifaces.Add(iface);
        }
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

    // Pass 1: define fields, constructors, and methods on every class
    var pendingCtors = new List<(PerlClassInfo Info, IrConstructor Ctor, PerlSubroutine? Sub)>();
    var pendingMethods = new List<(PerlClassInfo Info, IrMethod Method, string Name, PerlSubroutine Sub)>();
    foreach (var info in ordered) {
      packages.TryGetValue(info.Package, out var pkg);
      this.DeclareClassMembers(info, pkg, pendingCtors, pendingMethods);
    }

    // Pass 2: pre-compute constructor & method signatures so cross-calls resolve in any order
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

    // Pass 3: lower every constructor and method body
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

  private List<IrType> PredictCtorParamTypes(PerlClassInfo info, PerlSubroutine? sub) {
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

  private List<IrType> PredictMethodParamTypes(PerlClassInfo info, string subName, PerlSubroutine sub) {
    var list = new List<IrType> { IrType.Object(info.IrClass) };
    this.CollectPredictedParams(info.Package, subName, sub, list);
    return list;
  }

  private static PerlOp[]? MatchParamUnpack(PerlOp[] nodes, out PerlOp assignOp) {
    assignOp = null!;
    var first = nodes.FirstOrDefault(o => o.Name != "nextstate");
    if (first?.Name != "aassign")
      return null;
    var rhs = first.Children[0].Children;
    if (rhs.Length != 2 || rhs[0].Name != "padrange" || rhs[1].Children[0].Symbol != "_")
      return null;
    assignOp = first;
    return first.Children[1].Children;
  }

  private void CollectPredictedParams(string pkg, string subName, PerlSubroutine sub, List<IrType> list) {
    var targets = MatchParamUnpack(BodyNodes(sub), out _);
    if (targets is null)
      return;
    foreach (var target in targets.Skip(2))
      list.Add(this.ResolveParamType(pkg, subName, sub.Lexicals[target.Target]![1..], target.Name == "padav"));
  }

  private void DeclareClassMembers(
    PerlClassInfo info,
    PerlPackage? pkg,
    List<(PerlClassInfo, IrConstructor, PerlSubroutine?)> pendingCtors,
    List<(PerlClassInfo, IrMethod, string, PerlSubroutine)> pendingMethods) {
    var fields = new List<IrField>();
    if (info.Kind == PerlClassKind.Moose) {
      foreach (var attr in info.MooseSpec!.Attributes) {
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
      foreach (var (subName, sub) in pkg.Functions.OrderBy(e => e.Key, StringComparer.Ordinal)) {
        this._classSubKeys.Add($"{info.Package}::{subName}");
        if (subName is "new" or "DESTROY")
          continue;
        var fnName = $"{info.IrClass.Name}__{subName}";
        var overrides = baseMethods.GetValueOrDefault(subName);
        var impls = ifaceMethods.GetValueOrDefault(subName) ?? [];
        var dispatch = overrides is not null
          ? IrDispatch.Override
          : (impls.Count > 0 || this.IsOverriddenInDerived(info.IrClass, subName, this._tree.Packages))
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
        info.MethodsByName[subName] = method;
        pendingMethods.Add((info, method, subName, sub));
      }
    }

    info.IrClass.DefineMembers(ctors, methods, [], []);
  }

  private bool IsOverriddenInDerived(IrClassType baseCls, string methodName, Dictionary<string, PerlPackage> packages) {
    foreach (var other in this._classesByPkg.Values)
      if (!ReferenceEquals(other.IrClass, baseCls) && other.IrClass.DerivesFrom(baseCls)
          && packages.TryGetValue(other.Package, out var p) && p.Functions.ContainsKey(methodName))
        return true;
    return false;
  }

  private IrType ResolveMooseAttrType(string pkg, PerlMooseAttribute attr) {
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

  private void DiscoverInsideOutFields(PerlClassInfo info, PerlPackage pkg, List<IrField> fields) {
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (_, sub) in pkg.Functions) {
      foreach (var op in Descendants(sub.Body!)) {
        string? hashLex = null;
        if (op.Name == "padhv")
          hashLex = sub.Lexicals[op.Target];
        else if (op.Name == "multideref" && op.Deref is [var step] && step.Source == "padhv")
          hashLex = sub.Lexicals[step.Target];

        if (hashLex is null)
          continue;
        var rawName = hashLex[1..];
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

  private void DiscoverBlessedHashFields(PerlClassInfo info, PerlPackage pkg, List<IrField> fields) {
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
      foreach (var op in Descendants(sub.Body!)) {
        if (op.Name == "anonhash") {
          var pairs = StripMarks(op.Children);
          for (var i = 0; i + 1 < pairs.Length; i += 2)
            if (StringConst(pairs[i]) is { } key)
              AddKey(key);
        } else if (op.Name == "multideref" && op.Deref is [var step] && step.Kind == "hash" && step.IndexValue is { } k) {
          AddKey(k);
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

  private IrFunction LowerClassConstructor(PerlClassInfo info, IrConstructor ctor, PerlSubroutine sub) {
    this.ResetSubState(info.Package, "new", sub, IrType.Void);
    this._currentClassInfo = info;
    this._loweringConstructor = true;
    var selfLocal = new IrLocal("self", IrType.Object(info.IrClass));
    this._currentSelf = selfLocal;

    var nodes = BodyNodes(sub);
    var parameters = new List<IrLocal> { selfLocal };
    var consumed = new HashSet<PerlOp>(ReferenceEqualityComparer.Instance);
    var statements = new List<IrStmt>();

    this.ExtractClassParameters(nodes, info.Package, "new", skipFirstAsClass: true, selfLocal, parameters, consumed);
    this.LowerStatements(nodes, statements, consumed, allowImplicitReturn: false);
    if (statements[^1] is not IrReturn)
      statements.Add(new IrReturn(null));

    this._currentClassInfo = null;
    this._loweringConstructor = false;
    this._currentSelf = null;
    return new(ctor.Function, false, IrType.Void, parameters, [.. this._locals], new(statements));
  }

  private IrFunction LowerClassMethod(PerlClassInfo info, IrMethod method, string subName, PerlSubroutine sub) {
    var retType = this._contracts.TryGetType(info.Package, subName, "return", out var r) ? r : IrType.I64;
    this.ResetSubState(info.Package, subName, sub, retType);
    this._currentClassInfo = info;
    var selfLocal = new IrLocal("self", IrType.Object(info.IrClass));
    this._currentSelf = selfLocal;

    var nodes = BodyNodes(sub);
    var parameters = new List<IrLocal> { selfLocal };
    var consumed = new HashSet<PerlOp>(ReferenceEqualityComparer.Instance);
    var statements = new List<IrStmt>();

    this.ExtractClassParameters(nodes, info.Package, subName, skipFirstAsClass: false, selfLocal, parameters, consumed);
    this.LowerStatements(nodes, statements, consumed, allowImplicitReturn: true);
    if (statements[^1] is not IrReturn)
      statements.Add(new IrReturn(retType == IrType.Void ? null : DefaultExpr(retType)));

    this._currentClassInfo = null;
    this._currentSelf = null;
    return new(method.Function, false, retType, parameters, [.. this._locals], new(statements));
  }

  private void ResetSubState(string package, string name, PerlSubroutine sub, IrType retType) {
    this._currentSub = sub;
    this._currentPackage = package;
    this._currentSubName = name;
    this._currentReturnType = retType;
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
  }

  private void ExtractClassParameters(PerlOp[] nodes, string package, string name, bool skipFirstAsClass, IrLocal selfLocal, List<IrLocal> parameters, HashSet<PerlOp> consumed) {
    var targets = MatchParamUnpack(nodes, out var assignOp);
    if (targets is null)
      return;
    consumed.Add(assignOp);
    if (!skipFirstAsClass)
      this._variables[targets[1].Target] = selfLocal;
    foreach (var target in targets.Skip(2)) {
      var lex = this.LexicalName(target.Target);
      var ptype = this.ResolveParamType(package, name, lex, target.Name == "padav");
      var param = new IrLocal(lex, ptype);
      this._variables[target.Target] = param;
      parameters.Add(param);
    }
  }

  private bool TryLowerBlessStatement(PerlOp node, List<IrStmt> statements) {
    if (!this._loweringConstructor)
      return false;

    if (node.Name == "padsv_store" && node.Children[0].Name == "bless") {
      this._variables[node.Target] = this._currentSelf!;
      this.LowerBlessInit(node.Children[0], statements);
      return true;
    }
    if (node.Name == "return") {
      var vals = StripMarks(node.Children);
      if (vals.Length == 1 && vals[0].Name == "bless") {
        this.LowerBlessInit(vals[0], statements);
        statements.Add(new IrReturn(null));
        return true;
      }
    }
    return false;
  }

  private void LowerBlessInit(PerlOp blessOp, List<IrStmt> statements) {
    var target = UnwrapSingleNull(StripMarks(blessOp.Children)[0]);
    if (target.Name != "anonhash")
      return;
    var pairs = StripMarks(target.Children);
    for (var i = 0; i + 1 < pairs.Length; i += 2) {
      if (StringConst(pairs[i]) is { } key) {
        var field = this._currentClassInfo!.IrClass.AllFields.First(f => f.Name == key);
        var val = this.ToType(this.LiftExpr(pairs[i + 1]), field.Type);
        statements.Add(new IrFieldStore(new IrLocalRef(this._currentSelf!), field, val));
      }
    }
  }

  private bool TryResolveInsideOutAccess(PerlOp op, out IrExpr objExpr, out IrField field) {
    objExpr = null!;
    field = null!;
    if (this._currentClassInfo?.Kind != PerlClassKind.InsideOut)
      return false;
    var info = this._currentClassInfo;

    if (op.Name == "helem" && op.Children[0].Name == "padhv"
        && this.ResolveInsideOutHashField(info, op.Children[0].Target, out field)
        && this.TryUnwrapInsideOutKey(op.Children[1], out objExpr))
      return true;

    if (op.Name == "multideref" && op.Deref is [var step]
        && step.Source == "padhv"
        && this.ResolveInsideOutHashField(info, step.Target, out field)) {
      objExpr = new IrLocalRef(this._variables[step.IndexTarget]);
      return true;
    }
    return false;
  }

  private bool TryLowerInsideOutRead(PerlOp op, out IrExpr expr) {
    if (this.TryResolveInsideOutAccess(op, out var objExpr, out var field)) {
      expr = new IrFieldLoad(objExpr, field);
      return true;
    }
    expr = null!;
    return false;
  }

  private bool TryLowerInsideOutStore(PerlOp lhsOp, IrExpr value, List<IrStmt> statements) {
    if (!this.TryResolveInsideOutAccess(lhsOp, out var objExpr, out var field))
      return false;
    statements.Add(new IrFieldStore(objExpr, field, this.ToType(value, field.Type)));
    return true;
  }

  private bool ResolveInsideOutHashField(PerlClassInfo info, int padSlot, out IrField field) {
    var lex = this._currentSub.Lexicals[padSlot]!;
    return info.InsideOutHashes.TryGetValue(lex[1..], out field!);
  }

  private bool TryUnwrapInsideOutKey(PerlOp keyOp, out IrExpr objExpr) {
    keyOp = UnwrapSingleNull(keyOp);
    var inner = keyOp.Name == "stringify"
      ? StripMarks(keyOp.Children)[0]
      : keyOp.Name == "refaddr"
        ? keyOp.Children[0]
        : TryExtractCallTarget(keyOp, out _, out var args) ? args[0] : keyOp;
    objExpr = this.LiftExpr(inner);
    return objExpr.Type.IsObject;
  }

  private IrExpr LowerMethodCall(PerlOp[] children, PerlOp methodOp) {
    var methodName = methodOp.Symbol;
    if (methodOp.Name == "method" || string.IsNullOrEmpty(methodName))
      throw this.Fail(PerlFrontend.ObjectSystemUnsupported, "->method", cause: "OBJ-CLASSES");

    var argsAndRecv = StripMarks(children[..^1]);
    var recvOp = argsAndRecv[0];
    var argOps = argsAndRecv[1..];

    // 1. Constructor call: `ClassName->new(args...)`
    if (methodName == "new" && StringConst(recvOp) is { } pkgName && this._classesByPkg.TryGetValue(pkgName, out var newInfo)) {
      var ctor = newInfo.IrClass.Constructors[0];
      var ctorParamTypes = this._classMemberSigs[ctor.Function].ParamTypes;
      var ctorArgs = this.BindConstructorArgs(newInfo, argOps, ctorParamTypes);
      return new IrNew(IrType.Object(newInfo.IrClass), ctor, ctorArgs);
    }

    var receiver = this.LiftExpr(recvOp);

    // 2. `$obj->isa('ClassName')` / `$obj->does('RoleName')`
    if (methodName == "isa" && argOps.Length == 1 && StringConst(argOps[0]) is { } isaPkg && this._classesByPkg.TryGetValue(isaPkg, out var isaInfo))
      return new IrTypeTest(receiver, IrType.Object(isaInfo.IrClass));

    if (methodName == "does" && argOps.Length == 1 && StringConst(argOps[0]) is { } rolePkg && this._interfacesByPkg.TryGetValue(rolePkg, out var roleIface))
      return new IrTypeTest(receiver, IrType.Object(roleIface));

    // 3. `$self->SUPER::method(...)` (`method_super`)
    if (methodOp.Name == "method_super" && receiver.Type.ClassType is { Base: { } baseCls }) {
      var baseMethod = FindMethodInLineage(baseCls, methodName)
        ?? throw this.Fail(PerlFrontend.ObjectSystemUnsupported, $"->SUPER::{methodName}", cause: "OBJ-VIRTUAL");
      var (baseRet, baseParams) = this._classMemberSigs[baseMethod.Function];
      var baseArgs = argOps.Select((a, idx) => this.ToType(this.LiftExpr(a), baseParams[idx + 1])).ToArray();
      return new IrMethodCall(baseRet, baseMethod, receiver, baseArgs) { Kind = IrMethodCallKind.Base };
    }

    // 4. Interface call
    if (receiver.Type.InterfaceType is { } iface) {
      var im = iface.AllMethods.FirstOrDefault(m => m.Name == methodName)
        ?? throw this.Fail(PerlFrontend.ObjectSystemUnsupported, $"->{methodName}", cause: "OBJ-INTERFACES");
      var (imRet, imParams) = this._classMemberSigs[im.Function];
      var imArgs = argOps.Select((a, idx) => this.ToType(this.LiftExpr(a), imParams[idx + 1])).ToArray();
      return new IrMethodCall(imRet, im, receiver, imArgs) { Kind = IrMethodCallKind.Interface };
    }

    // 5. Class method or Moose attribute reader
    if (receiver.Type.ClassType is { } recvCls) {
      if (argOps.Length == 0 && this.FindMooseReader(recvCls, methodName) is { } readField)
        return new IrFieldLoad(receiver, readField);

      if (FindMethodInLineage(recvCls, methodName) is { } m) {
        var (mRet, mParams) = this._classMemberSigs[m.Function];
        var mArgs = argOps.Select((a, idx) => this.ToType(this.LiftExpr(a), mParams[idx + 1])).ToArray();
        var kind = m.IsVirtual ? IrMethodCallKind.Virtual : IrMethodCallKind.Direct;
        return new IrMethodCall(mRet, m, receiver, mArgs) { Kind = kind };
      }
    }

    throw this.Fail(PerlFrontend.ObjectSystemUnsupported, $"->{methodName}", cause: "OBJ-CLASSES");
  }

  private bool TryLowerMooseSetterStmt(PerlOp node, List<IrStmt> statements) {
    if (node.Name != "entersub")
      return false;
    var children = UnwrapList(node.Children);
    if (children.Length < 3 || children[^1].Name != "method_named")
      return false;
    var methodName = children[^1].Symbol!;
    var argsAndRecv = StripMarks(children[..^1]);
    if (argsAndRecv.Length != 2)
      return false;
    var receiver = this.LiftExpr(argsAndRecv[0]);
    if (receiver.Type.ClassType is { } recvCls && this.FindMooseWriter(recvCls, methodName) is { } field) {
      var val = this.ToType(this.LiftExpr(argsAndRecv[1]), field.Type);
      statements.Add(new IrFieldStore(receiver, field, val));
      return true;
    }
    return false;
  }

  private IrExpr[] BindConstructorArgs(PerlClassInfo info, PerlOp[] argOps, List<IrType> ctorParamTypes) {
    var expectedArgs = ctorParamTypes.Count - 1;
    // Support Moose `Class->new(field => val, ...)` named arguments as well as positional arguments
    if (info.Kind == PerlClassKind.Moose && argOps.Length > 0 && argOps.Length % 2 == 0
        && StringConst(argOps[0]) is { } firstKey
        && info.IrClass.AllFields.Any(f => f.Name.TrimStart('_') == firstKey || f.Name == firstKey)) {
      var named = new Dictionary<string, PerlOp>(StringComparer.Ordinal);
      for (var i = 0; i + 1 < argOps.Length; i += 2)
        if (StringConst(argOps[i]) is { } k)
          named[k] = argOps[i + 1];

      var allFields = info.IrClass.AllFields;
      var result = new IrExpr[expectedArgs];
      for (var i = 0; i < expectedArgs; ++i) {
        var f = allFields[i];
        var clean = f.Name.TrimStart('_');
        result[i] = named.TryGetValue(f.Name, out var op) || named.TryGetValue(clean, out op)
          ? this.ToType(this.LiftExpr(op), ctorParamTypes[i + 1])
          : DefaultExpr(ctorParamTypes[i + 1]);
      }
      return result;
    }

    var positional = new IrExpr[expectedArgs];
    for (var i = 0; i < expectedArgs; ++i)
      positional[i] = i < argOps.Length
        ? this.ToType(this.LiftExpr(argOps[i]), ctorParamTypes[i + 1])
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
