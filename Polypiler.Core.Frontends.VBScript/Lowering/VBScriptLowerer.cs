using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Polypiler.Core.Frontends.VBScript.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VBScript.Lowering;

internal sealed partial class VBScriptLowerer {

  private readonly string _file;
  private readonly VbsProgram _program;
  private readonly VBScriptImportOptions _options;
  private readonly List<Diagnostic> _diagnostics;

  private VBScriptContracts _contracts = null!;

  // Module-level symbols (case-insensitive).
  private readonly Dictionary<string, IrModuleConst> _moduleConsts = new(StringComparer.OrdinalIgnoreCase);
  private readonly List<IrModuleConst> _moduleConstList = [];
  private readonly Dictionary<string, IrGlobal> _globals = new(StringComparer.OrdinalIgnoreCase);
  private readonly List<IrGlobal> _globalList = [];
  private readonly Dictionary<string, ProcInfo> _procedures = new(StringComparer.OrdinalIgnoreCase);
  private readonly List<ProcInfo> _procedureList = [];
  private readonly Dictionary<string, ClassInfo> _classes = new(StringComparer.OrdinalIgnoreCase);
  private readonly List<ClassInfo> _classList = [];

  // On-demand helpers and Err globals.
  private readonly Dictionary<string, IrFunction> _helpers = new(StringComparer.Ordinal);
  private readonly List<IrFunction> _helperList = [];
  private IrGlobal? _errNumberGlobal;
  private IrGlobal? _errDescGlobal;

  private VBScriptLowerer(string file, VbsProgram program, VBScriptImportOptions options, List<Diagnostic> diagnostics) {
    this._file = file;
    this._program = program;
    this._options = options;
    this._diagnostics = diagnostics;
  }

  public static IrModule? Lower(string file, VbsProgram program, VBScriptImportOptions options, List<Diagnostic> diagnostics)
    => new VBScriptLowerer(file, program, options, diagnostics).Run();

  private sealed class ProcInfo(VbsProcedureDecl decl, string irName, string contractKey, ClassInfo? owner) {
    public VbsProcedureDecl Decl { get; } = decl;
    public string IrName { get; } = irName;
    public string ContractKey { get; } = contractKey;
    public ClassInfo? Owner { get; } = owner;
    public IrType[] ParamTypes { get; } = new IrType[decl.Parameters.Count];
    public IrType? ReturnType { get; set; }
    public Dictionary<string, IrType> LocalTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DeclaredLocals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IrMethod? Method { get; set; }
    public IrConstructor? Constructor { get; set; }
  }

