using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VBScript;

public static partial class VBScriptEmitter {

  private sealed partial class Writer(VBScriptProfile profile, VBScriptEmitOptions options) {

    private readonly VBScriptProfile _profile = profile;
    private readonly VBScriptEmitOptions _options = options;
    private readonly HashSet<string> _usedHelpers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _moduleTaken = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _functionNames = new(StringComparer.Ordinal);
    private readonly Dictionary<IrModuleConst, string> _constNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrGlobal, string> _globalNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrEnumType, Dictionary<IrEnumMember, string>> _enumMembers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrTypeDeclaration, string> _typeNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrTypeDeclaration, Dictionary<IrField, string>> _fieldNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrMethod, string> _methodSlotNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrConstructor, string> _ctorFactories = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<IrLocal, string> _locals = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _localTaken = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _declaredDims = [];
    private readonly Stack<LoopFrame> _loops = new();
    private readonly Stack<TryFrame> _tryFrames = new();
    private readonly List<string> _syntheticClasses = [];
    private readonly Dictionary<string, string> _fnRefClasses = new(StringComparer.Ordinal);
    private readonly Dictionary<(IrMethod Method, IrMethodCallKind Kind), string> _boundMethodClasses = [];

    private HashSet<IrLocal> _boxedLocals = new(ReferenceEqualityComparer.Instance);
    private HashSet<IrLocal> _addrBoxedLocals = new(ReferenceEqualityComparer.Instance);
    private HashSet<IrLocal> _arrayBoxedLocals = new(ReferenceEqualityComparer.Instance);
    private List<(IrLocal Param, string RawName, string BoxName)> _arrayParamSyncs = [];
    private Dictionary<string, IrFunction> _functionMap = new(StringComparer.Ordinal);

    private IrModule _module = null!;
    private IrFunction? _currentFunction;
    private string _currentFunctionVbsName = "";
    private IrType _currentReturnType = IrType.Void;
    private bool _inAsyncFunction;
    private string? _iteratorResultListVar;
    private int _tempCounter;
    private int _syntheticCounter;
    private int _tryBodyDepth;
    private string? _activeCatchErrVar;

    public List<Diagnostic> Problems { get; } = [];

    public IReadOnlyList<GeneratedFile> EmitModule(IrModule module) {
      this._module = module;
      if (module.ForeignFunctions.Count > 0)
        this.ReportConstruct($"foreign function '{module.ForeignFunctions[0].Name}'");

      this.AssignNames(module);

      this._functionMap = module.Functions.ToDictionary(f => f.Name, StringComparer.Ordinal);
      var classBlocks = this.EmitTypeDeclarations(module, this._functionMap);
      var functionBlocks = new List<string>(module.Functions.Count);
      foreach (var function in module.Functions)
        functionBlocks.Add(this.EmitFunction(function));

      var (constSection, dimSection, initBody) = this.EmitModuleData(module);

      var text = new StringBuilder();
      text.Append("Option Explicit\n");

      if (constSection.Length > 0)
        text.Append('\n').Append(constSection);
      if (dimSection.Length > 0)
        text.Append('\n').Append(dimSection);

      var helpers = VBScriptHelpers.Resolve(this._usedHelpers);
      if (helpers.Count > 0) {
        text.Append('\n');
        foreach (var helper in helpers)
          text.Append(helper).Append("\n\n");
      }

      if (classBlocks.Length > 0)
        text.Append('\n').Append(classBlocks);

      if (this._syntheticClasses.Count > 0) {
        text.Append('\n');
        foreach (var synthetic in this._syntheticClasses)
          text.Append(synthetic).Append('\n');
      }

      foreach (var fn in functionBlocks)
        if (fn.Length > 0)
          text.Append('\n').Append(fn);

      if (initBody.Length > 0) {
        text.Append("\nSub ply_init()\n");
        text.Append(initBody);
        text.Append("End Sub\nCall ply_init()\n");
      }

      var fileName = VBScriptNaming.ScriptFileName(module.Name, this._options.ScriptName);
      return [new(fileName, text.ToString())];
    }

