using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Cpp;

public static partial class CppEmitter {

  private enum ParamMode { Value, ConstRef, MutRef }

  private sealed partial class Writer(CppProfile profile, CppEmitOptions options) {

    private readonly CppProfile _profile = profile;
    private readonly CppEmitOptions _options = options;
    private readonly SortedSet<string> _headerIncludes = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _sourceIncludes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedHelpers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _functionNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IrFunction> _functionMap = new(StringComparer.Ordinal);
    private readonly Dictionary<IrFunction, ParamMode[]> _paramModes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrModuleConst, string> _constNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrGlobal, string> _globalNames = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrGlobal> _staticClassGlobals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrTypeDeclaration, string> _typeNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrEnumType, Dictionary<IrEnumMember, string>> _enumMembers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrField, string> _fieldNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrLocal, string> _locals = new();

    private IrModule _module = null!;
    private IrFunction? _currentFunction;
    private int _tempCounter;

    public List<Diagnostic> Problems { get; } = [];

    public IReadOnlyList<GeneratedFile> EmitModule(IrModule module) {
      this._module = module;
      this._functionMap.Clear();
      foreach (var fn in module.Functions)
        this._functionMap[fn.Name] = fn;

      var stem = CppNaming.FileStem(module.Name);
      var ns = CppNaming.Namespace(module.Name, this._options.Namespace);

      this.AssignTypesAndMembers(module);
      this.AssignFunctions(module);
      this.AssignConstantsAndGlobals(module);

      var freeFunctions = module.Functions
        .Where(f => !this._classFunctionOwners.ContainsKey(f.Name) && !this._interfaceFunctionNames.Contains(f.Name))
        .ToList();
      foreach (var fn in freeFunctions)
        this.AnalyzeParamModes(fn);
      this.PropagateMutRefParamModes(freeFunctions);

      // Emit header declarations
      var headerBody = new StringBuilder();
      this.EmitHeaderEnums(module, headerBody);
      this.EmitHeaderStructs(module, headerBody);
      this.EmitHeaderClassesAndInterfaces(module, headerBody);
      this.EmitHeaderFunctionDeclarations(freeFunctions, headerBody);

      // Emit implementation bodies
      var privateDecls = new StringBuilder();
      var privateBodies = new StringBuilder();
      var publicBodies = new StringBuilder();

      foreach (var fn in freeFunctions) {
        if (!fn.IsPublic) {
          privateDecls.Append("static ").Append(this.FormatFunctionSignature(fn, this._functionNames[fn.Name], inHeader: false, withParamNames: false)).Append(";\n");
          privateBodies.Append("static ").Append(this.EmitFunctionDefinition(fn, this._functionNames[fn.Name])).Append('\n');
        } else {
          publicBodies.Append(this.EmitFunctionDefinition(fn, this._functionNames[fn.Name])).Append('\n');
        }
      }

      var classImpls = this.EmitClassDefinitions(module);

      // Constants and globals (module-level)
      var constsAndGlobals = new StringBuilder();
      this.EmitModuleConstants(module, constsAndGlobals);
      this.EmitModuleGlobals(module, constsAndGlobals);

      var headerText = BuildHeader(ns, this._headerIncludes, headerBody);
      var sourceText = this.BuildSource(stem, ns, constsAndGlobals, privateDecls, privateBodies, classImpls, publicBodies, module);

      return [
        new GeneratedFile(stem + ".hpp", headerText),
        new GeneratedFile(stem + ".cpp", sourceText)
      ];
    }

    private static string BuildHeader(string ns, SortedSet<string> includes, StringBuilder body) {
      var text = new StringBuilder();
      text.Append("#pragma once\n\n");
      foreach (var inc in includes)
        text.Append("#include ").Append(inc).Append('\n');
      if (includes.Count > 0)
        text.Append('\n');
      text.Append("namespace ").Append(ns).Append(" {\n\n");
      text.Append(body);
      text.Append("} // namespace ").Append(ns).Append('\n');
      return text.ToString();
    }

