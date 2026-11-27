using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Batch;

/// <summary>Emits standalone Windows <c>cmd.exe</c> Batch scripts from verified IR modules.</summary>
public static partial class BatchEmitter {

  /// <summary>The Windows cmd.exe profile with command extensions enabled.</summary>
  public const string Profile = "cmd";

  /// <summary>The maximum number of constant array elements emitted for the cmd profile.</summary>
  public const int CmdArrayStorageBudget = 512;

  private const int _cmdLineLimit = 1024;
  private const string _nullToken = "__PLY_NULL__";
  private const string _noneToken = "__PLY_NONE__";

  /// <summary>A construct outside the cmd Batch target profile.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unsupported = new("PLY2850", DiagnosticSeverity.Error,
    ExitCode.UnsupportedConstruct, "'{0}' cannot be emitted for the cmd Batch profile", "batch.backend.profile");

  /// <summary>Checks that a verified module can be emitted as Windows cmd.exe Batch.</summary>
  /// <param name="module">The IR module to check.</param>
  /// <returns>The verification and backend diagnostics; empty when emission will succeed.</returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module) => Build(module).Diagnostics;

  /// <summary>Emits a single standalone batch file for a module accepted by <see cref="Check"/>.</summary>
  /// <param name="module">The IR module to emit.</param>
  /// <returns>The generated <c>kernel.bat</c> file.</returns>
  /// <exception cref="ArgumentException">The module fails verification or contains an unsupported construct.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module) {
    var (lines, diagnostics) = Build(module);
    if (diagnostics.Count > 0)
      throw new ArgumentException(diagnostics[0].Message, nameof(module));

    return [new("kernel.bat", string.Join("\r\n", lines) + "\r\n")];
  }

  private static (IReadOnlyList<string> Lines, IReadOnlyList<Diagnostic> Diagnostics) Build(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var verification = IrVerifier.Verify(module);
    if (verification.Count > 0)
      return ([], verification);

    try {
      if (KernelLines(module) is { } kernel)
        return (kernel, []);

      var lowered = SequenceFusion.Run(module);
      var emitter = new UnifiedEmitter(lowered);
      return (emitter.EmitAll(), []);
    } catch (Refusal error) {
      return ([], [Diagnostic.Create(Unsupported, null, error.Message)]);
    }
  }

  /// <summary>Returns the single public parameterless Int32 kernel function when the module has that shape, otherwise <see langword="null"/>.</summary>
  internal static IrFunction? Kernel(IrModule module)
    => module.Functions is [{ IsPublic: true, ReturnType: var type, Parameters.Count: 0 } function]
       && type == IrType.I32 && module.Constants.Count == 0 && module.Globals.Count == 0
       && module.Types.Count == 0 && module.Initializer is null ? function : null;

  private class Refusal(string detail) : Exception(detail);

  private sealed class KernelDiagnostic(string detail) : Refusal(detail);

  private sealed partial class UnifiedEmitter {

    private readonly IrModule _module;
    private readonly Dictionary<string, IrFunction> _functionsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _functionLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<IrClassType, int> _classIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrInterfaceType, int> _interfaceIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrStructType, int> _structIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrField, string> _fieldKeys = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrGlobal, string> _globalVars = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrModuleConst, string> _constVars = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrMethod, int> _virtualSlotIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrMethod, int> _interfaceMethodIds = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _pureSetlocalFunctions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedHelpers = new(StringComparer.Ordinal);
    private readonly HashSet<IrStructType> _neededStructCopies = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrStructType> _neededStructEqs = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrClassType> _neededRecordEqs = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrMethod> _neededVirtualDispatch = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrMethod> _neededInterfaceDispatch = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrEnumType> _neededEnumToString = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrEnumType> _neededEnumIsDefined = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrEnumType> _neededEnumParse = new(ReferenceEqualityComparer.Instance);
    private readonly List<string> _extraSubroutines = [];
    private readonly List<string> _functionLines = [];
    private int _nextLabel;
    private int _nextLambdaId;
    private int _nextForEachId;
    private int _constArraySlots;
    private bool _needsWorker;
    private bool _needsStringEscapes;
    private bool _needsInit;

    public UnifiedEmitter(IrModule module) {
      this._module = module;
      this.ValidateModule();
      this.BuildTables();
    }

    private void ValidateModule() {
      if (this._module.ForeignFunctions.Count > 0)
        throw new Refusal("foreign native interop functions");

      foreach (var type in this._module.Types) {
        if (type is IrStructType st)
          foreach (var field in st.Fields)
            RequireSupportedType(field.Type);
        else if (type is IrClassType cl)
          foreach (var field in cl.Fields)
            RequireSupportedType(field.Type);
      }

      foreach (var constant in this._module.Constants)
        RequireSupportedType(constant.Type);

      foreach (var global in this._module.Globals)
        RequireSupportedType(global.Type);

      foreach (var function in this._module.Functions) {
        RequireSupportedType(function.ReturnType);
        foreach (var p in function.Parameters)
          RequireSupportedType(p.Type);
        foreach (var l in function.Locals)
          RequireSupportedType(l.Type);
      }
    }

    private static void RequireSupportedType(IrType type) {
      if (type.IsBigInt)
        throw new Refusal("BigInt");
      if (type.IsFloat)
        throw new Refusal(type.ToString());
      if (type.Element is { } elem)
        RequireSupportedType(elem);
      if (type.Key is { } key)
        RequireSupportedType(key);
      if (type.Underlying is { } under)
        RequireSupportedType(under);
      if (type.Signature is { } sig) {
        RequireSupportedType(sig.Result);
        foreach (var param in sig.Parameters)
          RequireSupportedType(param);
      }
    }

    private void BuildTables() {
      var nextTypeId = 1;
      foreach (var ex in IrExceptionClass.All) {
        this._classIds[ex] = nextTypeId++;
        foreach (var field in ex.Fields)
          this._fieldKeys[field] = "f_" + field.Name;
      }

      foreach (var decl in this._module.Types) {
        switch (decl) {
          case IrClassType cl:
            this._classIds[cl] = nextTypeId++;
            var fi = 0;
            foreach (var field in cl.Fields)
              this._fieldKeys[field] = $"f{this._classIds[cl]}_{fi++}";
            break;
          case IrInterfaceType iface:
            this._interfaceIds[iface] = nextTypeId++;
            foreach (var method in iface.Methods)
              this._interfaceMethodIds[method] = this._interfaceMethodIds.Count + 1;
            break;
          case IrStructType st:
            this._structIds[st] = this._structIds.Count + 1;
            var sfi = 0;
            foreach (var field in st.Fields)
              this._fieldKeys[field] = $"sf{sfi++}";
            break;
        }
      }

      foreach (var decl in this._module.Types.OfType<IrClassType>())
        foreach (var method in decl.Methods)
          if (method.IsVirtual && ReferenceEquals(method.Root, method))
            this._virtualSlotIds.TryAdd(method, this._virtualSlotIds.Count + 1);

      var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var fnIndex = 0;
      foreach (var function in this._module.Functions) {
        this._functionsByName[function.Name] = function;
        var baseName = Sanitize(function.Name);
        var prefix = IrSequences.IsIterator(function) ? "seq_" : "fn_";
        var candidate = prefix + baseName;
        if (!usedLabels.Add(candidate)) {
          candidate = $"{prefix}{baseName}_{fnIndex}";
          usedLabels.Add(candidate);
        }

        this._functionLabels[function.Name] = candidate;
        ++fnIndex;
      }

      for (var i = 0; i < this._module.Globals.Count; ++i)
        this._globalVars[this._module.Globals[i]] = "_G_" + i.ToString(CultureInfo.InvariantCulture);

      for (var i = 0; i < this._module.Constants.Count; ++i)
        this._constVars[this._module.Constants[i]] = "_K_" + i.ToString(CultureInfo.InvariantCulture);

      this.AnalyzePureSetlocalFunctions();
    }

    private void AnalyzePureSetlocalFunctions() {
      var callGraph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
      var stateful = new HashSet<string>(StringComparer.Ordinal);

      foreach (var fn in this._module.Functions) {
        var callees = new HashSet<string>(StringComparer.Ordinal);
        callGraph[fn.Name] = callees;
        if (IrSequences.IsIterator(fn) || fn.IsAsync || fn.ReturnType.IsString || fn.ReturnType.IsStruct || fn.ReturnType.IsArray || fn.ReturnType.IsObject || fn.ReturnType.IsCollection) {
          stateful.Add(fn.Name);
          continue;
        }

        if (IsDirectlyStateful(fn.Body, callees))
          stateful.Add(fn.Name);
      }

      // Compute recursive functions (cycles in call graph).
      foreach (var fn in this._module.Functions) {
        if (Reaches(fn.Name, fn.Name, callGraph))
          stateful.Add(fn.Name);
      }

      // Propagate statefulness backwards along call edges.
      var changed = true;
      while (changed) {
        changed = false;
        foreach (var (caller, callees) in callGraph) {
          if (stateful.Contains(caller))
            continue;
          if (callees.Any(stateful.Contains)) {
            stateful.Add(caller);
            changed = true;
          }
        }
      }

      foreach (var fn in this._module.Functions)
        if (!stateful.Contains(fn.Name))
          this._pureSetlocalFunctions.Add(fn.Name);
    }

    private static bool Reaches(string start, string target, Dictionary<string, HashSet<string>> graph) {
      var visited = new HashSet<string>(StringComparer.Ordinal);
      var stack = new Stack<string>();
      if (graph.TryGetValue(start, out var direct))
        foreach (var next in direct)
          stack.Push(next);

      while (stack.Count > 0) {
        var current = stack.Pop();
        if (current == target)
          return true;
        if (visited.Add(current) && graph.TryGetValue(current, out var edges))
          foreach (var next in edges)
            stack.Push(next);
      }

      return false;
    }

    private static bool IsDirectlyStateful(IrBlock body, HashSet<string> callees) {
      foreach (var stmt in IrSequences.Statements(body)) {
        switch (stmt) {
          case IrGlobalAssign or IrStore or IrFieldStore or IrThrow or IrRethrow or IrTry or IrUsing
            or IrForEachCollection or IrForEachSequence or IrYield or IrYieldBreak or IrYieldFrom:
            return true;
        }

        foreach (var expr in IrTree.Expressions(stmt))
          foreach (var node in IrTree.Nodes(expr)) {
            switch (node) {
              case IrCall call:
                callees.Add(call.Function);
                if (IrExceptionClass.ConstructorOf(call.Function) is not null)
                  return true;
                break;
              case IrNew or IrMethodCall or IrStructNew or IrStructDefault or IrNewArray or IrArrayLoad or IrArrayLength
                or IrLambda or IrFunctionRef or IrBoundMethod or IrInvoke or IrCollection or IrArraySequence
                or IrAsync or IrAwait or IrThrowExpression or IrStrConst or IrCast or IrUnwrap
                or IrForeignCall or IrLastError:
                return true;
              case IrFieldLoad load when load.Target.Type.IsObject:
                return true;
              case IrUnary { Op: IrUnaryOp.NegateChecked }:
                return true;
              case IrBinary bin when IrOperators.Traps(bin.Op) || bin.Op is IrBinaryOp.Divide or IrBinaryOp.Remainder:
                return true;
              case IrConvert { Contract: IrConversion.Trap }:
                return true;
              case IrStep { Trap: true } or IrStep { Target: not IrLocalRef }:
                return true;
              case IrIntrinsic intrinsic when intrinsic.Op is not (IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight
                or IrIntrinsicOp.ParallelBitExtract or IrIntrinsicOp.ParallelBitDeposit
                or IrIntrinsicOp.PopCount or IrIntrinsicOp.LeadingZeroCount or IrIntrinsicOp.TrailingZeroCount
                or IrIntrinsicOp.LeadingOneCount or IrIntrinsicOp.TrailingOneCount or IrIntrinsicOp.Log2
                or IrIntrinsicOp.Parity or IrIntrinsicOp.ByteSwap or IrIntrinsicOp.BitReverse or IrIntrinsicOp.ReverseBitsInBytes
                or IrIntrinsicOp.IsPow2 or IrIntrinsicOp.RoundUpPow2 or IrIntrinsicOp.AbsWrap
                or IrIntrinsicOp.Min or IrIntrinsicOp.Max or IrIntrinsicOp.Sign or IrIntrinsicOp.AddSaturate
                or IrIntrinsicOp.SubSaturate or IrIntrinsicOp.MulSaturate or IrIntrinsicOp.AddOverflows
                or IrIntrinsicOp.SubOverflows or IrIntrinsicOp.MulOverflows or IrIntrinsicOp.IsolateLowestSet
                or IrIntrinsicOp.ClearLowestSet or IrIntrinsicOp.MaskToLowestSet or IrIntrinsicOp.HighWord
                or IrIntrinsicOp.JoinWords or IrIntrinsicOp.Nand or IrIntrinsicOp.Nor or IrIntrinsicOp.Xnor
                or IrIntrinsicOp.AndNot or IrIntrinsicOp.OrNot or IrIntrinsicOp.Implies or IrIntrinsicOp.BitSelect
                or IrIntrinsicOp.Majority or IrIntrinsicOp.EnumHasFlag or IrIntrinsicOp.EnumIsDefined
                or IrIntrinsicOp.BigMul or IrIntrinsicOp.MulHigh or IrIntrinsicOp.AddCarry or IrIntrinsicOp.CarryOut
                or IrIntrinsicOp.SubBorrow or IrIntrinsicOp.BorrowOut or IrIntrinsicOp.ISqrt):
                return true;
            }
          }
      }

      return false;
    }

    private static string Sanitize(string name) {
      var sb = new StringBuilder(name.Length);
      foreach (var ch in name)
        sb.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
      return sb.Length == 0 ? "f" : sb.ToString();
    }

    private string NewLabel(string prefix = "_L") => prefix + this._nextLabel++.ToString(CultureInfo.InvariantCulture);

    private void RequireHelper(string name) => this._usedHelpers.Add(name);

    private static void CheckLineLength(string line) {
      if (line.Length > _cmdLineLimit)
        throw new Refusal($"a generated command longer than {_cmdLineLimit} bytes");
    }

    public IReadOnlyList<string> EmitAll() {
      // Emit all functions first so we know exactly which helpers, trampolines, and init code are needed.
      foreach (var function in this._module.Functions) {
        if (this.IsAbstractOrLibraryStub(function))
          continue;

        var writer = new FunctionWriter(this, function, this._pureSetlocalFunctions.Contains(function.Name), false, null, null);
        writer.EmitFunction();
        this._functionLines.AddRange(writer.Lines);
      }

      this._needsInit = this._module.Constants.Count > 0
        || this._module.Globals.Count > 0
        || this._module.Initializer is not null
        || this._module.Types.OfType<IrClassType>().Any()
        || this._usedHelpers.Contains("trap");

      var result = new List<string>();
      this.EmitHeader(result);
      this.EmitDispatcher(result);
      if (this._needsWorker)
        this.EmitWorkerEntrypoint(result);
      if (this._needsInit)
        this.EmitInitRoutine(result);

      result.AddRange(this._functionLines);
      result.AddRange(this._extraSubroutines);
      this.EmitDynamicDispatchTrampolines(result);
      this.EmitStructHelpers(result);
      this.EmitEnumHelpers(result);
      this.EmitRuntimeHelpers(result);

      foreach (var line in result)
        CheckLineLength(line);

      return result;
    }

    private bool IsAbstractOrLibraryStub(IrFunction function) {
      foreach (var iface in this._module.Types.OfType<IrInterfaceType>())
        if (iface.Methods.Any(m => m.Function == function.Name))
          return true;

      foreach (var cl in this._module.Types.OfType<IrClassType>())
        if (cl.Methods.Any(m => m.Function == function.Name && m.Dispatch == IrDispatch.Abstract))
          return true;

      return false;
    }

    private void EmitHeader(List<string> lines) {
      lines.Add("@echo off");
      if (this._needsStringEscapes) {
        lines.Add("setlocal EnableExtensions DisableDelayedExpansion");
        lines.Add("set \"_BANG=!\"");
        lines.Add("set \"_CARET=^\"");
        lines.Add("set _QUOT=\"");
        lines.Add("setlocal EnableDelayedExpansion");
      } else {
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
      }

      if (this._needsWorker)
        lines.Add("if \"%~1\"==\"__ply_worker\" goto :__ply_worker");

      lines.Add("set \"_SP=0\"");
      lines.Add("set \"_HID=0\"");
      lines.Add("set \"_CS=0\"");
      lines.Add("set \"_WID=0\"");
      lines.Add("set \"_EX_ACTIVE=0\"");
      lines.Add("set \"_EX_STK_N=0\"");
      if (this._needsInit)
        lines.Add("call :__ply_init");
    }

    private void EmitDispatcher(List<string> lines) {
      var publicFns = this._module.Functions.Where(f => f.IsPublic).ToList();
      var defaultKernel = publicFns.FirstOrDefault(f => f.Parameters.Count == 0 && f.ReturnType == IrType.I32)
        ?? (publicFns.Count == 1 && publicFns[0].Parameters.Count == 0 ? publicFns[0] : null);

      if (defaultKernel is not null)
        lines.Add($"if \"%~1\"==\"\" goto :__ply_default");

      foreach (var fn in publicFns)
        lines.Add($"if \"%~1\"==\"{fn.Name}\" goto :__ply_cli_{Sanitize(fn.Name)}");

      if (defaultKernel is not null) {
        lines.Add(":__ply_default");
        lines.Add($"call :{this._functionLabels[defaultKernel.Name]}");
        lines.Add("if \"!_EX_ACTIVE!\"==\"1\" (>&2 echo PLY_BATCH_TRAP=!_EX_TRAP! & exit /b 97)");
        if (defaultKernel.ReturnType == IrType.I32) {
          lines.Add("exit /b !_RET!");
        } else {
          this.EmitCliReturnFormat(lines, defaultKernel.ReturnType);
          lines.Add("exit /b 0");
        }
      } else {
        lines.Add(">&2 echo PLY_BATCH_ERROR=UnknownEntryPoint");
        lines.Add("exit /b 1");
      }

      foreach (var fn in publicFns) {
        lines.Add($":__ply_cli_{Sanitize(fn.Name)}");
        lines.Add("shift");
        var callArgs = new List<string>(fn.Parameters.Count);
        for (var i = 0; i < fn.Parameters.Count; ++i) {
          var param = fn.Parameters[i];
          var argVar = "_CLI_A" + i.ToString(CultureInfo.InvariantCulture);
          this.EmitCliArgParse(lines, param.Type, argVar, i);
          callArgs.Add($"\"!{argVar}!\"");
          if (i >= 8 && i + 1 < fn.Parameters.Count)
            lines.Add("shift");
        }

        var argsSuffix = callArgs.Count == 0 ? "" : " " + string.Join(" ", callArgs);
        lines.Add($"call :{this._functionLabels[fn.Name]}{argsSuffix}");
        lines.Add("if \"!_EX_ACTIVE!\"==\"1\" (>&2 echo PLY_BATCH_TRAP=!_EX_TRAP! & exit /b 97)");
        var retType = fn.IsAsync ? fn.ReturnType.Element! : fn.ReturnType;
        this.EmitCliReturnFormat(lines, retType);
        lines.Add("exit /b 0");
      }
    }

    private void EmitCliArgParse(List<string> lines, IrType type, string dstVar, int paramIndex) {
      var slot = paramIndex < 9 ? (paramIndex + 1).ToString(CultureInfo.InvariantCulture) : "9";
      var rawExpr = $"%~{slot}";
      if (type == IrType.Bool) {
        lines.Add($"set \"_raw={rawExpr}\"");
        lines.Add($"if /i \"!_raw!\"==\"true\" (set \"{dstVar}=1\") else if \"!_raw!\"==\"1\" (set \"{dstVar}=1\") else (set \"{dstVar}=0\")");
        return;
      }

      if (type.IsOptional) {
        lines.Add($"set \"_raw={rawExpr}\"");
        var parseLbl = this.NewLabel("_cp");
        var doneLbl = this.NewLabel("_cd");
        lines.Add($"if \"!_raw!\"==\"\" (set \"{dstVar}={_noneToken}\" & goto :{doneLbl})");
        lines.Add($"if /i \"!_raw!\"==\"null\" (set \"{dstVar}={_noneToken}\" & goto :{doneLbl})");
        lines.Add($":{parseLbl}");
        this.EmitCliArgParseFromVar(lines, type.Underlying!, dstVar, "_raw");
        lines.Add($":{doneLbl}");
        return;
      }

      if (type.IsArray) {
        lines.Add($"set \"_raw={rawExpr}\"");
        lines.Add("set /a \"_HID+=1\"");
        lines.Add($"set \"{dstVar}=!_HID!\"");
        lines.Add($"set \"_AL_!_HID!=0\"");
        var doneArr = this.NewLabel("_ca");
        lines.Add($"if not defined _raw goto :{doneArr}");
        lines.Add($"if /i \"!_raw!\"==\"null\" (set \"{dstVar}={_nullToken}\" & goto :{doneArr})");
        lines.Add("set \"_raw=!_raw::=,!\"");
        lines.Add("set \"_ai=0\"");
        lines.Add("for %%E in (!_raw!) do (");
        lines.Add("  set \"_ae=%%~E\"");
        var elemVar = "_CE";
        var inner = new List<string>();
        this.EmitCliArgParseFromVar(inner, type.Element!, elemVar, "_ae");
        foreach (var l in inner)
          lines.Add("  " + l);
        lines.Add($"  for %%H in (!{dstVar}!) do set \"_A_%%H_!_ai!=!{elemVar}!\"");
        lines.Add("  set /a \"_ai+=1\"");
        lines.Add(")");
        lines.Add($"for %%H in (!{dstVar}!) do set \"_AL_%%H=!_ai!\"");
        lines.Add($":{doneArr}");
        return;
      }

      if (type.IsStruct) {
        var st = type.StructType!;
        lines.Add($"set \"_raw={rawExpr}\"");
        lines.Add("set \"_raw=!_raw::=,!\"");
        lines.Add("set /a \"_HID+=1\"");
        lines.Add($"set \"{dstVar}=!_HID!\"");
        lines.Add("set \"_fi=0\"");
        lines.Add("for %%E in (!_raw!) do (");
        lines.Add("  set \"_fe_!_fi!=%%~E\"");
        lines.Add("  set /a \"_fi+=1\"");
        lines.Add(")");
        for (var fi = 0; fi < st.Fields.Count; ++fi) {
          var field = st.Fields[fi];
          lines.Add($"set \"_fraw=!_fe_{fi}!\"");
          this.EmitCliArgParseFromVar(lines, field.Type, "_fval", "_fraw");
          lines.Add($"for %%H in (!{dstVar}!) do set \"_O_%%H_{this._fieldKeys[field]}=!_fval!\"");
        }

        return;
      }

      lines.Add($"set \"_raw={rawExpr}\"");
      this.EmitCliArgParseFromVar(lines, type, dstVar, "_raw");
    }

    private void EmitCliArgParseFromVar(List<string> lines, IrType type, string dstVar, string srcVar) {
      var integral = type.Integral;
      if (integral == IrType.Bool) {
        lines.Add($"if /i \"!{srcVar}!\"==\"true\" (set \"{dstVar}=1\") else if \"!{srcVar}!\"==\"1\" (set \"{dstVar}=1\") else (set \"{dstVar}=0\")");
      } else if (integral == IrType.U32) {
        this.RequireHelper("parse_u32");
        lines.Add($"call :__ply_parse_u32 \"!{srcVar}!\"");
        lines.Add($"set \"{dstVar}=!_RET!\"");
      } else if (integral == IrType.I64) {
        this.RequireHelper("parse_i64");
        lines.Add($"call :__ply_parse_i64 \"!{srcVar}!\"");
        lines.Add($"set \"{dstVar}=!_RET!\"");
      } else if (integral == IrType.U64) {
        this.RequireHelper("parse_u64");
        lines.Add($"call :__ply_parse_u64 \"!{srcVar}!\"");
        lines.Add($"set \"{dstVar}=!_RET!\"");
      } else if (integral.IsInteger) {
        lines.Add($"set /a \"{dstVar}=!{srcVar}!\"");
        this.EmitNarrowMask(lines, integral, dstVar);
      } else if (type.IsString) {
        lines.Add($"if /i \"!{srcVar}!\"==\"null\" (set \"{dstVar}={_nullToken}\") else (set /a \"_HID+=1\" & set \"{dstVar}=s!_HID!\" & set \"_S_s!_HID!=!{srcVar}!\")");
      } else {
        lines.Add($"set \"{dstVar}=!{srcVar}!\"");
      }
    }

    private void EmitCliReturnFormat(List<string> lines, IrType returnType) {
      if (returnType == IrType.Void)
        return;

      var integral = returnType.Integral;
      if (integral == IrType.Bool) {
        lines.Add("if \"!_RET!\"==\"1\" (echo True) else (echo False)");
      } else if (integral == IrType.U32) {
        this.RequireHelper("fmt_u32");
        lines.Add("call :__ply_fmt_u32 \"!_RET!\"");
        lines.Add("echo(!_RET!");
      } else if (integral == IrType.I64) {
        this.RequireHelper("fmt_i64");
        lines.Add("call :__ply_fmt_i64 \"!_RET!\"");
        lines.Add("echo(!_RET!");
      } else if (integral == IrType.U64) {
        this.RequireHelper("fmt_u64");
        lines.Add("call :__ply_fmt_u64 \"!_RET!\"");
        lines.Add("echo(!_RET!");
      } else if (returnType.IsString) {
        var nullLbl = this.NewLabel("_cs_nl");
        var dnLbl = this.NewLabel("_cs_dn");
        lines.Add($"if \"!_RET!\"==\"{_nullToken}\" goto :{nullLbl}");
        lines.Add("for %%S in (!_RET!) do echo(!_S_%%S!");
        lines.Add($"goto :{dnLbl}");
        lines.Add($":{nullLbl}");
        lines.Add("echo(");
        lines.Add($":{dnLbl}");
      } else {
        lines.Add("echo(!_RET!");
      }
    }

    private void EmitWorkerEntrypoint(List<string> lines) {
      lines.Add(":__ply_worker");
      lines.Add("set \"_SP=0\"");
      lines.Add("set \"_HID=0\"");
      lines.Add("set \"_CS=0\"");
      lines.Add("set \"_WID=0\"");
      lines.Add("set \"_EX_ACTIVE=0\"");
      lines.Add("set \"_EX_STK_N=0\"");
      if (this._needsInit)
        lines.Add("call :__ply_init");
      lines.Add("set \"_w_res=%~2\"");
      lines.Add("set \"_w_err=%~3\"");
      lines.Add("set \"_w_done=%~4\"");
      lines.Add("set \"_w_fn=%~5\"");
      lines.Add("call :!_w_fn! \"%~6\" \"%~7\" \"%~8\" \"%~9\"");
      lines.Add("if \"!_EX_ACTIVE!\"==\"1\" goto :__ply_worker_err");
      lines.Add(">\"!_w_res!\" echo(!_RET!");
      lines.Add("goto :__ply_worker_done");
      lines.Add(":__ply_worker_err");
      lines.Add(">\"!_w_err!\" echo(!_EX_TYPE!:!_EX_TRAP!");
      lines.Add(":__ply_worker_done");
      lines.Add(">\"!_w_done!\" echo(1");
      lines.Add("exit /b 0");
    }

    private void EmitInitRoutine(List<string> lines) {
      lines.Add(":__ply_init");
      // Emit _ISA_ type hierarchy table for all library exceptions + user classes.
      foreach (var ex in IrExceptionClass.All) {
        var subId = this._classIds[ex];
        foreach (var anc in ex.Lineage)
          lines.Add($"set \"_ISA_{subId}_{this._classIds[anc]}=1\"");
      }

      foreach (var cl in this._module.Types.OfType<IrClassType>()) {
        var subId = this._classIds[cl];
        foreach (var anc in cl.Lineage)
          lines.Add($"set \"_ISA_{subId}_{this._classIds[anc]}=1\"");
        foreach (var iface in cl.AllInterfaces)
          lines.Add($"set \"_ISA_{subId}_{this._interfaceIds[iface]}=1\"");
      }

      // Emit module constants.
      foreach (var constant in this._module.Constants) {
        var varName = this._constVars[constant];
        if (constant.Type.IsArray) {
          if (constant.Values.Count > CmdArrayStorageBudget || constant.Strings.Count > CmdArrayStorageBudget)
            throw new Refusal($"array storage exceeding {CmdArrayStorageBudget} elements");
          lines.Add("set /a \"_HID+=1\"");
          lines.Add($"set \"{varName}=!_HID!\"");
          var elemType = constant.Type.Element!;
          if (elemType.IsString) {
            lines.Add($"set \"_AL_!_HID!={constant.Strings.Count.ToString(CultureInfo.InvariantCulture)}\"");
            for (var i = 0; i < constant.Strings.Count; ++i) {
              var sLit = this.EscapeStringLiteral(constant.Strings[i]);
              lines.Add("set /a \"_HID+=1\"");
              lines.Add($"set \"_S_s!_HID!={sLit}\"");
              lines.Add($"for %%H in (!{varName}!) do set \"_A_%%H_{i.ToString(CultureInfo.InvariantCulture)}=s!_HID!\"");
            }
          } else {
            lines.Add($"set \"_AL_!_HID!={constant.Values.Count.ToString(CultureInfo.InvariantCulture)}\"");
            for (var i = 0; i < constant.Values.Count; ++i) {
              var formatted = FormatConstValue(elemType, constant.Values[i]);
              lines.Add($"for %%H in (!{varName}!) do set \"_A_%%H_{i.ToString(CultureInfo.InvariantCulture)}={formatted}\"");
            }
          }
        } else if (constant.Type.IsString) {
          var sLit = this.EscapeStringLiteral(constant.Strings[0]);
          lines.Add("set /a \"_HID+=1\"");
          lines.Add($"set \"_S_s!_HID!={sLit}\"");
          lines.Add($"set \"{varName}=s!_HID!\"");
        } else {
          lines.Add($"set \"{varName}={FormatConstValue(constant.Type, constant.Values[0])}\"");
        }
      }

      // Emit module globals in declaration order.
      if (this._module.Globals.Count > 0) {
        var dummyFn = new IrFunction("__ply_globals", false, IrType.Void, [], [], new([]));
        var initWriter = new FunctionWriter(this, dummyFn, false, false, null, "__ply_globals");
        lines.Add("set /a \"_SP+=1\"");
        foreach (var global in this._module.Globals) {
          var gVar = this._globalVars[global];
          if (global.Initializer is { } initExpr) {
            var valSlot = initWriter.EmitExpr(initExpr, 0);
            initWriter.EmitReadSlotToGlobal(valSlot, gVar);
          } else {
            initWriter.EmitDefaultValue(global.Type, 0);
            initWriter.EmitReadSlotToGlobal(0, gVar);
          }
        }

        lines.AddRange(initWriter.Lines);
        lines.Add("set /a \"_SP-=1\"");
      }

      if (this._module.Initializer is { } initializer)
        lines.Add($"call :{this._functionLabels[initializer]}");

      lines.Add("goto :eof");
    }

    private string EscapeStringLiteral(string text) {
      foreach (var ch in text)
        if (ch is < ' ' or > '~')
          throw new Refusal("non-printable or non-ASCII text in string literal");

      var sb = new StringBuilder(text.Length * 2);
      foreach (var ch in text) {
        switch (ch) {
          case '%':
            sb.Append("%%");
            break;
          case '!':
            this._needsStringEscapes = true;
            sb.Append("!_BANG!");
            break;
          case '^':
            this._needsStringEscapes = true;
            sb.Append("!_CARET!");
            break;
          case '"':
            this._needsStringEscapes = true;
            sb.Append("!_QUOT!");
            break;
          default:
            sb.Append(ch);
            break;
        }
      }

      return sb.ToString();
    }

    private static string FormatConstValue(IrType type, long value) {
      var integral = type.Integral;
      if (integral == IrType.Bool)
        return value == 0 ? "0" : "1";
      if (integral.IsLong) {
        var hi = unchecked((int)(value >> 32));
        var lo = unchecked((int)value);
        return $"{hi.ToString(CultureInfo.InvariantCulture)}:{lo.ToString(CultureInfo.InvariantCulture)}";
      }

      var wrapped = unchecked((int)IrOperators.Wrap(integral, value));
      return wrapped.ToString(CultureInfo.InvariantCulture);
    }

    private static string SafeInt32Literal(int value)
      => value == int.MinValue ? "-2147483647-1" : value.ToString(CultureInfo.InvariantCulture);

    private void EmitNarrowMask(List<string> lines, IrType integral, string varName) {
      switch (integral.Kind) {
        case IrTypeKind.U8:
          lines.Add($"set /a \"{varName}=({varName})&255\"");
          break;
        case IrTypeKind.I8:
          lines.Add($"set /a \"{varName}=((({varName})&255)^128)-128\"");
          break;
        case IrTypeKind.U16:
          lines.Add($"set /a \"{varName}=({varName})&65535\"");
          break;
        case IrTypeKind.I16:
          lines.Add($"set /a \"{varName}=((({varName})&65535)^32768)-32768\"");
          break;
      }
    }

  }

}
