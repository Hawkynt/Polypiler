using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    /// <summary>The foreign functions of the module (#1332), by their names in the IR.</summary>
    private readonly Dictionary<string, IrForeignFunction> _foreign = new(StringComparer.Ordinal);

    /// <summary>The Python wrapper name of every foreign function of the module.</summary>
    private readonly Dictionary<string, string> _foreignNames = new(StringComparer.Ordinal);

    /// <summary>The foreign functions called from the module's IR bodies.</summary>
    private readonly HashSet<string> _calledForeign = new(StringComparer.Ordinal);

    /// <summary>The <c>ctypes.Structure</c> class name for each struct that crosses a foreign boundary.</summary>
    private readonly Dictionary<IrStructType, string> _foreignStructs = new(ReferenceEqualityComparer.Instance);

    /// <summary>The order in which <c>ctypes.Structure</c> classes are declared (nested structs first).</summary>
    private readonly List<IrStructType> _foreignStructOrder = [];

    /// <summary>Assigns each foreign function a unique Python wrapper name.</summary>
    private void NameForeignFunctions(IrModule module) {
      foreach (var foreign in module.ForeignFunctions) {
        var wanted = PythonNaming.Function(foreign.Name, foreign.IsPublic);
        this._foreignNames[foreign.Name] = this.Take(wanted.Length == 0 ? "_foreign" : wanted);
      }
    }

    /// <summary>
    /// The awaits of the IR (#1330) and its native calls (#1332); <see langword="null"/> for any other
    /// expression. The IR awaits every async value where it creates it, so nothing of the caller runs
    /// between the start of the awaited work and its end, and no coroutine ever suspends on anything but
    /// another coroutine: Python runs them in exactly the order the interpreter does. An await of a call
    /// is <c>await</c>, of a result the value, of the completed task <c>None</c> (left out as a
    /// statement), and WhenAll is <c>await asyncio.gather(…)</c>, whose list is the IR's array: gather
    /// starts the coroutines in argument order and each runs to its end before the next starts, so the
    /// first fault in time is the first in argument order. Native calls and <see cref="IrLastError"/> are
    /// emitted through Python's standard library <c>ctypes</c>.
    /// </summary>
    private PyExpression? AsyncExpression(IrExpr expression) {
      switch (expression) {
        case IrAwait { Operand: IrAsync { Op: IrAsyncOp.Completed } }:
          return new PyNone();
        case IrAwait { Operand: IrAsync { Op: IrAsyncOp.FromResult } result }:
          return this.Expression(result.Arguments[0]);
        case IrAwait { Operand: IrAsync all }:
          return new PyAwait(new PyCall(new PyAttribute(Name("asyncio"), "gather"), [.. all.Arguments.Select(this.Expression)]));
        case IrAwait awaited:
          return new PyAwait(this.Expression(awaited.Operand));
        case IrForeignCall call:
          this._calledForeign.Add(call.Function);
          return new PyCall(Name(this._foreignNames[call.Function]), [.. call.Arguments.Select(this.Expression)]);
        case IrLastError:
          return new PyCall(new PyCall(Name("getattr"), [Name("ctypes"), new PyString("get_last_error"), new PyAttribute(Name("ctypes"), "get_errno")]), []);
        default:
          return null;
      }
    }

    /// <summary>The <c>ctypes</c> wrapper functions for every public or called foreign declaration of the module.</summary>
    private List<PyStatement> ForeignWrappers(IrModule module) {
      var wrappers = new List<PyStatement>();
      foreach (var foreign in module.ForeignFunctions)
        if (foreign.IsPublic || this._calledForeign.Contains(foreign.Name))
          wrappers.Add(this.ForeignWrapper(foreign));

      return wrappers;
    }

    /// <summary>The <c>ctypes.Structure</c> declarations for structs crossing a foreign boundary, nested structs first.</summary>
    private List<PyStatement> ForeignStructs() {
      var declarations = new List<PyStatement>();
      foreach (var structType in this._foreignStructOrder) {
        var className = this._foreignStructs[structType];
        var isUnion = structType.Layout is { Kind: IrLayoutKind.Explicit } layout && layout.Offsets.Count > 0 && layout.Offsets.All(offset => offset == 0);
        PyExpression baseClass = new PyAttribute(Name("ctypes"), isUnion ? "Union" : "Structure");
        var body = new List<PyStatement>();
        if (structType.Layout is { Kind: IrLayoutKind.Sequential, Pack: > 0 } packed)
          body.Add(new PyAssign(Name("_pack_"), Integer(packed.Pack)));

        var fields = structType.Fields.Select(field => (PyExpression)new PyTuple([
          new PyString(this._memberNames[field]),
          this.CTypeForField(field.Type)
        ])).ToList();
        body.Add(new PyAssign(Name("_fields_"), new PyList(fields)));
        declarations.Add(new PyClassDef(className, [baseClass], body));
      }

      return declarations;
    }

    /// <summary>Emits a <c>ctypes</c> wrapper function for one <see cref="IrForeignFunction"/>.</summary>
    private PyFunctionDef ForeignWrapper(IrForeignFunction function) {
      this.BeginFunction(function.Name);
      var parameters = new List<PyParameter>();
      var paramNames = new List<string>();
      foreach (var parameter in function.Parameters) {
        var name = PythonNaming.Free(PythonNaming.Local(parameter.Name), candidate => this._localNames.Contains(candidate) || this._moduleNames.Contains(candidate));
        this._localNames.Add(name);
        paramNames.Add(name);
        parameters.Add(new PyParameter(name, this.Annotation(parameter.Type)));
      }

      var returns = this.Annotation(function.ReturnType);
      var body = new List<PyStatement>();

      var libName = this.Temporary();
      var fnName = this.Temporary();
      PyExpression loader = function.Convention == IrCallingConvention.StdCall
        ? new PyCall(Name("getattr"), [Name("ctypes"), new PyString("WinDLL"), new PyAttribute(Name("ctypes"), "CDLL")])
        : new PyAttribute(Name("ctypes"), "CDLL");
      List<PyKeyword> loaderKeywords = function.SetLastError == true
        ? [new("use_last_error", new PyBool(true)), new("use_errno", new PyBool(true))]
        : [];
      body.Add(new PyAssign(Name(libName), new PyCall(loader, [new PyString(function.Library)]) { Keywords = loaderKeywords }));
      body.Add(new PyAssign(Name(fnName), new PyCall(Name("getattr"), [Name(libName), new PyString(function.Symbol)])));

      var argTypes = new List<PyExpression>();
      var callArgs = new List<PyExpression>();
      var post = new List<PyStatement>();
      for (var i = 0; i < function.Parameters.Count; ++i) {
        var parameter = function.Parameters[i];
        var argName = paramNames[i];
        argTypes.Add(this.CTypeForMarshal(parameter.Type, parameter.Marshal));
        callArgs.Add(this.MarshalForeignArgument(parameter, argName, body, post));
      }

      body.Add(new PyAssign(new PyAttribute(Name(fnName), "argtypes"), new PyList(argTypes)));
      PyExpression restype = function.ReturnType == IrType.Void
        ? new PyNone()
        : this.CTypeForMarshal(function.ReturnType, function.Result);
      body.Add(new PyAssign(new PyAttribute(Name(fnName), "restype"), restype));

      var rawCall = new PyCall(Name(fnName), callArgs);
      if (function.ReturnType == IrType.Void) {
        body.Add(new PyExpressionStatement(rawCall));
        body.AddRange(post);
      } else if (post.Count == 0 && function.Result.Kind == IrMarshalKind.Blittable) {
        body.Add(new PyReturn(rawCall));
      } else {
        var resultName = this.Temporary();
        body.Add(new PyAssign(Name(resultName), rawCall));
        body.AddRange(post);
        body.Add(new PyReturn(this.UnmarshalForeignResult(function.ReturnType, function.Result, Name(resultName))));
      }

      return new PyFunctionDef(this._foreignNames[function.Name], parameters, returns, body);
    }

    /// <summary>Registers a struct for <c>ctypes.Structure</c> emission (nested structs first) and returns its Python class name.</summary>
    private string CStructClassName(IrStructType type) {
      if (this._foreignStructs.TryGetValue(type, out var existing))
        return existing;

      foreach (var field in type.Fields)
        if (field.Type.StructType is { } nested)
          this.CStructClassName(nested);

      var name = this.Take("_Ctypes" + this.TypeNameOf(type));
      this._foreignStructs[type] = name;
      this._foreignStructOrder.Add(type);
      return name;
    }

    /// <summary>The <c>ctypes</c> type expression for a field of a <c>ctypes.Structure</c>.</summary>
    private PyExpression CTypeForField(IrType type) {
      if (type == IrType.Bool)
        return CType("c_int32");
      if (type.StructType is { } nested)
        return Name(this.CStructClassName(nested));

      return this.CTypeForScalar(type, nativeSize: false);
    }

    /// <summary>The <c>ctypes</c> type expression for a foreign parameter or result.</summary>
    private PyExpression CTypeForMarshal(IrType type, IrMarshal marshal) => marshal.Kind switch {
      IrMarshalKind.Blittable => this.CTypeForScalar(type, marshal.NativeSize),
      IrMarshalKind.Bool => marshal.BoolForm switch {
        IrBoolForm.U1 => CType("c_uint8"),
        IrBoolForm.I1 => CType("c_int8"),
        _ => CType("c_int32")
      },
      IrMarshalKind.String => marshal.Encoding == IrStringEncoding.Unicode ? CType("c_wchar_p") : CType("c_char_p"),
      IrMarshalKind.Array => new PyCall(CType("POINTER"), [this.CTypeForScalar(type.Element!, nativeSize: false)]),
      IrMarshalKind.ByRef => new PyCall(CType("POINTER"), [
        type.Element!.StructType is { } st
          ? Name(this.CStructClassName(st))
          : this.CTypeForScalar(type.Element!, marshal.NativeSize)
      ]),
      IrMarshalKind.Struct => Name(this.CStructClassName(type.StructType!)),
      _ => CType("c_void_p")
    };

    /// <summary>The <c>ctypes</c> scalar type expression for an IR number, enum, or pointer type.</summary>
    private PyExpression CTypeForScalar(IrType type, bool nativeSize) {
      if (type.IsPointer)
        return CType("c_void_p");

      var integral = type.Integral;
      if (nativeSize)
        return CType(integral == IrType.I64 ? "c_ssize_t" : "c_size_t");

      return integral.Kind switch {
        IrTypeKind.I8 => CType("c_int8"),
        IrTypeKind.U8 => CType("c_uint8"),
        IrTypeKind.I16 => CType("c_int16"),
        IrTypeKind.U16 => CType("c_uint16"),
        IrTypeKind.I32 => CType("c_int32"),
        IrTypeKind.U32 => CType("c_uint32"),
        IrTypeKind.I64 => CType("c_int64"),
        IrTypeKind.U64 => CType("c_uint64"),
        IrTypeKind.F32 => CType("c_float"),
        _ => CType("c_double")
      };
    }

    private static PyAttribute CType(string name) => new(Name("ctypes"), name);

    /// <summary>Marshals one Python argument into the expression passed to the <c>ctypes</c> foreign callable.</summary>
    private PyExpression MarshalForeignArgument(IrForeignParameter parameter, string argName, List<PyStatement> pre, List<PyStatement> post) {
      var arg = Name(argName);
      var marshal = parameter.Marshal;
      switch (marshal.Kind) {
        case IrMarshalKind.Blittable or IrMarshalKind.Handle:
          return arg;
        case IrMarshalKind.Bool:
          return new PyConditional(arg, Integer(1), Integer(0));
        case IrMarshalKind.String:
          if (marshal.Encoding == IrStringEncoding.Unicode)
            return arg;
          return new PyConditional(Compare(arg, "is", new PyNone()), new PyNone(), new PyCall(new PyAttribute(arg, "encode"), [new PyString("utf-8")]));
        case IrMarshalKind.Struct:
          return this.ToCStruct(parameter.Type.StructType!, arg);
        case IrMarshalKind.Array: {
          var elemCtype = this.CTypeForScalar(parameter.Type.Element!, nativeSize: false);
          var bufName = this.Temporary();
          var arrayType = Binary(elemCtype, "*", new PyCall(Name("len"), [arg]));
          var bufInit = new PyConditional(Compare(arg, "is", new PyNone()), new PyNone(), new PyCall(arrayType, [new PyStarred(arg)]));
          pre.Add(new PyAssign(Name(bufName), bufInit));
          if (marshal.Direction != IrMarshalDirection.In) {
            var idxName = this.Temporary();
            var loop = new PyFor(idxName, new PyCall(Name("range"), [new PyCall(Name("len"), [arg])]), [
              new PyAssign(new PySubscript(arg, Name(idxName)), new PySubscript(Name(bufName), Name(idxName)))
            ]);
            post.Add(new PyIf(Compare(Name(bufName), "is not", new PyNone()), [loop], []));
          }
          return Name(bufName);
        }
        default: {
          // ByRef: one-element list holding a numeric or struct value.
          var elem = parameter.Type.Element!;
          var refName = this.Temporary();
          if (elem.StructType is { } st) {
            var cls = Name(this.CStructClassName(st));
            var init = marshal.Direction == IrMarshalDirection.Out
              ? new PyCall(cls, [])
              : this.ToCStruct(st, new PySubscript(arg, Integer(0)));
            pre.Add(new PyAssign(Name(refName), init));
            if (marshal.Direction != IrMarshalDirection.In)
              post.Add(new PyAssign(new PySubscript(arg, Integer(0)), this.FromCStruct(st, Name(refName))));
          } else {
            var elemCtype = this.CTypeForScalar(elem, marshal.NativeSize);
            var initVal = marshal.Direction == IrMarshalDirection.Out ? this.Zero(elem.Integral) : new PySubscript(arg, Integer(0));
            pre.Add(new PyAssign(Name(refName), new PyCall(elemCtype, [initVal])));
            if (marshal.Direction != IrMarshalDirection.In)
              post.Add(new PyAssign(new PySubscript(arg, Integer(0)), new PyAttribute(Name(refName), "value")));
          }
          return new PyCall(CType("byref"), [Name(refName)]);
        }
      }
    }

    /// <summary>Converts a Python dataclass struct value into its <c>ctypes.Structure</c> counterpart.</summary>
    private PyExpression ToCStruct(IrStructType type, PyExpression value) {
      var cls = Name(this.CStructClassName(type));
      var args = type.Fields.Select(field => {
        PyExpression member = new PyAttribute(value, this._memberNames[field]);
        if (field.Type.StructType is { } nested)
          return this.ToCStruct(nested, member);
        if (field.Type == IrType.Bool)
          return new PyConditional(member, Integer(1), Integer(0));
        return member;
      }).ToList();
      return new PyCall(cls, args);
    }

    /// <summary>Converts a <c>ctypes.Structure</c> value back into its Python dataclass struct counterpart.</summary>
    private PyExpression FromCStruct(IrStructType type, PyExpression value) {
      var cls = Name(this.TypeNameOf(type));
      var args = type.Fields.Select(field => {
        PyExpression member = new PyAttribute(value, this._memberNames[field]);
        if (field.Type.StructType is { } nested)
          return this.FromCStruct(nested, member);
        if (field.Type == IrType.Bool)
          return new PyCall(Name("bool"), [member]);
        return member;
      }).ToList();
      return new PyCall(cls, args);
    }

    /// <summary>Unmarshals the result returned by a <c>ctypes</c> foreign callable into the IR result type.</summary>
    private PyExpression UnmarshalForeignResult(IrType type, IrMarshal marshal, PyExpression result) => marshal.Kind switch {
      IrMarshalKind.Bool => new PyCall(Name("bool"), [result]),
      IrMarshalKind.Handle => new PyConditional(Compare(result, "is", new PyNone()), Integer(0), new PyCall(Name("int"), [result])),
      IrMarshalKind.Struct => this.FromCStruct(type.StructType!, result),
      _ => result
    };

  }

}