    private string BuildSource(
      string stem,
      string ns,
      StringBuilder constsAndGlobals,
      StringBuilder privateDecls,
      StringBuilder privateBodies,
      string classImpls,
      StringBuilder publicBodies,
      IrModule module) {
      var text = new StringBuilder();
      text.Append("#include \"").Append(stem).Append(".hpp\"\n");
      var extraIncludes = this._sourceIncludes.Where(inc => !this._headerIncludes.Contains(inc)).ToList();
      foreach (var inc in extraIncludes)
        text.Append("#include ").Append(inc).Append('\n');
      text.Append('\n');
      text.Append("namespace ").Append(ns).Append(" {\n\n");

      var usedHelpers = CppHelpers.Order.Where(this._usedHelpers.Contains).ToList();
      if (usedHelpers.Count > 0 || constsAndGlobals.Length > 0) {
        text.Append("namespace {\n\n");
        foreach (var helperName in usedHelpers)
          text.Append(CppHelpers.Get(helperName).Code).Append("\n\n");
        if (constsAndGlobals.Length > 0)
          text.Append(constsAndGlobals).Append('\n');
        text.Append("} // namespace\n\n");
      }

      if (privateDecls.Length > 0)
        text.Append(privateDecls).Append('\n');
      if (classImpls.Length > 0)
        text.Append(classImpls);
      if (privateBodies.Length > 0)
        text.Append(privateBodies);
      if (publicBodies.Length > 0)
        text.Append(publicBodies);

      if (module.Initializer is { } initName && this._functionNames.TryGetValue(initName, out var cppInit)) {
        text.Append("namespace {\nstruct PlyModuleInit { PlyModuleInit() { ").Append(cppInit).Append("(); } };\n");
        text.Append("PlyModuleInit ply_module_init_{};\n} // namespace\n\n");
      }

      text.Append("} // namespace ").Append(ns).Append('\n');
      return text.ToString();
    }

    private void AssignTypesAndMembers(IrModule module) {
      var takenTypes = new HashSet<string>(StringComparer.Ordinal);
      foreach (var decl in module.Types) {
        var name = CppNaming.Free(CppNaming.Pascal(decl.Name), takenTypes.Contains);
        takenTypes.Add(name);
        this._typeNames[decl] = name;

        if (decl is IrEnumType @enum) {
          var memberMap = new Dictionary<IrEnumMember, string>(ReferenceEqualityComparer.Instance);
          var takenMembers = new HashSet<string>(StringComparer.Ordinal);
          foreach (var member in @enum.Members) {
            var mname = CppNaming.Free(CppNaming.Pascal(member.Name), takenMembers.Contains);
            takenMembers.Add(mname);
            memberMap[member] = mname;
          }
          this._enumMembers[@enum] = memberMap;
        } else if (decl is IrStructType st) {
          var takenFields = new HashSet<string>(StringComparer.Ordinal);
          foreach (var field in st.Fields) {
            var fname = CppNaming.Free(CppNaming.Identifier(field.Name, "field"), takenFields.Contains);
            takenFields.Add(fname);
            this._fieldNames[field] = fname;
          }
        }
      }

      this.PrepareClassesAndInterfaces(module);
    }

    private void AssignFunctions(IrModule module) {
      var taken = new HashSet<string>(StringComparer.Ordinal);
      foreach (var fn in module.Functions) {
        if (this._classFunctionOwners.ContainsKey(fn.Name) || this._interfaceFunctionNames.Contains(fn.Name))
          continue;

        var baseName = CppNaming.Identifier(fn.Name, "fn");
        var assigned = CppNaming.Free(baseName, taken.Contains);
        taken.Add(assigned);
        this._functionNames[fn.Name] = assigned;
      }
    }

    private void AssignConstantsAndGlobals(IrModule module) {
      var taken = new HashSet<string>(StringComparer.Ordinal);
      foreach (var constant in module.Constants) {
        var name = CppNaming.Free("ply_c_" + CppNaming.Identifier(constant.Name, "const"), taken.Contains);
        taken.Add(name);
        this._constNames[constant] = name;
      }

      foreach (var global in module.Globals) {
        if (this._staticClassGlobals.Contains(global))
          continue;
        var name = CppNaming.Free("ply_g_" + CppNaming.Identifier(global.Name, "global"), taken.Contains);
        taken.Add(name);
        this._globalNames[global] = name;
      }
    }

