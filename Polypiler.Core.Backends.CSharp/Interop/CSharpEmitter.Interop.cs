using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// Native calls as P/Invoke in every profile (#1332): a foreign function is a <c>static extern</c> method with
/// <c>[DllImport]</c> carrying every argument the source gave, or - where the source wrote <c>[LibraryImport]</c> and the
/// framework has it - a <c>static partial</c> method of a <c>partial</c> class with <c>[LibraryImport]</c>. Below that a
/// <c>LibraryImport</c> becomes the <c>DllImport</c> of the same marshalling; a UTF-8 string is
/// <c>UnmanagedType.LPUTF8Str</c> where the framework has it and a zero-terminated <c>byte[]</c> of
/// <c>Encoding.UTF8</c> elsewhere. <c>docs/reference/foreign-calls.md</c> has the table.
/// </summary>
public static partial class CSharpEmitter {

  /// <summary>The namespace of the attributes and of <c>Marshal</c>, by its full name.</summary>
  private const string InteropNamespace = "global::System.Runtime.InteropServices.";

  /// <summary>
  /// Whether the extern method of a foreign function is called through a private method of its own: it takes an
  /// <c>IntPtr</c> or <c>UIntPtr</c> by reference, which the one-element <c>long[]</c> or <c>ulong[]</c> of the IR cannot be
  /// passed as.
  /// </summary>
  private static bool NeedsAdapter(IrForeignFunction function)
    => function.Parameters.Any(parameter => parameter.Marshal is { Kind: IrMarshalKind.ByRef, NativeSize: true });

  /// <summary>The name in the module of the method that calls a foreign function for the IR: the function's name with a <c>$</c>, which C# spells with an underscore.</summary>
  private static string AdapterName(IrForeignFunction function) => function.Name + "$";

  /// <summary>The name in the module of the extern method behind the wrapper that turns UTF-8 strings into bytes.</summary>
  private static string NativeName(IrForeignFunction function) => function.Name + "$native";

  /// <summary>The names a foreign function may take among the members of the class: its own, that of its adapter and that of the extern method behind its wrapper.</summary>
  private static IEnumerable<string> ForeignNames(IrModule module)
    => module.ForeignFunctions.SelectMany(function => new[] { function.Name, AdapterName(function), NativeName(function) });

  /// <summary>A text as a C# string literal.</summary>
  private static string Quoted(string text) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(text, true);

  private sealed partial class Writer {

    /// <summary>Whether the class writes a foreign function as <c>[LibraryImport]</c>, which makes it <c>partial</c>.</summary>
    private bool LibraryImports => this._module.ForeignFunctions.Any(this.IsLibraryImport);

    /// <summary>Whether a foreign function is written as <c>[LibraryImport]</c>: the source wrote it, and the framework has it.</summary>
    private bool IsLibraryImport(IrForeignFunction function)
      => function.Origin == IrForeignOrigin.LibraryImport && this._table.HasApi(CSharpInteropRows.LibraryImport, this._profile);

    /// <summary>The modifiers of the static class: <c>partial</c> as well where a <c>[LibraryImport]</c> method is in it.</summary>
    private IReadOnlyList<string> ClassModifiers => this.LibraryImports ? ["public", "static", "partial"] : ["public", "static"];

    /// <summary>The lines of the header of a class with native calls: the libraries, and what a <c>[LibraryImport]</c> asks of the project.</summary>
    private IEnumerable<string> ForeignNotes() {
      if (this._module.ForeignFunctions.Count == 0)
        yield break;

      yield return $"Native calls: {string.Join(", ", this._module.ForeignFunctions.Select(function => function.Library).Distinct())}, as the source names them; the library has to be there at run time.";
      if (this.LibraryImports)
        yield return "LibraryImport: the project that compiles this file must set AllowUnsafeBlocks and run the interop source generator of .NET (Microsoft.Interop.LibraryImportGenerator).";
    }

    /// <summary>The foreign function of a call.</summary>
    private IrForeignFunction ForeignFunction(string name) => this._module.ForeignFunctions.First(function => function.Name == name);