    private void AssignNames(IrModule module) {
      foreach (var @enum in module.Types.OfType<IrEnumType>()) {
        var map = new Dictionary<IrEnumMember, string>(ReferenceEqualityComparer.Instance);
        var prefix = VBScriptNaming.Identifier(@enum.Name, "e_") + "_";
        foreach (var member in @enum.Members) {
          var name = VBScriptNaming.Free(prefix + VBScriptNaming.Identifier(member.Name, "m_"), this._moduleTaken);
          map[member] = name;
        }
        this._enumMembers[@enum] = map;
      }

      foreach (var st in module.Types.OfType<IrStructType>()) {
        var name = VBScriptNaming.Free(VBScriptNaming.Identifier(st.Name, "t_"), this._moduleTaken);
        this._typeNames[st] = name;
        this.AssignFieldNames(st, st.Fields);
      }

      foreach (var iface in module.Types.OfType<IrInterfaceType>()) {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var method in iface.Methods)
          this._methodSlotNames[method] = VBScriptNaming.Free(VBScriptNaming.Identifier(method.Name, "m_"), taken);
      }

      var classes = OrderClasses(module.Types.OfType<IrClassType>().Where(c => !c.IsLibrary).ToList());
      foreach (var cls in classes) {
        var name = VBScriptNaming.Free(VBScriptNaming.Identifier(cls.Name, "c_"), this._moduleTaken);
        this._typeNames[cls] = name;
        this.AssignFieldNames(cls, cls.AllFields);

        var slotTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ply_is", "ply_equals", "ply_kind" };
        foreach (var f in this._fieldNames[cls].Values)
          slotTaken.Add(f);

        foreach (var method in cls.Methods) {
          if (method.IsStatic)
            continue;
          if (method.Overrides is { } over && this._methodSlotNames.TryGetValue(over.Root, out var baseSlot)) {
            this._methodSlotNames[method] = baseSlot;
            slotTaken.Add(baseSlot);
            continue;
          }
          if (method.Implements.Count > 0 && this._methodSlotNames.TryGetValue(method.Implements[0], out var ifaceSlot)) {
            this._methodSlotNames[method] = ifaceSlot;
            slotTaken.Add(ifaceSlot);
            continue;
          }
          this._methodSlotNames[method] = VBScriptNaming.Free(VBScriptNaming.Identifier(method.Name, "m_"), slotTaken);
        }

        for (var i = 0; i < cls.Constructors.Count; ++i)
          this._ctorFactories[cls.Constructors[i]] = VBScriptNaming.Free($"new_{name}_{i.ToString(CultureInfo.InvariantCulture)}", this._moduleTaken);
      }

      foreach (var constant in module.Constants)
        this._constNames[constant] = VBScriptNaming.Free(VBScriptNaming.Identifier(constant.Name, "k_"), this._moduleTaken);

      foreach (var global in module.Globals)
        this._globalNames[global] = VBScriptNaming.Free(VBScriptNaming.Identifier(global.Name, "g_"), this._moduleTaken);

      foreach (var function in module.Functions) {
        var raw = VBScriptNaming.Identifier(function.Name, "fn_");
        this._functionNames[function.Name] = VBScriptNaming.Free(raw, this._moduleTaken);
      }
    }

    private void AssignFieldNames(IrTypeDeclaration decl, IReadOnlyList<IrField> fields) {
      var map = new Dictionary<IrField, string>(ReferenceEqualityComparer.Instance);
      var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ply_clone", "ply_equals", "ply_is", "ply_kind" };
      foreach (var field in fields) {
        if (field == IrExceptionClass.Message)
          map[field] = "Message";
        else if (field == IrExceptionClass.InnerException)
          map[field] = "InnerException";
        else
          map[field] = VBScriptNaming.Free(VBScriptNaming.Identifier(field.Name, "f_"), taken);
      }
      this._fieldNames[decl] = map;
    }