    private void AnalyzeParamModes(IrFunction fn) {
      var modes = new ParamMode[fn.Parameters.Count];
      for (var i = 0; i < fn.Parameters.Count; ++i) {
        var p = fn.Parameters[i];
        if (p.Type.IsArray || p.Type.IsList) {
          var reassigned = IsLocalReassigned(fn.Body, p);
          if (reassigned)
            modes[i] = ParamMode.Value;
          else if (IsCollectionMutated(fn.Body, p))
            modes[i] = ParamMode.MutRef;
          else
            modes[i] = ParamMode.ConstRef;
        } else if (p.Type == IrType.String) {
          modes[i] = IsLocalReassigned(fn.Body, p) ? ParamMode.Value : ParamMode.ConstRef;
        } else {
          modes[i] = ParamMode.Value;
        }
      }
      this._paramModes[fn] = modes;
    }

    private void EmitHeaderEnums(IrModule module, StringBuilder text) {
      foreach (var @enum in module.Types.OfType<IrEnumType>()) {
        var name = this._typeNames[@enum];
        var underlying = this.CppType(@enum.Underlying, inHeader: true);
        text.Append("enum class ").Append(name).Append(" : ").Append(underlying).Append(" {\n");
        var members = this._enumMembers[@enum];
        for (var i = 0; i < @enum.Members.Count; ++i) {
          var m = @enum.Members[i];
          text.Append("  ").Append(members[m]).Append(" = ").Append(FormatIntLiteral(@enum.Underlying, m.Value));
          text.Append(i + 1 < @enum.Members.Count ? ",\n" : "\n");
        }
        text.Append("};\n\n");
      }
    }

    private void EmitHeaderStructs(IrModule module, StringBuilder text) {
      foreach (var st in module.Types.OfType<IrStructType>()) {
        var name = this._typeNames[st];
        text.Append("struct ").Append(name).Append(" {\n");
        foreach (var field in st.Fields) {
          var ftype = this.CppType(field.Type, inHeader: true);
          var fname = this._fieldNames[field];
          text.Append("  ").Append(ftype).Append(' ').Append(fname).Append("{};\n");
        }
        text.Append('\n');
        text.Append("  bool operator==(const ").Append(name).Append("& other) const {\n");
        if (st.Fields.Count == 0) {
          text.Append("    (void)other;\n    return true;\n");
        } else {
          text.Append("    return ");
          for (var i = 0; i < st.Fields.Count; ++i) {
            var field = st.Fields[i];
            var fname = this._fieldNames[field];
            if (i > 0)
              text.Append(" && ");
            if (field.Type.IsFloat) {
              this._headerIncludes.Add("<cmath>");
              text.Append("((this->").Append(fname).Append(" == other.").Append(fname)
                .Append(") || (std::isnan(this->").Append(fname).Append(") && std::isnan(other.").Append(fname).Append(")))");
            } else {
              text.Append("(this->").Append(fname).Append(" == other.").Append(fname).Append(')');
            }
          }
          text.Append(";\n");
        }
        text.Append("  }\n");
        text.Append("  bool operator!=(const ").Append(name).Append("& other) const { return !(*this == other); }\n");
        text.Append("};\n\n");
      }
    }

    private void EmitHeaderFunctionDeclarations(List<IrFunction> functions, StringBuilder text) {
      foreach (var fn in functions) {
        if (!fn.IsPublic)
          continue;
        text.Append(this.FormatFunctionSignature(fn, this._functionNames[fn.Name], inHeader: true, withParamNames: true)).Append(";\n");
      }
      if (functions.Any(f => f.IsPublic))
        text.Append('\n');
    }

