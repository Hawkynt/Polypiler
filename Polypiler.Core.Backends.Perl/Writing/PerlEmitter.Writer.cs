using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Analysis;
using Polypiler.Core.Analysis.Range;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Perl;

public static partial class PerlEmitter {

  private sealed partial class Writer(PerlProfile profile, PerlEmitOptions options) {

    private readonly PerlProfile _profile = profile;
    private readonly PerlEmitOptions _options = options;
    private readonly HashSet<string> _usedHelpers = new(StringComparer.Ordinal);
    private readonly HashSet<IrStructType> _equatableStructs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _functionNames = new(StringComparer.Ordinal);
    private readonly Dictionary<IrModuleConst, string> _constNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrGlobal, string> _globalNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrEnumType, Dictionary<IrEnumMember, string>> _enumMembers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrLocal, string> _locals = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<LoopFrame> _loops = new();
    private readonly Stack<TryFrame> _tryFrames = new();

    private IrModule _module = null!;
    private IrFunction? _currentFunction;
    private RangeAnalysis? _ranges;
    private ProgramPoint _point;
    private bool _integerFunction;
    private bool _isIterator;
    private string? _asyncResultVar;
    private SeqReturnFrame? _seqReturnFrame;
    private bool _usesPosix;
    private bool _usesCarp;
    private int _tempCounter;
    private int _labelCounter;

    public List<Diagnostic> Problems { get; } = [];

    public IReadOnlyDictionary<string, string> AssignFunctionNames(IrModule module) {
      this._functionNames.Clear();
      var taken = new HashSet<string>(StringComparer.Ordinal);
      var publicOwners = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var function in module.Functions) {
        if (this._options.TestFunctions.Contains(function.Name)
            || this._classFunctionOwners.ContainsKey(function.Name)
            || this._interfaceFunctionNames.Contains(function.Name))
          continue;

        var mapped = PerlNaming.Function(function.Name, function.IsPublic);
        if (mapped.Length == 0) {
          this.Problems.Add(Diagnostic.Create(NameCollision, null, $"export '{function.Name}' has no valid Perl name"));
          continue;
        }

        if (function.IsPublic) {
          if (publicOwners.TryGetValue(mapped, out var existing)) {
            this.Problems.Add(Diagnostic.Create(NameCollision, null,
              $"exports '{existing}' and '{function.Name}' both map to the Perl name '{mapped}'"));
            continue;
          }
          publicOwners[mapped] = function.Name;
          taken.Add(mapped);
          this._functionNames[function.Name] = mapped;
        } else {
          var unique = PerlNaming.Free(mapped, taken.Contains);
          taken.Add(unique);
          this._functionNames[function.Name] = unique;
        }
      }

      return new Dictionary<string, string>(this._functionNames, StringComparer.Ordinal);
    }