    /// <summary>
    /// The methods of the foreign functions: the extern or partial declaration of each - behind a wrapper of the
    /// source's signature where a UTF-8 string is passed as bytes - and the adapter of one that needs it.
    /// </summary>
    private IEnumerable<CsMember> ForeignMembers() {
      foreach (var function in this._module.ForeignFunctions) {
        if (this.Wrapped(function)) {
          yield return this.Wrapper(function);
          yield return this.Declaration(function) with { Name = this._members[NativeName(function)], Modifiers = ["private", "static", "extern"] };
        } else {
          yield return this.Declaration(function);
        }

        if (NeedsAdapter(function))
          yield return this.Adapter(function);
      }
    }

    /// <summary>Whether a foreign function passes a UTF-8 string as bytes, which only a wrapper of its own signature hides from the callers.</summary>
    private bool Wrapped(IrForeignFunction function) {
      var libraryImport = this.IsLibraryImport(function);
      return function.Parameters.Any(parameter => this.AsUtf8Bytes(parameter.Marshal, libraryImport));
    }

    /// <summary>
    /// The method of the source's signature that passes every UTF-8 string to the extern method as its bytes, with a
    /// terminating zero (<c>Encoding.UTF8</c> is in every profile), and every other argument as it is.
    /// </summary>
    private CsMethod Wrapper(IrForeignFunction function) {
      var names = Names(function.Parameters.Select(parameter => parameter.Name), []);
      var parameters = function.Parameters.Select(parameter => this.Parameter(parameter, names[parameter.Name], true) with { Attributes = [] }).ToList();
      var passed = this.Passed(function, (index, _) => new CsName(names[function.Parameters[index].Name]), true);
      for (var index = 0; index < function.Parameters.Count; ++index)
        if (function.Parameters[index].Marshal.Kind is IrMarshalKind.ByRef or IrMarshalKind.Handle || function.Parameters[index].Marshal.NativeSize)
          passed[index] = function.Parameters[index].Marshal.Kind == IrMarshalKind.ByRef
            ? new CsOutArgument(new CsName(names[function.Parameters[index].Name])) { Keyword = this.ReferenceKeyword(function.Parameters[index].Marshal) }
            : new CsName(names[function.Parameters[index].Name]);

      var call = new CsInvocation(new CsName(this._members[NativeName(function)]), passed);
      return new CsMethod([function.IsPublic ? "public" : "private", "static"], new CsType(this.NativeTypeName(function.ReturnType, function.Result)), this._members[function.Name], [],
        parameters, new CsBlock([function.ReturnType == IrType.Void ? new CsExpressionStatement(call) : new CsReturn(call)]), null);
    }

    /// <summary>A foreign function as P/Invoke: the attribute, the modifiers, and every parameter with its own attributes.</summary>
    private CsMethod Declaration(IrForeignFunction function) {
      var libraryImport = this.IsLibraryImport(function);
      List<string> arguments = [Quoted(function.Library)];
      if (function.EntryPoint is { } entryPoint)
        arguments.Add($"EntryPoint = {Quoted(entryPoint)}");

      if (libraryImport) {
        if (function.CharSet is { } marshalling)
          arguments.Add($"StringMarshalling = {InteropNamespace}StringMarshalling.{(marshalling == IrStringEncoding.Utf8 ? "Utf8" : "Utf16")}");
      } else if (function.CharSet is { } charSet && charSet != IrStringEncoding.Utf8) {
        arguments.Add($"CharSet = {InteropNamespace}CharSet.{charSet}");
      }

      if (!libraryImport && function.Convention != IrCallingConvention.Default)
        arguments.Add($"CallingConvention = {InteropNamespace}CallingConvention.{function.Convention}");

      if (function.SetLastError is { } setLastError)
        arguments.Add($"SetLastError = {(setLastError ? "true" : "false")}");

      if (!libraryImport && function.ExactSpelling is { } exactSpelling)
        arguments.Add($"ExactSpelling = {(exactSpelling ? "true" : "false")}");

      List<string> attributes = [$"{InteropNamespace}{(libraryImport ? "LibraryImport" : "DllImport")}({string.Join(", ", arguments)})"];
      if (function.Result is { Kind: IrMarshalKind.Bool, Attributed: true } result)
        attributes.Add($"return: {MarshalAs(result.BoolForm.ToString())}");

      var names = Names(function.Parameters.Select(parameter => parameter.Name), []);
      var parameters = function.Parameters.Select(parameter => this.Parameter(parameter, names[parameter.Name], libraryImport)).ToList();
      List<string> modifiers = [function.IsPublic ? "public" : "private", "static", libraryImport ? "partial" : "extern"];
      if (function.Parameters.Any(parameter => parameter.Type.IsPointer))
        modifiers.Add("unsafe");

      return new CsMethod(modifiers, new CsType(this.NativeTypeName(function.ReturnType, function.Result)), this._members[function.Name], [], parameters, null, null) {
        Attributes = attributes
      };
    }

