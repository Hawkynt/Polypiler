using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Cpp;

public static partial class CppEmitter {

  private sealed partial class Writer {

    private readonly Dictionary<IrMethod, string> _methodNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IrClassType> _classFunctionOwners = new(StringComparer.Ordinal);
    private readonly HashSet<string> _interfaceFunctionNames = new(StringComparer.Ordinal);

    private IrClassType? _currentClass;
    private IrLocal? _currentThis;

    private void PrepareClassesAndInterfaces(IrModule module) {
      this._methodNames.Clear();
      this._classFunctionOwners.Clear();
      this._interfaceFunctionNames.Clear();
      this._staticClassGlobals.Clear();

      foreach (var iface in module.Types.OfType<IrInterfaceType>()) {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in iface.Methods) {
          this._interfaceFunctionNames.Add(method.Function);
          var name = CppNaming.Free(MethodBaseName(method), taken.Contains);
          taken.Add(name);
          this._methodNames[method] = name;
        }
      }

      foreach (var exCls in module.Types.OfType<IrClassType>().Where(c => !c.IsLibrary && IrExceptionClass.IsException(c)))
        foreach (var ctor in exCls.Constructors)
          this._classFunctionOwners[ctor.Function] = exCls;

      var classes = OrderClassesByHierarchy(module.Types.OfType<IrClassType>().Where(IsUserClass).ToList());
      foreach (var cls in classes) {
        var clsName = this._typeNames[cls];
        var takenMembers = new HashSet<string>(StringComparer.Ordinal) { clsName };

        foreach (var field in cls.Fields) {
          var fname = CppNaming.Free(CppNaming.Identifier(field.Name, "field"), takenMembers.Contains);
          takenMembers.Add(fname);
          this._fieldNames[field] = fname;
        }

        foreach (var sf in cls.StaticFields) {
          var sname = CppNaming.Free(CppNaming.Identifier(sf.Name, "sfield"), takenMembers.Contains);
          takenMembers.Add(sname);
          this._staticClassGlobals.Add(sf);
          this._globalNames[sf] = $"{clsName}::{sname}";
        }

        foreach (var ctor in cls.Constructors)
          this._classFunctionOwners[ctor.Function] = cls;

        foreach (var method in cls.Methods) {
          this._classFunctionOwners[method.Function] = cls;
          if (method.Overrides is { } overridden) {
            var slotName = this._methodNames[overridden.Root];
            this._methodNames[method] = slotName;
            takenMembers.Add(slotName);
            continue;
          }
          if (method.Implements.Count > 0) {
            var ifaceName = this._methodNames[method.Implements[0]];
            this._methodNames[method] = ifaceName;
            takenMembers.Add(ifaceName);
            continue;
          }

          var baseName = MethodBaseName(method);
          var assigned = CppNaming.Free(baseName, takenMembers.Contains);
          takenMembers.Add(assigned);
          this._methodNames[method] = assigned;
        }
      }
    }

    private static bool IsUserClass(IrClassType cls)
      => !cls.IsLibrary && !IrExceptionClass.IsException(cls);

    private static List<IrClassType> OrderClassesByHierarchy(List<IrClassType> classes) {
      var ordered = new List<IrClassType>(classes.Count);
      var visited = new HashSet<IrClassType>(ReferenceEqualityComparer.Instance);
      void Visit(IrClassType cls) {
        if (!visited.Add(cls))
          return;
        if (cls.Base is { } b && IsUserClass(b))
          Visit(b);
        ordered.Add(cls);
      }
      foreach (var cls in classes)
        Visit(cls);
      return ordered;
    }

    private static string MethodBaseName(IrMethod method) {
      var raw = method.Kind switch {
        IrMethodKind.Getter => "get_" + method.Name,
        IrMethodKind.Setter => "set_" + method.Name,
        _ => method.Name
      };
      return CppNaming.Identifier(raw, "method");
    }

