using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Cpp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Cpp.Lowering;

internal sealed partial class CppLowerer {

  private static readonly Dictionary<string, IrType> _primitiveTypes = new(StringComparer.Ordinal) {
    ["void"] = IrType.Void,
    ["bool"] = IrType.Bool,
    ["char"] = IrType.I8,
    ["signed char"] = IrType.I8,
    ["int8_t"] = IrType.I8,
    ["unsigned char"] = IrType.U8,
    ["uint8_t"] = IrType.U8,
    ["short"] = IrType.I16,
    ["short int"] = IrType.I16,
    ["signed short"] = IrType.I16,
    ["signed short int"] = IrType.I16,
    ["int16_t"] = IrType.I16,
    ["unsigned short"] = IrType.U16,
    ["unsigned short int"] = IrType.U16,
    ["uint16_t"] = IrType.U16,
    ["int"] = IrType.I32,
    ["signed"] = IrType.I32,
    ["signed int"] = IrType.I32,
    ["int32_t"] = IrType.I32,
    ["unsigned"] = IrType.U32,
    ["unsigned int"] = IrType.U32,
    ["uint32_t"] = IrType.U32,
    ["long"] = IrType.I64,
    ["long int"] = IrType.I64,
    ["signed long"] = IrType.I64,
    ["signed long int"] = IrType.I64,
    ["long long"] = IrType.I64,
    ["long long int"] = IrType.I64,
    ["signed long long"] = IrType.I64,
    ["signed long long int"] = IrType.I64,
    ["int64_t"] = IrType.I64,
    ["ptrdiff_t"] = IrType.I64,
    ["ssize_t"] = IrType.I64,
    ["unsigned long"] = IrType.U64,
    ["unsigned long int"] = IrType.U64,
    ["unsigned long long"] = IrType.U64,
    ["unsigned long long int"] = IrType.U64,
    ["uint64_t"] = IrType.U64,
    ["size_t"] = IrType.U64,
    ["float"] = IrType.F32,
    ["double"] = IrType.F64
  };

  private static readonly Dictionary<string, IrClassType> _exceptionClasses = new(StringComparer.Ordinal) {
    ["exception"] = IrExceptionClass.Exception,
    ["runtime_error"] = IrExceptionClass.InvalidOperationException,
    ["logic_error"] = IrExceptionClass.InvalidOperationException,
    ["invalid_argument"] = IrExceptionClass.ArgumentException,
    ["out_of_range"] = IrExceptionClass.ArgumentOutOfRangeException,
    ["overflow_error"] = IrExceptionClass.OverflowException,
    ["domain_error"] = IrExceptionClass.ArithmeticException,
    ["range_error"] = IrExceptionClass.ArithmeticException
  };

  private readonly string _file;
  private readonly string _moduleName;
  private readonly Dictionary<string, CppTypeRef> _aliases = new(StringComparer.Ordinal);
  private readonly Dictionary<string, IrStructType> _structs = new(StringComparer.Ordinal);
  private readonly Dictionary<string, CppStructDecl> _structDecls = new(StringComparer.Ordinal);
  private readonly Dictionary<string, IrEnumType> _enums = new(StringComparer.Ordinal);
  private readonly Dictionary<string, (IrEnumType Enum, long Value)> _unscopedEnumMembers = new(StringComparer.Ordinal);
  private readonly List<IrTypeDeclaration> _types = [];
  private readonly List<IrModuleConst> _constants = [];
  private readonly Dictionary<string, IrModuleConst> _constantMap = new(StringComparer.Ordinal);
  private readonly List<IrGlobal> _globals = [];
  private readonly Dictionary<string, IrGlobal> _globalMap = new(StringComparer.Ordinal);
  private readonly Dictionary<string, FunctionSignature> _signatures = new(StringComparer.Ordinal);
  private readonly Dictionary<string, CppFuncDecl> _templates = new(StringComparer.Ordinal);
  private readonly Dictionary<string, string> _qualifiedNames = new(StringComparer.Ordinal);
  private readonly List<IrFunction> _functions = [];
  private readonly Dictionary<string, IrFunction> _instantiatedTemplates = new(StringComparer.Ordinal);

  private CppLowerer(string file, string moduleName) {
    this._file = file;
    this._moduleName = moduleName;
  }

  public static IrModule Lower(string file, string moduleName, CppTranslationUnit unit)
    => new CppLowerer(file, moduleName).Run(unit);

  private sealed record FunctionSignature(string IrName, IrType ReturnType, IReadOnlyList<IrType> ParamTypes, bool IsPublic);

  private sealed class FunctionContext {

    public required string Name { get; init; }
    public required IrType ReturnType { get; init; }
    public IReadOnlyDictionary<string, IrType>? TypeSubst { get; init; }
    public List<IrLocal> Locals { get; } = [];
    public HashSet<string> UsedLocalNames { get; } = new(StringComparer.Ordinal);
    public Stack<Dictionary<string, IrLocal>> Scopes { get; } = new();
    public int TempCounter { get; set; }

    public IrLocal AllocateLocal(string baseName, IrType type) {
      var name = baseName;
      var suffix = 1;
      while (!this.UsedLocalNames.Add(name))
        name = $"{baseName}_{suffix++}";

      var local = new IrLocal(name, type);
      this.Locals.Add(local);
      return local;
    }

    public IrLocal AllocateTemp(IrType type) => this.AllocateLocal($"__t{this.TempCounter++}", type);

    public IrLocal? Lookup(string name) {
      foreach (var scope in this.Scopes)
        if (scope.TryGetValue(name, out var local))
          return local;

      return null;
    }

  }

  private IrModule Run(CppTranslationUnit unit) {
    var flatDecls = new List<(CppDecl Decl, string? Namespace, bool IsPrivate)>();
    CollectDeclarations(unit.Declarations, null, false, flatDecls);

    // Pass 1: type aliases, enums, structs
    foreach (var (decl, _, _) in flatDecls) {
      switch (decl) {
        case CppUsingNamespaceDecl:
          break;
        case CppTypeAliasDecl alias:
          this._aliases[alias.Name] = alias.Target;
          break;
        case CppEnumDecl enumDecl:
          this.DeclareEnum(enumDecl);
          break;
        case CppStructDecl structDecl:
          var st = new IrStructType(structDecl.Name) { IsEquatable = structDecl.HasDefaultEquals };
          this._structs[structDecl.Name] = st;
          this._structDecls[structDecl.Name] = structDecl;
          this._types.Add(st);
          break;
      }
    }

    foreach (var (decl, _, _) in flatDecls) {
      if (decl is CppStructDecl structDecl)
        this.DefineStructFields(structDecl);
    }

    // Pass 2: globals & constants
    foreach (var (decl, ns, _) in flatDecls) {
      if (decl is CppGlobalVarDecl globalDecl)
        this.LowerGlobal(globalDecl, ns);
    }

    // Pass 3: register function signatures & templates
    var funcBodies = new List<(CppFuncDecl Decl, string IrName, bool IsPublic)>();
    foreach (var (decl, ns, isPrivate) in flatDecls) {
      if (decl is not CppFuncDecl func)
        continue;

      var simpleName = StripLeadingColons(func.Name);
      if (func.TypeParams.Count > 0) {
        this._templates[simpleName] = func;
        if (ns is not null)
          this._qualifiedNames[$"{ns}::{simpleName}"] = simpleName;

        continue;
      }

      var irName = simpleName;
      if (this._signatures.ContainsKey(irName) && ns is not null)
        irName = $"{ns.Replace("::", "_", StringComparison.Ordinal)}_{simpleName}";

      var retType = this.ResolveType(func.ReturnType, isLocal: false, null);
      var paramTypes = func.Params.Select(p => this.ResolveType(p.Type, isLocal: false, null)).ToList();
      var isPublic = !func.IsStatic && !isPrivate;
      this._signatures[irName] = new(irName, retType, paramTypes, isPublic);
      if (ns is not null)
        this._qualifiedNames[$"{ns}::{simpleName}"] = irName;

      if (func.Body is not null)
        funcBodies.Add((func, irName, isPublic));
    }

    // Pass 4: lower function bodies
    foreach (var (func, irName, isPublic) in funcBodies)
      this._functions.Add(this.LowerFunction(func, irName, isPublic, null));

    return new IrModule(this._moduleName, this._functions, this._constants) {
      Types = this._types,
      Globals = this._globals
    };
  }