    /// <summary><c>MarshalAs(UnmanagedType.…)</c>.</summary>
    private static string MarshalAs(string unmanaged) => $"{InteropNamespace}MarshalAs({InteropNamespace}UnmanagedType.{unmanaged})";

    /// <summary>The C# type of a value at the boundary: <c>IntPtr</c> for a handle, <c>UIntPtr</c> or <c>IntPtr</c> for a number of the size of a pointer, else its own.</summary>
    private string NativeTypeName(IrType type, IrMarshal marshal)
      => marshal.Kind == IrMarshalKind.Handle || (marshal.NativeSize && type == IrType.I64) ? "global::System.IntPtr"
        : marshal.NativeSize ? "global::System.UIntPtr"
        : this.TypeName(type);

    /// <summary>Whether a string is passed as the zero-terminated bytes of its UTF-8, the profile having no <c>LPUTF8Str</c>.</summary>
    private bool AsUtf8Bytes(IrMarshal marshal, bool libraryImport)
      => marshal is { Kind: IrMarshalKind.String, Encoding: IrStringEncoding.Utf8 } && !libraryImport && !this._table.HasApi(CSharpInteropRows.Utf8String, this._profile);

    /// <summary>A parameter of a foreign function, with the attributes of its marshalling.</summary>
    private CsParameter Parameter(IrForeignParameter parameter, string name, bool libraryImport) {
      var marshal = parameter.Marshal;
      var attributes = new List<string>();
      string? modifier = null;
      var type = parameter.Type;
      switch (marshal.Kind) {
        case IrMarshalKind.Bool when marshal.Attributed:
          attributes.Add(MarshalAs(marshal.BoolForm.ToString()));
          break;
        case IrMarshalKind.String when this.AsUtf8Bytes(marshal, libraryImport):
          return new CsParameter(new CsType("byte[]"), name);
        case IrMarshalKind.String:
          if (marshal.Directed)
            attributes.Add($"{InteropNamespace}In");

          // A LibraryImport of UTF-8 written as DllImport says it of every string, which no CharSet can.
          if (marshal.Attributed || (marshal.Encoding == IrStringEncoding.Utf8 && !libraryImport))
            attributes.Add(MarshalAs(marshal.Encoding switch { IrStringEncoding.Ansi => "LPStr", IrStringEncoding.Unicode => "LPWStr", _ => "LPUTF8Str" }));

          break;
        case IrMarshalKind.Array:
          if (marshal.Directed && marshal.Direction != IrMarshalDirection.Out)
            attributes.Add($"{InteropNamespace}In");
          if (marshal.Directed && marshal.Direction != IrMarshalDirection.In)
            attributes.Add($"{InteropNamespace}Out");
          if (marshal.Attributed)
            attributes.Add(MarshalAs("LPArray"));

          break;
        case IrMarshalKind.ByRef:
          type = type.Element!;
          modifier = marshal.Direction switch { IrMarshalDirection.Out => "out", IrMarshalDirection.InOut => "ref", _ => this.InParameters ? "in" : "ref" };
          if (modifier == "ref" && marshal.Direction == IrMarshalDirection.In)
            attributes.Add($"{InteropNamespace}In");

          break;
      }

      return new CsParameter(new CsType(this.NativeTypeName(type, marshal)), name, modifier) { Attributes = attributes };
    }