    private static List<IrClassType> OrderClasses(List<IrClassType> classes) {
      var ordered = new List<IrClassType>(classes.Count);
      var seen = new HashSet<IrClassType>(ReferenceEqualityComparer.Instance);
      void Visit(IrClassType cls) {
        if (!seen.Add(cls))
          return;
        if (cls.Base is { IsLibrary: false } parent)
          Visit(parent);
        ordered.Add(cls);
      }
      foreach (var cls in classes)
        Visit(cls);
      return ordered;
    }

    private (string Consts, string Dims, string InitBody) EmitModuleData(IrModule module) {
      var consts = new StringBuilder();
      var dims = new StringBuilder();
      var init = new StringBuilder();

      foreach (var @enum in module.Types.OfType<IrEnumType>()) {
        foreach (var member in @enum.Members)
          consts.Append("Const ").Append(this._enumMembers[@enum][member]).Append(" = ")
            .Append(FormatIntLiteral(@enum.Underlying, member.Value)).Append('\n');
      }

      foreach (var constant in module.Constants) {
        var name = this._constNames[constant];
        if (constant.Type.IsArray) {
          dims.Append("Dim ").Append(name).Append('\n');
          var elem = constant.Type.Element!;
          if (elem == IrType.String)
            init.Append("  ").Append(name).Append(" = Array(").AppendJoin(", ", constant.Strings.Select(VBScriptNaming.StringLiteral)).Append(")\n");
          else
            init.Append("  ").Append(name).Append(" = Array(").AppendJoin(", ", constant.Values.Select(v => FormatScalarLiteral(elem, v))).Append(")\n");
        } else {
          var lit = constant.Type == IrType.String
            ? VBScriptNaming.StringLiteral(constant.Strings[0])
            : FormatScalarLiteral(constant.Type, constant.Values[0]);
          if (IsPureConstLiteral(lit)) {
            consts.Append("Const ").Append(name).Append(" = ").Append(lit).Append('\n');
          } else {
            dims.Append("Dim ").Append(name).Append('\n');
            init.Append("  ").Append(name).Append(" = ").Append(lit).Append('\n');
          }
        }
      }

      this._currentFunction = null;
      this._declaredDims.Clear();
      this._tempCounter = 0;
      var globalInitStmts = new StringBuilder();
      foreach (var global in module.Globals) {
        if (!this.CheckType(global.Type, global.Name))
          continue;
        var name = this._globalNames[global];
        dims.Append("Dim ").Append(name).Append('\n');
        var val = global.Initializer is { } expr
          ? this.CopyIfStruct(global.Type, this.EmitExpr(expr, globalInitStmts, 1), globalInitStmts, 1)
          : this.DefaultValue(global.Type);
        globalInitStmts.Append("  ").Append(AssignPrefix(global.Type)).Append(name).Append(" = ").Append(val).Append('\n');
      }

      if (this._declaredDims.Count > 0)
        init.Append("  Dim ").AppendJoin(", ", this._declaredDims).Append('\n');
      init.Append(globalInitStmts);

      if (module.Initializer is { } initFn && this._functionNames.TryGetValue(initFn, out var vbsInit))
        init.Append("  Call ").Append(vbsInit).Append("()\n");

      return (consts.ToString(), dims.ToString(), init.ToString());
    }

    private static bool IsPureConstLiteral(string literal)
      => !literal.Contains('(') && !literal.Contains(" & ", StringComparison.Ordinal);