    private void EmitHeaderClassesAndInterfaces(IrModule module, StringBuilder text) {
      var interfaces = module.Types.OfType<IrInterfaceType>().ToList();
      var classes = OrderClassesByHierarchy(module.Types.OfType<IrClassType>().Where(IsUserClass).ToList());
      var userExceptions = module.Types.OfType<IrClassType>().Where(c => !c.IsLibrary && IrExceptionClass.IsException(c)).ToList();

      foreach (var iface in interfaces)
        text.Append("class ").Append(this._typeNames[iface]).Append(";\n");
      foreach (var cls in classes)
        text.Append("class ").Append(this._typeNames[cls]).Append(";\n");
      if (interfaces.Count > 0 || classes.Count > 0)
        text.Append('\n');

      foreach (var iface in interfaces) {
        var name = this._typeNames[iface];
        text.Append("class ").Append(name);
        if (iface.BaseInterfaces.Count > 0)
          text.Append(" : ").AppendJoin(", ", iface.BaseInterfaces.Select(b => $"public virtual {this._typeNames[b]}"));
        text.Append(" {\npublic:\n");
        text.Append("  virtual ~").Append(name).Append("() = default;\n");
        foreach (var method in iface.Methods) {
          var fn = this._functionMap[method.Function];
          this.AnalyzeParamModes(fn);
          text.Append("  virtual ").Append(this.FormatFunctionSignature(fn, this._methodNames[method], inHeader: true, withParamNames: true, skipParams: 1)).Append(" = 0;\n");
        }
        text.Append("};\n\n");
      }

      foreach (var exCls in userExceptions) {
        this._headerIncludes.Add("<stdexcept>");
        this._headerIncludes.Add("<string>");
        var name = this._typeNames[exCls];
        text.Append("class ").Append(name).Append(" : public std::runtime_error {\npublic:\n");
        text.Append("  explicit ").Append(name).Append("(const std::string& msg) : std::runtime_error(msg) {}\n");
        text.Append("  ").Append(name).Append("() : std::runtime_error(").Append(CppNaming.StringLiteral(IrExceptionClass.DefaultMessage(exCls))).Append(") {}\n");
        text.Append("};\n\n");
      }

      foreach (var cls in classes) {
        this._headerIncludes.Add("<memory>");
        var name = this._typeNames[cls];
        var bases = new List<string>();
        if (cls.Base is { } baseCls)
          bases.Add($"public {this._typeNames[baseCls]}");
        else
          bases.Add($"public std::enable_shared_from_this<{name}>");

        var baseIfaces = cls.Base is { } b ? new HashSet<IrInterfaceType>(b.AllInterfaces, ReferenceEqualityComparer.Instance) : [];
        foreach (var iface in cls.Interfaces)
          if (!baseIfaces.Contains(iface))
            bases.Add($"public virtual {this._typeNames[iface]}");

        text.Append("class ").Append(name).Append(" : ").AppendJoin(", ", bases).Append(" {\npublic:\n");

        foreach (var field in cls.Fields)
          text.Append("  ").Append(this.CppType(field.Type, inHeader: true)).Append(' ').Append(this._fieldNames[field]).Append("{};\n");

        foreach (var sf in cls.StaticFields) {
          var localName = this._globalNames[sf][(name.Length + 2)..];
          text.Append("  static ").Append(this.CppType(sf.Type, inHeader: true)).Append(' ').Append(localName).Append(";\n");
        }

        if (cls.Fields.Count > 0 || cls.StaticFields.Count > 0)
          text.Append('\n');

        text.Append("  virtual ~").Append(name).Append("() = default;\n");

        if (cls.Constructors.Count == 0) {
          text.Append("  ").Append(name).Append("() = default;\n");
        } else {
          foreach (var ctor in cls.Constructors) {
            var fn = this._functionMap[ctor.Function];
            this.AnalyzeParamModes(fn);
            text.Append("  ").Append(this.FormatCtorSignature(name, fn, inHeader: true)).Append(";\n");
          }
        }

        foreach (var method in cls.Methods) {
          var fn = this._functionMap[method.Function];
          this.AnalyzeParamModes(fn);
          var skip = method.IsStatic ? 0 : 1;
          var sig = this.FormatFunctionSignature(fn, this._methodNames[method], inHeader: true, withParamNames: true, skipParams: skip);
          var prefix = method.IsStatic ? "static " : (method.Dispatch is IrDispatch.Virtual or IrDispatch.Abstract ? "virtual " : "");
          var suffix = method.Dispatch == IrDispatch.Abstract ? " = 0"
            : (method.Dispatch == IrDispatch.Override || method.Implements.Count > 0 ? (method.IsSealedOverride ? " override final" : " override") : "");
          text.Append("  ").Append(prefix).Append(sig).Append(suffix).Append(";\n");
        }

        if (cls.IsRecord)
          text.Append("  bool operator==(const ").Append(name).Append("& other) const;\n");

        text.Append("};\n\n");
      }
    }