    private void EmitModuleConstants(IrModule module, StringBuilder text) {
      foreach (var constant in module.Constants) {
        var name = this._constNames[constant];
        var type = this.CppType(constant.Type, inHeader: false);
        if (constant.Type.IsArray) {
          var elem = constant.Type.Element!;
          if (elem == IrType.String) {
            text.Append("const ").Append(type).Append(' ').Append(name).Append(" = {")
              .AppendJoin(", ", constant.Strings.Select(CppNaming.StringLiteral)).Append("};\n");
          } else {
            text.Append("const ").Append(type).Append(' ').Append(name).Append(" = {")
              .AppendJoin(", ", constant.Values.Select(v => this.FormatScalarConstant(elem, v))).Append("};\n");
          }
        } else if (constant.Type == IrType.String) {
          text.Append("const ").Append(type).Append(' ').Append(name).Append(" = ")
            .Append(CppNaming.StringLiteral(constant.Strings[0])).Append(";\n");
        } else {
          text.Append("const ").Append(type).Append(' ').Append(name).Append(" = ")
            .Append(this.FormatScalarConstant(constant.Type, constant.Values[0])).Append(";\n");
        }
      }
    }

    private void EmitModuleGlobals(IrModule module, StringBuilder text) {
      foreach (var global in module.Globals) {
        if (this._staticClassGlobals.Contains(global))
          continue;
        var name = this._globalNames[global];
        var type = this.CppType(global.Type, inHeader: false);
        var init = global.Initializer is { } expr ? this.EmitExpr(expr) : this.DefaultValue(global.Type);
        text.Append(type).Append(' ').Append(name).Append(" = ").Append(init).Append(";\n");
      }
    }

    private string FormatFunctionSignature(IrFunction fn, string cppName, bool inHeader, bool withParamNames, int skipParams = 0) {
      var ret = this.CppType(fn.ReturnType, inHeader);
      var modes = this._paramModes[fn];
      var sb = new StringBuilder();
      sb.Append(ret).Append(' ').Append(cppName).Append('(');
      var first = true;
      for (var i = skipParams; i < fn.Parameters.Count; ++i) {
        if (!first)
          sb.Append(", ");
        first = false;
        var p = fn.Parameters[i];
        sb.Append(this.FormatParamType(p.Type, modes[i], inHeader));
        if (withParamNames)
          sb.Append(' ').Append(CppNaming.Identifier(p.Name, "p"));
      }
      sb.Append(')');
      return sb.ToString();
    }

    private string EmitFunctionDefinition(IrFunction fn, string cppName) {
      this._currentFunction = fn;
      this._locals.Clear();
      this._tempCounter = 0;

      var taken = new HashSet<string>(StringComparer.Ordinal);
      foreach (var p in fn.Parameters) {
        var name = CppNaming.Free(CppNaming.Identifier(p.Name, "p"), taken.Contains);
        taken.Add(name);
        this._locals[p] = name;
      }
      foreach (var local in fn.Locals) {
        var name = CppNaming.Free(CppNaming.Identifier(local.Name, "v"), taken.Contains);
        taken.Add(name);
        this._locals[local] = name;
      }

      var modes = this._paramModes[fn];
      var ret = this.CppType(fn.ReturnType, inHeader: false);
      var usedLocals = CollectReferencedLocals(fn.Body);

      var text = new StringBuilder();
      text.Append(ret).Append(' ').Append(cppName).Append('(');
      for (var i = 0; i < fn.Parameters.Count; ++i) {
        if (i > 0)
          text.Append(", ");
        var p = fn.Parameters[i];
        if (!usedLocals.Contains(p))
          text.Append("[[maybe_unused]] ");
        text.Append(this.FormatParamType(p.Type, modes[i], inHeader: false)).Append(' ').Append(this._locals[p]);
      }
      text.Append(") {\n");
      this.EmitBlockStatements(fn.Body, text, 1);
      text.Append("}\n");

      this._currentFunction = null;
      return text.ToString();
    }

    private string FormatParamType(IrType type, ParamMode mode, bool inHeader) {
      var baseType = this.CppType(type, inHeader);
      return mode switch {
        ParamMode.ConstRef => $"const {baseType}&",
        ParamMode.MutRef => $"{baseType}&",
        _ => baseType
      };
    }