    private string EmitTypeDeclarations(IrModule module, Dictionary<string, IrFunction> functionMap) {
      var text = new StringBuilder();

      foreach (var st in module.Types.OfType<IrStructType>()) {
        foreach (var f in st.Fields)
          this.CheckType(f.Type, st.Name);

        var name = this._typeNames[st];
        var fields = this._fieldNames[st];
        text.Append("Class ").Append(name).Append('\n');
        foreach (var field in st.Fields)
          text.Append("  Public ").Append(fields[field]).Append('\n');

        text.Append("  Private Sub Class_Initialize()\n");
        foreach (var field in st.Fields)
          text.Append("    ").Append(AssignPrefix(field.Type)).Append(fields[field]).Append(" = ").Append(this.DefaultValue(field.Type)).Append('\n');
        text.Append("  End Sub\n");

        text.Append("  Public Function ply_clone()\n");
        text.Append("    Dim c\n    Set c = New ").Append(name).Append('\n');
        foreach (var field in st.Fields) {
          var fname = fields[field];
          if (field.Type.IsStruct)
            text.Append("    Set c.").Append(fname).Append(" = ").Append(fname).Append(".ply_clone()\n");
          else
            text.Append("    ").Append(AssignPrefix(field.Type)).Append("c.").Append(fname).Append(" = ").Append(fname).Append('\n');
        }
        text.Append("    Set ply_clone = c\n  End Function\n");

        text.Append("  Public Function ply_equals(ByVal other)\n");
        foreach (var field in st.Fields)
          this.EmitFieldEqualsCheck(field, fields[field], text);
        text.Append("    ply_equals = True\n  End Function\n");
        text.Append("End Class\n\n");
      }

      var classes = OrderClasses(module.Types.OfType<IrClassType>().Where(c => !c.IsLibrary).ToList());
      foreach (var cls in classes) {
        foreach (var f in cls.AllFields)
          this.CheckType(f.Type, cls.Name);

        var name = this._typeNames[cls];
        var fields = this._fieldNames[cls];
        var isEx = IrExceptionClass.IsException(cls);
        text.Append("Class ").Append(name).Append('\n');
        if (isEx)
          text.Append("  Public ply_kind\n");
        foreach (var field in cls.AllFields)
          text.Append("  Public ").Append(fields[field]).Append('\n');

        text.Append("  Private Sub Class_Initialize()\n");
        if (isEx)
          text.Append("    ply_kind = ").Append(VBScriptNaming.StringLiteral(cls.Name)).Append('\n');
        foreach (var field in cls.AllFields)
          text.Append("    ").Append(AssignPrefix(field.Type)).Append(fields[field]).Append(" = ").Append(this.DefaultValue(field.Type)).Append('\n');
        text.Append("  End Sub\n");

        text.Append("  Public Function ply_is(ByVal t)\n");
        var matches = cls.Lineage.Select(a => a.Name).Concat(cls.AllInterfaces.Select(i => i.Name)).Distinct(StringComparer.Ordinal).ToList();
        text.Append("    ply_is = (").AppendJoin(" Or ", matches.Select(m => $"t = {VBScriptNaming.StringLiteral(m)}")).Append(")\n");
        text.Append("  End Function\n");

        var emittedSlots = new HashSet<IrMethod>(ReferenceEqualityComparer.Instance);
        foreach (var ancestor in cls.Lineage) {
          foreach (var method in ancestor.Methods) {
            if (method.IsStatic || !emittedSlots.Add(method.Root))
              continue;
            var selected = cls.Select(method);
            if (selected.Dispatch == IrDispatch.Abstract)
              continue;
            var fn = functionMap[selected.Function];
            var slotName = this._methodSlotNames[method.Root];
            var targetFn = this._functionNames[selected.Function];
            var extraParams = fn.Parameters.Skip(1).Select((p, idx) => $"{(p.Type.IsArray ? "ByRef" : "ByVal")} p_{idx.ToString(CultureInfo.InvariantCulture)}").ToList();
            var argNames = new[] { "Me" }.Concat(Enumerable.Range(0, extraParams.Count).Select(idx => $"p_{idx.ToString(CultureInfo.InvariantCulture)}"));
            if (fn.ReturnType == IrType.Void) {
              text.Append("  Public Sub ").Append(slotName).Append('(').AppendJoin(", ", extraParams).Append(")\n");
              text.Append("    Call ").Append(targetFn).Append('(').AppendJoin(", ", argNames).Append(")\n");
              text.Append("  End Sub\n");
            } else {
              text.Append("  Public Function ").Append(slotName).Append('(').AppendJoin(", ", extraParams).Append(")\n");
              text.Append("    ").Append(AssignPrefix(fn.ReturnType)).Append(slotName).Append(" = ").Append(targetFn).Append('(').AppendJoin(", ", argNames).Append(")\n");
              text.Append("  End Function\n");
            }
          }
        }

        if (cls.IsRecord) {
          text.Append("  Public Function ply_equals(ByVal other)\n");
          text.Append("    If other Is Nothing Then ply_equals = False : Exit Function\n");
          text.Append("    If Not other.ply_is(").Append(VBScriptNaming.StringLiteral(cls.Name)).Append(") Then ply_equals = False : Exit Function\n");
          foreach (var field in cls.AllFields)
            this.EmitFieldEqualsCheck(field, fields[field], text);
          text.Append("    ply_equals = True\n  End Function\n");
        }

        text.Append("End Class\n\n");

        foreach (var ctor in cls.Constructors) {
          var fn = functionMap[ctor.Function];
          var factoryName = this._ctorFactories[ctor];
          var targetFn = this._functionNames[ctor.Function];
          var extraParams = fn.Parameters.Skip(1).Select((p, idx) => $"{(p.Type.IsArray ? "ByRef" : "ByVal")} p_{idx.ToString(CultureInfo.InvariantCulture)}").ToList();
          var argNames = new[] { "inst" }.Concat(Enumerable.Range(0, extraParams.Count).Select(idx => $"p_{idx.ToString(CultureInfo.InvariantCulture)}"));
          text.Append("Function ").Append(factoryName).Append('(').AppendJoin(", ", extraParams).Append(")\n");
          text.Append("  Dim inst\n  Set inst = New ").Append(name).Append('\n');
          text.Append("  Call ").Append(targetFn).Append('(').AppendJoin(", ", argNames).Append(")\n");
          text.Append("  Set ").Append(factoryName).Append(" = inst\n");
          text.Append("End Function\n\n");
        }
      }

      return text.ToString();
    }