  private static void CollectDeclarations(
    IReadOnlyList<CppDecl> decls,
    string? currentNs,
    bool isPrivate,
    List<(CppDecl Decl, string? Namespace, bool IsPrivate)> output) {
    foreach (var decl in decls) {
      if (decl is CppNamespaceDecl ns) {
        var nextNs = ns.Name is null ? currentNs : currentNs is null ? ns.Name : $"{currentNs}::{ns.Name}";
        CollectDeclarations(ns.Declarations, nextNs, isPrivate || ns.Name is null, output);
      } else {
        output.Add((decl, currentNs, isPrivate));
      }
    }
  }

  private void DeclareEnum(CppEnumDecl decl) {
    var underlying = decl.UnderlyingType is not null
      ? this.ResolveType(decl.UnderlyingType, isLocal: false, null)
      : IrType.I32;
    if (!underlying.IsInteger || underlying.IsEnum)
      throw this.UnsupportedType(decl.Span, decl.UnderlyingType!.Name);

    var members = new List<IrEnumMember>();
    var localMembers = new Dictionary<string, long>(StringComparer.Ordinal);
    long nextValue = 0;
    foreach (var m in decl.Members) {
      var val = m.Value is not null
        ? this.EvalConstantInt(m.Value, localMembers)
        : nextValue;
      var wrapped = IrOperators.Wrap(underlying, val);
      members.Add(new(m.Name, wrapped));
      localMembers[m.Name] = wrapped;
      nextValue = val + 1;
    }

    var enumType = new IrEnumType(decl.Name, underlying, members) { IsPublic = true };
    this._enums[decl.Name] = enumType;
    this._types.Add(enumType);
    if (!decl.IsScoped) {
      foreach (var m in members)
        this._unscopedEnumMembers[m.Name] = (enumType, m.Value);
    }
  }

  private void DefineStructFields(CppStructDecl decl) {
    var st = this._structs[decl.Name];
    var fields = new List<IrField>();
    foreach (var f in decl.Fields) {
      var fType = this.ResolveType(f.Type, isLocal: false, null);
      if (fType.IsView || fType.IsOptional || fType.IsString || fType.IsCollection)
        throw this.UnsupportedType(f.Span, f.Type.Name);

      fields.Add(new(f.Name, fType, f.Type.IsConst));
    }

    st.Define(fields);
  }

  private void LowerGlobal(CppGlobalVarDecl decl, string? ns) {
    var type = this.ResolveType(decl.Type, isLocal: false, null);
    var name = StripLeadingColons(decl.Name);
    if (ns is not null)
      this._qualifiedNames[$"{ns}::{name}"] = name;

    if (decl.IsConst && decl.Initializer is not null && this.TryBuildModuleConst(name, type, decl.Initializer, out var moduleConst)) {
      this._constants.Add(moduleConst!);
      this._constantMap[name] = moduleConst!;
      return;
    }

    var dummyCtx = new FunctionContext { Name = name, ReturnType = type };
    dummyCtx.Scopes.Push(new(StringComparer.Ordinal));
    IrExpr? initExpr = null;
    if (decl.Initializer is not null)
      initExpr = this.LowerInitializer(dummyCtx, type, decl.Type, decl.Initializer);
    else if (type.IsArray && decl.Type.ArraySize is > 0)
      initExpr = new IrNewArray(type, new IrConst(IrType.I32, decl.Type.ArraySize.Value));
    else if (type.IsString)
      initExpr = new IrStrConst("");

    var global = new IrGlobal(name, type, initExpr);
    this._globals.Add(global);
    this._globalMap[name] = global;
  }

  private bool TryBuildModuleConst(string name, IrType type, CppExpr init, out IrModuleConst? result) {
    result = null;
    if (type.IsStruct || type.IsView || type.IsOptional || type.IsCollection)
      return false;

    if (type.IsString && init is CppStringLiteral strLit) {
      result = IrModuleConst.OfStrings(name, IrType.String, [strLit.Value]);
      return true;
    }

    if (type == IrType.Bool && this.TryEvalConstValue(init, type, out var bVal)) {
      result = new(name, type, [bVal]);
      return true;
    }

    if (type.IsNumeric && this.TryEvalConstValue(init, type, out var nVal)) {
      result = new(name, type, [nVal]);
      return true;
    }

    if (type.IsArray && init is CppInitListExpr list) {
      var elem = type.Element!;
      if (elem.IsNumeric) {
        var vals = new List<long>();
        foreach (var el in list.Elements) {
          if (!this.TryEvalConstValue(el, elem, out var v))
            return false;

          vals.Add(v);
        }

        result = new(name, type, vals);
        return true;
      }

      if (elem.IsString) {
        var strs = new List<string>();
        foreach (var el in list.Elements) {
          if (el is not CppStringLiteral s)
            return false;

          strs.Add(s.Value);
        }

        result = IrModuleConst.OfStrings(name, type, strs);
        return true;
      }
    }

    return false;
  }

  private bool TryEvalConstValue(CppExpr expr, IrType target, out long value) {
    value = 0;
    switch (expr) {
      case CppBoolLiteral b when target == IrType.Bool:
        value = b.Value ? 1 : 0;
        return true;
      case CppIntLiteral i when target.IsInteger:
        value = IrOperators.Wrap(target, unchecked((long)i.Value));
        return true;
      case CppIntLiteral i when target.IsFloat:
        value = IrOperators.Bits(target, i.Value);
        return true;
      case CppFloatLiteral f when target.IsFloat:
        value = IrOperators.Bits(target, f.Value);
        return true;
      case CppCharLiteral c when target.IsInteger:
        value = IrOperators.Wrap(target, c.Value);
        return true;
      case CppUnaryExpr { Op: "-", IsPostfix: false } u when this.TryEvalConstValue(u.Operand, target, out var inner):
        value = target.IsFloat
          ? IrOperators.Bits(target, -IrOperators.Float(target, inner))
          : IrOperators.Wrap(target, unchecked(-inner));
        return true;
      case CppIdentExpr id when this.TryResolveEnumValue(id.Name, out var eType, out var eVal) && (target == eType.Type || target.IsInteger):
        value = IrOperators.Wrap(target, eVal);
        return true;
      default:
        return false;
    }
  }

  private long EvalConstantInt(CppExpr expr, IReadOnlyDictionary<string, long>? localMembers = null) => expr switch {
    CppIntLiteral i => unchecked((long)i.Value),
    CppCharLiteral c => c.Value,
    CppUnaryExpr { Op: "-", IsPostfix: false } u => -this.EvalConstantInt(u.Operand, localMembers),
    CppUnaryExpr { Op: "+", IsPostfix: false } u => this.EvalConstantInt(u.Operand, localMembers),
    CppUnaryExpr { Op: "~", IsPostfix: false } u => ~this.EvalConstantInt(u.Operand, localMembers),
    CppBinaryExpr { Op: "+" } b => this.EvalConstantInt(b.Left, localMembers) + this.EvalConstantInt(b.Right, localMembers),
    CppBinaryExpr { Op: "-" } b => this.EvalConstantInt(b.Left, localMembers) - this.EvalConstantInt(b.Right, localMembers),
    CppBinaryExpr { Op: "*" } b => this.EvalConstantInt(b.Left, localMembers) * this.EvalConstantInt(b.Right, localMembers),
    CppBinaryExpr { Op: "<<" } b => this.EvalConstantInt(b.Left, localMembers) << (int)this.EvalConstantInt(b.Right, localMembers),
    CppBinaryExpr { Op: "|" } b => this.EvalConstantInt(b.Left, localMembers) | this.EvalConstantInt(b.Right, localMembers),
    CppIdentExpr id when localMembers is not null && localMembers.TryGetValue(id.Name, out var localVal) => localVal,
    CppIdentExpr id when this.TryResolveEnumValue(id.Name, out _, out var val) => val,
    _ => throw this.Unsupported(expr.Span, "non-constant enumerator expression")
  };

  // ---------------------------------------------------------------- type resolution