    public IReadOnlyList<GeneratedFile> EmitModule(IrModule module) {
      this._module = module;
      var package = this._options.PackageName is { Length: > 0 } custom ? custom : PerlNaming.Package(module.Name);
      this.PrepareClassesAndInterfaces(module, package);
      this.AssignFunctionNames(module);
      this.AssignEnums(module);
      this.AssignConstantsAndGlobals(module);

      var testFunctions = module.Functions.Where(f => this._options.TestFunctions.Contains(f.Name)).ToList();
      var codeFunctions = module.Functions.Where(f =>
        !this._options.TestFunctions.Contains(f.Name)
        && !this._classFunctionOwners.ContainsKey(f.Name)
        && !this._interfaceFunctionNames.Contains(f.Name)).ToList();

      var subBodies = new List<string>(codeFunctions.Count);
      foreach (var function in codeFunctions)
        subBodies.Add(this.EmitFunction(function));

      var classPackages = this.EmitClassAndInterfacePackages(module);

      var initCode = new StringBuilder();
      foreach (var global in module.Globals) {
        var gname = this._globalNames[global];
        var init = global.Initializer is { } expr
          ? this.EmitExpr(expr)
          : this.DefaultValue(global.Type);
        initCode.Append("my $").Append(gname).Append(" = ").Append(init).Append(";\n");
      }

      if (module.Initializer is { } initName && this._functionNames.TryGetValue(initName, out var perlInit))
        initCode.Append(perlInit).Append("();\n");

      var exports = codeFunctions
        .Where(f => f.IsPublic && this._functionNames.ContainsKey(f.Name))
        .Select(f => this._functionNames[f.Name])
        .Order(StringComparer.Ordinal)
        .ToList();

      var text = new StringBuilder();
      var usedHelperList = PerlHelpers.Order.Where(this._usedHelpers.Contains).Select(PerlHelpers.Descriptor).ToList();
      if (EmittedHelper.HeaderLine(usedHelperList.Select(h => h.Licence)) is { } header)
        text.Append("# ").Append(header).Append('\n');

      text.Append("package ").Append(package).Append(";\n");
      if (this._profile.VersionPragma is { } pragma)
        text.Append(pragma).Append('\n');
      text.Append("use strict;\nuse warnings;\n");
      if (this._profile.HasExporterImport)
        text.Append("use Exporter 'import';\n");
      else
        text.Append("use Exporter ();\nour @ISA = qw(Exporter);\n");
      if (this._usesCarp)
        text.Append("use Carp ();\n");
      if (this._usesPosix)
        text.Append("use POSIX ();\n");
      text.Append('\n');
      text.Append("our $VERSION = '").Append(this._options.ModuleVersion).Append("';\n");
      text.Append("our @EXPORT_OK = qw(").AppendJoin(' ', exports).Append(");\n");
      this.AppendExportTags(text, module);
      text.Append("BEGIN { die \"").Append(package).Append(" requires a perl with 64-bit integers\\n\" unless ~0 > 4294967295; }\n\n");

      this.AppendEnums(text, module);
      this.AppendConstants(text, module);
      if (initCode.Length > 0)
        text.Append(initCode).Append('\n');

      foreach (var helper in usedHelperList)
        text.Append(helper.Text).Append("\n\n");

      var emittedStructs = new HashSet<IrStructType>(ReferenceEqualityComparer.Instance);
      while (this._equatableStructs.FirstOrDefault(s => !emittedStructs.Contains(s)) is { } st) {
        emittedStructs.Add(st);
        text.Append(this.EmitStructEqualsHelper(st)).Append("\n\n");
      }

      foreach (var body in subBodies)
        text.Append(body).Append('\n');

      if (classPackages.Length > 0)
        text.Append(classPackages);

      text.Append("1;\n");
      this.AppendPod(text, package, codeFunctions, module);

      var source = text.ToString();
      var files = new List<GeneratedFile> {
        new(PerlNaming.PackagePath(package), source) { Helpers = EmittedHelper.Measure(usedHelperList, source) }
      };

      if (testFunctions.Count > 0) {
        var testBodies = new List<(string Name, string Body)>(testFunctions.Count);
        foreach (var tf in testFunctions) {
          this._currentFunction = tf;
          this._locals.Clear();
          this._loops.Clear();
          this._tryFrames.Clear();
          this._integerFunction = false;
          this._ranges = new RangeAnalysis(tf);
          var sb = new StringBuilder();
          this.EmitBlockStatements(tf.Body, sb, 1);
          testBodies.Add((tf.Name, sb.ToString()));
        }
        this._currentFunction = null;
        this._ranges = null;
        var testSource = PerlTestWriter.Emit(package, exports, testBodies);
        files.Add(new(PerlNaming.TestPath(package), testSource));
      }

      return files;
    }

    private void AssignEnums(IrModule module) {
      var enums = module.Types.OfType<IrEnumType>().ToList();
      var clashNames = enums.SelectMany(e => e.Members.Select(m => PerlNaming.Constant(m.Name)))
        .GroupBy(n => n, StringComparer.Ordinal)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)
        .ToHashSet(StringComparer.Ordinal);

