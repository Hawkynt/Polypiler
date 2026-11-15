using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// Native calls through P/Invoke (#1332): a <c>static extern</c> method with <c>[DllImport]</c> and a
/// <c>static partial</c> method with <c>[LibraryImport]</c> become foreign functions of the module, with their
/// library, symbol, calling convention, character set and the marshalling of every parameter; a call of one is an
/// <see cref="IrForeignCall"/>. An <c>out</c>, <c>ref</c> or <c>in</c> argument passes a one-element array that is
/// written back to its local after the call. Whatever lies outside the construct table of
/// <c>docs/reference/foreign-calls.md</c> is refused by name.
/// </summary>
internal sealed partial class Lowering {

  private const string InteropNamespace = "System.Runtime.InteropServices";

  /// <summary>The foreign functions of the class by their methods.</summary>
  private readonly Dictionary<IMethodSymbol, IrForeignFunction> _foreign = new(SymbolEqualityComparer.Default);

  /// <summary>Whether a method is a foreign declaration: <c>extern</c>, or a <c>partial</c> one without a body that carries <c>[LibraryImport]</c>.</summary>
  private static bool IsForeignDeclaration(MemberDeclarationSyntax member)
    => member is MethodDeclarationSyntax method && (method.Modifiers.Any(SyntaxKind.ExternKeyword)
      || (method.Modifiers.Any(SyntaxKind.PartialKeyword) && method.Body is null && method.ExpressionBody is null
        && method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => attribute.Name.ToString().Split('.')[^1] is "LibraryImport" or "LibraryImportAttribute")));

  /// <summary>Declares the foreign functions of the class before any body is lowered, which calls them.</summary>
  private void DeclareForeign(IEnumerable<(string Path, BoundModel Model, ClassDeclarationSyntax Type)> classes) {
    foreach (var (file, semantics, type) in classes) {
      path = file;
      model = semantics;
      foreach (var method in type.Members.Where(IsForeignDeclaration).Cast<MethodDeclarationSyntax>()) {
        try {
          var symbol = (IMethodSymbol)model.GetDeclaredSymbol(method)!;
          this._foreign[symbol] = this.ForeignFunction(method, symbol);
        } catch (UnsupportedException unsupported) {
          this.Report(unsupported);
        }
      }
    }
  }

  /// <summary>The foreign functions declared, in source order.</summary>
  private IReadOnlyList<IrForeignFunction> ForeignFunctions => [.. this._foreign.Values];

  /// <summary>
  /// A declaration of the class as a foreign function. The compiler has seen to the rest of its form: a member of a
  /// static class is static, <c>[DllImport]</c> stands on a <c>static extern</c> method that is not generic (CS0601,
  /// CS7042), and no other modifier fits a method of a static class.
  /// </summary>
  private IrForeignFunction ForeignFunction(MethodDeclarationSyntax method, IMethodSymbol symbol) {
    if (method.TypeParameterList is not null)
      throw new UnsupportedException(method, "a generic native function");

    IrForeignFunction? declared = null;
    foreach (var attribute in symbol.GetAttributes())
      declared = attribute.AttributeClass!.ToDisplayString() switch {
        $"{InteropNamespace}.DllImportAttribute" => this.DllImport(method, symbol, attribute),
        $"{InteropNamespace}.LibraryImportAttribute" => this.LibraryImport(method, symbol, attribute),
        _ => throw new UnsupportedException(method, $"the attribute [{attribute.AttributeClass.Name}] on a native function")
      };

    if (declared is null)
      throw new UnsupportedException(method, "an extern method without [DllImport]");

    // The marshalling of a result is a truth value's alone.
    if (symbol.GetReturnTypeAttributes().FirstOrDefault() is { } returned) {
      if (declared.ReturnType != IrType.Bool)
        throw new UnsupportedException(method, $"the attribute [return: {returned.AttributeClass!.Name}] on the result of type {symbol.ReturnType.ToDisplayString()}");

      declared = declared with { Result = this.BoolMarshal(symbol.GetReturnTypeAttributes(), method) };
    }

    return declared;
  }

  private IrForeignFunction DllImport(MethodDeclarationSyntax method, IMethodSymbol symbol, AttributeData attribute) {
    // The compiler refuses a DllImport without the name of its library (CS0591).
    var library = (string)attribute.ConstructorArguments[0].Value!;
    string? entryPoint = null;
    IrStringEncoding? charSet = null;
    var convention = IrCallingConvention.Default;
    bool? setLastError = null, exactSpelling = null;
    foreach (var (key, value) in attribute.NamedArguments)
      switch (key) {
        case "EntryPoint":
          entryPoint = value.Value as string;
          break;
        case "CharSet":
          charSet = (CharSet)(int)value.Value! switch {
            CharSet.Unicode => IrStringEncoding.Unicode,
            CharSet.Auto => throw new UnsupportedException(method, "CharSet.Auto, which depends on the platform"),
            _ => IrStringEncoding.Ansi
          };
          break;
        case "CallingConvention":
          convention = (CallingConvention)(int)value.Value! switch {
            CallingConvention.Winapi => IrCallingConvention.Winapi,
            CallingConvention.Cdecl => IrCallingConvention.Cdecl,
            CallingConvention.StdCall => IrCallingConvention.StdCall,
            CallingConvention.ThisCall => IrCallingConvention.ThisCall,
            _ => IrCallingConvention.FastCall
          };
          break;
        case "SetLastError":
          setLastError = (bool)value.Value!;
          break;
        case "ExactSpelling":
          exactSpelling = (bool)value.Value!;
          break;
        default:
          throw new UnsupportedException(method, $"DllImport with {key}");
      }

    var encoding = charSet ?? IrStringEncoding.Ansi;
    return new IrForeignFunction(symbol.Name, symbol.DeclaredAccessibility == Accessibility.Public, library, this.ForeignResult(symbol, method), this.ForeignParameters(method, symbol, encoding)) {
      Origin = IrForeignOrigin.DllImport, EntryPoint = entryPoint, CharSet = charSet, Convention = convention,
      SetLastError = setLastError, ExactSpelling = exactSpelling, Result = this.ResultMarshal(symbol)
    };
  }

  private IrForeignFunction LibraryImport(MethodDeclarationSyntax method, IMethodSymbol symbol, AttributeData attribute) {
    var library = attribute.ConstructorArguments[0].Value as string is { Length: > 0 } named ? named : throw new UnsupportedException(method, "a native function without the name of its library");
    string? entryPoint = null;
    IrStringEncoding? charSet = null;
    bool? setLastError = null;
    foreach (var (key, value) in attribute.NamedArguments)
      switch (key) {
        case "EntryPoint":
          entryPoint = value.Value as string;
          break;
        case "StringMarshalling":
          charSet = (StringMarshalling)(int)value.Value! switch {
            StringMarshalling.Utf8 => IrStringEncoding.Utf8,
            StringMarshalling.Utf16 => IrStringEncoding.Unicode,
            _ => throw new UnsupportedException(method, "StringMarshalling.Custom")
          };
          break;
        case "SetLastError":
          setLastError = (bool)value.Value!;
          break;
        default:
          throw new UnsupportedException(method, $"LibraryImport with {key}");
      }

    var parameters = this.ForeignParameters(method, symbol, charSet);
    return new IrForeignFunction(symbol.Name, symbol.DeclaredAccessibility == Accessibility.Public, library, this.ForeignResult(symbol, method), parameters) {
      Origin = IrForeignOrigin.LibraryImport, EntryPoint = entryPoint, CharSet = charSet, SetLastError = setLastError, Result = this.ResultMarshal(symbol)
    };
  }

  /// <summary>The IR type of the result of a native function: nothing, a number, a truth value, a handle or a struct of numbers.</summary>
  private IrType ForeignResult(IMethodSymbol symbol, MethodDeclarationSyntax method) {
    if (symbol.ReturnsVoid)
      return IrType.Void;

    var type = this.ForeignType(symbol.ReturnType, method.ReturnType, out _, out _);
    return type.IsNumeric || type == IrType.Bool || type.IsStruct
      ? type
      : throw new UnsupportedException(method.ReturnType, $"a native function that returns {symbol.ReturnType.ToDisplayString()}");
  }

  private IrMarshal ResultMarshal(IMethodSymbol symbol) {
    var type = symbol.ReturnType;
    return type.SpecialType switch {
      SpecialType.System_Boolean => new IrMarshal(IrMarshalKind.Bool),
      SpecialType.System_IntPtr => new IrMarshal(IrMarshalKind.Handle),
      SpecialType.System_UIntPtr => IrMarshal.Blittable with { NativeSize = true },
      _ => type.TypeKind == TypeKind.Struct && type.SpecialType == SpecialType.None ? new IrMarshal(IrMarshalKind.Struct) : IrMarshal.Blittable
    };
  }

  /// <summary>
  /// The IR type of a type at the boundary: <c>nint</c> and <c>IntPtr</c> as an <c>i64</c> handle, <c>nuint</c> and
  /// <c>UIntPtr</c> as a <c>u64</c> of the size of a pointer, a struct only of numbers, and every other type as it maps.
  /// </summary>
  private IrType ForeignType(ITypeSymbol type, SyntaxNode node, out bool handle, out bool nativeSize) {
    handle = type.SpecialType == SpecialType.System_IntPtr;
    nativeSize = handle || type.SpecialType == SpecialType.System_UIntPtr;
    if (nativeSize)
      return handle ? IrType.I64 : IrType.U64;

    if (type.TypeKind == TypeKind.Delegate)
      throw new UnsupportedException(node, $"the delegate {type.ToDisplayString()} passed to a native function");

    if (type.SpecialType == SpecialType.System_Char)
      throw new UnsupportedException(node, "a char passed to a native function");

    // A class - StringBuilder, SafeHandle, object - is marshalled by rules of its own, which the table does not have.
    if (type.TypeKind == TypeKind.Class && type.SpecialType != SpecialType.System_String)
      throw new UnsupportedException(node, $"the class {type.ToDisplayString()} at the boundary of a native function");

    var mapped = this.Map(type, node);
    if (mapped.IsEnum)
      throw new UnsupportedException(node, $"the enum {type.ToDisplayString()} passed to a native function");

    if (mapped.StructType is { } structure && structure.Fields.FirstOrDefault(field => !field.Type.IsNumeric) is { } field)
      throw new UnsupportedException(node, $"the struct {structure.Name} with the field {field.Name} of the type {field.Type} passed to a native function");

    return mapped;
  }

  private List<IrForeignParameter> ForeignParameters(MethodDeclarationSyntax method, IMethodSymbol symbol, IrStringEncoding? encoding) {
    var parameters = new List<IrForeignParameter>();
    for (var index = 0; index < symbol.Parameters.Length; ++index) {
      var parameter = symbol.Parameters[index];
      var syntax = method.ParameterList.Parameters[index];
      if (parameter.IsParams || syntax.Default is not null)
        throw new UnsupportedException(syntax, "a params or optional parameter of a native function");

      var type = this.ForeignType(parameter.Type, syntax.Type!, out var handle, out var nativeSize);
      var attributes = parameter.GetAttributes();
      var directions = attributes.Where(attribute => attribute.AttributeClass!.Name is "InAttribute" or "OutAttribute").ToList();
      var marshalAs = attributes.Where(attribute => attribute.AttributeClass!.Name is "MarshalAsAttribute").ToList();
      if (attributes.FirstOrDefault(attribute => !directions.Contains(attribute) && !marshalAs.Contains(attribute)) is { } other)
        throw new UnsupportedException(syntax, $"the attribute [{other.AttributeClass!.Name}] on a parameter of a native function");

      var unmanaged = marshalAs.Select(attribute => (UnmanagedType)(int)attribute.ConstructorArguments[0].Value!).Cast<UnmanagedType?>().FirstOrDefault();
      if (marshalAs.Count > 0 && marshalAs[0].NamedArguments.Length > 0)
        throw new UnsupportedException(syntax, $"MarshalAs with {marshalAs[0].NamedArguments[0].Key}");

      IrMarshal marshal;
      if (parameter.RefKind != RefKind.None) {
        if (directions.Count > 0 || unmanaged is not null || !(type.IsNumeric || type.IsStruct))
          throw new UnsupportedException(syntax, $"the {parameter.RefKind.ToString().ToLowerInvariant()} parameter of the type {parameter.Type.ToDisplayString()} of a native function");

        marshal = new IrMarshal(IrMarshalKind.ByRef, parameter.RefKind switch {
          RefKind.Out => IrMarshalDirection.Out,
          RefKind.Ref => IrMarshalDirection.InOut,
          _ => IrMarshalDirection.In
        }) { NativeSize = nativeSize };
        type = IrType.Array(type);
      } else if (handle || nativeSize) {
        marshal = handle ? new IrMarshal(IrMarshalKind.Handle) : IrMarshal.Blittable with { NativeSize = true };
      } else if (type == IrType.Bool) {
        marshal = this.BoolMarshal(attributes, syntax) with { Attributed = unmanaged is not null };
      } else if (type.IsString) {
        var stringEncoding = unmanaged switch {
          UnmanagedType.LPStr => IrStringEncoding.Ansi,
          UnmanagedType.LPWStr => IrStringEncoding.Unicode,
          UnmanagedType.LPUTF8Str => IrStringEncoding.Utf8,
          null => encoding ?? throw new UnsupportedException(syntax, "a string parameter of a LibraryImport without StringMarshalling"),
          _ => throw new UnsupportedException(syntax, $"a string marshalled as UnmanagedType.{unmanaged}")
        };
        if (directions.Any(attribute => attribute.AttributeClass!.Name == "OutAttribute"))
          throw new UnsupportedException(syntax, "a string marshalled out of a native function");

        marshal = new IrMarshal(IrMarshalKind.String) { Encoding = stringEncoding, Attributed = unmanaged is not null, Directed = directions.Count > 0 };
      } else if (type.IsArray && type.Element!.IsNumeric) {
        if (unmanaged is not (null or UnmanagedType.LPArray))
          throw new UnsupportedException(syntax, $"an array marshalled as UnmanagedType.{unmanaged}");

        var isIn = directions.Any(attribute => attribute.AttributeClass!.Name == "InAttribute");
        var isOut = directions.Any(attribute => attribute.AttributeClass!.Name == "OutAttribute");
        marshal = new IrMarshal(IrMarshalKind.Array, isIn == isOut ? IrMarshalDirection.InOut : isIn ? IrMarshalDirection.In : IrMarshalDirection.Out) {
          Attributed = unmanaged is not null, Directed = directions.Count > 0
        };
      } else if ((type.IsNumeric || type.IsStruct || type.IsPointer) && unmanaged is null && directions.Count == 0) {
        marshal = type.IsStruct ? new IrMarshal(IrMarshalKind.Struct) : IrMarshal.Blittable;
      } else {
        throw new UnsupportedException(syntax, $"the parameter {parameter.Name} of the type {parameter.Type.ToDisplayString()} of a native function");
      }

      parameters.Add(new IrForeignParameter(parameter.Name, type, marshal));
    }

    return parameters;
  }

  /// <summary>The marshalling of a truth value by its <c>[MarshalAs]</c>: <c>Bool</c>, <c>U1</c> or <c>I1</c>; the 4-byte <c>BOOL</c> without one.</summary>
  private IrMarshal BoolMarshal(IEnumerable<AttributeData> attributes, SyntaxNode node) {
    var marshalAs = attributes.FirstOrDefault(attribute => attribute.AttributeClass!.Name == "MarshalAsAttribute");
    if (marshalAs is null)
      return new IrMarshal(IrMarshalKind.Bool);

    var form = (UnmanagedType)(int)marshalAs.ConstructorArguments[0].Value! switch {
      UnmanagedType.Bool => IrBoolForm.Bool,
      UnmanagedType.U1 => IrBoolForm.U1,
      UnmanagedType.I1 => IrBoolForm.I1,
      var other => throw new UnsupportedException(node, $"a bool marshalled as UnmanagedType.{other}")
    };
    return new IrMarshal(IrMarshalKind.Bool) { BoolForm = form, Attributed = true };
  }

  /// <summary>
  /// A value that involves a native function: a call of one, which converts to the type the compiler converts it to;
  /// <c>Marshal.GetLastWin32Error()</c> or <c>Marshal.GetLastPInvokeError()</c>; or a native function named without a
  /// call, which is refused. <see langword="null"/> for any other expression.
  /// </summary>
  private IrExpr? ForeignValue(ExpressionSyntax expression) {
    if (expression is InvocationExpressionSyntax invocation && model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method) {
      if (method is { Name: "GetLastWin32Error" or "GetLastPInvokeError", ContainingType: { Name: "Marshal" } owner } && owner.ContainingNamespace.ToDisplayString() == InteropNamespace)
        return this.Converted(new IrLastError(method.Name == "GetLastPInvokeError"), this.Map(model.GetTypeInfo(expression).ConvertedType, expression));

      if (!this._foreign.TryGetValue(method, out var function))
        return null;

      var call = this.ForeignCall(invocation, function, null);
      var converted = model.GetTypeInfo(expression).ConvertedType;
      return converted is null or { SpecialType: SpecialType.System_IntPtr or SpecialType.System_UIntPtr } || function.ReturnType == IrType.Void
        ? call
        : this.Converted(call, this.Map(converted, expression));
    }

    if (expression is IdentifierNameSyntax or MemberAccessExpressionSyntax && model.GetSymbolInfo(expression).Symbol is IMethodSymbol named && this._foreign.ContainsKey(named))
      throw new UnsupportedException(expression, $"the native function {named.Name} used as a value");

    return null;
  }

  /// <summary>
  /// A call of a native function. Every argument is evaluated left to right; an <c>out</c>, <c>ref</c> or <c>in</c>
  /// argument is a one-element array - a new one for a discard, one that holds the local for the others - and after the
  /// call the element goes back to the local of an <c>out</c> or <c>ref</c>. Where no statements can be added
  /// (<paramref name="into"/> is <see langword="null"/>) only discards are passed by reference.
  /// </summary>
  private IrExpr ForeignCall(InvocationExpressionSyntax invocation, IrForeignFunction function, List<IrStmt>? into) {
    var arguments = invocation.ArgumentList.Arguments;
    if (arguments.Any(argument => argument.NameColon is not null))
      throw new UnsupportedException(invocation, "a named argument of a native call");

    var values = new List<IrExpr>();
    var writes = new List<(Func<IrExpr, IrStmt> Target, IrExpr Array)>();
    for (var index = 0; index < function.Parameters.Count; ++index) {
      var parameter = function.Parameters[index];
      var argument = arguments[index];
      if (parameter.Marshal.Kind != IrMarshalKind.ByRef) {
        var value = this.ForeignArgument(argument.Expression, parameter);
        values.Add(into is null ? value : this.Once(value, into));
        continue;
      }

      var fresh = new IrNewArray(parameter.Type, new IrConst(IrType.I32, 1));
      if (IsDiscard(argument.Expression)) {
        values.Add(fresh);
        continue;
      }

      if (into is null)
        throw new UnsupportedException(argument, $"an {argument.RefKindKeyword.Text} argument of a native call inside an expression");

      var target = parameter.Marshal.Direction == IrMarshalDirection.In ? null : this.Target(argument.Expression);
      IrExpr initial = parameter.Marshal.Direction == IrMarshalDirection.Out
        ? fresh
        : new IrIntrinsic(IrIntrinsicOp.ArrayOf, parameter.Type, [Convert(this.Value(argument.Expression), parameter.Type.Element!)]);
      var array = this.Temporary(initial, into);
      values.Add(array);
      if (target is not null)
        writes.Add((target, array));
    }

    var call = new IrForeignCall(function.Name, function.ReturnType, values);
    if (into is null)
      return call;

    // With statements to add to, the call is one of them: its result in a temporary, the write-backs after it.
    IrExpr result = function.ReturnType == IrType.Void ? call : this.Temporary(call, into);
    if (function.ReturnType == IrType.Void)
      into.Add(new IrExprStmt(call));

    foreach (var (target, array) in writes)
      into.Add(target(new IrArrayLoad(array, new IrConst(IrType.I32, 0))));

    return result;
  }

  /// <summary>Whether an argument is a discard: <c>out _</c> or <c>out var _</c>.</summary>
  private bool IsDiscard(ExpressionSyntax expression)
    => expression is DeclarationExpressionSyntax { Designation: DiscardDesignationSyntax }
      || (expression is IdentifierNameSyntax && model.GetSymbolInfo(expression).Symbol is IDiscardSymbol);

  /// <summary>
  /// An argument of a parameter passed by value: of a number of the size of a pointer the integer it is converted from,
  /// as the parameter's <c>i64</c> or <c>u64</c>; of any other parameter the value of its type.
  /// </summary>
  private IrExpr ForeignArgument(ExpressionSyntax expression, IrForeignParameter parameter) {
    if (!parameter.Marshal.NativeSize && parameter.Marshal.Kind != IrMarshalKind.Handle)
      return this.Value(expression);

    if (model.GetConstantValue(expression) is { HasValue: true, Value: { } constant })
      return new IrConst(parameter.Type, Carried(constant));

    if (model.GetSymbolInfo(expression).Symbol is IFieldSymbol { Name: "Zero", ContainingType.SpecialType: SpecialType.System_IntPtr or SpecialType.System_UIntPtr })
      return new IrConst(parameter.Type, 0);

    var inner = expression is CastExpressionSyntax cast && model.GetTypeInfo(cast).Type!.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr
      ? cast.Expression
      : expression;
    var natural = model.GetTypeInfo(inner).Type!.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr
      ? this.ForeignValue(inner) ?? throw new UnsupportedException(inner, model.GetTypeInfo(inner).Type!.ToDisplayString())
      : this.Read(inner);
    return Convert(natural, parameter.Type);
  }

  /// <summary>
  /// A statement whose native call passes a local by reference: the call standing alone, assigned to a local or
  /// initializing one. <see langword="false"/> for any other statement, which lowers as usual.
  /// </summary>
  private bool ForeignStatement(ExpressionSyntax statement, List<IrStmt> into) {
    switch (statement) {
      case InvocationExpressionSyntax call when this.ByReference(call) is { } function:
        this.ForeignCall(call, function, into);
        return true;
      case AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Right: InvocationExpressionSyntax call } assignment
        when this.ByReference(call) is { } function:
        var target = this.Target(assignment.Left);
        var result = this.ForeignCall(call, function, into);
        if (target is not null)
          into.Add(target(this.Converted(result, this.Map(model.GetTypeInfo(assignment.Left).Type, assignment.Left))));

        return true;
      default:
        return false;
    }
  }

  /// <summary>A declaration of one local whose initializer is a native call that passes a local by reference.</summary>
  private bool ForeignStatement(VariableDeclarationSyntax declaration, List<IrStmt> into) {
    if (declaration.Variables is not [{ Initializer.Value: InvocationExpressionSyntax call } variable] || this.ByReference(call) is not { } function)
      return false;

    var result = this.ForeignCall(call, function, into);
    var symbol = (ILocalSymbol)model.GetDeclaredSymbol(variable)!;
    var declare = this.Declared(symbol, variable);
    into.Add(declare(this.Converted(result, this.Map(symbol.Type, variable))));
    return true;
  }

  /// <summary>The native function of a call that passes a local by reference, or <see langword="null"/>.</summary>
  private IrForeignFunction? ByReference(InvocationExpressionSyntax call)
    => model.GetSymbolInfo(call).Symbol is IMethodSymbol method && this._foreign.TryGetValue(method, out var function)
      && call.ArgumentList.Arguments.Any(argument => !argument.RefKindKeyword.IsKind(SyntaxKind.None) && !this.IsDiscard(argument.Expression))
        ? function : null;

  /// <summary>
  /// The layout of a struct the source states with <c>[StructLayout]</c> and <c>[FieldOffset]</c>; <see langword="null"/>
  /// without one. The compiler sees to it that every field of an explicit layout has an offset and no field of another
  /// one has (CS0625, CS0636).
  /// </summary>
  private static IrStructLayout? LayoutOf(INamedTypeSymbol type, SyntaxNode node) {
    var layout = type.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass!.Name == "StructLayoutAttribute");
    if (layout is null)
      return null;

    var kind = (LayoutKind)(int)layout.ConstructorArguments[0].Value! switch {
      LayoutKind.Sequential => IrLayoutKind.Sequential,
      LayoutKind.Explicit => IrLayoutKind.Explicit,
      _ => throw new UnsupportedException(node, "LayoutKind.Auto")
    };
    int pack = 0, size = 0;
    foreach (var (key, value) in layout.NamedArguments)
      switch (key) {
        case "Pack":
          pack = (int)value.Value!;
          break;
        case "Size":
          size = (int)value.Value!;
          break;
        default:
          throw new UnsupportedException(node, $"StructLayout with {key}");
      }

    var offsets = type.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsStatic)
      .Select(field => field.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass!.Name == "FieldOffsetAttribute"));
    return new IrStructLayout(kind, pack, size, kind == IrLayoutKind.Explicit ? [.. offsets.Select(offset => (int)offset!.ConstructorArguments[0].Value!)] : []);
  }

  /// <summary>Whether an attribute of a struct is <c>[StructLayout]</c>, which <see cref="LayoutOf"/> reads.</summary>
  private static bool IsLayoutAttribute(AttributeSyntax attribute)
    => attribute.Name.ToString().Split('.')[^1] is "StructLayout" or "StructLayoutAttribute";

}