  internal IrType ResolveType(CppTypeRef typeRef, bool isLocal, IReadOnlyDictionary<string, IrType>? typeSubst) {
    var baseName = StripStd(StripLeadingColons(typeRef.Name));

    if (typeSubst is not null && typeSubst.TryGetValue(baseName, out var subst))
      return this.ApplyTypeDecorators(subst, typeRef);

    if (this._aliases.TryGetValue(baseName, out var aliased)) {
      var resolved = this.ResolveType(aliased, isLocal, typeSubst);
      return this.ApplyTypeDecorators(resolved, typeRef);
    }

    if (baseName == "string")
      return this.ApplyContainerOrValue(IrType.String, typeRef);

    if (baseName == "span") {
      if (typeRef.TypeArgs.Count != 1 || typeRef.TypeArgs[0].Type is not { } elemRef)
        throw this.UnsupportedType(typeRef.Span, typeRef.Name);

      var elem = this.ResolveType(elemRef, isLocal: false, typeSubst);
      return elem.IsNumeric
        ? (elemRef.IsConst ? IrType.ReadOnlySpan(elem) : IrType.Span(elem))
        : IrType.Array(elem);
    }

    if (baseName == "array") {
      if (typeRef.TypeArgs.Count != 2 || typeRef.TypeArgs[0].Type is not { } elemRef || typeRef.TypeArgs[1].IntValue is null)
        throw this.UnsupportedType(typeRef.Span, typeRef.Name);

      var elem = this.ResolveType(elemRef, isLocal: false, typeSubst);
      return IrType.Array(elem);
    }

    if (baseName == "vector") {
      if (typeRef.TypeArgs.Count != 1 || typeRef.TypeArgs[0].Type is not { } elemRef)
        throw this.UnsupportedType(typeRef.Span, typeRef.Name);

      var elem = this.ResolveType(elemRef, isLocal: false, typeSubst);
      return isLocal && !typeRef.IsReference && !typeRef.IsPointer ? IrType.List(elem) : IrType.Array(elem);
    }

    if (baseName == "optional") {
      if (typeRef.TypeArgs.Count != 1 || typeRef.TypeArgs[0].Type is not { } elemRef)
        throw this.UnsupportedType(typeRef.Span, typeRef.Name);

      var elem = this.ResolveType(elemRef, isLocal: false, typeSubst);
      if (!elem.IsOptionalValue)
        throw this.UnsupportedType(typeRef.Span, $"std::optional<{elemRef.Name}>");

      return IrType.Optional(elem);
    }

    if (this._structs.TryGetValue(baseName, out var structType))
      return this.ApplyTypeDecorators(IrType.Struct(structType), typeRef);

    if (this._enums.TryGetValue(baseName, out var enumType))
      return this.ApplyTypeDecorators(enumType.Type, typeRef);

    if (!_primitiveTypes.TryGetValue(baseName, out var prim))
      throw this.UnsupportedType(typeRef.Span, typeRef.Name);

    return this.ApplyTypeDecorators(prim, typeRef);
  }

  private IrType ApplyTypeDecorators(IrType baseType, CppTypeRef typeRef) {
    if (typeRef.ArraySize is not null)
      return IrType.Array(baseType);

    if (typeRef.IsPointer) {
      if (baseType == IrType.Void)
        throw this.UnsupportedType(typeRef.Span, "void*");

      return baseType.IsNumeric
        ? (typeRef.IsConst ? IrType.ReadOnlySpan(baseType) : IrType.Span(baseType))
        : IrType.Array(baseType);
    }

    return baseType;
  }

  private IrType ApplyContainerOrValue(IrType baseType, CppTypeRef typeRef)
    => typeRef.ArraySize is not null || typeRef.IsPointer ? IrType.Array(baseType) : baseType;

  private static IrClassType? ResolveExceptionClass(string name)
    => _exceptionClasses.GetValueOrDefault(StripStd(StripLeadingColons(name)));

  // ---------------------------------------------------------------- functions

  private IrFunction LowerFunction(CppFuncDecl func, string irName, bool isPublic, IReadOnlyDictionary<string, IrType>? typeSubst) {
    var retType = this.ResolveType(func.ReturnType, isLocal: false, typeSubst);
    var ctx = new FunctionContext { Name = irName, ReturnType = retType, TypeSubst = typeSubst };
    var rootScope = new Dictionary<string, IrLocal>(StringComparer.Ordinal);
    ctx.Scopes.Push(rootScope);

    var parameters = new List<IrLocal>();
    for (var i = 0; i < func.Params.Count; ++i) {
      var p = func.Params[i];
      var pType = this.ResolveType(p.Type, isLocal: false, typeSubst);
      var pName = string.IsNullOrEmpty(p.Name) ? $"__p{i}" : p.Name;
      var local = new IrLocal(pName, pType);
      ctx.UsedLocalNames.Add(pName);
      rootScope[pName] = local;
      parameters.Add(local);
    }

    var body = this.LowerBlock(ctx, func.Body!);
    return new(irName, isPublic, retType, parameters, ctx.Locals, body);
  }

  // ---------------------------------------------------------------- statements

  private IrBlock LowerBlock(FunctionContext ctx, CppBlockStmt block) {
    ctx.Scopes.Push(new(StringComparer.Ordinal));
    var stmts = new List<IrStmt>();
    foreach (var stmt in block.Statements)
      this.LowerStatementInto(ctx, stmt, stmts);

    ctx.Scopes.Pop();
    return new(stmts);
  }

  private IrBlock LowerAsBlock(FunctionContext ctx, CppStmt stmt) {
    if (stmt is CppBlockStmt block)
      return this.LowerBlock(ctx, block);

    ctx.Scopes.Push(new(StringComparer.Ordinal));
    var stmts = new List<IrStmt>();
    this.LowerStatementInto(ctx, stmt, stmts);
    ctx.Scopes.Pop();
    return new(stmts);
  }

  private void LowerStatementInto(FunctionContext ctx, CppStmt stmt, List<IrStmt> output) {
    switch (stmt) {
      case CppBlockStmt block:
        output.Add(this.LowerBlock(ctx, block));
        break;
      case CppVarDeclStmt decl:
        this.LowerVarDecl(ctx, decl, output);
        break;
      case CppExprStmt exprStmt:
        this.LowerExprStmt(ctx, exprStmt.Expr, output);
        break;
      case CppIfStmt ifStmt:
        var cond = this.ToBool(this.LowerExpr(ctx, ifStmt.Condition, null));
        var thenBlock = this.LowerAsBlock(ctx, ifStmt.Then);
        var elseBlock = ifStmt.Else is not null ? this.LowerAsBlock(ctx, ifStmt.Else) : null;
        output.Add(new IrIf(cond, thenBlock, elseBlock));
        break;
      case CppWhileStmt whileStmt:
        output.Add(new IrWhile(
          this.ToBool(this.LowerExpr(ctx, whileStmt.Condition, null)),
          this.LowerAsBlock(ctx, whileStmt.Body)));
        break;
      case CppDoWhileStmt doStmt:
        output.Add(new IrDoWhile(
          this.LowerAsBlock(ctx, doStmt.Body),
          this.ToBool(this.LowerExpr(ctx, doStmt.Condition, null))));
        break;
      case CppForStmt forStmt:
        this.LowerFor(ctx, forStmt, output);
        break;
      case CppRangeForStmt rangeFor:
        this.LowerRangeFor(ctx, rangeFor, output);
        break;
      case CppSwitchStmt switchStmt:
        this.LowerSwitch(ctx, switchStmt, output);
        break;
      case CppBreakStmt:
        output.Add(new IrBreak());
        break;
      case CppContinueStmt:
        output.Add(new IrContinue());
        break;
      case CppReturnStmt ret:
        if (ret.Value is null) {
          if (ctx.ReturnType != IrType.Void)
            throw this.SyntaxError(ret.Span, "missing return value");

          output.Add(new IrReturn(null));
        } else {
          var val = this.ConvertTo(this.LowerExpr(ctx, ret.Value, ctx.ReturnType), ctx.ReturnType, ret.Span);
          output.Add(new IrReturn(val));
        }

        break;
      case CppTryStmt tryStmt:
        this.LowerTry(ctx, tryStmt, output);
        break;
      case CppThrowStmt throwStmt:
        this.LowerThrow(ctx, throwStmt, output);
        break;
    }
  }

  private void LowerVarDecl(FunctionContext ctx, CppVarDeclStmt decl, List<IrStmt> output) {
    IrType type;
    IrExpr? init;
    if (decl.Type is null) {
      if (decl.Initializer is null)
        throw this.SyntaxError(decl.Span, "'auto' variable requires an initializer");

      init = this.LowerExpr(ctx, decl.Initializer, null);
      type = init.Type;
    } else {
      type = this.ResolveType(decl.Type, isLocal: true, ctx.TypeSubst);
      init = decl.Initializer is not null
        ? this.LowerInitializer(ctx, type, decl.Type, decl.Initializer)
        : this.DefaultInitializer(type, decl.Type);
    }

    var local = ctx.AllocateLocal(decl.Name, type);
    ctx.Scopes.Peek()[decl.Name] = local;
    output.Add(new IrDeclare(local, init));
  }