    /// <summary>Whether the language version has <c>in</c> parameters (C# 7.2); below, an <c>in</c> reference is <c>[In] ref</c>, which marshals the same.</summary>
    private bool InParameters => this._profile.LanguageVersion >= new Version(7, 2);

    /// <summary>The keyword of an argument passed by reference, as <see cref="Parameter"/> declared it.</summary>
    private string ReferenceKeyword(IrMarshal marshal)
      => marshal.Direction switch { IrMarshalDirection.Out => "out", IrMarshalDirection.InOut => "ref", _ => this.InParameters ? "in" : "ref" };

    /// <summary>
    /// The arguments of the extern method for the arguments of the IR: a number of the size of a pointer cast to its
    /// <c>IntPtr</c> or <c>UIntPtr</c>, the element 0 of a one-element array passed by reference, a UTF-8 string as its
    /// zero-terminated bytes where the profile has no <c>LPUTF8Str</c>, and every other one as it is.
    /// </summary>
    /// <param name="function">The foreign function.</param>
    /// <param name="argument">Writes an argument by its position at a binding: a primary expression, a cast, or <see cref="Loose"/> for as it is stored.</param>
    /// <param name="toNative">Whether the extern method behind a wrapper is called, which takes a UTF-8 string as bytes.</param>
    private List<CsExpression> Passed(IrForeignFunction function, Func<int, int, CsExpression> argument, bool toNative = false) {
      var libraryImport = this.IsLibraryImport(function);
      var passed = new List<CsExpression>();
      for (var index = 0; index < function.Parameters.Count; ++index) {
        var parameter = function.Parameters[index];
        var marshal = parameter.Marshal;
        if (marshal.Kind == IrMarshalKind.ByRef)
          passed.Add(new CsOutArgument(new CsElementAccess(argument(index, Primary), new CsIntegerLiteral(0) { Type = IrType.I32 })) { Keyword = this.ReferenceKeyword(marshal) });
        else if (marshal.Kind == IrMarshalKind.Handle || marshal.NativeSize)
          passed.Add(new CsCast(new CsType(this.NativeTypeName(parameter.Type, marshal)), argument(index, Cast)));
        else if (toNative && this.AsUtf8Bytes(marshal, libraryImport))
          passed.Add(CSharpFragments.Fill("global::System.Text.Encoding.UTF8.GetBytes({0} + \"\\0\")", [argument(index, Primary)], () => ""));
        else
          passed.Add(argument(index, Loose));
      }

      return passed;
    }

    /// <summary>The result of the extern method as the IR has it: a handle or a number of the size of a pointer as a <c>long</c> or <c>ulong</c>.</summary>
    private (CsExpression Node, int Binding) Returned(IrForeignFunction function, CsExpression call)
      => function.Result.Kind == IrMarshalKind.Handle || function.Result.NativeSize
        ? (new CsCast(new CsType(this.TypeName(function.ReturnType)), call), Cast)
        : (call, Primary);

    /// <summary>A foreign call or the last error, or <see langword="null"/> for any other expression.</summary>
    private (CsExpression Node, int Binding)? ForeignNode(IrExpr expression) {
      switch (expression) {
        case IrForeignCall call:
          var function = this.ForeignFunction(call.Function);
          if (NeedsAdapter(function))
            return (new CsInvocation(this.Qualified(this._members[AdapterName(function)]), [.. call.Arguments.Select(this.Stored)]), Primary);

          var passed = this.Passed(function, (index, level) => level == Loose ? this.Stored(call.Arguments[index]) : this.Operand(call.Arguments[index], level));
          return this.Returned(function, new CsInvocation(this.Qualified(this._members[function.Name]), passed));
        case IrLastError error:
          var method = error.PInvoke && this._table.HasApi(CSharpInteropRows.LastPInvokeError, this._profile) ? "GetLastPInvokeError" : "GetLastWin32Error";
          return (new CsInvocation(CSharpFragments.Expression($"{InteropNamespace}Marshal.{method}"), []), Primary);
        default:
          return null;
      }
    }