    private string FormatCtorSignature(string clsName, IrFunction fn, bool inHeader) {
      var modes = this._paramModes[fn];
      var sb = new StringBuilder();
      if (fn.Parameters.Count == 2)
        sb.Append("explicit ");
      sb.Append(clsName).Append('(');
      for (var i = 1; i < fn.Parameters.Count; ++i) {
        if (i > 1)
          sb.Append(", ");
        var p = fn.Parameters[i];
        sb.Append(this.FormatParamType(p.Type, modes[i], inHeader)).Append(' ').Append(CppNaming.Identifier(p.Name, "p"));
      }
      sb.Append(')');
      return sb.ToString();
    }

    private string EmitClassDefinitions(IrModule module) {
      var text = new StringBuilder();
      var classes = OrderClassesByHierarchy(module.Types.OfType<IrClassType>().Where(IsUserClass).ToList());
      foreach (var cls in classes) {
        this._currentClass = cls;
        var clsName = this._typeNames[cls];

        foreach (var sf in cls.StaticFields) {
          var sType = this.CppType(sf.Type, inHeader: false);
          var init = sf.Initializer is { } expr ? this.EmitExpr(expr) : this.DefaultValue(sf.Type);
          text.Append(sType).Append(' ').Append(this._globalNames[sf]).Append(" = ").Append(init).Append(";\n\n");
        }

        foreach (var ctor in cls.Constructors)
          this.EmitCtorDefinition(cls, clsName, this._functionMap[ctor.Function], text);

        foreach (var method in cls.Methods) {
          if (method.Dispatch == IrDispatch.Abstract)
            continue;
          this.EmitMethodDefinition(clsName, method, this._functionMap[method.Function], text);
        }

        if (cls.IsRecord)
          this.EmitClassRecordEqualsDefinition(cls, clsName, text);

        this._currentClass = null;
      }

      return text.ToString();
    }

    private void EmitCtorDefinition(IrClassType cls, string clsName, IrFunction fn, StringBuilder text) {
      this._currentFunction = fn;
      this._currentThis = fn.Parameters[0];
      this._locals.Clear();
      this._tempCounter = 0;

      var taken = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 1; i < fn.Parameters.Count; ++i) {
        var p = fn.Parameters[i];
        var pname = CppNaming.Free(CppNaming.Identifier(p.Name, "p"), taken.Contains);
        taken.Add(pname);
        this._locals[p] = pname;
      }
      foreach (var local in fn.Locals) {
        var lname = CppNaming.Free(CppNaming.Identifier(local.Name, "v"), taken.Contains);
        taken.Add(lname);
        this._locals[local] = lname;
      }

      var startIndex = 0;
      var initList = "";
      if (fn.Body.Statements.Count > 0 &&
          fn.Body.Statements[0] is IrExprStmt { Expression: IrCall ctorCall } &&
          ctorCall.Arguments.Count >= 1 &&
          ctorCall.Arguments[0] is IrLocalRef firstRef &&
          ReferenceEquals(firstRef.Local, this._currentThis)) {
        var ctorArgs = string.Join(", ", ctorCall.Arguments.Skip(1).Select(this.EmitExpr));
        if (cls.Base is { } baseCls && baseCls.Constructors.Any(c => c.Function == ctorCall.Function)) {
          initList = $" : {this._typeNames[baseCls]}({ctorArgs})";
          startIndex = 1;
        } else if (cls.Constructors.Any(c => c.Function == ctorCall.Function)) {
          initList = $" : {clsName}({ctorArgs})";
          startIndex = 1;
        }
      }

      var modes = this._paramModes[fn];
      var usedLocals = CollectReferencedLocals(fn.Body);
      text.Append(clsName).Append("::").Append(clsName).Append('(');
      for (var i = 1; i < fn.Parameters.Count; ++i) {
        if (i > 1)
          text.Append(", ");
        var p = fn.Parameters[i];
        if (!usedLocals.Contains(p))
          text.Append("[[maybe_unused]] ");
        text.Append(this.FormatParamType(p.Type, modes[i], inHeader: false)).Append(' ').Append(this._locals[p]);
      }
      text.Append(')').Append(initList).Append(" {\n");
      this.EmitBlockStatements(fn.Body, text, 1, startIndex);
      text.Append("}\n\n");