      foreach (var @enum in enums) {
        var map = new Dictionary<IrEnumMember, string>(ReferenceEqualityComparer.Instance);
        var prefix = PerlNaming.Constant(@enum.Name) + "_";
        foreach (var member in @enum.Members) {
          var baseName = PerlNaming.Constant(member.Name);
          map[member] = clashNames.Contains(baseName) ? prefix + baseName : baseName;
        }
        this._enumMembers[@enum] = map;
      }
    }

    private void AssignConstantsAndGlobals(IrModule module) {
      var taken = new HashSet<string>(StringComparer.Ordinal);
      foreach (var constant in module.Constants) {
        var name = PerlNaming.Free("_C_" + PerlNaming.Constant(constant.Name), taken.Contains);
        taken.Add(name);
        this._constNames[constant] = name;
      }
      foreach (var global in module.Globals) {
        var name = PerlNaming.Free("_g_" + PerlNaming.Local(global.Name), taken.Contains);
        taken.Add(name);
        this._globalNames[global] = name;
      }
    }

    private void AppendExportTags(StringBuilder text, IrModule module) {
      var publicEnums = module.Types.OfType<IrEnumType>().Where(e => e.IsPublic && e.Members.Count > 0).ToList();
      if (publicEnums.Count == 0)
        return;

      var allEnumMembers = new List<string>();
      text.Append("our %EXPORT_TAGS = (\n");
      foreach (var @enum in publicEnums) {
        var members = @enum.Members.Select(m => this._enumMembers[@enum][m]).ToList();
        allEnumMembers.AddRange(members);
        var tag = PerlNaming.Snake(@enum.Name).Trim('_');
        text.Append("    ").Append(tag).Append(" => [qw(").AppendJoin(' ', members).Append(")],\n");
      }
      text.Append(");\n");
      text.Append("push @EXPORT_OK, qw(").AppendJoin(' ', allEnumMembers).Append(");\n");
    }

    private void AppendEnums(StringBuilder text, IrModule module) {
      foreach (var @enum in module.Types.OfType<IrEnumType>()) {
        if (@enum.Members.Count == 0)
          continue;
        if (this._profile.HasConstantHashRef) {
          text.Append("use constant {\n");
          foreach (var member in @enum.Members)
            text.Append("    ").Append(this._enumMembers[@enum][member]).Append(" => ")
              .Append(FormatIntConstant(@enum.Underlying, member.Value)).Append(",\n");
          text.Append("};\n\n");
        } else {
          foreach (var member in @enum.Members)
            text.Append("use constant ").Append(this._enumMembers[@enum][member]).Append(" => ")
              .Append(FormatIntConstant(@enum.Underlying, member.Value)).Append(";\n");
          text.Append('\n');
        }
      }
    }

    private void AppendConstants(StringBuilder text, IrModule module) {
      if (module.Constants.Count == 0)
        return;

      foreach (var constant in module.Constants) {
        var name = this._constNames[constant];
        if (constant.Type.IsArray) {
          var elem = constant.Type.Element!;
          if (elem == IrType.String) {
            text.Append("my $").Append(name).Append(" = [")
              .AppendJoin(", ", constant.Strings.Select(PerlNaming.StringLiteral)).Append("];\n");
          } else if (elem == IrType.U8 && this._profile.ByteStorage == PerlByteStorage.String) {
            var hex = string.Concat(constant.Values.Select(v => (v & 0xFF).ToString("x2", CultureInfo.InvariantCulture)));
            text.Append("my $").Append(name).Append(" = \\(my $_b_").Append(name)
              .Append(" = pack('H*', '").Append(hex).Append("'));\n");
          } else {
            text.Append("my $").Append(name).Append(" = [")
              .AppendJoin(", ", constant.Values.Select(v => FormatScalarConstant(elem, v))).Append("];\n");
          }
        } else if (constant.Type == IrType.String) {
          text.Append("my $").Append(name).Append(" = ")
            .Append(PerlNaming.StringLiteral(constant.Strings[0])).Append(";\n");
        } else {
          text.Append("my $").Append(name).Append(" = ")
            .Append(FormatScalarConstant(constant.Type, constant.Values[0])).Append(";\n");
        }
      }
      text.Append('\n');
    }

    private string EmitFunction(IrFunction function) {
      this._currentFunction = function;
      this._locals.Clear();
      this._loops.Clear();
      this._tryFrames.Clear();
      this._isIterator = false;
      this._asyncResultVar = null;
      this._seqReturnFrame = null;
      this._tempCounter = 0;
      this._labelCounter = 0;

      if (!this.CheckType(function.ReturnType, function.Name, isExport: function.IsPublic))
        return "";
      foreach (var parameter in function.Parameters)
        this.CheckType(parameter.Type, function.Name, isExport: function.IsPublic);
      foreach (var local in function.Locals)
        this.CheckType(local.Type, function.Name, isExport: false);

      var taken = new HashSet<string>(StringComparer.Ordinal);
      foreach (var parameter in function.Parameters) {
        var name = PerlNaming.Free(PerlNaming.Local(parameter.Name), taken.Contains);
        taken.Add(name);
        this._locals[parameter] = "$" + name;
      }
      foreach (var local in function.Locals) {
        var name = PerlNaming.Free(PerlNaming.Local(local.Name), taken.Contains);
        taken.Add(name);
        this._locals[local] = "$" + name;
      }

      // Integer-only sub optimization (#433): use integer; at top when no float, u64, pointer or string-byte storage appears.
      this._integerFunction = IsIntegerOnlyFunction(function);
      this._ranges = new RangeAnalysis(function);

      var subName = this._functionNames.TryGetValue(function.Name, out var mapped) ? mapped : "_" + PerlNaming.Local(function.Name);
      var text = new StringBuilder();
      text.Append("sub ").Append(subName).Append(" {\n");
      if (this._integerFunction)
        text.Append("    use integer;\n");

      if (function.IsPublic) {
        this._usesCarp = true;
        var paramList = string.Join(", ", function.Parameters.Select(p => this._locals[p]));
        text.Append("    @_ == ").Append(function.Parameters.Count.ToString(CultureInfo.InvariantCulture))
          .Append(" or Carp::croak('usage: ").Append(subName).Append('(').Append(paramList).Append(")');\n");
      }

      if (function.Parameters.Count > 0) {
        text.Append("    my (").AppendJoin(", ", function.Parameters.Select(p => this._locals[p])).Append(") = @_;\n");
      }

      // String-or-array byte API shaping (#431) for exported read-only u8[] parameters in array storage mode.
      if (function.IsPublic && this._options.ShapeByteApi && this._profile.ByteStorage == PerlByteStorage.Array) {
        foreach (var parameter in function.Parameters) {
          if (parameter.Type.IsArray && parameter.Type.Element == IrType.U8 && !IsArrayWritten(function.Body, parameter)) {
            this._usesCarp = true;
            var pvar = this._locals[parameter];
            text.Append("    if (defined(").Append(pvar).Append(") && ref(").Append(pvar).Append(") ne 'ARRAY') {\n");
            text.Append("        my $_s = ").Append(pvar).Append(";\n");
            if (this._profile.HasUtf8Downgrade)
              text.Append("        utf8::downgrade($_s, 1) or Carp::croak('wide character in octet string');\n");
            else
              text.Append("        $_s =~ /[^\\x00-\\xFF]/ and Carp::croak('wide character in octet string');\n");
            text.Append("        ").Append(pvar).Append(" = [ unpack('C*', $_s) ];\n");
            text.Append("    }\n");
          }
        }
      }

      this.EmitFunctionBody(function, text, 1);

      text.Append("}\n");
      this._currentFunction = null;
      this._isIterator = false;
      this._asyncResultVar = null;
      this._seqReturnFrame = null;
      this._ranges = null;
      return text.ToString();
    }

    private void EmitFunctionBody(IrFunction function, StringBuilder text, int indent) {
      var pad = Indent(indent);
      if (IrSequences.IsIterator(function)) {
        var saved = new List<(string Param, string Saved)>(function.Parameters.Count);
        foreach (var parameter in function.Parameters) {
          var local = this._locals[parameter];
          var copy = this.Temp("_p");
          text.Append(pad).Append("my ").Append(copy).Append(" = ").Append(local).Append(";\n");
          saved.Add((local, copy));
        }

        text.Append(pad).Append("return sub {\n");
        var innerPad = Indent(indent + 1);
        text.Append(innerPad).Append("my ($_yield) = @_;\n");
        foreach (var (param, copy) in saved)
          text.Append(innerPad).Append("my ").Append(param).Append(" = ").Append(copy).Append(";\n");

        this._isIterator = true;
        this.EmitBlockStatements(function.Body, text, indent + 1);
        this._isIterator = false;
        text.Append(innerPad).Append("return;\n");
        text.Append(pad).Append("};\n");
        return;
      }

      if (function.IsAsync) {
        var valVar = this.Temp("_aval");
        var okVar = this.Temp("_aok");
        text.Append(pad).Append("my ").Append(valVar).Append(" = undef;\n");
        text.Append(pad).Append("my ").Append(okVar).Append(" = eval {\n");
        this._asyncResultVar = valVar;
        this.EmitBlockStatements(function.Body, text, indent + 1);
        this._asyncResultVar = null;
        text.Append(Indent(indent + 1)).Append("1;\n");
        text.Append(pad).Append("};\n");
        text.Append(pad).Append("return { done => 1, error => $@ } unless ").Append(okVar).Append(";\n");
        text.Append(pad).Append("return { done => 1, value => ").Append(valVar).Append(" };\n");
        return;
      }

      this.EmitBlockStatements(function.Body, text, indent);

      if (function.ReturnType == IrType.Void && !EndsWithReturn(function.Body))
        text.Append(pad).Append("return;\n");
    }

    private static bool IsIntegerOnlyFunction(IrFunction function) {
      if (NeedsMixed(function.ReturnType))
        return false;
      if (function.Parameters.Any(p => NeedsMixed(p.Type)))
        return false;
      if (function.Locals.Any(l => NeedsMixed(l.Type)))
        return false;
      foreach (var expr in IrTree.Nodes(function.Body)) {
        if (NeedsMixed(expr.Type))
          return false;
        if (expr is IrIntrinsic { Op: IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig })
          return false;
      }
      return true;
    }

    private static bool NeedsMixed(IrType type)
      => type.IsFloat || type == IrType.U64 || type.IsPointer || type.IsView || type.IsMemory
        || ((type.IsAsync || type.IsLazySequence) && NeedsMixed(type.Element!));

    private static bool IsArrayWritten(IrBlock body, IrLocal parameter) {
      foreach (var stmt in AllStatements(body)) {
        if (stmt is IrStore { Array: IrLocalRef r } && r.Local == parameter)
          return true;
        if (stmt is IrExprStmt { Expression: IrIntrinsic { Op: IrIntrinsicOp.ArrayCopy, Arguments: [_, _, IrLocalRef dst, _, _] } } && dst.Local == parameter)
          return true;
        if (stmt is IrExprStmt { Expression: IrIntrinsic { Op: IrIntrinsicOp.ArrayFill or IrIntrinsicOp.ArrayClear, Arguments: [IrLocalRef fillTarget, ..] } } && fillTarget.Local == parameter)
          return true;
        if (stmt is IrExprStmt { Expression: IrIntrinsic { Op: IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig, Arguments: [IrLocalRef storeTarget, ..] } } && storeTarget.Local == parameter)
          return true;
      }
      return false;
    }

    private static bool EndsWithReturn(IrBlock block)
      => block.Statements.Count > 0 && block.Statements[^1] is IrReturn;

    private void AppendPod(StringBuilder text, string package, List<IrFunction> functions, IrModule module) {
      var exports = functions.Where(f => f.IsPublic && this._functionNames.ContainsKey(f.Name)).ToList();
      text.Append("\n__END__\n\n=head1 NAME\n\n").Append(package).Append("\n\n=head1 SYNOPSIS\n\n");
      text.Append("    use ").Append(package).Append(" qw(")
        .AppendJoin(' ', exports.Select(f => this._functionNames[f.Name]).Order(StringComparer.Ordinal))
        .Append(");\n\n");
      foreach (var function in exports) {
        var sub = this._functionNames[function.Name];
        var args = string.Join(", ", function.Parameters.Select(p => "$" + PerlNaming.Local(p.Name)));
        text.Append("=head2 ").Append(sub).Append('(').Append(args).Append(")\n\n");
        text.Append("Returns ").Append(function.ReturnType).Append(".\n\n");
      }
      foreach (var st in module.Types.OfType<IrStructType>()) {
        text.Append("=head2 Record ").Append(st.Name).Append("\n\nHash reference with keys: ")
          .AppendJoin(", ", st.Fields.Select(f => PerlNaming.HashKey(f.Name))).Append(".\n\n");
      }
      text.Append("=cut\n");
    }

    private bool CheckType(IrType type, string functionName, bool isExport) {
      if (isExport && type.IsFunction) {
        this.Problems.Add(Diagnostic.Create(ExportUnsupported, null, functionName, type.ToString()) with {
          Cause = "OBJ-CLOSURES"
        });
        return false;
      }
      if (type.IsBigInt) {
        this.Problems.Add(Diagnostic.Create(TypeUnsupported, null, type.ToString(), this._profile.Name) with { Cause = $"function {functionName}" });
        return false;
      }
      return true;
    }

    private void UseHelper(string name) {
      this._usedHelpers.Add(name);
      if (name is PerlHelpers.FloatDivide or PerlHelpers.FloatRemainder or PerlHelpers.FloatRemainder56
          or PerlHelpers.FloatTrunc or PerlHelpers.FloatRoundAway or PerlHelpers.FloatRoundEven)
        this._usesPosix = true;
      if (name is PerlHelpers.FloatDivide56 or PerlHelpers.FormatFloat)
        this._usedHelpers.Add(PerlHelpers.FloatSignBit);
      if (name == PerlHelpers.TrapMatches)
        this._usedHelpers.Add(PerlHelpers.TrapKind);
    }

    private string HelperCall(string name) {
      this.UseHelper(name);
      return this._currentClass is not null ? $"{this._package}::{name}" : name;
    }

    private string Temp(string prefix = "_t")
      => "$" + prefix + (++this._tempCounter).ToString(CultureInfo.InvariantCulture);

    private string NextLabel(string prefix = "LOOP")
      => prefix + (++this._labelCounter).ToString(CultureInfo.InvariantCulture);

    private string LocalName(IrLocal local) {
      if (this._locals.TryGetValue(local, out var name))
        return name;
      var fresh = "$" + PerlNaming.Free(PerlNaming.Local(local.Name), n => this._locals.Values.Contains("$" + n));
      this._locals[local] = fresh;
      return fresh;
    }

    private string FunctionPerlName(string irName)
      => this._functionNames.TryGetValue(irName, out var name) ? name : PerlNaming.Function(irName, false);

    private string UnsupportedOp(string detail) {
      this.Problems.Add(Diagnostic.Create(OperationUnsupported, null, detail) with {
        Cause = this._currentFunction is { } f ? $"function {f.Name}" : this._module.Name
      });
      return "0";
    }

    private static string Indent(int level) => new(' ', level * 4);

    private sealed record LoopFrame(string Label, string? ContinueLabel, int TryDepth, bool IsSequence = false);

    private sealed record TryFrame(string? ReturnFlag, string? ReturnValue, string? BreakFlag, string? ContinueFlag);

    private sealed record SeqReturnFrame(string ReturnFlag, string ReturnValue);

  }

}
