using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>The Rust wrapper name of each foreign function, by IR name.</summary>
    private readonly Dictionary<string, string> _foreignWrappers = new(StringComparer.Ordinal);

    /// <summary>The Rust extern symbol identifier of each foreign function, by IR name.</summary>
    private readonly Dictionary<string, string> _foreignExterns = new(StringComparer.Ordinal);

    /// <summary>The foreign functions called by at least one IR function of the module.</summary>
    private readonly HashSet<string> _calledForeign = new(StringComparer.Ordinal);

    /// <summary>
    /// Lowers every <see cref="IrUsing"/> in the module to its declaration and <c>try</c>/<c>finally</c>
    /// (<see cref="IrUsing.Lowered"/>) before any analysis runs, so nullness, ownership and exception
    /// propagation see the <c>finally</c> cleanup directly.
    /// </summary>
    private static IrModule LowerUsing(IrModule module) {
      if (!module.Functions.Any(function => HasUsing(function.Body)))
        return module;

      return module with {
        Functions = [.. module.Functions.Select(function => HasUsing(function.Body) ? function with { Body = LowerUsingBlock(function.Body) } : function)]
      };
    }

    private static bool HasUsing(IrBlock block)
      => block.Statements.Any(statement => statement is IrUsing || IrTree.Blocks(statement).Any(HasUsing));

    private static IrBlock LowerUsingBlock(IrBlock block)
      => new([.. block.Statements.Select(LowerUsingStatement)]);

    private static IrStmt LowerUsingStatement(IrStmt statement) => statement switch {
      IrUsing scope => LowerUsingBlock(scope.Lowered()),
      IrBlock block => LowerUsingBlock(block),
      IrIf choice => choice with { Then = LowerUsingBlock(choice.Then), Else = choice.Else is null ? null : LowerUsingBlock(choice.Else) },
      IrWhile loop => loop with { Body = LowerUsingBlock(loop.Body) },
      IrDoWhile loop => loop with { Body = LowerUsingBlock(loop.Body) },
      IrFor loop => loop with { Body = LowerUsingBlock(loop.Body), Step = LowerUsingBlock(loop.Step) },
      IrSwitch choice => choice with { Sections = [.. choice.Sections.Select(section => section with { Body = LowerUsingBlock(section.Body) })] },
      IrTry attempt => attempt with {
        Body = LowerUsingBlock(attempt.Body),
        Catches = [.. attempt.Catches.Select(clause => clause with { Body = LowerUsingBlock(clause.Body) })],
        Finally = attempt.Finally is null ? null : LowerUsingBlock(attempt.Finally)
      },
      IrForEachSequence loop => loop with { Body = LowerUsingBlock(loop.Body) },
      IrForEachCollection loop => loop with { Body = LowerUsingBlock(loop.Body) },
      _ => statement
    };

    /// <summary>
    /// Writes a line to standard output (<see cref="IrWriteLine"/>): <c>println!()</c> for null or
    /// the empty string, <c>println!("{}", "...")</c> for a literal without lone surrogates, and
    /// <c>String::from_utf16_lossy</c> for any other UTF-16 string (mapping <c>None</c> to <c>""</c>).
    /// </summary>
    private void WriteLine(IrWriteLine write, List<RsStatement> into) {
      switch (write.Value) {
        case IrNull:
        case IrStrConst { Value.Length: 0 }:
          into.Add(new RsExpressionStatement(new RsMacro("println", [])));
          return;
        case IrStrConst constant when !HasLoneSurrogate(constant.Value):
          into.Add(new RsExpressionStatement(new RsMacro("println", [new RsString("{}"), new RsString(constant.Value)])));
          return;
      }

      RsExpression formatted;
      if (this.IsNullableString(write.Value)) {
        var option = this.NullableStringPlace(write.Value) ?? this.NullableStringValue(write.Value);
        var mapped = new RsMethodCall(
          new RsMethodCall(this.Receiver(option), "as_ref", []),
          "map",
          [new RsClosure(false, [new RsParameter("s", "")], new RsCall(new RsPath("String::from_utf16_lossy"), [new RsPath("s")]))]);
        formatted = new RsMethodCall(mapped, "unwrap_or_default", []);
      } else {
        formatted = new RsCall(new RsPath("String::from_utf16_lossy"), [this.StringArgument(write.Value)]);
      }

      into.Add(new RsExpressionStatement(new RsMacro("println", [new RsString("{}"), formatted])));
    }

    /// <summary>
    /// Awaits an async value (<see cref="IrAwait"/>): a call of an async function awaits its future
    /// before propagating any exception; <see cref="IrAsyncOp.FromResult"/> evaluates its value directly;
    /// <see cref="IrAsyncOp.WhenAll"/> awaits every task in argument order and collects their results.
    /// </summary>
    private RsExpression Await(IrAwait awaited) => awaited.Operand switch {
      IrCall call => this.AwaitCall(call),
      IrAsync { Op: IrAsyncOp.FromResult } result => this.AsyncValue(result.Arguments[0]),
      IrAsync { Op: IrAsyncOp.WhenAll, Arguments.Count: 0 } => new RsCall(new RsPath("Vec::new"), []) { Type = awaited.Type },
      _ => new RsMacro("vec",
        [new RsArray([.. ((IrAsync)awaited.Operand).Arguments.Select(task => this.AwaitCall((IrCall)task))])]) { Bracketed = true, Type = awaited.Type }
    };

    /// <summary>Awaits a direct call of an async function, propagating its exception after <c>.await</c>.</summary>
    private RsExpression AwaitCall(IrCall call) {
      var resultType = call.Type.Element!;
      RsExpression raw = new RsCall(new RsPath(this._functions[call.Function]), this.Arguments(call)) { Type = resultType };
      RsExpression awaited = new RsAwait(raw) { Type = resultType };
      return this._throwing.Contains(call.Function) ? this.Propagated(awaited) : awaited;
    }

    /// <summary>Evaluates the operand of <see cref="IrAsyncOp.FromResult"/> in the ownership form its type needs.</summary>
    private RsExpression AsyncValue(IrExpr value) => value.Type switch {
      { IsString: true } when this.IsNullableString(value) => this.NullableStringValue(value),
      { IsString: true } => this.StringValue(value),
      { IsObject: true } => this.ObjectValueAs(value, this.MayBeNull(value), value.Type),
      _ => this.Expression(value)
    };

    /// <summary>Names the wrapper and extern declarations of every foreign function of the module.</summary>
    private void NameForeign(IrModule module) {
      this._foreignWrappers.Clear();
      this._foreignExterns.Clear();
      this._calledForeign.Clear();
      if (module.ForeignFunctions.Count == 0)
        return;

      var taken = this._functions.Values.ToHashSet(StringComparer.Ordinal);
      foreach (var foreign in module.ForeignFunctions) {
        var baseName = RustNames.Snake(foreign.Name, this.Edition);
        var wrapper = RustNames.Unique([foreign.Name], _ => baseName, taken)[0];
        this._foreignWrappers[foreign.Name] = wrapper;

        var rawName = "ply_ffi_" + wrapper.Replace("r#", "", StringComparison.Ordinal);
        this._foreignExterns[foreign.Name] = RustNames.Unique([rawName], name => name, taken)[0];
      }
    }

    /// <summary>A call of a foreign function (<see cref="IrForeignCall"/>), routed through its safe wrapper.</summary>
    private RsExpression ForeignCall(IrForeignCall call) {
      var foreign = this._module.ForeignFunctions.First(candidate => candidate.Name == call.Function);
      this._calledForeign.Add(foreign.Name);
      var wrapper = this._foreignWrappers[foreign.Name];
      this.RecordForeignUnsafe(wrapper, this._function.Name, foreign);

      var arguments = new List<RsExpression>(call.Arguments.Count);
      for (var index = 0; index < call.Arguments.Count; ++index) {
        var parameter = foreign.Parameters[index];
        var argument = call.Arguments[index];
        arguments.Add(parameter.Marshal.Kind switch {
          IrMarshalKind.Array or IrMarshalKind.ByRef => this.Borrow(argument, parameter.Marshal.Direction != IrMarshalDirection.In),
          IrMarshalKind.String => this.StringArgument(argument),
          _ => this.Expression(argument)
        });
      }

      return new RsCall(new RsPath(wrapper), arguments) { Type = call.Type };
    }

    private void RecordForeignUnsafe(string wrapper, string caller, IrForeignFunction foreign) {
      if (!this._unsafe.Any(use => use.Helper == wrapper && use.Function == caller))
        this._unsafe.Add(new RustUnsafeUse(wrapper, caller, $"foreign call to '{foreign.Library}::{foreign.Symbol}'"));
    }

    /// <summary>
    /// The <c>extern</c> blocks and <c>#[allow(unsafe_code)]</c> wrappers of the foreign functions that
    /// are exported or called by the module. Rust 2024 requires <c>unsafe extern</c> blocks.
    /// </summary>
    private IEnumerable<RsItem> ForeignItems(IrModule module) {
      var active = module.ForeignFunctions.Where(foreign => foreign.IsPublic || this._calledForeign.Contains(foreign.Name)).ToList();
      if (active.Count == 0)
        yield break;

      var groups = new List<(string Library, string Abi, List<RsFunction> Decls)>();
      var wrappers = new List<RsFunction>();

      foreach (var foreign in active) {
        this._construct = $"the foreign function '{foreign.Name}'";
        if (foreign.SetLastError == true) {
          this.Refuse("SetLastError on a foreign function");
          continue;
        }

        var abi = ForeignAbi(foreign.Convention);
        var wrapperName = this._foreignWrappers[foreign.Name];
        var externName = this._foreignExterns[foreign.Name];
        if (foreign.IsPublic)
          this.RecordForeignUnsafe(wrapperName, foreign.Name, foreign);

        var externParameters = new List<RsParameter>(foreign.Parameters.Count);
        var wrapperParameters = new List<RsParameter>(foreign.Parameters.Count);
        var preamble = new List<RsStatement>();
        var epilogue = new List<RsStatement>();
        var callArguments = new List<RsExpression>(foreign.Parameters.Count);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var valid = true;

        foreach (var parameter in foreign.Parameters) {
          var paramName = RustNames.Unique([parameter.Name], name => RustNames.Snake(name, this.Edition), taken)[0];
          if (!this.BuildForeignParameter(parameter, paramName, externParameters, wrapperParameters, preamble, epilogue, callArguments)) {
            valid = false;
            break;
          }
        }

        if (!valid)
          continue;

        var externReturn = this.ForeignReturnAbiType(foreign);
        var wrapperReturn = foreign.ReturnType == IrType.Void ? null : this.Type(foreign.ReturnType);

        var externDecl = new RsFunction(externName, "", externParameters, externReturn, new RsBlock([], null) { IsDeclaration = true }) {
          Attributes = [$"link_name = \"{EscapeString(foreign.Symbol)}\""]
        };

        var group = groups.FirstOrDefault(item => item.Library == foreign.Library && item.Abi == abi);
        if (group.Decls is null) {
          group = (foreign.Library, abi, []);
          groups.Add(group);
        }
        group.Decls.Add(externDecl);

        var rawCall = new RsCall(new RsPath(externName), callArguments);
        var unsafeCall = new RsUnsafe(new RsBlock([], rawCall));
        var bodyStatements = new List<RsStatement>(preamble);
        bodyStatements.Add(new RsComment($"SAFETY: calls foreign symbol '{foreign.Symbol}' in library '{foreign.Library}' with marshalled arguments."));

        RsExpression? tail;
        if (foreign.ReturnType == IrType.Void) {
          bodyStatements.Add(new RsExpressionStatement(unsafeCall));
          bodyStatements.AddRange(epilogue);
          tail = null;
        } else {
          var converted = this.ConvertForeignResult(foreign, epilogue.Count > 0 ? new RsPath("_ply_result") : (RsExpression)unsafeCall);
          if (epilogue.Count > 0) {
            bodyStatements.Add(new RsLet("_ply_result", false, null, unsafeCall));
            bodyStatements.AddRange(epilogue);
            tail = converted;
          } else {
            tail = converted;
          }
        }

        wrappers.Add(new RsFunction(wrapperName, foreign.IsPublic ? "pub" : "", wrapperParameters, wrapperReturn, new RsBlock(bodyStatements, tail)) {
          Attributes = ["allow(unsafe_code)"]
        });
      }

      foreach (var (library, abi, decls) in groups) {
        var isUnsafeExtern = this.Edition >= 2024;
        var attributes = new List<string> { $"link(name = \"{EscapeString(library)}\")" };
        if (isUnsafeExtern)
          attributes.Add("allow(unsafe_code)");
        yield return new RsExternBlock(abi, isUnsafeExtern, decls) { Attributes = attributes };
      }

      foreach (var wrapper in wrappers)
        yield return wrapper;
    }

    private bool BuildForeignParameter(
      IrForeignParameter parameter,
      string paramName,
      List<RsParameter> externParameters,
      List<RsParameter> wrapperParameters,
      List<RsStatement> preamble,
      List<RsStatement> epilogue,
      List<RsExpression> callArguments) {
      var path = new RsPath(paramName);
      switch (parameter.Marshal.Kind) {
        case IrMarshalKind.Blittable: {
          if (parameter.Type.IsPointer) {
            this.Refuse("a pointer parameter of a foreign function");
            return false;
          }

          var rustType = TypeName(parameter.Type)!;
          wrapperParameters.Add(new RsParameter(paramName, rustType));
          if (parameter.Marshal.NativeSize) {
            var native = parameter.Type == IrType.U64 ? "usize" : "isize";
            externParameters.Add(new RsParameter(paramName, native));
            callArguments.Add(new RsCast(path, native));
          } else {
            externParameters.Add(new RsParameter(paramName, rustType));
            callArguments.Add(path);
          }

          return true;
        }
        case IrMarshalKind.Struct: {
          var rustType = this.Type(parameter.Type);
          wrapperParameters.Add(new RsParameter(paramName, rustType));
          externParameters.Add(new RsParameter(paramName, rustType));
          callArguments.Add(path);
          return true;
        }
        case IrMarshalKind.Handle: {
          wrapperParameters.Add(new RsParameter(paramName, "i64"));
          externParameters.Add(new RsParameter(paramName, "isize"));
          callArguments.Add(new RsCast(path, "isize"));
          return true;
        }
        case IrMarshalKind.Bool: {
          var abiType = BoolAbiType(parameter.Marshal.BoolForm);
          wrapperParameters.Add(new RsParameter(paramName, "bool"));
          externParameters.Add(new RsParameter(paramName, abiType));
          callArguments.Add(new RsCall(new RsPath($"{abiType}::from"), [path]));
          return true;
        }
        case IrMarshalKind.Array: {
          var elem = TypeName(parameter.Type.Element!)!;
          var mutable = parameter.Marshal.Direction != IrMarshalDirection.In;
          wrapperParameters.Add(new RsParameter(paramName, mutable ? $"&mut [{elem}]" : $"&[{elem}]"));
          externParameters.Add(new RsParameter(paramName, mutable ? $"*mut {elem}" : $"*const {elem}"));
          callArguments.Add(new RsMethodCall(path, mutable ? "as_mut_ptr" : "as_ptr", []));
          return true;
        }
        case IrMarshalKind.ByRef: {
          var elemType = parameter.Type.Element!;
          var elem = this.Type(elemType);
          var mutable = parameter.Marshal.Direction != IrMarshalDirection.In;
          wrapperParameters.Add(new RsParameter(paramName, mutable ? $"&mut [{elem}]" : $"&[{elem}]"));
          if (!parameter.Marshal.NativeSize) {
            externParameters.Add(new RsParameter(paramName, mutable ? $"*mut {elem}" : $"*const {elem}"));
            callArguments.Add(new RsMethodCall(path, mutable ? "as_mut_ptr" : "as_ptr", []));
            return true;
          }

          var native = elemType == IrType.U64 ? "usize" : "isize";
          var tmp = "_ply_ref_" + paramName.Replace("r#", "", StringComparison.Ordinal);
          externParameters.Add(new RsParameter(paramName, mutable ? $"*mut {native}" : $"*const {native}"));
          RsExpression initial = parameter.Marshal.Direction == IrMarshalDirection.Out
            ? new RsLiteral("0")
            : new RsCast(new RsIndex(path, new RsLiteral("0")), native);
          preamble.Add(new RsLet(tmp, mutable, native, initial));
          callArguments.Add(new RsReference(mutable, new RsPath(tmp)));
          if (mutable)
            epilogue.Add(new RsExpressionStatement(new RsAssign(new RsIndex(path, new RsLiteral("0")), "=", new RsCast(new RsPath(tmp), elem))));
          return true;
        }
        default: {
          wrapperParameters.Add(new RsParameter(paramName, BorrowedString));
          var buf = "_ply_str_" + paramName.Replace("r#", "", StringComparison.Ordinal);
          if (parameter.Marshal.Encoding == IrStringEncoding.Unicode) {
            externParameters.Add(new RsParameter(paramName, "*const u16"));
            preamble.Add(new RsLet(buf, true, "Vec<u16>", new RsMethodCall(path, "to_vec", [])));
            preamble.Add(new RsExpressionStatement(new RsMethodCall(new RsPath(buf), "push", [new RsLiteral("0")])));
          } else {
            externParameters.Add(new RsParameter(paramName, "*const u8"));
            preamble.Add(new RsLet(buf, true, "Vec<u8>", new RsMethodCall(new RsCall(new RsPath("String::from_utf16_lossy"), [path]), "into_bytes", [])));
            preamble.Add(new RsExpressionStatement(new RsMethodCall(new RsPath(buf), "push", [new RsLiteral("0")])));
          }

          callArguments.Add(new RsMethodCall(new RsPath(buf), "as_ptr", []));
          return true;
        }
      }
    }

    private string? ForeignReturnAbiType(IrForeignFunction foreign) {
      if (foreign.ReturnType == IrType.Void)
        return null;

      return foreign.Result.Kind switch {
        IrMarshalKind.Blittable when foreign.Result.NativeSize => foreign.ReturnType == IrType.U64 ? "usize" : "isize",
        IrMarshalKind.Blittable => TypeName(foreign.ReturnType)!,
        IrMarshalKind.Struct => this.Type(foreign.ReturnType),
        IrMarshalKind.Handle => "isize",
        _ => BoolAbiType(foreign.Result.BoolForm)
      };
    }

    private RsExpression ConvertForeignResult(IrForeignFunction foreign, RsExpression raw) => foreign.Result.Kind switch {
      IrMarshalKind.Blittable when foreign.Result.NativeSize => new RsCast(raw, TypeName(foreign.ReturnType)!) { Type = foreign.ReturnType },
      IrMarshalKind.Handle => new RsCast(raw, "i64") { Type = IrType.I64 },
      IrMarshalKind.Bool => new RsBinary(raw, "!=", new RsLiteral("0")) { Type = IrType.Bool },
      _ => raw
    };

    private static string BoolAbiType(IrBoolForm form) => form switch {
      IrBoolForm.U1 => "u8",
      IrBoolForm.I1 => "i8",
      _ => "i32"
    };

    private static string ForeignAbi(IrCallingConvention convention) => convention switch {
      IrCallingConvention.StdCall => "stdcall",
      IrCallingConvention.ThisCall => "thiscall",
      IrCallingConvention.FastCall => "fastcall",
      _ => "C"
    };

    private static string EscapeString(string value)
      => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

  }

}