      this._currentThis = null;
      this._currentFunction = null;
    }

    private void EmitMethodDefinition(string clsName, IrMethod method, IrFunction fn, StringBuilder text) {
      this._currentFunction = fn;
      this._currentThis = method.IsStatic ? null : fn.Parameters[0];
      this._locals.Clear();
      this._tempCounter = 0;

      var skip = method.IsStatic ? 0 : 1;
      var taken = new HashSet<string>(StringComparer.Ordinal);
      for (var i = skip; i < fn.Parameters.Count; ++i) {
        var p = fn.Parameters[i];
        var pname = CppNaming.Free(CppNaming.Identifier(p.Name, "p"), taken.Contains);
        taken.Add(pname);
        this._locals[p] = pname;
      }
      foreach (var local in fn.Locals) {
        var lname = CppNaming.Free(CppNaming.Identifier(local.Name, "v"), taken.Contains);
        taken.Add(lname);
        this._locals[local] = lname;
      }

      var modes = this._paramModes[fn];
      var usedLocals = CollectReferencedLocals(fn.Body);
      var ret = this.CppType(fn.ReturnType, inHeader: false);
      text.Append(ret).Append(' ').Append(clsName).Append("::").Append(this._methodNames[method]).Append('(');
      for (var i = skip; i < fn.Parameters.Count; ++i) {
        if (i > skip)
          text.Append(", ");
        var p = fn.Parameters[i];
        if (!usedLocals.Contains(p))
          text.Append("[[maybe_unused]] ");
        text.Append(this.FormatParamType(p.Type, modes[i], inHeader: false)).Append(' ').Append(this._locals[p]);
      }
      text.Append(") {\n");
      this.EmitBlockStatements(fn.Body, text, 1);
      text.Append("}\n\n");

      this._currentThis = null;
      this._currentFunction = null;
    }

    private void EmitClassRecordEqualsDefinition(IrClassType cls, string clsName, StringBuilder text) {
      text.Append("bool ").Append(clsName).Append("::operator==(const ").Append(clsName).Append("& other) const {\n");
      var fields = cls.AllFields;
      if (fields.Count == 0) {
        text.Append("  (void)other;\n  return true;\n");
      } else {
        text.Append("  return ");
        for (var i = 0; i < fields.Count; ++i) {
          var field = fields[i];
          var fname = this._fieldNames[field];
          if (i > 0)
            text.Append(" && ");
          if (field.Type.IsFloat) {
            this._sourceIncludes.Add("<cmath>");
            text.Append("((this->").Append(fname).Append(" == other.").Append(fname)
              .Append(") || (std::isnan(this->").Append(fname).Append(") && std::isnan(other.").Append(fname).Append(")))");
          } else {
            text.Append("(this->").Append(fname).Append(" == other.").Append(fname).Append(')');
          }
        }
        text.Append(";\n");
      }
      text.Append("}\n\n");
    }

    private string EmitClassFieldLoad(IrFieldLoad load) {
      if (load.Field == IrExceptionClass.Message) {
        this._sourceIncludes.Add("<string>");
        return $"std::string(({this.EmitExpr(load.Target)}).what())";
      }

      var fname = this._fieldNames[load.Field];
      if (load.Target is IrLocalRef r && ReferenceEquals(r.Local, this._currentThis))
        return $"this->{fname}";

      this.UseHelper(CppHelpers.NonNull);
      return $"{CppHelpers.NonNull}({this.EmitExpr(load.Target)})->{fname}";
    }

    private void EmitClassFieldStore(IrFieldStore store, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var fname = this._fieldNames[store.Field];
      var val = this.EmitExpr(store.Value);
      if (store.Target is IrLocalRef r && ReferenceEquals(r.Local, this._currentThis)) {
        text.Append(pad).Append("this->").Append(fname).Append(" = ").Append(val).Append(";\n");
        return;
      }

      this.UseHelper(CppHelpers.NonNull);
      text.Append(pad).Append(CppHelpers.NonNull).Append('(').Append(this.EmitExpr(store.Target))
        .Append(")->").Append(fname).Append(" = ").Append(val).Append(";\n");
    }

    private string EmitObjectNew(IrNew objNew) {
      var cls = objNew.Type.ClassType!;
      if (IrExceptionClass.IsException(cls))
        return this.EmitExceptionConstruction(cls, objNew.Arguments);

      this._sourceIncludes.Add("<memory>");
      var clsName = this._typeNames[cls];
      var args = string.Join(", ", objNew.Arguments.Select(this.EmitExpr));
      return $"std::make_shared<{clsName}>({args})";
    }

    private string EmitMethodCall(IrMethodCall call) {
      var methodName = this._methodNames[call.Method];
      var args = string.Join(", ", call.Arguments.Select(this.EmitExpr));

      if (call.Target is null) {
        var owner = this._classFunctionOwners[call.Method.Function];
        return $"{this._typeNames[owner]}::{methodName}({args})";
      }

      if (call.Kind == IrMethodCallKind.Base) {
        var owner = this._classFunctionOwners[call.Method.Function];
        return $"this->{this._typeNames[owner]}::{methodName}({args})";
      }

      if (call.Target is IrLocalRef r && ReferenceEquals(r.Local, this._currentThis))
        return $"this->{methodName}({args})";

      this.UseHelper(CppHelpers.NonNull);
      return $"{CppHelpers.NonNull}({this.EmitExpr(call.Target)})->{methodName}({args})";
    }

    private string EmitTypeTest(IrTypeTest test) {
      this._sourceIncludes.Add("<memory>");
      var targetName = this._typeNames[test.Target.Declaration!];
      return $"(std::dynamic_pointer_cast<{targetName}>({this.EmitExpr(test.Value)}) != nullptr)";
    }

    private string EmitAsCast(IrAsCast cast) {
      this._sourceIncludes.Add("<memory>");
      var targetName = this._typeNames[cast.Type.Declaration!];
      return $"std::dynamic_pointer_cast<{targetName}>({this.EmitExpr(cast.Value)})";
    }

    private string EmitCast(IrCast cast) {
      this.UseHelper(CppHelpers.Cast);
      var targetName = this._typeNames[cast.Type.Declaration!];
      return $"{CppHelpers.Cast}<{targetName}>({this.EmitExpr(cast.Value)})";
    }

    private string EmitClassRecordEquals(IrStructEquals equals) {
      var l = this.EmitExpr(equals.Left);
      var r = this.EmitExpr(equals.Right);
      return $"[&]() {{ auto ply_l = {l}; auto ply_r = {r}; return (!ply_l && !ply_r) || (ply_l && ply_r && (*ply_l == *ply_r)); }}()";
    }

    private string EmitExceptionValue(IrExpr exception) {
      if (exception is IrNew { Type.ClassType: { } cls } n && IrExceptionClass.IsException(cls))
        return this.EmitExceptionConstruction(cls, n.Arguments);
      return this.EmitExpr(exception);
    }

    private string EmitExceptionConstruction(IrClassType cls, IReadOnlyList<IrExpr> arguments) {
      var exType = cls == IrExceptionClass.Exception ? "std::runtime_error" : this.ExceptionCppType(cls, inHeader: false);
      if (cls == IrExceptionClass.InvalidCastException) {
        this._sourceIncludes.Add("<typeinfo>");
        return "std::bad_cast()";
      }

      this._sourceIncludes.Add("<stdexcept>");
      if (arguments.Count == 0)
        return $"{exType}({CppNaming.StringLiteral(IrExceptionClass.DefaultMessage(cls))})";

      return $"{exType}({this.EmitExpr(arguments[0])})";
    }

    private string ExceptionCppType(IrClassType cls, bool inHeader) {
      var includes = inHeader ? this._headerIncludes : this._sourceIncludes;
      if (!cls.IsLibrary)
        return this._typeNames[cls];

      if (cls == IrExceptionClass.InvalidCastException) {
        includes.Add("<typeinfo>");
        return "std::bad_cast";
      }

      includes.Add("<stdexcept>");
      return cls.Name switch {
        "Exception" => "std::exception",
        "OverflowException" => "std::overflow_error",
        "DivideByZeroException" => "std::domain_error",
        "IndexOutOfRangeException" or "ArgumentOutOfRangeException" or "KeyNotFoundException" => "std::out_of_range",
        "ArgumentException" or "FormatException" => "std::invalid_argument",
        "InvalidOperationException" or "AssertionException" => "std::logic_error",
        _ => "std::runtime_error"
      };
    }

  }

}