    private void EmitFieldEqualsCheck(IrField field, string fname, StringBuilder text) {
      if (field.Type.IsStruct)
        text.Append("    If Not ").Append(fname).Append(".ply_equals(other.").Append(fname).Append(") Then ply_equals = False : Exit Function\n");
      else if (field.Type == IrType.String)
        text.Append("    If StrComp(").Append(fname).Append(", other.").Append(fname).Append(", 0) <> 0 Then ply_equals = False : Exit Function\n");
      else if (field.Type.IsArray)
        text.Append("    If Not ").Append(this.Use(VBScriptHelpers.ArrEq)).Append('(').Append(fname).Append(", other.").Append(fname).Append(") Then ply_equals = False : Exit Function\n");
      else if (NeedsSet(field.Type))
        text.Append("    If Not (").Append(fname).Append(" Is other.").Append(fname).Append(") Then ply_equals = False : Exit Function\n");
      else
        text.Append("    If ").Append(fname).Append(" <> other.").Append(fname).Append(" Then ply_equals = False : Exit Function\n");
    }

    private string EmitFunction(IrFunction function) {
      if (IrSequences.IsIterator(function))
        return this.EmitIteratorFunction(function);

      this._currentFunction = function;
      this._currentFunctionVbsName = this._functionNames[function.Name];
      this._inAsyncFunction = function.IsAsync;
      this._currentReturnType = function.IsAsync ? function.ReturnType.Element! : function.ReturnType;
      this._iteratorResultListVar = null;
      this._locals.Clear();
      this._localTaken.Clear();
      this._declaredDims.Clear();
      this._loops.Clear();
      this._tryFrames.Clear();
      this._boxedLocals.Clear();
      this._addrBoxedLocals.Clear();
      this._arrayBoxedLocals.Clear();
      this._arrayParamSyncs.Clear();
      this._tempCounter = 0;
      this._tryBodyDepth = 0;
      this._activeCatchErrVar = null;

      // Reserve the function name itself so a local never shadows the return assignment target!
      this._localTaken.Add(this._currentFunctionVbsName);

      if (!this.CheckType(function.ReturnType, function.Name))
        return "";
      foreach (var p in function.Parameters)
        this.CheckType(p.Type, function.Name);
      foreach (var l in function.Locals)
        this.CheckType(l.Type, function.Name);

      this.AnalyzeBodyStorage(function.Parameters, function.Locals, function.Body);

      var paramDecls = new List<string>(function.Parameters.Count);
      var prologue = new StringBuilder();
      foreach (var p in function.Parameters) {
        var pname = VBScriptNaming.Free(VBScriptNaming.Identifier(p.Name), this._localTaken);
        this._locals[p] = pname;
        if (this._boxedLocals.Contains(p)) {
          this.Use(VBScriptHelpers.Cell);
          var rawName = VBScriptNaming.Free("ply_p_" + pname, this._localTaken);
          paramDecls.Add($"{(p.Type.IsArray ? "ByRef" : "ByVal")} {rawName}");
          this._declaredDims.Add(pname);
          prologue.Append("  Set ").Append(pname).Append(" = New ply_Cell\n");
          prologue.Append("  ").Append(AssignPrefix(p.Type)).Append(pname).Append(".Val = ").Append(rawName).Append('\n');
        } else if (this._addrBoxedLocals.Contains(p)) {
          this.Use(VBScriptHelpers.SpanSupport);
          var rawName = VBScriptNaming.Free("ply_p_" + pname, this._localTaken);
          paramDecls.Add($"ByVal {rawName}");
          this._declaredDims.Add(pname);
          prologue.Append("  Set ").Append(pname).Append(" = ply_buf_single(").Append(rawName).Append(", ")
            .Append(ElemKindCode(p.Type)).Append(", ").Append(p.Type.Size.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        } else if (this._arrayBoxedLocals.Contains(p)) {
          this.Use(VBScriptHelpers.SpanSupport);
          var rawName = VBScriptNaming.Free("ply_p_" + pname, this._localTaken);
          paramDecls.Add($"ByRef {rawName}");
          this._declaredDims.Add(pname);
          this._arrayParamSyncs.Add((p, rawName, pname));
          var elem = p.Type.Element!;
          prologue.Append("  Set ").Append(pname).Append(" = ply_buf_wrap(").Append(rawName).Append(", ")
            .Append(ElemKindCode(elem)).Append(", ").Append(elem.Size.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        } else {
          paramDecls.Add($"{(p.Type.IsArray ? "ByRef" : "ByVal")} {pname}");
        }
      }

      foreach (var l in function.Locals) {
        var lname = VBScriptNaming.Free(VBScriptNaming.Identifier(l.Name), this._localTaken);
        this._locals[l] = lname;
        this._declaredDims.Add(lname);
      }

      var bodyText = new StringBuilder();
      if (this._inAsyncFunction) {
        this.Use(VBScriptHelpers.TaskSupport);
        var errNum = this.NewTemp("ply_an");
        var errSrc = this.NewTemp("ply_as");
        var errDesc = this.NewTemp("ply_ad");
        bodyText.Append(prologue);
        bodyText.Append("  On Error Resume Next\n");
        bodyText.Append("  Do\n");
        ++this._tryBodyDepth;
        this.EmitBlock(function.Body, bodyText, 2);
        --this._tryBodyDepth;
        bodyText.Append("  Loop Until True\n");
        bodyText.Append("  ").Append(errNum).Append(" = Err.Number\n");
        bodyText.Append("  ").Append(errSrc).Append(" = Err.Source\n");
        bodyText.Append("  ").Append(errDesc).Append(" = Err.Description\n");
        bodyText.Append("  Err.Clear\n");
        bodyText.Append("  On Error GoTo 0\n");
        bodyText.Append("  If ").Append(errNum).Append(" <> 0 Then\n");
        this.EmitArrayParamSyncs(bodyText, 2);
        bodyText.Append("    Set ").Append(this._currentFunctionVbsName).Append(" = ply_task_fault(ply_capture_err(")
          .Append(errNum).Append(", ").Append(errSrc).Append(", ").Append(errDesc).Append("))\n");
        bodyText.Append("    Exit Function\n");
        bodyText.Append("  End If\n");
        this.EmitArrayParamSyncs(bodyText, 1);
        bodyText.Append("  Set ").Append(this._currentFunctionVbsName).Append(" = ply_task_completed()\n");
      } else {
        bodyText.Append(prologue);
        this.EmitBlock(function.Body, bodyText, 1);
        this.EmitArrayParamSyncs(bodyText, 1);
      }

      var header = new StringBuilder();
      var isSub = !this._inAsyncFunction && function.ReturnType == IrType.Void;
      header.Append(isSub ? "Sub " : "Function ").Append(this._currentFunctionVbsName).Append('(');
      header.AppendJoin(", ", paramDecls);
      header.Append(")\n");

      if (this._declaredDims.Count > 0)
        header.Append("  Dim ").AppendJoin(", ", this._declaredDims).Append('\n');

      header.Append(bodyText);
      header.Append(isSub ? "End Sub\n" : "End Function\n");

      this._currentFunction = null;
      this._inAsyncFunction = false;
      this._currentReturnType = IrType.Void;
      return header.ToString();
    }

    private void EmitArrayParamSyncs(StringBuilder text, int indent) {
      if (this._arrayParamSyncs.Count == 0)
        return;
      var pad = Indent(indent);
      foreach (var (_, rawName, boxName) in this._arrayParamSyncs)
        text.Append(pad).Append("If Not ").Append(boxName).Append(".IsNullArr Then ").Append(rawName).Append(" = ").Append(boxName).Append(".Data\n");
    }

    private bool CheckType(IrType type, string context) {
      if (type.IsArray)
        return this.CheckType(type.Element!, context);
      if (type.IsOptional)
        return this.CheckType(type.Underlying!, context);
      if (type.IsCollection) {
        var ok = true;
        if (type.Key is { } key)
          ok &= this.CheckType(key, context);
        return ok & this.CheckType(type.Element!, context);
      }
      if (type.IsLazySequence || type.IsAsync || type.IsView || type.IsMemory || type.IsPointer)
        return type.Element is null || type.Element == IrType.Void || this.CheckType(type.Element, context);
      if (type.IsFunction) {
        var sig = type.Signature!;
        var ok = sig.Result == IrType.Void || this.CheckType(sig.Result, context);
        foreach (var p in sig.Parameters)
          ok &= this.CheckType(p, context);
        return ok;
      }

      if (type.Kind == IrTypeKind.BigInt) {
        this.Problems.Add(Diagnostic.Create(UnsupportedType, null, type.ToString(), this._profile.Name) with { Cause = context });
        return false;
      }

      return true;
    }

    private string ReportConstruct(string name) {
      this.Problems.Add(Diagnostic.Create(UnsupportedConstruct, null, name) with {
        Cause = this._currentFunction is { } f ? $"function {f.Name}" : this._module.Name
      });
      return "0";
    }

    private string ReportIntrinsic(string name) {
      this.Problems.Add(Diagnostic.Create(UnsupportedIntrinsic, null, name) with {
        Cause = this._currentFunction is { } f ? $"function {f.Name}" : this._module.Name
      });
      return "0";
    }

    private string Use(string helper) {
      this._usedHelpers.Add(helper);
      return helper;
    }

    private string NewTemp(string prefix = "ply_t") {
      var name = prefix + (++this._tempCounter).ToString(CultureInfo.InvariantCulture);
      this._declaredDims.Add(name);
      return name;
    }

    private string LocalName(IrLocal local) => this._locals[local];

    private string FieldName(IrType targetType, IrField field) {
      if (field == IrExceptionClass.Message)
        return "Message";
      if (field == IrExceptionClass.InnerException)
        return "InnerException";
      return this._fieldNames[targetType.Declaration!][field];
    }

    private static bool NeedsSet(IrType type)
      => type.IsObject || type.IsStruct || type.IsFunction || type.IsCollection
         || type.IsLazySequence || type.IsAsync || type.IsView || type.IsMemory || type.IsPointer
         || (type.IsOptional && NeedsSet(type.Underlying!));

    private static string AssignPrefix(IrType type) => NeedsSet(type) ? "Set " : "";

    private string DefaultValue(IrType type) {
      if (type.IsOptional)
        return NeedsSet(type) ? "Nothing" : "Null";
      if (type.IsView || type.IsMemory) {
        this.Use(VBScriptHelpers.SpanSupport);
        var elem = type.Element!;
        return $"ply_span_empty({ElemKindCode(elem)}, {elem.Size.ToString(CultureInfo.InvariantCulture)})";
      }
      if (type.IsObject || type.IsFunction || type.IsCollection || type.IsLazySequence || type.IsAsync || type.IsPointer)
        return "Nothing";
      if (type.IsStruct)
        return "New " + this._typeNames[type.StructType!];
      if (type.IsArray || type == IrType.String)
        return "Null";
      if (type == IrType.Bool)
        return "False";
      if (type.IsFloat)
        return "0.0";
      return "0";
    }

    private static string FormatScalarLiteral(IrType type, long value) {
      var integral = type.Integral;
      if (integral == IrType.Bool)
        return value != 0 ? "True" : "False";
      if (integral.IsFloat)
        return FormatFloatLiteral(IrOperators.Float(integral, value));
      return FormatIntLiteral(integral, value);
    }

    private static string FormatIntLiteral(IrType type, long value) {
      if (type == IrType.U64)
        return $"CDbl({((ulong)value).ToString(CultureInfo.InvariantCulture)}.0)";
      if (type == IrType.U32) {
        var u = (uint)value;
        return u > int.MaxValue ? $"CDbl({u.ToString(CultureInfo.InvariantCulture)}.0)" : u.ToString(CultureInfo.InvariantCulture);
      }
      if (type == IrType.I64)
        return value is < int.MinValue or > int.MaxValue
          ? $"CDbl({value.ToString(CultureInfo.InvariantCulture)}.0)"
          : value.ToString(CultureInfo.InvariantCulture);
      return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatFloatLiteral(double value) {
      if (double.IsNaN(value))
        return "0.0";
      if (double.IsPositiveInfinity(value))
        return "1.79769313486231E+308";
      if (double.IsNegativeInfinity(value))
        return "-1.79769313486231E+308";
      var text = value.ToString("R", CultureInfo.InvariantCulture);
      if (!text.Contains('.') && !text.Contains('E') && !text.Contains('e'))
        text += ".0";
      return text;
    }

    private static string Indent(int level) => new(' ', level * 2);

    private sealed record LoopFrame(string? BreakFlag, string? ContinueFlag, bool IsForLoop, int TryDepth, bool IsSwitch);

    private sealed record TryFrame(string? ReturnFlag, string? ReturnValue, string? BreakFlag, string? ContinueFlag);

  }

}