  private sealed class ClassInfo(VbsClassDecl decl, IrClassType irClass) {
    public VbsClassDecl Decl { get; } = decl;
    public IrClassType IrClass { get; } = irClass;
    public Dictionary<string, IrType> FieldTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> FieldPublic { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VbsDimVar> FieldVars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IrField> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProcInfo> Methods { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProcInfo> Getters { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProcInfo> Setters { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IrProperty> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ProcInfo? Initializer { get; set; }
    public IrConstructor DefaultCtor { get; set; } = null!;
  }

  private sealed class ProcContext(
    ProcInfo proc,
    IrLocal? receiver,
    IrLocal? resultLocal,
    Dictionary<string, IrLocal> locals,
    List<IrLocal> localList) {
    public ProcInfo Proc { get; } = proc;
    public IrLocal? Receiver { get; } = receiver;
    public IrLocal? ResultLocal { get; } = resultLocal;
    public Dictionary<string, IrLocal> Locals { get; } = locals;
    public List<IrLocal> LocalList { get; } = localList;
    public Dictionary<string, IrExpr> LocalConsts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnassignedLocals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Stack<VbsExitKind> LoopStack { get; } = new();
    public bool ResumeNext { get; set; }
    private int _tempId;

    public IrLocal NewTemp(IrType type, string prefix = "__t") {
      var local = new IrLocal($"{prefix}{this._tempId++}", type);
      this.LocalList.Add(local);
      return local;
    }
  }

  private IrModule? Run() {
    var topConsts = new List<VbsConstDecl>();
    var topDims = new List<VbsDimDecl>();
    var topProcs = new List<VbsProcedureDecl>();
    var topClasses = new List<VbsClassDecl>();
    var topStmts = new List<VbsStmt>();

    foreach (var stmt in this._program.TopLevelStatements) {
      switch (stmt) {
        case VbsConstDecl c:
          topConsts.Add(c);
          break;
        case VbsDimDecl d:
          topDims.Add(d);
          break;
        case VbsProcedureDecl p:
          topProcs.Add(p);
          break;
        case VbsClassDecl cls:
          topClasses.Add(cls);
          break;
        default:
          topStmts.Add(stmt);
          break;
      }
    }

    // 1. Register class shells first so contracts can resolve class type names.
    foreach (var cls in topClasses) {
      var irClass = new IrClassType(cls.Name);
      var info = new ClassInfo(cls, irClass);
      this._classes[cls.Name] = info;
      this._classList.Add(info);
    }

    // 2. Parse explicit contracts.
    this._contracts = VBScriptContracts.Parse(this._file, this._options.Contracts, this.ResolveClassType, this._diagnostics);
    if (this._diagnostics.Count > 0)
      return null;

    // 3. Evaluate top-level Const declarations.
    foreach (var c in topConsts)
      this.RegisterModuleConst(c);

    // 4. Register top-level procedures & class members + parse their doc comments.
    foreach (var p in topProcs) {
      var info = new ProcInfo(p, p.Name, p.Name, null);
      this._contracts.AddDocComments(this._file, p.Name, p.DocComments, p.Line, this.ResolveClassType, this._diagnostics);
      this._procedures[p.Name] = info;
      this._procedureList.Add(info);
    }

    foreach (var ci in this._classList) {
      foreach (var dim in ci.Decl.Fields) {
        foreach (var v in dim.Variables) {
          ci.FieldVars[v.Name] = v;
          ci.FieldPublic[v.Name] = dim.IsPublic;
          if (this._contracts.TryGet($"{ci.Decl.Name}.{v.Name}", out var ct))
            ci.FieldTypes[v.Name] = ct;
        }
      }

      foreach (var member in ci.Decl.Members) {
        if (member.Kind == VbsProcedureKind.Sub && member.Name.Equals("Class_Initialize", StringComparison.OrdinalIgnoreCase)) {
          var initInfo = new ProcInfo(member, $"{ci.Decl.Name}..ctor", $"{ci.Decl.Name}.Class_Initialize", ci);
          ci.Initializer = initInfo;
          continue;
        }

        var suffix = member.Kind switch {
          VbsProcedureKind.PropertyGet => $"get_{member.Name}",
          VbsProcedureKind.PropertyLet => $"set_{member.Name}",
          _ => member.Name
        };
        var irName = $"{ci.Decl.Name}.{suffix}";
        var key = $"{ci.Decl.Name}.{member.Name}";
        this._contracts.AddDocComments(this._file, key, member.DocComments, member.Line, this.ResolveClassType, this._diagnostics);
        var info = new ProcInfo(member, irName, key, ci);
        if (member.Kind == VbsProcedureKind.PropertyGet)
          ci.Getters[member.Name] = info;
        else if (member.Kind == VbsProcedureKind.PropertyLet)
          ci.Setters[member.Name] = info;
        else
          ci.Methods[member.Name] = info;
      }
    }

    // If there are top-level executable statements, wrap them in a Main Sub (if none exists) or _TopLevel.
    ProcInfo? topMain = null;
    if (topStmts.Count > 0) {
      var mainName = this._procedures.ContainsKey("Main") ? "_TopLevel" : "Main";
      var mainDecl = new VbsProcedureDecl(VbsProcedureKind.Sub, mainName, [], topStmts, IsPublic: true, [], topStmts[0].Line, topStmts[0].Column);
      topMain = new ProcInfo(mainDecl, mainName, mainName, null);
      this._procedures[mainName] = topMain;
      this._procedureList.Add(topMain);
    }

    if (this._diagnostics.Count > 0)
      return null;

    // 5. Register top-level Dim variables (globals).
    var globalTypes = new Dictionary<string, IrType>(StringComparer.OrdinalIgnoreCase);
    var globalVars = new List<VbsDimVar>();
    foreach (var dim in topDims) {
      foreach (var v in dim.Variables) {
        globalVars.Add(v);
        if (this._contracts.TryGet(v.Name, out var ct))
          globalTypes[v.Name] = ct;
      }
    }

    // 6. Seed explicit contract types on all procedures and class members.
    foreach (var p in this.AllProcedures())
      this.SeedContractTypes(p);

    // 7. Run iterative type inference across all procedures, class fields, and globals.
    for (var pass = 0; pass < 4; ++pass) {
      foreach (var p in this.AllProcedures())
        this.InferProcedureTypes(p, globalTypes);

      // Sync property getter <-> setter types.
      foreach (var ci in this._classList) {
        foreach (var (propName, getter) in ci.Getters) {
          if (ci.Setters.TryGetValue(propName, out var setter) && setter.ParamTypes.Length == 1) {
            if (getter.ReturnType is not null && setter.ParamTypes[0] is null)
              setter.ParamTypes[0] = getter.ReturnType;
            else if (setter.ParamTypes[0] is not null && getter.ReturnType is null)
              getter.ReturnType = setter.ParamTypes[0];
          }
        }
      }
    }

    // 8. Finalize defaults for any remaining uninferred types.
    foreach (var v in globalVars) {
      if (!globalTypes.TryGetValue(v.Name, out var gType))
        gType = v.IsArray ? IrType.Array(IrType.I32) : IrType.I32;

      var init = this.BuildGlobalInitializer(v, gType);
      var global = new IrGlobal(v.Name, gType, init);
      this._globals[v.Name] = global;
      this._globalList.Add(global);
    }

    foreach (var ci in this._classList)
      this.FinalizeClassTypes(ci);

    foreach (var p in this.AllProcedures())
      this.FinalizeProcedureTypes(p);

    // 9. Lower all procedures and class members to IrFunctions.
    var functions = new List<IrFunction>();
    foreach (var ci in this._classList) {
      functions.Add(this.LowerClassConstructor(ci));
      foreach (var m in ci.Methods.Values)
        functions.Add(this.LowerProcedure(m));
      foreach (var g in ci.Getters.Values)
        functions.Add(this.LowerProcedure(g));
      foreach (var s in ci.Setters.Values)
        functions.Add(this.LowerProcedure(s));
    }

    foreach (var p in this._procedureList)
      functions.Add(this.LowerProcedure(p));

    if (this._diagnostics.Count > 0)
      return null;

    functions.AddRange(this._helperList);
    var moduleName = DeriveModuleName(this._file);
    var types = this._classList.Select(c => (IrTypeDeclaration)c.IrClass).ToList();
    return new IrModule(moduleName, functions, this._moduleConstList) {
      Types = types,
      Globals = this._globalList
    };
  }

  private IEnumerable<ProcInfo> AllProcedures() {
    foreach (var p in this._procedureList)
      yield return p;
    foreach (var ci in this._classList) {
      if (ci.Initializer is not null)
        yield return ci.Initializer;
      foreach (var m in ci.Methods.Values)
        yield return m;
      foreach (var g in ci.Getters.Values)
        yield return g;
      foreach (var s in ci.Setters.Values)
        yield return s;
    }
  }

  private IrType? ResolveClassType(string name)
    => this._classes.TryGetValue(name, out var ci) ? IrType.Object(ci.IrClass) : null;

  private void RegisterModuleConst(VbsConstDecl decl) {
    if (!this.TryEvalConst(decl.Value, out var type, out var longVal, out var strVal)) {
      this.AddDiagnostic(VBScriptFrontend.UnsupportedConstruct, decl.Line, decl.Column, $"non-constant Const '{decl.Name}'");
      return;
    }

    var mc = type == IrType.String
      ? IrModuleConst.OfStrings(decl.Name, IrType.String, [strVal!])
      : new IrModuleConst(decl.Name, type, [longVal]);
    this._moduleConsts[decl.Name] = mc;
    this._moduleConstList.Add(mc);
  }

  private bool TryEvalConst(VbsExpr expr, out IrType type, out long longVal, out string? strVal) {
    type = IrType.I32;
    longVal = 0;
    strVal = null;
    switch (expr) {
      case VbsIntLit i:
        type = i.Value is >= int.MinValue and <= int.MaxValue ? IrType.I32 : IrType.I64;
        longVal = i.Value;
        return true;
      case VbsFloatLit f:
        type = IrType.F64;
        longVal = IrOperators.Bits(IrType.F64, f.Value);
        return true;
      case VbsStringLit s:
        type = IrType.String;
        strVal = s.Value;
        return true;
      case VbsBoolLit b:
        type = IrType.Bool;
        longVal = b.Value ? 1 : 0;
        return true;
      case VbsUnaryExpr { Op: VbsUnaryOp.Negate, Operand: var inner } when this.TryEvalConst(inner, out var it, out var iv, out _):
        if (it == IrType.F64) {
          type = IrType.F64;
          longVal = IrOperators.Bits(IrType.F64, -IrOperators.Float(IrType.F64, iv));
          return true;
        }
        if (it.IsInteger) {
          type = it;
          longVal = -iv;
          return true;
        }
        return false;
      case VbsIdentExpr id when this._moduleConsts.TryGetValue(id.Name, out var mc):
        type = mc.Type;
        if (type == IrType.String)
          strVal = mc.Strings[0];
        else
          longVal = mc.Values[0];
        return true;
      default:
        return false;
    }
  }

  private IrExpr? BuildGlobalInitializer(VbsDimVar v, IrType type) {
    if (type.IsArray) {
      if (v.UpperBound is not null && this.TryEvalConst(v.UpperBound, out var bt, out var bv, out _) && bt.IsInteger) {
        var len = (int)bv + 1;
        return type.Element == IrType.String
          ? new IrIntrinsic(IrIntrinsicOp.ArrayOf, type, Enumerable.Repeat<IrExpr>(new IrStrConst(""), Math.Max(0, len)).ToList())
          : new IrNewArray(type, new IrConst(IrType.I32, len));
      }
      return new IrIntrinsic(IrIntrinsicOp.ArrayOf, type, []);
    }

    return type.IsObject ? null : this.DefaultValueOf(type);
  }

  private void SeedContractTypes(ProcInfo proc) {
    for (var i = 0; i < proc.Decl.Parameters.Count; ++i) {
      var pName = proc.Decl.Parameters[i].Name;
      if (this._contracts.TryGet($"{proc.ContractKey}.{pName}", out var pt))
        proc.ParamTypes[i] = pt;
    }

    if (proc.Decl.Kind is VbsProcedureKind.Sub or VbsProcedureKind.PropertyLet)
      proc.ReturnType = IrType.Void;
    else if (this._contracts.TryGet($"{proc.ContractKey}.return", out var rt))
      proc.ReturnType = rt;
  }

  private void InferProcedureTypes(ProcInfo proc, Dictionary<string, IrType> globalTypes) {
    foreach (var stmt in proc.Decl.Body)
      this.InferStmtTypes(stmt, proc, globalTypes);
  }

  private void InferStmtTypes(VbsStmt stmt, ProcInfo proc, Dictionary<string, IrType> globalTypes) {
    switch (stmt) {
      case VbsDimDecl dim:
        foreach (var v in dim.Variables) {
          proc.DeclaredLocals.Add(v.Name);
          if (v.IsArray && !proc.LocalTypes.ContainsKey(v.Name))
            proc.LocalTypes[v.Name] = IrType.Array(IrType.I32);
        }
        break;
      case VbsConstDecl c:
        proc.DeclaredLocals.Add(c.Name);
        if (this.InferExprType(c.Value, proc, globalTypes) is { } ct)
          proc.LocalTypes[c.Name] = ct;
        break;
      case VbsReDimStmt redim:
        foreach (var v in redim.Variables) {
          proc.DeclaredLocals.Add(v.Name);
          if (!proc.LocalTypes.ContainsKey(v.Name))
            proc.LocalTypes[v.Name] = IrType.Array(IrType.I32);
        }
        break;
      case VbsAssignStmt assign:
        this.InferAssignTypes(assign, proc, globalTypes);
        break;
      case VbsCallStmt call:
        this.InferCallTargetTypes(call.Target, call.Arguments, proc, globalTypes);
        break;
      case VbsIfStmt ifStmt:
        this.HintBoolOrExpr(ifStmt.Condition, proc, globalTypes);
        foreach (var s in ifStmt.ThenBody)
          this.InferStmtTypes(s, proc, globalTypes);
        foreach (var ei in ifStmt.ElseIfs) {
          this.HintBoolOrExpr(ei.Condition, proc, globalTypes);
          foreach (var s in ei.Body)
            this.InferStmtTypes(s, proc, globalTypes);
        }
        if (ifStmt.ElseBody is not null)
          foreach (var s in ifStmt.ElseBody)
            this.InferStmtTypes(s, proc, globalTypes);
        break;
      case VbsSelectStmt sel:
        var selType = this.InferExprType(sel.Expression, proc, globalTypes);
        foreach (var c in sel.Cases) {
          foreach (var e in c.Expressions) {
            var caseType = this.InferExprType(e, proc, globalTypes);
            if (selType is null && caseType is not null)
              this.ConstrainExpr(sel.Expression, caseType, proc, globalTypes);
          }
          foreach (var s in c.Body)
            this.InferStmtTypes(s, proc, globalTypes);
        }
        if (sel.ElseBody is not null)
          foreach (var s in sel.ElseBody)
            this.InferStmtTypes(s, proc, globalTypes);
        break;
      case VbsForStmt forStmt: {
        var st = this.InferExprType(forStmt.Start, proc, globalTypes);
        var et = this.InferExprType(forStmt.End, proc, globalTypes);
        var stepT = forStmt.Step is not null ? this.InferExprType(forStmt.Step, proc, globalTypes) : null;
        var loopType = (st == IrType.F64 || et == IrType.F64 || stepT == IrType.F64) ? IrType.F64 : IrType.I32;
        this.RecordVarType(forStmt.Variable, loopType, proc, globalTypes);
        foreach (var s in forStmt.Body)
          this.InferStmtTypes(s, proc, globalTypes);
        break;
      }
      case VbsForEachStmt fe: {
        var colType = this.InferExprType(fe.Collection, proc, globalTypes);
        if (colType is { IsArray: true }) {
          this.RecordVarType(fe.Variable, colType.Element!, proc, globalTypes);
        } else {
          this.ConstrainExpr(fe.Collection, IrType.Array(IrType.I32), proc, globalTypes);
          this.RecordVarType(fe.Variable, IrType.I32, proc, globalTypes);
        }
        foreach (var s in fe.Body)
          this.InferStmtTypes(s, proc, globalTypes);
        break;
      }
      case VbsDoLoopStmt loop:
        if (loop.Condition is not null)
          this.HintBoolOrExpr(loop.Condition, proc, globalTypes);
        foreach (var s in loop.Body)
          this.InferStmtTypes(s, proc, globalTypes);
        break;
    }
  }

  private void InferAssignTypes(VbsAssignStmt assign, ProcInfo proc, Dictionary<string, IrType> globalTypes) {
    var valType = this.InferExprType(assign.Value, proc, globalTypes);

    if (assign.Target is VbsIdentExpr id) {
      if (proc.Decl.Kind is VbsProcedureKind.Function or VbsProcedureKind.PropertyGet
          && id.Name.Equals(proc.Decl.Name, StringComparison.OrdinalIgnoreCase)) {
        if (valType is not null && !this._contracts.TryGet($"{proc.ContractKey}.return", out _))
          proc.ReturnType = WidenType(proc.ReturnType, valType);
        return;
      }

      if (valType is not null)
        this.RecordVarType(id.Name, valType, proc, globalTypes);
      return;
    }

    if (assign.Target is VbsCallOrIndexExpr idx && idx.Target is VbsIdentExpr arrId) {
      foreach (var a in idx.Arguments)
        this.ConstrainExpr(a, IrType.I32, proc, globalTypes);
      if (valType is not null)
        this.RecordArrayElementType(arrId.Name, valType, proc, globalTypes);
      return;
    }

    if (assign.Target is VbsMemberExpr mem) {
      var targetType = this.InferExprType(mem.Target, proc, globalTypes);
      if (targetType is { ClassType: { } cls } && this._classes.TryGetValue(cls.Name, out var ci) && valType is not null) {
        if (ci.FieldVars.ContainsKey(mem.Member) && !this._contracts.TryGet($"{ci.Decl.Name}.{mem.Member}", out _))
          ci.FieldTypes[mem.Member] = WidenType(ci.FieldTypes.GetValueOrDefault(mem.Member), valType);
        else if (ci.Setters.TryGetValue(mem.Member, out var setter) && setter.ParamTypes.Length == 1 && setter.ParamTypes[0] is null)
          setter.ParamTypes[0] = valType;
      }
    }
  }

  private void HintBoolOrExpr(VbsExpr expr, ProcInfo proc, Dictionary<string, IrType> globalTypes) {
    if (expr is VbsIdentExpr id)
      this.ConstrainExpr(id, IrType.Bool, proc, globalTypes);
    else
      this.InferExprType(expr, proc, globalTypes);
  }

  private IrType? InferExprType(VbsExpr expr, ProcInfo proc, Dictionary<string, IrType> globalTypes) {
    switch (expr) {
      case VbsIntLit i:
        return i.Value is >= int.MinValue and <= int.MaxValue ? IrType.I32 : IrType.I64;
      case VbsFloatLit:
        return IrType.F64;
      case VbsStringLit:
        return IrType.String;
      case VbsBoolLit:
        return IrType.Bool;
      case VbsEmptyLit or VbsNullLit or VbsNothingLit:
        return null;
      case VbsMeExpr when proc.Owner is not null:
        return IrType.Object(proc.Owner.IrClass);
      case VbsNewExpr ne when this._classes.TryGetValue(ne.ClassName, out var ci):
        return IrType.Object(ci.IrClass);
      case VbsIdentExpr id:
        return this.LookupInferredVarType(id.Name, proc, globalTypes);
      case VbsUnaryExpr u: {
        var ot = this.InferExprType(u.Operand, proc, globalTypes);
        if (u.Op == VbsUnaryOp.Not)
          return ot is { IsInteger: true } ? ot : IrType.Bool;
        return ot ?? IrType.I32;
      }
      case VbsBinaryExpr b:
        return this.InferBinaryType(b, proc, globalTypes);
      case VbsMemberExpr m:
        return this.InferMemberType(m, proc, globalTypes);
      case VbsCallOrIndexExpr call:
        return this.InferCallTargetTypes(call.Target, call.Arguments, proc, globalTypes);
      default:
        return null;
    }
  }

  private IrType? InferBinaryType(VbsBinaryExpr b, ProcInfo proc, Dictionary<string, IrType> globalTypes) {
    var lt = this.InferExprType(b.Left, proc, globalTypes);
    var rt = this.InferExprType(b.Right, proc, globalTypes);
    switch (b.Op) {
      case VbsBinaryOp.Concat:
        return IrType.String;
      case VbsBinaryOp.Equal or VbsBinaryOp.NotEqual or VbsBinaryOp.Less or VbsBinaryOp.LessEqual or VbsBinaryOp.Greater or VbsBinaryOp.GreaterEqual or VbsBinaryOp.Is:
        if (lt is not null && rt is null)
          this.ConstrainExpr(b.Right, lt, proc, globalTypes);
        else if (rt is not null && lt is null)
          this.ConstrainExpr(b.Left, rt, proc, globalTypes);
        return IrType.Bool;
      case VbsBinaryOp.And or VbsBinaryOp.Or or VbsBinaryOp.Xor:
        if (lt is { IsInteger: true } || rt is { IsInteger: true })
          return IrType.I32;
        if (lt == IrType.Bool || rt == IrType.Bool) {
          this.ConstrainExpr(b.Left, IrType.Bool, proc, globalTypes);
          this.ConstrainExpr(b.Right, IrType.Bool, proc, globalTypes);
          return IrType.Bool;
        }
        return IrType.I32;
      case VbsBinaryOp.Divide or VbsBinaryOp.Power:
        return IrType.F64;
      case VbsBinaryOp.IntDivide or VbsBinaryOp.Mod:
        this.ConstrainExpr(b.Left, IrType.I32, proc, globalTypes);
        this.ConstrainExpr(b.Right, IrType.I32, proc, globalTypes);
        return IrType.I32;
      case VbsBinaryOp.Add when leftTypeStr(lt, rt):
        return IrType.String;
      default:
        return (lt == IrType.F64 || rt == IrType.F64) ? IrType.F64 : (lt ?? rt ?? IrType.I32);
    }

    static bool leftTypeStr(IrType? l, IrType? r) => l == IrType.String && r == IrType.String;
  }

  private IrType? InferMemberType(VbsMemberExpr m, ProcInfo proc, Dictionary<string, IrType> globalTypes) {
    if (m.Target is VbsIdentExpr { Name: var objName } && objName.Equals("Err", StringComparison.OrdinalIgnoreCase)) {
      if (m.Member.Equals("Number", StringComparison.OrdinalIgnoreCase))
        return IrType.I32;
      if (m.Member.Equals("Description", StringComparison.OrdinalIgnoreCase))
        return IrType.String;
      return null;
    }

    var targetType = this.InferExprType(m.Target, proc, globalTypes);
    if (targetType is { ClassType: { } cls } && this._classes.TryGetValue(cls.Name, out var ci)) {
      if (ci.FieldTypes.TryGetValue(m.Member, out var ft))
        return ft;
      if (ci.Getters.TryGetValue(m.Member, out var getter))
        return getter.ReturnType;
      if (ci.Methods.TryGetValue(m.Member, out var method))
        return method.ReturnType;
    }

    return null;
  }

  private IrType? InferCallTargetTypes(VbsExpr target, IReadOnlyList<VbsExpr> args, ProcInfo proc, Dictionary<string, IrType> globalTypes) {
    var argTypes = new IrType?[args.Count];
    for (var i = 0; i < args.Count; ++i)
      argTypes[i] = this.InferExprType(args[i], proc, globalTypes);

    if (target is VbsIdentExpr id) {
      // Built-in?
      if (this.TryInferBuiltinType(id.Name, args, argTypes, proc, globalTypes, out var builtinType))
        return builtinType;

      // Array indexing on a variable?
      var varType = this.LookupInferredVarType(id.Name, proc, globalTypes);
      if (varType is { IsArray: true }) {
        foreach (var a in args)
          this.ConstrainExpr(a, IrType.I32, proc, globalTypes);
        return varType.Element;
      }

      // Class method on Me?
      if (proc.Owner is not null && proc.Owner.Methods.TryGetValue(id.Name, out var ownMethod)) {
        this.PropagateCallArgTypes(ownMethod, argTypes);
        return ownMethod.ReturnType;
      }

      // Module procedure?
      if (this._procedures.TryGetValue(id.Name, out var callee)) {
        this.PropagateCallArgTypes(callee, argTypes);
        return callee.ReturnType;
      }

      // Might be an untyped parameter used as an array!
      for (var i = 0; i < proc.Decl.Parameters.Count; ++i) {
        if (proc.Decl.Parameters[i].Name.Equals(id.Name, StringComparison.OrdinalIgnoreCase) && proc.ParamTypes[i] is null) {
          proc.ParamTypes[i] = IrType.Array(IrType.I32);
          return IrType.I32;
        }
      }
    } else if (target is VbsMemberExpr mem) {
      var recvType = this.InferExprType(mem.Target, proc, globalTypes);
      if (recvType is { ClassType: { } cls } && this._classes.TryGetValue(cls.Name, out var ci)
          && ci.Methods.TryGetValue(mem.Member, out var method)) {
        this.PropagateCallArgTypes(method, argTypes);
        return method.ReturnType;
      }
    }

    return null;
  }

  private void PropagateCallArgTypes(ProcInfo callee, IrType?[] argTypes) {
    var count = Math.Min(callee.ParamTypes.Length, argTypes.Length);
    for (var i = 0; i < count; ++i) {
      var pName = callee.Decl.Parameters[i].Name;
      if (argTypes[i] is { } at && !this._contracts.TryGet($"{callee.ContractKey}.{pName}", out _))
        callee.ParamTypes[i] = WidenType(callee.ParamTypes[i], at);
    }
  }

  private bool TryInferBuiltinType(
    string name,
    IReadOnlyList<VbsExpr> args,
    IrType?[] argTypes,
    ProcInfo proc,
    Dictionary<string, IrType> globalTypes,
    out IrType? result) {
    result = null;
    switch (name.ToLowerInvariant()) {
      case "abs":
        result = argTypes.Length == 1 && argTypes[0] == IrType.F64 ? IrType.F64 : (argTypes.Length == 1 ? argTypes[0] ?? IrType.I32 : IrType.I32);
        return true;
      case "sqr" or "cdbl" or "int" or "fix" or "round":
        result = IrType.F64;
        return true;
      case "sgn" or "cint" or "clng":
        result = IrType.I32;
        return true;
      case "len" or "asc":
        if (args.Count >= 1)
          this.ConstrainExpr(args[0], IrType.String, proc, globalTypes);
        result = IrType.I32;
        return true;
      case "instr":
        if (args.Count == 2) {
          this.ConstrainExpr(args[0], IrType.String, proc, globalTypes);
          this.ConstrainExpr(args[1], IrType.String, proc, globalTypes);
        } else if (args.Count >= 3) {
          this.ConstrainExpr(args[0], IrType.I32, proc, globalTypes);
          this.ConstrainExpr(args[1], IrType.String, proc, globalTypes);
          this.ConstrainExpr(args[2], IrType.String, proc, globalTypes);
        }
        result = IrType.I32;
        return true;
      case "left" or "right" or "mid" or "replace" or "trim" or "ltrim" or "rtrim" or "ucase" or "lcase":
        if (args.Count >= 1)
          this.ConstrainExpr(args[0], IrType.String, proc, globalTypes);
        result = IrType.String;
        return true;
      case "chr":
        if (args.Count >= 1)
          this.ConstrainExpr(args[0], IrType.I32, proc, globalTypes);
        result = IrType.String;
        return true;
      case "cstr":
        result = IrType.String;
        return true;
      case "split":
        if (args.Count >= 1)
          this.ConstrainExpr(args[0], IrType.String, proc, globalTypes);
        result = IrType.Array(IrType.String);
        return true;
      case "join":
        if (args.Count >= 1)
          this.ConstrainExpr(args[0], IrType.Array(IrType.String), proc, globalTypes);
        result = IrType.String;
        return true;
      case "cbool" or "isempty" or "isnull" or "isnumeric":
        result = IrType.Bool;
        return true;
      case "ubound" or "lbound":
        if (args.Count >= 1 && argTypes[0] is null)
          this.ConstrainExpr(args[0], IrType.Array(IrType.I32), proc, globalTypes);
        result = IrType.I32;
        return true;
      case "array": {
        IrType? elem = null;
        foreach (var at in argTypes)
          if (at is not null)
            elem = WidenType(elem, at);
        result = IrType.Array(elem ?? IrType.I32);
        return true;
      }
      default:
        return false;
    }
  }

  private void ConstrainExpr(VbsExpr expr, IrType expected, ProcInfo proc, Dictionary<string, IrType> globalTypes) {
    if (expr is not VbsIdentExpr id)
      return;

    for (var i = 0; i < proc.Decl.Parameters.Count; ++i) {
      if (proc.Decl.Parameters[i].Name.Equals(id.Name, StringComparison.OrdinalIgnoreCase)) {
        if (proc.ParamTypes[i] is null && !this._contracts.TryGet($"{proc.ContractKey}.{id.Name}", out _))
          proc.ParamTypes[i] = expected;
        return;
      }
    }

    if (proc.LocalTypes.TryGetValue(id.Name, out var lt)) {
      if (lt.IsArray && expected.IsArray)
        proc.LocalTypes[id.Name] = expected;
      return;
    }

    if (proc.Owner is not null && proc.Owner.FieldVars.ContainsKey(id.Name) && !proc.Owner.FieldTypes.ContainsKey(id.Name))
      proc.Owner.FieldTypes[id.Name] = expected;
  }

  private void RecordVarType(string name, IrType type, ProcInfo proc, Dictionary<string, IrType> globalTypes) {
    for (var i = 0; i < proc.Decl.Parameters.Count; ++i) {
      if (proc.Decl.Parameters[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) {
        if (!this._contracts.TryGet($"{proc.ContractKey}.{name}", out _))
          proc.ParamTypes[i] = WidenType(proc.ParamTypes[i], type);
        return;
      }
    }

    if (proc.Owner is not null && proc.Owner.FieldVars.ContainsKey(name)) {
      if (!this._contracts.TryGet($"{proc.Owner.Decl.Name}.{name}", out _))
        proc.Owner.FieldTypes[name] = WidenType(proc.Owner.FieldTypes.GetValueOrDefault(name), type);
      return;
    }

    if (!proc.DeclaredLocals.Contains(name) && globalTypes.TryGetValue(name, out var gt)) {
      if (!this._contracts.TryGet(name, out _))
        globalTypes[name] = WidenType(gt, type);
      return;
    }

    proc.LocalTypes[name] = WidenType(proc.LocalTypes.GetValueOrDefault(name), type);
  }

  private void RecordArrayElementType(string name, IrType elemType, ProcInfo proc, Dictionary<string, IrType> globalTypes) {
    var arrType = IrType.Array(elemType);
    if (proc.LocalTypes.TryGetValue(name, out var existing) && existing.IsArray) {
      proc.LocalTypes[name] = IrType.Array(WidenType(existing.Element == IrType.I32 ? null : existing.Element, elemType));
      return;
    }
    this.RecordVarType(name, arrType, proc, globalTypes);
  }

  private IrType? LookupInferredVarType(string name, ProcInfo proc, Dictionary<string, IrType> globalTypes) {
    if (proc.Decl.Kind is VbsProcedureKind.Function or VbsProcedureKind.PropertyGet
        && name.Equals(proc.Decl.Name, StringComparison.OrdinalIgnoreCase))
      return proc.ReturnType;

    for (var i = 0; i < proc.Decl.Parameters.Count; ++i)
      if (proc.Decl.Parameters[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        return proc.ParamTypes[i];

    if (proc.LocalTypes.TryGetValue(name, out var lt))
      return lt;

    if (proc.Owner is not null) {
      if (proc.Owner.FieldTypes.TryGetValue(name, out var ft))
        return ft;
      if (proc.Owner.Getters.TryGetValue(name, out var g))
        return g.ReturnType;
    }

    if (this._moduleConsts.TryGetValue(name, out var mc))
      return mc.Type;

    if (globalTypes.TryGetValue(name, out var gt))
      return gt;

    return null;
  }

  private static IrType WidenType(IrType? existing, IrType incoming) {
    if (existing is null || existing == incoming)
      return incoming;
    if ((existing == IrType.I32 && incoming == IrType.F64) || (existing == IrType.F64 && incoming == IrType.I32))
      return IrType.F64;
    if ((existing == IrType.I32 && incoming == IrType.I64) || (existing == IrType.I64 && incoming == IrType.I32))
      return IrType.I64;
    if (existing.IsArray && incoming.IsArray)
      return IrType.Array(WidenType(existing.Element, incoming.Element!));
    return existing;
  }

  private void FinalizeClassTypes(ClassInfo ci) {
    var fields = new List<IrField>();
    foreach (var (name, v) in ci.FieldVars) {
      var type = ci.FieldTypes.GetValueOrDefault(name) ?? (v.IsArray ? IrType.Array(IrType.I32) : IrType.I32);
      ci.FieldTypes[name] = type;
      var field = new IrField(name, type, IsReadOnly: false) {
        Visibility = ci.FieldPublic[name] ? IrVisibility.Public : IrVisibility.Private
      };
      ci.Fields[name] = field;
      fields.Add(field);
    }

    ci.IrClass.Define(fields);
    ci.IrClass.Inherit(null, []);

    var ctor = new IrConstructor($"{ci.Decl.Name}..ctor") { Visibility = IrVisibility.Public };
    ci.DefaultCtor = ctor;
    if (ci.Initializer is not null)
      ci.Initializer.Constructor = ctor;

    var methods = new List<IrMethod>();
    foreach (var m in ci.Methods.Values) {
      var method = new IrMethod(m.Decl.Name, m.IrName, IsStatic: false) {
        Visibility = m.Decl.IsPublic ? IrVisibility.Public : IrVisibility.Private,
        Kind = IrMethodKind.Method
      };
      m.Method = method;
      methods.Add(method);
    }

    var propNames = new HashSet<string>(ci.Getters.Keys, StringComparer.OrdinalIgnoreCase);
    propNames.UnionWith(ci.Setters.Keys);
    var properties = new List<IrProperty>();
    foreach (var propName in propNames) {
      ci.Getters.TryGetValue(propName, out var getterProc);
      ci.Setters.TryGetValue(propName, out var setterProc);
      var propType = getterProc?.ReturnType
        ?? (setterProc is { ParamTypes.Length: >= 1 } ? setterProc.ParamTypes[0] : null)
        ?? IrType.I32;

      IrMethod? getterMethod = null;
      if (getterProc is not null) {
        getterProc.ReturnType = propType;
        getterMethod = new IrMethod(getterProc.Decl.Name, getterProc.IrName, IsStatic: false) {
          Visibility = getterProc.Decl.IsPublic ? IrVisibility.Public : IrVisibility.Private,
          Kind = IrMethodKind.Getter
        };
        getterProc.Method = getterMethod;
        methods.Add(getterMethod);
      }

      IrMethod? setterMethod = null;
      if (setterProc is not null) {
        if (setterProc.ParamTypes.Length >= 1)
          setterProc.ParamTypes[0] = propType;
        setterMethod = new IrMethod(setterProc.Decl.Name, setterProc.IrName, IsStatic: false) {
          Visibility = setterProc.Decl.IsPublic ? IrVisibility.Public : IrVisibility.Private,
          Kind = IrMethodKind.Setter
        };
        setterProc.Method = setterMethod;
        methods.Add(setterMethod);
      }

      var prop = new IrProperty(propName, propType) {
        Getter = getterMethod,
        Setter = setterMethod,
        Visibility = IrVisibility.Public
      };
      ci.Properties[propName] = prop;
      properties.Add(prop);
    }

    ci.IrClass.DefineMembers([ctor], methods, properties, []);
  }

  private void FinalizeProcedureTypes(ProcInfo proc) {
    for (var i = 0; i < proc.ParamTypes.Length; ++i)
      proc.ParamTypes[i] ??= IrType.I32;

    if (proc.Decl.Kind is VbsProcedureKind.Sub or VbsProcedureKind.PropertyLet)
      proc.ReturnType = IrType.Void;
    else
      proc.ReturnType ??= IrType.I32;
  }

  private IrFunction LowerClassConstructor(ClassInfo ci) {
    var receiver = new IrLocal("me", IrType.Object(ci.IrClass));
    var locals = new Dictionary<string, IrLocal>(StringComparer.OrdinalIgnoreCase);
    var localList = new List<IrLocal>();
    var stmts = new List<IrStmt>();

    // Initialize string and array fields so they are never null before Class_Initialize.
    foreach (var (name, field) in ci.Fields) {
      if (field.Type == IrType.String) {
        stmts.Add(new IrFieldStore(new IrLocalRef(receiver), field, new IrStrConst("")));
      } else if (field.Type.IsArray) {
        var v = ci.FieldVars[name];
        var init = this.BuildGlobalInitializer(v, field.Type)!;
        stmts.Add(new IrFieldStore(new IrLocalRef(receiver), field, init));
      }
    }

    if (ci.Initializer is not null) {
      var ctx = new ProcContext(ci.Initializer, receiver, null, locals, localList);
      this.LowerBlockInto(ci.Initializer.Decl.Body, ctx, stmts);
    }

    return new IrFunction(ci.DefaultCtor.Function, IsPublic: true, IrType.Void, [receiver], localList, new IrBlock(stmts));
  }

  private IrFunction LowerProcedure(ProcInfo proc) {
    var parameters = new List<IrLocal>();
    var locals = new Dictionary<string, IrLocal>(StringComparer.OrdinalIgnoreCase);
    var localList = new List<IrLocal>();

    IrLocal? receiver = null;
    if (proc.Owner is not null) {
      receiver = new IrLocal("me", IrType.Object(proc.Owner.IrClass));
      parameters.Add(receiver);
    }

    for (var i = 0; i < proc.Decl.Parameters.Count; ++i) {
      var p = new IrLocal(proc.Decl.Parameters[i].Name, proc.ParamTypes[i]);
      parameters.Add(p);
      locals[p.Name] = p;
    }

    var stmts = new List<IrStmt>();
    IrLocal? resultLocal = null;
    if (proc.ReturnType != IrType.Void) {
      resultLocal = new IrLocal("__result", proc.ReturnType!);
      localList.Add(resultLocal);
      stmts.Add(new IrDeclare(resultLocal, this.DefaultValueOf(proc.ReturnType!)));
    }

    var ctx = new ProcContext(proc, receiver, resultLocal, locals, localList);

    // Pre-declare all Dim'd / implicitly-created locals at procedure scope (VBScript locals are procedure-scoped!).
    this.PreDeclareLocals(proc.Decl.Body, ctx, stmts);
    this.LowerBlockInto(proc.Decl.Body, ctx, stmts);

    if (resultLocal is not null)
      stmts.Add(new IrReturn(new IrLocalRef(resultLocal)));

    return new IrFunction(proc.IrName, proc.Decl.IsPublic, proc.ReturnType!, parameters, localList, new IrBlock(stmts));
  }

  private void PreDeclareLocals(IReadOnlyList<VbsStmt> body, ProcContext ctx, List<IrStmt> stmts) {
    foreach (var stmt in body)
      this.CollectLocalDecls(stmt, ctx, stmts);
  }

  private void CollectLocalDecls(VbsStmt stmt, ProcContext ctx, List<IrStmt> stmts) {
    switch (stmt) {
      case VbsDimDecl dim:
        foreach (var v in dim.Variables) {
          if (ctx.Locals.ContainsKey(v.Name))
            continue;
          var type = ctx.Proc.LocalTypes.GetValueOrDefault(v.Name) ?? (v.IsArray ? IrType.Array(IrType.I32) : IrType.I32);
          var local = new IrLocal(v.Name, type);
          ctx.Locals[v.Name] = local;
          ctx.LocalList.Add(local);
          stmts.Add(new IrDeclare(local, this.DefaultValueOf(type)));
          if (!v.IsArray)
            ctx.UnassignedLocals.Add(v.Name);
        }
        break;
      case VbsForStmt f:
        this.EnsureImplicitLocal(f.Variable, ctx, stmts);
        this.PreDeclareLocals(f.Body, ctx, stmts);
        break;
      case VbsForEachStmt fe:
        this.EnsureImplicitLocal(fe.Variable, ctx, stmts);
        this.PreDeclareLocals(fe.Body, ctx, stmts);
        break;
      case VbsIfStmt i:
        this.PreDeclareLocals(i.ThenBody, ctx, stmts);
        foreach (var ei in i.ElseIfs)
          this.PreDeclareLocals(ei.Body, ctx, stmts);
        if (i.ElseBody is not null)
          this.PreDeclareLocals(i.ElseBody, ctx, stmts);
        break;
      case VbsSelectStmt sel:
        foreach (var c in sel.Cases)
          this.PreDeclareLocals(c.Body, ctx, stmts);
        if (sel.ElseBody is not null)
          this.PreDeclareLocals(sel.ElseBody, ctx, stmts);
        break;
      case VbsDoLoopStmt loop:
        this.PreDeclareLocals(loop.Body, ctx, stmts);
        break;
      case VbsAssignStmt { Target: VbsIdentExpr id }:
        if (!this._program.OptionExplicit)
          this.EnsureImplicitLocal(id.Name, ctx, stmts);
        break;
    }
  }

  private void EnsureImplicitLocal(string name, ProcContext ctx, List<IrStmt> stmts) {
    if (ctx.Locals.ContainsKey(name))
      return;
    if (ctx.Proc.Decl.Kind is VbsProcedureKind.Function or VbsProcedureKind.PropertyGet
        && name.Equals(ctx.Proc.Decl.Name, StringComparison.OrdinalIgnoreCase))
      return;
    if (ctx.Proc.Owner is not null && (ctx.Proc.Owner.Fields.ContainsKey(name) || ctx.Proc.Owner.Setters.ContainsKey(name)))
      return;
    if (this._globals.ContainsKey(name) || this._moduleConsts.ContainsKey(name))
      return;

    var type = ctx.Proc.LocalTypes.GetValueOrDefault(name) ?? IrType.I32;
    var local = new IrLocal(name, type);
    ctx.Locals[name] = local;
    ctx.LocalList.Add(local);
    stmts.Add(new IrDeclare(local, this.DefaultValueOf(type)));
  }

  private IrExpr DefaultValueOf(IrType type) {
    if (type == IrType.String)
      return new IrStrConst("");
    if (type == IrType.Bool)
      return new IrConst(IrType.Bool, 0);
    if (type.IsFloat)
      return new IrConst(type, IrOperators.Bits(type, 0.0));
    if (type.IsArray)
      return new IrIntrinsic(IrIntrinsicOp.ArrayOf, type, []);
    if (type.IsObject)
      return new IrNull(type);
    if (type.IsOptional)
      return new IrNone(type);
    return new IrConst(type, 0);
  }

  private (IrGlobal Number, IrGlobal Desc) EnsureErrGlobals() {
    if (this._errNumberGlobal is null) {
      this._errNumberGlobal = new IrGlobal("__vbs_err_number", IrType.I32, new IrConst(IrType.I32, 0));
      this._errDescGlobal = new IrGlobal("__vbs_err_desc", IrType.String, new IrStrConst(""));
      this._globalList.Add(this._errNumberGlobal);
      this._globalList.Add(this._errDescGlobal);
    }
    return (this._errNumberGlobal, this._errDescGlobal!);
  }

  private void AddDiagnostic(DiagnosticDescriptor descriptor, int line, int column, params object[] args)
    => this._diagnostics.Add(Diagnostic.Create(descriptor, new(this._file, line, column, line, column), args));

  private static string DeriveModuleName(string file) {
    var baseName = Path.GetFileNameWithoutExtension(file);
    if (string.IsNullOrWhiteSpace(baseName))
      return "VBScriptModule";

    var sb = new StringBuilder(baseName.Length);
    foreach (var c in baseName)
      if (char.IsAsciiLetterOrDigit(c) || c == '_')
        sb.Append(c);

    if (sb.Length == 0 || char.IsAsciiDigit(sb[0]))
      sb.Insert(0, '_');

    return sb.ToString();
  }

}