  private IrExpr? DefaultInitializer(IrType type, CppTypeRef typeRef) {
    if (type.IsList)
      return new IrCollection(IrCollectionOp.ListNew, type, []);

    if (type.IsArray) {
      var len = StaticArrayLength(typeRef);
      return len is > 0 ? new IrNewArray(type, new IrConst(IrType.I32, len.Value)) : null;
    }

    if (type.IsStruct)
      return new IrStructDefault(type);

    if (type.IsOptional)
      return new IrNone(type);

    if (type.IsString)
      return new IrStrConst("");

    return null;
  }

  private static int? StaticArrayLength(CppTypeRef typeRef) {
    if (typeRef.ArraySize is > 0)
      return typeRef.ArraySize.Value;

    if (StripStd(StripLeadingColons(typeRef.Name)) == "array" && typeRef.TypeArgs.Count == 2 && typeRef.TypeArgs[1].IntValue is { } n)
      return (int)n;

    return null;
  }

  private IrExpr LowerInitializer(FunctionContext ctx, IrType targetType, CppTypeRef? typeRef, CppExpr init) {
    if (init is CppInitListExpr list && (list.Type is null || this.ResolveType(list.Type, isLocal: true, ctx.TypeSubst) == targetType)) {
      if (targetType.IsStruct)
        return this.LowerStructInit(ctx, targetType, list.Elements, list.Span);

      if (targetType.IsList) {
        var elemType = targetType.Element!;
        if (list.Elements.Count == 0)
          return new IrCollection(IrCollectionOp.ListNew, targetType, []);

        var items = list.Elements.Select(e => this.ConvertTo(this.LowerExpr(ctx, e, elemType), elemType, e.Span)).ToList();
        return new IrCollection(IrCollectionOp.ListOf, targetType, items);
      }

      if (targetType.IsArray) {
        var elemType = targetType.Element!;
        var items = list.Elements.Select(e => this.ConvertTo(this.LowerExpr(ctx, e, elemType), elemType, e.Span)).ToList();
        var staticLen = typeRef is not null ? StaticArrayLength(typeRef) : null;
        if (staticLen is > 0) {
          while (items.Count < staticLen.Value)
            items.Add(this.ZeroExpr(elemType));
        }

        return new IrIntrinsic(IrIntrinsicOp.ArrayOf, targetType, items);
      }

      if (targetType.IsOptional) {
        return list.Elements.Count == 0
          ? new IrNone(targetType)
          : new IrSome(this.ConvertTo(this.LowerExpr(ctx, list.Elements[0], targetType.Underlying), targetType.Underlying!, list.Span));
      }

      if (list.Elements.Count == 0)
        return this.ZeroExpr(targetType);

      return this.ConvertTo(this.LowerExpr(ctx, list.Elements[0], targetType), targetType, list.Span);
    }

    return this.ConvertTo(this.LowerExpr(ctx, init, targetType), targetType, init.Span);
  }

  private IrExpr LowerStructInit(FunctionContext ctx, IrType structType, IReadOnlyList<CppExpr> elements, SourceRange span) {
    var st = structType.StructType!;
    if (elements.Count == 0)
      return new IrStructDefault(structType);

    var decl = this._structDecls[st.Name];
    var args = new List<IrExpr>(st.Fields.Count);
    for (var i = 0; i < st.Fields.Count; ++i) {
      var field = st.Fields[i];
      if (i < elements.Count)
        args.Add(this.ConvertTo(this.LowerExpr(ctx, elements[i], field.Type), field.Type, elements[i].Span));
      else if (decl.Fields[i].DefaultValue is { } defExpr)
        args.Add(this.ConvertTo(this.LowerExpr(ctx, defExpr, field.Type), field.Type, defExpr.Span));
      else
        args.Add(this.ZeroExpr(field.Type));
    }

    return new IrStructNew(structType, args);
  }

  private void LowerFor(FunctionContext ctx, CppForStmt forStmt, List<IrStmt> output) {
    ctx.Scopes.Push(new(StringComparer.Ordinal));
    var wrapped = new List<IrStmt>();
    if (forStmt.Init is not null)
      this.LowerStatementInto(ctx, forStmt.Init, wrapped);

    var cond = forStmt.Condition is not null
      ? this.ToBool(this.LowerExpr(ctx, forStmt.Condition, null))
      : new IrConst(IrType.Bool, 1);
    var body = this.LowerAsBlock(ctx, forStmt.Body);
    var stepStmts = new List<IrStmt>();
    if (forStmt.Step is not null)
      this.LowerExprStmt(ctx, forStmt.Step, stepStmts);

    wrapped.Add(new IrFor(cond, body, new(stepStmts)));
    ctx.Scopes.Pop();
    output.Add(new IrBlock(wrapped));
  }

  private void LowerRangeFor(FunctionContext ctx, CppRangeForStmt rangeFor, List<IrStmt> output) {
    var iterable = this.LowerExpr(ctx, rangeFor.Iterable, null);
    if (iterable.Type.IsList) {
      var elemType = rangeFor.VarType is not null
        ? this.ResolveType(rangeFor.VarType, isLocal: true, ctx.TypeSubst)
        : iterable.Type.Element!;
      ctx.Scopes.Push(new(StringComparer.Ordinal));
      var loopVar = ctx.AllocateLocal(rangeFor.VarName, elemType);
      ctx.Scopes.Peek()[rangeFor.VarName] = loopVar;
      var body = this.LowerAsBlock(ctx, rangeFor.Body);
      ctx.Scopes.Pop();
      output.Add(new IrForEachCollection(iterable, EnumerationMode.Live, loopVar, null, body));
      return;
    }

    if (!iterable.Type.IsSequence)
      throw this.Unsupported(rangeFor.Span, $"range-based for over {iterable.Type}");

    ctx.Scopes.Push(new(StringComparer.Ordinal));
    var stmts = new List<IrStmt>();
    IrExpr seqRef;
    if (iterable is IrLocalRef or IrGlobalRef or IrConstRef) {
      seqRef = iterable;
    } else {
      var seqLocal = ctx.AllocateTemp(iterable.Type);
      stmts.Add(new IrDeclare(seqLocal, iterable));
      seqRef = new IrLocalRef(seqLocal);
    }

    var idxLocal = ctx.AllocateTemp(IrType.I32);
    var lenLocal = ctx.AllocateTemp(IrType.I32);
    IrExpr lenExpr = seqRef.Type.IsView
      ? new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [seqRef])
      : new IrArrayLength(seqRef);
    stmts.Add(new IrDeclare(idxLocal, new IrConst(IrType.I32, 0)));
    stmts.Add(new IrDeclare(lenLocal, lenExpr));

    var elemLoad = new IrArrayLoad(seqRef, new IrLocalRef(idxLocal));
    var itemType = rangeFor.VarType is not null
      ? this.ResolveType(rangeFor.VarType, isLocal: true, ctx.TypeSubst)
      : elemLoad.Type;
    var itemLocal = ctx.AllocateLocal(rangeFor.VarName, itemType);
    ctx.Scopes.Peek()[rangeFor.VarName] = itemLocal;

    var innerBody = new List<IrStmt> {
      new IrDeclare(itemLocal, this.ConvertTo(elemLoad, itemType, rangeFor.Span))
    };
    var userBody = this.LowerAsBlock(ctx, rangeFor.Body);
    innerBody.AddRange(userBody.Statements);