    /// <summary>
    /// The method that calls a foreign function with an <c>IntPtr</c> or <c>UIntPtr</c> by reference for the IR: it takes
    /// the one-element array, passes a local of the native type, and stores the local back into the array afterwards.
    /// </summary>
    private CsMethod Adapter(IrForeignFunction function) {
      var result = "result";
      var names = Names(function.Parameters.Select(parameter => parameter.Name).Concat(function.Parameters.Select(parameter => parameter.Name + "Native")).Append(result), []);
      var statements = new List<CsStatement>();
      var natives = new Dictionary<int, string>();
      for (var index = 0; index < function.Parameters.Count; ++index) {
        var parameter = function.Parameters[index];
        if (parameter.Marshal is not { Kind: IrMarshalKind.ByRef, NativeSize: true })
          continue;

        var native = names[parameter.Name + "Native"];
        natives[index] = native;
        var element = new CsElementAccess(new CsName(names[parameter.Name]), new CsIntegerLiteral(0) { Type = IrType.I32 });
        var type = new CsType(this.NativeTypeName(parameter.Type.Element!, parameter.Marshal));
        statements.Add(new CsLocalDeclaration(type, native, parameter.Marshal.Direction == IrMarshalDirection.Out ? null : new CsCast(type, element)));
      }

      var passed = this.Passed(function, (index, _) => new CsName(names[function.Parameters[index].Name]));
      foreach (var (index, native) in natives)
        passed[index] = new CsOutArgument(new CsName(native)) { Keyword = this.ReferenceKeyword(function.Parameters[index].Marshal) };

      var (call, _) = this.Returned(function, new CsInvocation(new CsName(this._members[function.Name]), passed));
      if (function.ReturnType == IrType.Void)
        statements.Add(new CsExpressionStatement(call));
      else
        statements.Add(new CsLocalDeclaration(new CsType(this.TypeName(function.ReturnType)), names[result], call));

      foreach (var (index, native) in natives.Where(pair => function.Parameters[pair.Key].Marshal.Direction != IrMarshalDirection.In)) {
        var parameter = function.Parameters[index];
        var element = new CsElementAccess(new CsName(names[parameter.Name]), new CsIntegerLiteral(0) { Type = IrType.I32 });
        statements.Add(new CsExpressionStatement(new CsAssignment(element, "=", new CsCast(new CsType(this.TypeName(parameter.Type.Element!)), new CsName(native)))));
      }

      if (function.ReturnType != IrType.Void)
        statements.Add(new CsReturn(new CsName(names[result])));

      var parameters = function.Parameters.Select(parameter => new CsParameter(new CsType(this.TypeName(parameter.Type)), names[parameter.Name])).ToList();
      return new CsMethod(["private", "static"], new CsType(this.TypeName(function.ReturnType)), this._members[AdapterName(function)], [], parameters, new CsBlock(statements), null);
    }

    /// <summary>
    /// A struct with the layout the source stated: <c>[StructLayout]</c> on it and <c>[FieldOffset]</c> on every field of
    /// an explicit one, which a struct declares first, in order, and a record struct as its positional parameters.
    /// </summary>
    private CsMember Laid(CsMember member) {
      var type = (CsTypeDeclaration)member;
      if (this._module.Types.OfType<IrStructType>().First(candidate => this._typeNames[candidate.Name] == type.Name) is not { Layout: { } layout })
        return member;

      List<string> arguments = [$"{InteropNamespace}LayoutKind.{layout.Kind}"];
      if (layout.Pack != 0)
        arguments.Add($"Pack = {layout.Pack}");
      if (layout.Size != 0)
        arguments.Add($"Size = {layout.Size}");

      var laid = type with { Attributes = [$"{InteropNamespace}StructLayout({string.Join(", ", arguments)})"] };
      if (layout.Kind != IrLayoutKind.Explicit)
        return laid;

      string Offset(int index) => $"{InteropNamespace}FieldOffset({layout.Offsets[index]})";
      return laid.Parameters is { } positional
        ? laid with { Parameters = [.. positional.Select((parameter, index) => parameter with { Attributes = [$"field: {Offset(index)}"] })] }
        : laid with { Members = [.. laid.Members!.Select((field, index) => index < layout.Offsets.Count ? (CsField)field with { Attributes = [Offset(index)] } : field)] };
    }

  }

}