    private string CppType(IrType type, bool inHeader) {
      var includes = inHeader ? this._headerIncludes : this._sourceIncludes;
      if (type.EnumType is { } enumType)
        return this._typeNames[enumType];

      switch (type.Kind) {
        case IrTypeKind.Void: return "void";
        case IrTypeKind.Bool: return "bool";
        case IrTypeKind.I8: includes.Add("<cstdint>"); return "std::int8_t";
        case IrTypeKind.U8: includes.Add("<cstdint>"); return "std::uint8_t";
        case IrTypeKind.I16: includes.Add("<cstdint>"); return "std::int16_t";
        case IrTypeKind.U16: includes.Add("<cstdint>"); return "std::uint16_t";
        case IrTypeKind.I32: includes.Add("<cstdint>"); return "std::int32_t";
        case IrTypeKind.U32: includes.Add("<cstdint>"); return "std::uint32_t";
        case IrTypeKind.I64: includes.Add("<cstdint>"); return "std::int64_t";
        case IrTypeKind.U64: includes.Add("<cstdint>"); return "std::uint64_t";
        case IrTypeKind.F32: return "float";
        case IrTypeKind.F64: return "double";
        case IrTypeKind.String: includes.Add("<string>"); return "std::string";
        case IrTypeKind.Array or IrTypeKind.List:
          includes.Add("<vector>");
          return $"std::vector<{this.CppType(type.Element!, inHeader)}>";
        case IrTypeKind.Optional:
          includes.Add("<optional>");
          return $"std::optional<{this.CppType(type.Underlying!, inHeader)}>";
        case IrTypeKind.Struct:
          return this._typeNames[type.StructType!];
        case IrTypeKind.Object:
          if (type.ClassType is { } cls && IrExceptionClass.IsException(cls))
            return this.ExceptionCppType(cls, inHeader);
          includes.Add("<memory>");
          return $"std::shared_ptr<{this._typeNames[type.Declaration!]}>";
        default:
          this.ReportUnsupportedType(type);
          return "int";
      }
    }

    private string UnsignedCppType(IrType type) {
      this._sourceIncludes.Add("<cstdint>");
      return type.Integral.Kind switch {
        IrTypeKind.I8 or IrTypeKind.U8 => "std::uint8_t",
        IrTypeKind.I16 or IrTypeKind.U16 => "std::uint16_t",
        IrTypeKind.I32 or IrTypeKind.U32 => "std::uint32_t",
        _ => "std::uint64_t"
      };
    }

    private string DefaultValue(IrType type) {
      if (type.IsOptional) {
        this._sourceIncludes.Add("<optional>");
        return "std::nullopt";
      }
      if (type.IsObject)
        return "nullptr";
      if (type == IrType.Bool)
        return "false";
      if (type == IrType.F32)
        return "0.0f";
      if (type == IrType.F64)
        return "0.0";
      if (type.IsEnum)
        return $"{this.CppType(type, inHeader: false)}{{}}";
      if (type.IsInteger)
        return $"static_cast<{this.CppType(type, inHeader: false)}>(0)";
      return $"{this.CppType(type, inHeader: false)}{{}}";
    }

    private void UseHelper(string name) {
      if (!this._usedHelpers.Add(name))
        return;
      foreach (var header in CppHelpers.Get(name).Headers)
        this._sourceIncludes.Add(header);
    }

    private string Temp(string prefix = "ply_t")
      => prefix + (++this._tempCounter).ToString(CultureInfo.InvariantCulture);

    private string LocalName(IrLocal local) => this._locals[local];

    private void ReportUnsupportedType(IrType type)
      => this.Problems.Add(Diagnostic.Create(UnsupportedType, null, type.ToString(), this._profile.Name) with {
        Cause = this._currentFunction is { } f ? $"function {f.Name}" : this._module.Name
      });