    var cond = new IrBinary(IrBinaryOp.Less, new IrLocalRef(idxLocal), new IrLocalRef(lenLocal));
    var step = new IrBlock([new IrAssign(idxLocal, new IrBinary(IrBinaryOp.Add, new IrLocalRef(idxLocal), new IrConst(IrType.I32, 1)))]);
    stmts.Add(new IrFor(cond, new(innerBody), step));
    ctx.Scopes.Pop();
    output.Add(new IrBlock(stmts));
  }

  private void LowerSwitch(FunctionContext ctx, CppSwitchStmt switchStmt, List<IrStmt> output) {
    var value = this.LowerExpr(ctx, switchStmt.Value, null);
    if (!value.Type.IsInteger)
      throw this.Unsupported(switchStmt.Span, $"switch on {value.Type}");

    var sections = new List<IrSwitchSection>();
    for (var i = 0; i < switchStmt.Cases.Count; ++i) {
      var c = switchStmt.Cases[i];
      var labels = new List<long>();
      var isDefault = false;
      foreach (var lbl in c.Labels) {
        if (lbl is null)
          isDefault = true;
        else
          labels.Add(IrOperators.Wrap(value.Type, this.EvalConstantInt(lbl)));
      }

      if (i < switchStmt.Cases.Count - 1 && c.Statements.Count > 0 && !EndsWithJump(c.Statements))
        throw this.Unsupported(c.Span, "switch fall-through");

      ctx.Scopes.Push(new(StringComparer.Ordinal));
      var bodyStmts = new List<IrStmt>();
      foreach (var s in c.Statements)
        this.LowerStatementInto(ctx, s, bodyStmts);

      ctx.Scopes.Pop();
      sections.Add(new(labels, isDefault, new(bodyStmts)));
    }

    output.Add(new IrSwitch(value, sections));
  }

  private static bool EndsWithJump(IReadOnlyList<CppStmt> stmts) {
    var last = stmts[^1];
    return last is CppBreakStmt or CppReturnStmt or CppContinueStmt or CppThrowStmt
      || (last is CppBlockStmt b && b.Statements.Count > 0 && EndsWithJump(b.Statements));
  }

  private void LowerTry(FunctionContext ctx, CppTryStmt tryStmt, List<IrStmt> output) {
    var body = this.LowerBlock(ctx, tryStmt.Body);
    var catches = new List<IrCatch>();
    foreach (var clause in tryStmt.Catches) {
      var exClass = clause.ExceptionType is null
        ? IrExceptionClass.Exception
        : ResolveExceptionClass(clause.ExceptionType.Name)
          ?? throw this.UnsupportedType(clause.Span, clause.ExceptionType.Name);
      IrLocal? exLocal = null;
      ctx.Scopes.Push(new(StringComparer.Ordinal));
      if (clause.VarName is not null) {
        exLocal = ctx.AllocateLocal(clause.VarName, IrType.Object(exClass));
        ctx.Scopes.Peek()[clause.VarName] = exLocal;
      }

      var cBody = this.LowerBlock(ctx, clause.Body);
      ctx.Scopes.Pop();
      catches.Add(new(exClass, exLocal, null, cBody));
    }

    output.Add(new IrTry(body, catches, null));
  }

  private void LowerThrow(FunctionContext ctx, CppThrowStmt throwStmt, List<IrStmt> output) {
    if (throwStmt.Value is null) {
      output.Add(new IrRethrow());
      return;
    }

    if (throwStmt.Value is CppCallExpr { Callee: CppIdentExpr exIdent } call
        && ResolveExceptionClass(exIdent.Name) is { } exClass) {
      if (call.Arguments.Count == 0) {
        output.Add(new IrThrow(new IrNew(IrType.Object(exClass), IrExceptionClass.Constructor(exClass, 0), [])));
        return;
      }

      var msg = this.ConvertTo(this.LowerExpr(ctx, call.Arguments[0], IrType.String), IrType.String, call.Arguments[0].Span);
      output.Add(new IrThrow(new IrNew(IrType.Object(exClass), IrExceptionClass.Constructor(exClass, 1), [msg])));
      return;
    }

    throw this.Unsupported(throwStmt.Span, "throwing non-standard exception");
  }

  private void LowerExprStmt(FunctionContext ctx, CppExpr expr, List<IrStmt> output) {
    if (expr is CppAssignExpr assign) {
      this.LowerAssignStmt(ctx, assign, output);
      return;
    }

    output.Add(new IrExprStmt(this.LowerExpr(ctx, expr, null)));
  }

  private void LowerAssignStmt(FunctionContext ctx, CppAssignExpr assign, List<IrStmt> output) {
    // Handle list element assignment: vec[i] = val
    if (assign.Target is CppIndexExpr idxExpr) {
      var target = this.LowerExpr(ctx, idxExpr.Target, null);
      var index = this.ToIndex(this.LowerExpr(ctx, idxExpr.Index, IrType.I32));
      if (target.Type.IsList) {
        var elemType = target.Type.Element!;
        var newVal = this.ComputeAssignedValue(ctx, assign.Op, () => new IrCollection(IrCollectionOp.ListGet, elemType, [target, index]), elemType, assign.Value, assign.Span);
        output.Add(new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [target, index, newVal])));
        return;
      }

      if (target.Type.IsWritableSequence) {
        var elemType = target.Type.Element!;
        var newVal = this.ComputeAssignedValue(ctx, assign.Op, () => new IrArrayLoad(target, index), elemType, assign.Value, assign.Span);
        output.Add(new IrStore(target, index, newVal));
        return;
      }

      throw this.Unsupported(assign.Span, $"assignment to element of {target.Type}");
    }

    if (assign.Target is CppMemberExpr memExpr) {
      var target = this.LowerExpr(ctx, memExpr.Target, null);
      var field = target.Type.StructType?.Field(memExpr.Member)
        ?? throw this.Unresolved(memExpr.Span, $"{target.Type}.{memExpr.Member}");
      var newVal = this.ComputeAssignedValue(ctx, assign.Op, () => new IrFieldLoad(target, field), field.Type, assign.Value, assign.Span);
      output.Add(new IrFieldStore(target, field, newVal));
      return;
    }

    if (assign.Target is CppIdentExpr ident) {
      if (ctx.Lookup(ident.Name) is { } local) {
        var newVal = this.ComputeAssignedValue(ctx, assign.Op, () => new IrLocalRef(local), local.Type, assign.Value, assign.Span);
        output.Add(new IrAssign(local, newVal));
        return;
      }

      var gName = this.ResolveSymbolName(ident.Name);
      if (this._globalMap.TryGetValue(gName, out var global)) {
        var newVal = this.ComputeAssignedValue(ctx, assign.Op, () => new IrGlobalRef(global), global.Type, assign.Value, assign.Span);
        output.Add(new IrGlobalAssign(global, newVal));
        return;
      }

      throw this.Unresolved(ident.Span, ident.Name);
    }

    throw this.Unsupported(assign.Span, "assignment target");
  }

  private IrExpr ComputeAssignedValue(
    FunctionContext ctx,
    string assignOp,
    Func<IrExpr> readTarget,
    IrType targetType,
    CppExpr rhsExpr,
    SourceRange span) {
    if (assignOp == "=")
      return this.ConvertTo(this.LowerExpr(ctx, rhsExpr, targetType), targetType, span);

    var lhs = readTarget();
    var rhs = this.LowerExpr(ctx, rhsExpr, targetType);
    if (assignOp == "+=" && targetType.IsString)
      return new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [lhs, this.ConvertTo(rhs, IrType.String, span)]);

    var binOp = assignOp[..^1];
    var combined = this.BuildBinary(binOp, lhs, rhs, span);
    return this.ConvertTo(combined, targetType, span);
  }

  // ---------------------------------------------------------------- expressions

  private IrExpr LowerExpr(FunctionContext ctx, CppExpr expr, IrType? expected) => expr switch {
    CppIntLiteral i => LowerIntLiteral(i, expected),
    CppFloatLiteral f => new IrConst(f.IsFloat32 ? IrType.F32 : IrType.F64, IrOperators.Bits(f.IsFloat32 ? IrType.F32 : IrType.F64, f.Value)),
    CppBoolLiteral b => new IrConst(IrType.Bool, b.Value ? 1 : 0),
    CppCharLiteral c => new IrConst(IrType.I8, c.Value),
    CppStringLiteral s => new IrStrConst(s.Value),
    CppNullLiteral n => this.LowerNullLiteral(n, expected),
    CppIdentExpr id => this.LowerIdent(ctx, id),
    CppUnaryExpr u => this.LowerUnary(ctx, u),
    CppBinaryExpr b => this.LowerBinary(ctx, b),
    CppTernaryExpr t => this.LowerTernary(ctx, t, expected),
    CppAssignExpr a => this.LowerAssignExpr(ctx, a),
    CppCastExpr c => this.LowerCast(ctx, c),
    CppCallExpr c => this.LowerCall(ctx, c),
    CppIndexExpr idx => this.LowerIndex(ctx, idx),
    CppMemberExpr m => this.LowerMember(ctx, m),
    CppSizeofExpr s => this.LowerSizeof(ctx, s),
    _ => this.LowerInitListExpr(ctx, (CppInitListExpr)expr, expected)
  };

  private static IrExpr LowerIntLiteral(CppIntLiteral lit, IrType? expected) {
    var type = (lit.IsUnsigned, lit.WidthBits) switch {
      (true, 64) => IrType.U64,
      (false, 64) => IrType.I64,
      (true, 32) => IrType.U32,
      _ => IrType.I32
    };
    if (expected is not null && expected.IsInteger && !expected.IsEnum && !lit.IsUnsigned && lit.WidthBits == 32) {
      var signed = unchecked((long)lit.Value);
      if (signed >= IrOperators.Minimum(expected) && signed <= IrOperators.Maximum(expected))
        return new IrConst(expected, IrOperators.Wrap(expected, signed));
    }

    return new IrConst(type, IrOperators.Wrap(type, unchecked((long)lit.Value)));
  }

  private IrExpr LowerNullLiteral(CppNullLiteral lit, IrType? expected) {
    if (lit.IsNullopt) {
      if (expected is { IsOptional: true })
        return new IrNone(expected);

      throw this.Unsupported(lit.Span, "std::nullopt without target optional type");
    }

    if (expected is { IsNullable: true })
      return new IrNull(expected);

    throw this.Unsupported(lit.Span, "nullptr without nullable target type");
  }

  private IrExpr LowerIdent(FunctionContext ctx, CppIdentExpr id) {
    if (id.Name is "std::string::npos" or "string::npos")
      return new IrConst(IrType.U64, -1L);

    if (ctx.Lookup(id.Name) is { } local)
      return new IrLocalRef(local);

    if (this.TryResolveEnumValue(id.Name, out var enumType, out var enumVal))
      return new IrConst(enumType.Type, enumVal);

    var resolved = this.ResolveSymbolName(id.Name);
    if (this._constantMap.TryGetValue(resolved, out var constant))
      return new IrConstRef(constant);

    if (this._globalMap.TryGetValue(resolved, out var global))
      return new IrGlobalRef(global);

    throw this.Unresolved(id.Span, id.Name);
  }

  private bool TryResolveEnumValue(string name, out IrEnumType enumType, out long value) {
    var clean = StripLeadingColons(name);
    var sep = clean.LastIndexOf("::", StringComparison.Ordinal);
    if (sep > 0) {
      var typeName = clean[..sep];
      var memberName = clean[(sep + 2)..];
      if (this._enums.TryGetValue(typeName, out enumType!) && enumType.Member(memberName) is { } member) {
        value = member.Value;
        return true;
      }
    }

    if (this._unscopedEnumMembers.TryGetValue(clean, out var entry)) {
      enumType = entry.Enum;
      value = entry.Value;
      return true;
    }

    enumType = null!;
    value = 0;
    return false;
  }

  private IrExpr LowerUnary(FunctionContext ctx, CppUnaryExpr unary) {
    if (unary.Op is "++" or "--") {
      var target = this.LowerExpr(ctx, unary.Operand, null);
      if (target is IrLocalRef or IrGlobalRef or IrArrayLoad && target.Type.IsNumeric)
        return new IrStep(target, unary.Op == "++", unary.IsPostfix, false);

      throw this.Unsupported(unary.Span, $"'{unary.Op}' on {target.Type}");
    }

    var operand = this.LowerExpr(ctx, unary.Operand, null);
    switch (unary.Op) {
      case "!":
        return new IrUnary(IrUnaryOp.LogicalNot, this.ToBool(operand));
      case "+":
        return this.PromoteNarrow(operand);
      case "-": {
        var promoted = this.PromoteNarrow(operand);
        return promoted.Type is { Kind: IrTypeKind.U32 or IrTypeKind.U64 }
          ? new IrBinary(IrBinaryOp.Subtract, new IrConst(promoted.Type, 0), promoted)
          : new IrUnary(IrUnaryOp.Negate, promoted);
      }
      case "~": {
        var promoted = this.PromoteNarrow(operand);
        return new IrUnary(IrUnaryOp.BitNot, promoted);
      }
      default: // "*"
        if (operand.Type.IsOptional)
          return new IrUnwrap(operand);

        if (operand.Type.IsSequence)
          return new IrArrayLoad(operand, new IrConst(IrType.I32, 0));

        throw this.Unsupported(unary.Span, $"dereference of {operand.Type}");
    }
  }

  private IrExpr LowerBinary(FunctionContext ctx, CppBinaryExpr bin) {
    if (bin.Op is "&&" or "||") {
      var lBool = this.ToBool(this.LowerExpr(ctx, bin.Left, IrType.Bool));
      var rBool = this.ToBool(this.LowerExpr(ctx, bin.Right, IrType.Bool));
      return new IrBinary(bin.Op == "&&" ? IrBinaryOp.LogicalAnd : IrBinaryOp.LogicalOr, lBool, rBool);
    }

    if (bin.Op is "==" or "!=") {
      if (bin.Right is CppNullLiteral rNull)
        return this.LowerNullComparison(this.LowerExpr(ctx, bin.Left, null), rNull, bin.Op == "==", bin.Span);

      if (bin.Left is CppNullLiteral lNull)
        return this.LowerNullComparison(this.LowerExpr(ctx, bin.Right, null), lNull, bin.Op == "==", bin.Span);
    }

    var left = this.LowerExpr(ctx, bin.Left, null);
    var right = this.LowerExpr(ctx, bin.Right, left.Type.IsEnum ? left.Type : null);
    return this.BuildBinary(bin.Op, left, right, bin.Span);
  }

  private IrExpr LowerNullComparison(IrExpr value, CppNullLiteral nullLit, bool isEqual, SourceRange span) {
    IrExpr isEmpty = value.Type.IsOptional
      ? new IrIsNone(value)
      : value.Type.IsNullable
        ? new IrIsNull(value)
        : throw this.Unsupported(span, $"comparison of {value.Type} with {(nullLit.IsNullopt ? "std::nullopt" : "nullptr")}");
    return isEqual ? isEmpty : new IrUnary(IrUnaryOp.LogicalNot, isEmpty);
  }

  private IrExpr BuildBinary(string op, IrExpr left, IrExpr right, SourceRange span) {
    if (left.Type.IsString || right.Type.IsString) {
      var ls = this.ConvertTo(left, IrType.String, span);
      var rs = this.ConvertTo(right, IrType.String, span);
      return op switch {
        "+" => new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [ls, rs]),
        "==" => new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [ls, rs]),
        "!=" => new IrUnary(IrUnaryOp.LogicalNot, new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [ls, rs])),
        "<" => new IrBinary(IrBinaryOp.Less, new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [ls, rs]), new IrConst(IrType.I32, 0)),
        "<=" => new IrBinary(IrBinaryOp.LessOrEqual, new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [ls, rs]), new IrConst(IrType.I32, 0)),
        ">" => new IrBinary(IrBinaryOp.Greater, new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [ls, rs]), new IrConst(IrType.I32, 0)),
        ">=" => new IrBinary(IrBinaryOp.GreaterOrEqual, new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [ls, rs]), new IrConst(IrType.I32, 0)),
        _ => throw this.Unsupported(span, $"string operator '{op}'")
      };
    }

    if (left.Type.IsStruct && left.Type == right.Type && op is "==" or "!=") {
      var eq = new IrStructEquals(left, right);
      return op == "==" ? eq : new IrUnary(IrUnaryOp.LogicalNot, eq);
    }

    if (left.Type.IsEnum && left.Type == right.Type && op is "==" or "!=")
      return new IrBinary(op == "==" ? IrBinaryOp.Equal : IrBinaryOp.NotEqual, left, right);

    if (left.Type == IrType.Bool && right.Type == IrType.Bool && op is "==" or "!=")
      return new IrBinary(op == "==" ? IrBinaryOp.Equal : IrBinaryOp.NotEqual, left, right);

    if (op is "<<" or ">>") {
      var lShift = this.PromoteNarrow(left);
      var rShift = this.ConvertTo(right, IrType.I32, span);
      return new IrBinary(op == "<<" ? IrBinaryOp.ShiftLeft : IrBinaryOp.ShiftRight, lShift, rShift);
    }

    var common = UsualArithmeticType(left.Type, right.Type);
    var lConv = this.ConvertTo(left, common, span);
    var rConv = this.ConvertTo(right, common, span);
    var irOp = op switch {
      "+" => IrBinaryOp.Add,
      "-" => IrBinaryOp.Subtract,
      "*" => IrBinaryOp.Multiply,
      "/" => IrBinaryOp.Divide,
      "%" => IrBinaryOp.Remainder,
      "&" => IrBinaryOp.And,
      "|" => IrBinaryOp.Or,
      "^" => IrBinaryOp.Xor,
      "==" => IrBinaryOp.Equal,
      "!=" => IrBinaryOp.NotEqual,
      "<" => IrBinaryOp.Less,
      "<=" => IrBinaryOp.LessOrEqual,
      ">" => IrBinaryOp.Greater,
      _ => IrBinaryOp.GreaterOrEqual
    };
    return new IrBinary(irOp, lConv, rConv);
  }

  private IrExpr LowerTernary(FunctionContext ctx, CppTernaryExpr ternary, IrType? expected) {
    var cond = this.ToBool(this.LowerExpr(ctx, ternary.Condition, IrType.Bool));
    if (ternary.Then is CppNullLiteral && expected is null) {
      var rhs = this.LowerExpr(ctx, ternary.Else, null);
      var optType = rhs.Type.IsOptional ? rhs.Type : IrType.Optional(rhs.Type);
      return new IrConditional(cond, new IrNone(optType), this.ConvertTo(rhs, optType, ternary.Else.Span));
    }

    if (ternary.Else is CppNullLiteral && expected is null) {
      var lhs = this.LowerExpr(ctx, ternary.Then, null);
      var optType = lhs.Type.IsOptional ? lhs.Type : IrType.Optional(lhs.Type);
      return new IrConditional(cond, this.ConvertTo(lhs, optType, ternary.Then.Span), new IrNone(optType));
    }

    var whenTrue = this.LowerExpr(ctx, ternary.Then, expected);
    var whenFalse = this.LowerExpr(ctx, ternary.Else, expected ?? whenTrue.Type);
    var resultType = whenTrue.Type == whenFalse.Type
      ? whenTrue.Type
      : whenTrue.Type.IsOptional ? whenTrue.Type
      : whenFalse.Type.IsOptional ? whenFalse.Type
      : UsualArithmeticType(whenTrue.Type, whenFalse.Type);
    return new IrConditional(
      cond,
      this.ConvertTo(whenTrue, resultType, ternary.Then.Span),
      this.ConvertTo(whenFalse, resultType, ternary.Else.Span));
  }

  private IrExpr LowerAssignExpr(FunctionContext ctx, CppAssignExpr assign) {
    if (assign.Target is CppIdentExpr ident && ctx.Lookup(ident.Name) is { } local) {
      var val = this.ComputeAssignedValue(ctx, assign.Op, () => new IrLocalRef(local), local.Type, assign.Value, assign.Span);
      return new IrBind(local, val);
    }

    throw this.Unsupported(assign.Span, "non-local assignment in expression position");
  }

  private IrExpr LowerCast(FunctionContext ctx, CppCastExpr cast) {
    var target = this.ResolveType(cast.TargetType, isLocal: false, ctx.TypeSubst);
    var operand = this.LowerExpr(ctx, cast.Operand, target);
    if (cast.Kind == "bit_cast")
      return this.LowerBitCast(target, operand, cast.Span);

    if (cast.Kind == "reinterpret_cast") {
      if (operand.Type.IsView && target.IsView && operand.Type.Kind == target.Kind)
        return new IrIntrinsic(IrIntrinsicOp.Reinterpret, target, [operand]);

      if (operand.Type.IsNumeric && target.IsNumeric && operand.Type.Size == target.Size)
        return this.LowerBitCast(target, operand, cast.Span);

      throw this.Unsupported(cast.Span, $"reinterpret_cast<{target}>({operand.Type})");
    }

    return this.ConvertTo(operand, target, cast.Span);
  }

  private IrExpr LowerIndex(FunctionContext ctx, CppIndexExpr idx) {
    var target = this.LowerExpr(ctx, idx.Target, null);
    var index = this.ToIndex(this.LowerExpr(ctx, idx.Index, IrType.I32));
    if (target.Type.IsString)
      return new IrConvert(IrType.I8, new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [target, index]), IrConversion.Wrap);

    if (target.Type.IsList)
      return new IrCollection(IrCollectionOp.ListGet, target.Type.Element!, [target, index]);

    if (target.Type.IsSequence)
      return new IrArrayLoad(target, index);

    throw this.Unsupported(idx.Span, $"indexing {target.Type}");
  }

  private IrExpr LowerMember(FunctionContext ctx, CppMemberExpr mem) {
    var target = this.LowerExpr(ctx, mem.Target, null);
    if (target.Type.StructType is { } st && st.Field(mem.Member) is { } field)
      return new IrFieldLoad(target, field);

    throw this.Unresolved(mem.Span, $"{target.Type}.{mem.Member}");
  }

  private IrExpr LowerSizeof(FunctionContext ctx, CppSizeofExpr sizeofExpr) {
    var type = this.ResolveType(sizeofExpr.TargetType, isLocal: false, ctx.TypeSubst);
    return new IrConst(IrType.U64, SizeOfType(type));
  }

  private static long SizeOfType(IrType type) {
    if (type == IrType.Bool)
      return 1;

    if (type.IsNumeric)
      return type.Size;

    if (type.StructType is { } st)
      return st.Fields.Sum(f => SizeOfType(f.Type));

    return 8;
  }

  private IrExpr LowerInitListExpr(FunctionContext ctx, CppInitListExpr list, IrType? expected) {
    var targetType = list.Type is not null
      ? this.ResolveType(list.Type, isLocal: false, ctx.TypeSubst)
      : expected ?? throw this.Unsupported(list.Span, "untyped brace initializer");
    return this.LowerInitializer(ctx, targetType, list.Type, list);
  }

  // ---------------------------------------------------------------- calls & templates

  private IrExpr LowerCall(FunctionContext ctx, CppCallExpr call) {
    if (call.Callee is CppMemberExpr mem)
      return this.LowerMemberCall(ctx, mem, call.Arguments, call.Span);

    if (call.Callee is not CppIdentExpr ident)
      throw this.UnsupportedCall(call.Span, "indirect call");

    var rawName = StripLeadingColons(ident.Name);
    if (this.TryLowerStdIntrinsic(ctx, rawName, call.TypeArgs, call.Arguments, call.Span, out var intrinsic))
      return intrinsic!;

    var resolvedName = this.ResolveSymbolName(rawName);
    if (this._templates.TryGetValue(resolvedName, out var templateDecl))
      return this.LowerTemplateCall(ctx, templateDecl, call.TypeArgs, call.Arguments, call.Span);

    if (this._signatures.TryGetValue(resolvedName, out var sig)) {
      if (call.Arguments.Count != sig.ParamTypes.Count)
        throw this.UnsupportedCall(call.Span, $"{rawName} (expected {sig.ParamTypes.Count} args, got {call.Arguments.Count})");

      var args = new List<IrExpr>(sig.ParamTypes.Count);
      for (var i = 0; i < sig.ParamTypes.Count; ++i)
        args.Add(this.ConvertTo(this.LowerExpr(ctx, call.Arguments[i], sig.ParamTypes[i]), sig.ParamTypes[i], call.Arguments[i].Span));

      return new IrCall(sig.IrName, sig.ReturnType, args);
    }

    if (this._structs.TryGetValue(rawName, out var st))
      return this.LowerStructInit(ctx, IrType.Struct(st), call.Arguments, call.Span);

    throw this.UnsupportedCall(call.Span, rawName);
  }

  private IrExpr LowerTemplateCall(
    FunctionContext ctx,
    CppFuncDecl templateDecl,
    IReadOnlyList<CppTypeArg> explicitTypeArgs,
    IReadOnlyList<CppExpr> argExprs,
    SourceRange span) {
    if (argExprs.Count != templateDecl.Params.Count)
      throw this.UnsupportedCall(span, $"{templateDecl.Name} (expected {templateDecl.Params.Count} args, got {argExprs.Count})");

    var subst = new Dictionary<string, IrType>(StringComparer.Ordinal);
    var loweredArgs = new IrExpr?[argExprs.Count];

    if (explicitTypeArgs.Count > 0) {
      if (explicitTypeArgs.Count != templateDecl.TypeParams.Count)
        throw this.UnsupportedCall(span, $"{templateDecl.Name}<...> (wrong type argument count)");

      for (var i = 0; i < templateDecl.TypeParams.Count; ++i) {
        if (explicitTypeArgs[i].Type is not { } tRef)
          throw this.Unsupported(span, "non-type template argument");

        subst[templateDecl.TypeParams[i]] = this.ResolveType(tRef, isLocal: false, ctx.TypeSubst);
      }
    } else {
      for (var i = 0; i < argExprs.Count; ++i) {
        var lowered = this.LowerExpr(ctx, argExprs[i], null);
        loweredArgs[i] = lowered;
        this.DeduceTypeParam(templateDecl.Params[i].Type, lowered.Type, templateDecl.TypeParams, subst);
      }
    }

    foreach (var tp in templateDecl.TypeParams)
      if (!subst.ContainsKey(tp))
        throw this.UnsupportedCall(span, $"{templateDecl.Name} (could not deduce '{tp}')");

    var suffix = string.Join("__", templateDecl.TypeParams.Select(tp => MangleType(subst[tp])));
    var mangledName = $"{StripLeadingColons(templateDecl.Name)}__{suffix}";
    if (!this._signatures.TryGetValue(mangledName, out var sig)) {
      var retType = this.ResolveType(templateDecl.ReturnType, isLocal: false, subst);
      var paramTypes = templateDecl.Params.Select(p => this.ResolveType(p.Type, isLocal: false, subst)).ToList();
      sig = new(mangledName, retType, paramTypes, false);
      this._signatures[mangledName] = sig;
      var fn = this.LowerFunction(templateDecl, mangledName, false, subst);
      this._instantiatedTemplates[mangledName] = fn;
      this._functions.Add(fn);
    }

    var finalArgs = new List<IrExpr>(sig.ParamTypes.Count);
    for (var i = 0; i < sig.ParamTypes.Count; ++i) {
      var a = loweredArgs[i] ?? this.LowerExpr(ctx, argExprs[i], sig.ParamTypes[i]);
      finalArgs.Add(this.ConvertTo(a, sig.ParamTypes[i], argExprs[i].Span));
    }

    return new IrCall(sig.IrName, sig.ReturnType, finalArgs);
  }

  private void DeduceTypeParam(CppTypeRef paramType, IrType actualType, IReadOnlyList<string> typeParams, Dictionary<string, IrType> subst) {
    var name = StripStd(StripLeadingColons(paramType.Name));
    if (typeParams.Contains(name)) {
      subst.TryAdd(name, paramType.IsPointer && actualType.Element is not null ? actualType.Element : actualType);
      return;
    }

    if (name is "span" or "vector" or "array" && paramType.TypeArgs.Count >= 1 && paramType.TypeArgs[0].Type is { } elemRef && actualType.Element is { } actualElem)
      this.DeduceTypeParam(elemRef, actualElem, typeParams, subst);
    else if (name == "optional" && paramType.TypeArgs.Count == 1 && paramType.TypeArgs[0].Type is { } optRef && actualType.Underlying is { } actualUnder)
      this.DeduceTypeParam(optRef, actualUnder, typeParams, subst);
  }

  private static string MangleType(IrType type) => type.ToString()
    .Replace("[]", "_arr", StringComparison.Ordinal)
    .Replace("<", "_", StringComparison.Ordinal)
    .Replace(">", "", StringComparison.Ordinal)
    .Replace(" ", "_", StringComparison.Ordinal)
    .Replace("?", "_opt", StringComparison.Ordinal);

  // ---------------------------------------------------------------- conversions & helpers

  internal IrExpr PromoteNarrow(IrExpr expr) {
    if (expr.Type == IrType.Bool)
      return new IrConditional(expr, new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 0));

    if (expr.Type.IsEnum) {
      var under = new IrConvert(expr.Type.Integral, expr, IrConversion.Wrap);
      return under.Type.Bits < 32 ? new IrConvert(IrType.I32, under, IrConversion.Wrap) : under;
    }

    return expr.Type.IsInteger && expr.Type.Bits < 32
      ? new IrConvert(IrType.I32, expr, IrConversion.Wrap)
      : expr;
  }

  internal IrExpr ToBool(IrExpr expr) {
    if (expr.Type == IrType.Bool)
      return expr;

    if (expr.Type.IsOptional)
      return new IrUnary(IrUnaryOp.LogicalNot, new IrIsNone(expr));

    if (expr.Type.IsNullable)
      return new IrUnary(IrUnaryOp.LogicalNot, new IrIsNull(expr));

    var promoted = this.PromoteNarrow(expr);
    return new IrBinary(IrBinaryOp.NotEqual, promoted, this.ZeroExpr(promoted.Type));
  }

  internal IrExpr ToIndex(IrExpr expr) {
    var unwrapped = expr.Type.IsEnum ? new IrConvert(expr.Type.Integral, expr, IrConversion.Wrap) : expr;
    return unwrapped.Type == IrType.I32 ? unwrapped : new IrConvert(IrType.I32, unwrapped, IrConversion.Wrap);
  }

  internal IrExpr ConvertTo(IrExpr expr, IrType target, SourceRange span) {
    if (expr.Type == target)
      return expr;

    if (target == IrType.Bool)
      return this.ToBool(expr);

    if (target.IsOptional) {
      if (expr.Type == target.Underlying)
        return new IrSome(expr);

      return new IrSome(this.ConvertTo(expr, target.Underlying!, span));
    }

    if (expr.Type.IsList && target.IsArray && expr.Type.Element == target.Element)
      return new IrCollection(IrCollectionOp.ListCopyToArray, target, [expr]);

    if (expr.Type.IsArray && target.IsList && expr.Type.Element == target.Element)
      return new IrCollection(IrCollectionOp.ListFromArray, target, [expr]);

    if (target.IsView && expr.Type.IsSequence && expr.Type.Element == target.Element
        && (target.Kind == IrTypeKind.ReadOnlySpan || expr.Type.IsWritableSequence))
      return new IrIntrinsic(IrIntrinsicOp.ViewOf, target, [expr]);

    if (expr.Type.IsList && target.IsView && expr.Type.Element == target.Element) {
      var asArr = new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(target.Element!), [expr]);
      return new IrIntrinsic(IrIntrinsicOp.ViewOf, target, [asArr]);
    }

    if (expr.Type == IrType.Bool && target.IsNumeric)
      return new IrConditional(expr, this.OneExpr(target), this.ZeroExpr(target));

    if (expr.Type.IsEnum && target.IsNumeric) {
      var toUnderlying = new IrConvert(expr.Type.Integral, expr, IrConversion.Wrap);
      return toUnderlying.Type == target ? toUnderlying : this.ConvertTo(toUnderlying, target, span);
    }

    if (target.IsEnum && expr.Type.IsNumeric) {
      var toUnderlying = expr.Type == target.Integral ? expr : this.ConvertTo(expr, target.Integral, span);
      return new IrConvert(target, toUnderlying, IrConversion.Wrap);
    }

    if (expr.Type.IsNumeric && target.IsNumeric) {
      var contract = expr.Type.IsFloat && target.IsInteger ? IrConversion.Saturate : IrConversion.Wrap;
      return new IrConvert(target, expr, contract);
    }

    throw this.UnsupportedType(span, $"conversion from {expr.Type} to {target}");
  }

  internal static IrType UsualArithmeticType(IrType left, IrType right) {
    if (left == IrType.F64 || right == IrType.F64)
      return IrType.F64;

    if (left == IrType.F32 || right == IrType.F32)
      return IrType.F32;

    var l = left.IsInteger && left.Bits >= 32 ? left.Integral : IrType.I32;
    var r = right.IsInteger && right.Bits >= 32 ? right.Integral : IrType.I32;
    if (l == r)
      return l;

    if (l == IrType.U64 || r == IrType.U64)
      return IrType.U64;

    if (l == IrType.I64 || r == IrType.I64)
      return IrType.I64;

    return IrType.U32;
  }

  internal IrExpr ZeroExpr(IrType type) => type.Kind switch {
    IrTypeKind.Bool => new IrConst(IrType.Bool, 0),
    IrTypeKind.F32 or IrTypeKind.F64 => new IrConst(type, IrOperators.Bits(type, 0.0)),
    IrTypeKind.Struct => new IrStructDefault(type),
    _ => new IrConst(type, 0)
  };

  private IrExpr OneExpr(IrType type)
    => type.IsFloat ? new IrConst(type, IrOperators.Bits(type, 1.0)) : new IrConst(type, 1);

  private string ResolveSymbolName(string name) {
    var clean = StripLeadingColons(name);
    return this._qualifiedNames.TryGetValue(clean, out var mapped) ? mapped : clean;
  }

  private static string StripLeadingColons(string name)
    => name.StartsWith("::", StringComparison.Ordinal) ? name[2..] : name;

  internal static string StripStd(string name)
    => name.StartsWith("std::", StringComparison.Ordinal) ? name[5..] : name;

  internal CppDiagnosticException SyntaxError(SourceRange span, string msg)
    => new(Diagnostic.Create(CppFrontend.SyntaxError, span, msg));

  internal CppDiagnosticException Unsupported(SourceRange span, string construct)
    => new(Diagnostic.Create(CppFrontend.UnsupportedConstruct, span, construct));

  internal CppDiagnosticException UnsupportedType(SourceRange span, string typeName)
    => new(Diagnostic.Create(CppFrontend.UnsupportedType, span, typeName));

  internal CppDiagnosticException UnsupportedCall(SourceRange span, string callName)
    => new(Diagnostic.Create(CppFrontend.UnsupportedCall, span, callName));

  internal CppDiagnosticException Unresolved(SourceRange span, string symbol)
    => new(Diagnostic.Create(CppFrontend.UnresolvedSymbol, span, symbol));

}