    private string ReportUnsupportedConstruct(string detail) {
      this.Problems.Add(Diagnostic.Create(UnsupportedConstruct, null, detail) with {
        Cause = this._currentFunction is { } f ? $"function {f.Name}" : this._module.Name
      });
      return "0";
    }

    private string ReportUnsupportedIntrinsic(IrIntrinsicOp op) {
      this.Problems.Add(Diagnostic.Create(UnsupportedIntrinsic, null, op.ToString()) with {
        Cause = this._currentFunction is { } f ? $"function {f.Name}" : this._module.Name
      });
      return "0";
    }

    private static string Indent(int level) => new(' ', level * 2);

    private static bool IsLocalReassigned(IrBlock body, IrLocal local) {
      foreach (var stmt in AllStatements(body)) {
        if (stmt is IrAssign a && a.Local == local)
          return true;
        if (IrTree.Expressions(stmt).OfType<IrBind>().Any(b => b.Local == local))
          return true;
      }
      return false;
    }

    private static bool IsCollectionMutated(IrBlock body, IrLocal local) {
      foreach (var stmt in AllStatements(body)) {
        if (stmt is IrStore { Array: IrLocalRef r } && r.Local == local)
          return true;
        if (stmt is IrFieldStore { Target: IrArrayLoad { Array: IrLocalRef r2 } } && r2.Local == local)
          return true;
        foreach (var expr in IrTree.Expressions(stmt)) {
          if (expr is IrIntrinsic { Op: IrIntrinsicOp.ArrayCopy, Arguments: [_, _, IrLocalRef dst, _, _] } && dst.Local == local)
            return true;
          if (expr is IrIntrinsic { Op: IrIntrinsicOp.ArrayFill or IrIntrinsicOp.ArrayClear or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig, Arguments: [IrLocalRef t, ..] } && t.Local == local)
            return true;
          if (expr is IrCollection col && IrCollections.Mutates(col.Op) && col.Arguments.Count > 0 && col.Arguments[0] is IrLocalRef lr && lr.Local == local)
            return true;
          if (expr is IrStep { Target: IrArrayLoad { Array: IrLocalRef sr } } && sr.Local == local)
            return true;
        }
      }
      return false;
    }

    private void PropagateMutRefParamModes(List<IrFunction> functions) {
      var changed = true;
      while (changed) {
        changed = false;
        foreach (var fn in functions) {
          var modes = this._paramModes[fn];
          foreach (var stmt in AllStatements(fn.Body)) {
            foreach (var call in IrTree.Expressions(stmt).OfType<IrCall>()) {
              if (!this._functionMap.TryGetValue(call.Function, out var callee) || !this._paramModes.TryGetValue(callee, out var calleeModes))
                continue;
              for (var argIdx = 0; argIdx < call.Arguments.Count && argIdx < calleeModes.Length; ++argIdx) {
                if (calleeModes[argIdx] != ParamMode.MutRef || call.Arguments[argIdx] is not IrLocalRef localRef)
                  continue;
                for (var pIdx = 0; pIdx < fn.Parameters.Count; ++pIdx) {
                  if (fn.Parameters[pIdx] == localRef.Local && modes[pIdx] == ParamMode.ConstRef) {
                    modes[pIdx] = ParamMode.MutRef;
                    changed = true;
                  }
                }
              }
            }
          }
        }
      }
    }

    private static HashSet<IrLocal> CollectReferencedLocals(IrBlock body) {
      var used = new HashSet<IrLocal>();
      foreach (var stmt in AllStatements(body)) {
        if (stmt is IrAssign a)
          used.Add(a.Local);
        foreach (var expr in IrTree.Expressions(stmt)) {
          if (expr is IrLocalRef r)
            used.Add(r.Local);
          else if (expr is IrBind b)
            used.Add(b.Local);
        }
      }
      return used;
    }

    private static IEnumerable<IrStmt> AllStatements(IrBlock block) {
      foreach (var stmt in block.Statements) {
        yield return stmt;
        foreach (var child in IrTree.Blocks(stmt))
          foreach (var nested in AllStatements(child))
            yield return nested;
      }
    }

  }

}
