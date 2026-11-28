using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Core.Verification.Runners;

/// <summary>How the C# driver holds a value of an image type.</summary>
public enum CSharpContainer {

  /// <summary>An integer, <c>bool</c>, <c>char</c>, <c>float</c> or <c>double</c>.</summary>
  Scalar,

  /// <summary>An enum over an integer type; the element is the underlying type.</summary>
  Enum,

  /// <summary>A <c>string</c>, the image <c>utf16</c>.</summary>
  String,

  /// <summary>An array <c>T[]</c>; the element is <c>T</c>.</summary>
  Array,

  /// <summary>A <c>Span&lt;T&gt;</c> over an array the driver makes; the element is <c>T</c>.</summary>
  Span,

  /// <summary>A <c>ReadOnlySpan&lt;T&gt;</c> over an array the driver makes; the element is <c>T</c>.</summary>
  ReadOnlySpan,

  /// <summary>A reference annotated nullable, <c>T[]?</c> or <c>string?</c>; the element is the type without the annotation.</summary>
  Optional,

  /// <summary>A value tuple; the members are <c>Item1</c>, <c>Item2</c>, ….</summary>
  Tuple,

  /// <summary>A struct; the members are its instance fields, an auto-property's under the property's name.</summary>
  Struct

}

/// <summary>A member of a tuple or struct shape.</summary>
/// <param name="Name">The name the driver reads and writes it by.</param>
/// <param name="Shape">Its shape.</param>
public sealed record CSharpMember(string Name, CSharpShape Shape);

/// <summary>A C# type the driver can decode and encode, with the image type it travels as.</summary>
/// <param name="Image">The image type.</param>
/// <param name="TypeName">The fully qualified C# type, such as <c>int</c> or <c>global::System.Span&lt;int&gt;</c>.</param>
/// <param name="Container">How the driver holds it.</param>
/// <param name="Element">The element of an array, span, optional or enum; <see langword="null"/> otherwise.</param>
/// <param name="Members">The members of a tuple or struct; empty otherwise.</param>
public sealed record CSharpShape(ImageType Image, string TypeName, CSharpContainer Container, CSharpShape? Element, IReadOnlyList<CSharpMember> Members) {

  /// <summary>A scalar of a C# keyword type.</summary>
  /// <param name="image">The scalar image type.</param>
  /// <param name="keyword">The C# keyword, such as <c>int</c>.</param>
  public static CSharpShape Scalar(ImageType image, string keyword) => new(image, keyword, CSharpContainer.Scalar, null, []);

}

/// <summary>How an argument is passed: the modifier at the call.</summary>
public enum CSharpPassing {

  /// <summary>By value.</summary>
  Value,

  /// <summary><c>ref</c>.</summary>
  Ref,

  /// <summary><c>out</c>.</summary>
  Out,

  /// <summary><c>in</c> (also for a <c>ref readonly</c> parameter).</summary>
  In

}

/// <summary>A parameter of an export as the driver passes it.</summary>
/// <param name="Name">The name in the signature.</param>
/// <param name="Shape">The declared type.</param>
/// <param name="Passing">The modifier at the call.</param>
/// <param name="Mode">The mode in the signature.</param>
public sealed record CSharpParameterBinding(string Name, CSharpShape Shape, CSharpPassing Passing, ParameterMode Mode);

/// <summary>An export bound to its declaration: what the driver calls and how.</summary>
/// <param name="Export">The export as the caller names it, such as <c>T.F</c>.</param>
/// <param name="Method">The method the driver calls, fully qualified, such as <c>global::T.F</c>.</param>
/// <param name="Parameters">The parameters in order.</param>
/// <param name="Result">The result; <see langword="null"/> for <c>void</c>.</param>
public sealed record CSharpExportBinding(string Export, string Method, IReadOnlyList<CSharpParameterBinding> Parameters, CSharpShape? Result) {

  /// <summary>The call signature of the binding.</summary>
  public CallSignature Signature => new([.. this.Parameters.Select(parameter => new CallParameter(parameter.Name, parameter.Shape.Image, parameter.Mode))], this.Result?.Image);

}

/// <summary>
/// Derives the call signature of a C# method from its declaration: the derivation table of the C#
/// runner's work order. Integers, <c>bool</c>, <c>char</c>, <c>float</c> and <c>double</c> are their
/// scalar images; <c>ref</c>, <c>out</c> and <c>in</c> give the modes <c>inout</c>, <c>out</c> and
/// <c>in</c>; <c>T[]</c> and <c>ReadOnlySpan&lt;T&gt;</c> are <c>seq&lt;T&gt;</c> <c>in</c>,
/// <c>Span&lt;T&gt;</c> <c>seq&lt;T&gt;</c> <c>inout</c>, of <c>byte</c> the image <c>bytes</c>; a
/// nullable annotation is <c>opt</c>; <c>string</c> is <c>utf16</c>; an enum is <c>enum</c> of its
/// underlying type; a value tuple and a struct are <c>tuple</c> and <c>record</c> in declaration order.
/// Every other type has no derived image.
/// </summary>
public static class CSharpSignatures {

  /// <summary>The call signature of a method.</summary>
  /// <param name="method">The method.</param>
  /// <returns>The signature.</returns>
  /// <exception cref="ArgumentException">A parameter or the result has no derived image, or the method is generic.</exception>
  public static CallSignature Derive(IMethodSymbol method) => Bind(method, null).Signature;

  /// <summary>
  /// Binds a method to a signature: the derived one, or the supplied one, which must name the derived
  /// image types and modes, except that an array the derivation passes <c>in</c> may be declared
  /// <c>inout</c>, whose content after the call then belongs to the output image.
  /// </summary>
  /// <param name="method">The method.</param>
  /// <param name="supplied">The supplied signature, or <see langword="null"/> for the derived one.</param>
  /// <param name="export">The export's name as the caller gave it; the method's own when <see langword="null"/>.</param>
  /// <returns>The binding.</returns>
  /// <exception cref="ArgumentException">A type has no derived image, the method is generic, or the supplied signature does not fit the declaration.</exception>
  public static CSharpExportBinding Bind(IMethodSymbol method, CallSignature? supplied, string? export = null) {
    ArgumentNullException.ThrowIfNull(method);
    if (method.IsGenericMethod)
      throw new ArgumentException($"'{method.Name}' is generic; a driver calls only methods without type parameters", nameof(method));
    if (!method.IsStatic)
      throw new ArgumentException($"'{method.Name}' is an instance method; a driver calls only static methods", nameof(method));

    var parameters = method.Parameters.Select(Parameter).ToList();
    var result = method.ReturnsVoid ? null : Shape(method.ReturnType, method.ReturnNullableAnnotation, "the result");
    if (method.RefKind != RefKind.None)
      throw new ArgumentException($"'{method.Name}' returns by reference, which has no derived image", nameof(method));

    var name = export ?? $"{method.ContainingType.ToDisplayString()}.{method.Name}";
    var qualified = $"{method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{method.Name}";
    if (supplied is not null)
      parameters = Fit(parameters, result, supplied);

    return new(name, qualified, parameters, result);
  }

  /// <summary>
  /// Finds the method an export names in C# sources: <c>Type.Method</c> or <c>Namespace.Type.Method</c>,
  /// a nested type written with dots, or a bare method name, which one type of the sources must declare.
  /// The sources are only read, never compiled into a program.
  /// </summary>
  /// <param name="files">The files; every <c>*.cs</c> among them is read.</param>
  /// <param name="export">The export.</param>
  /// <param name="parameterCount">The number of parameters, which picks among overloads; any count when <see langword="null"/>.</param>
  /// <returns>The method.</returns>
  /// <exception cref="ArgumentException">No method, or more than one, has that name and count.</exception>
  public static IMethodSymbol Find(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, string export, int? parameterCount = null) {
    ArgumentNullException.ThrowIfNull(files);
    ArgumentException.ThrowIfNullOrWhiteSpace(export);
    var options = new CSharpParseOptions(LanguageVersion.Latest);
    var trees = files.Where(file => file.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
      .OrderBy(file => file.Key, StringComparer.Ordinal)
      .Select(file => CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(file.Value.Span), options, file.Key));
    var compilation = CSharpCompilation.Create("polypiler-export-lookup", trees, _references.Value,
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    var segments = export.Split('.');
    IEnumerable<INamespaceOrTypeSymbol> containers;
    if (segments.Length == 1) {
      containers = Types(compilation.Assembly.GlobalNamespace);
    } else {
      INamespaceOrTypeSymbol container = compilation.Assembly.GlobalNamespace;
      foreach (var segment in segments[..^1]) {
        var next = container.GetMembers(segment).OfType<INamespaceOrTypeSymbol>().FirstOrDefault();
        container = next ?? throw new ArgumentException($"the export '{export}' does not exist: there is no '{segment}'", nameof(export));
      }

      containers = [container];
    }

    var methods = containers.SelectMany(container => container.GetMembers(segments[^1])).OfType<IMethodSymbol>()
      .Where(method => parameterCount is null || method.Parameters.Length == parameterCount).ToList();
    return methods.Count switch {
      0 => throw new ArgumentException($"the export '{export}' does not exist", nameof(export)),
      1 => methods[0],
      _ => throw new ArgumentException($"the export '{export}' names {methods.Count} overloads with {parameterCount} parameters", nameof(export))
    };
  }

  /// <summary>Every type of the sources, nested ones included, in declaration order.</summary>
  private static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol container) {
    foreach (var member in container.GetMembers().OfType<INamespaceOrTypeSymbol>()) {
      if (member is INamedTypeSymbol type)
        yield return type;
      foreach (var nested in Types(member))
        yield return nested;
    }
  }

  /// <summary>The supplied parameters, checked against the derived ones and taking their names and modes.</summary>
  private static List<CSharpParameterBinding> Fit(List<CSharpParameterBinding> derived, CSharpShape? result, CallSignature supplied) {
    if (supplied.Parameters.Count != derived.Count)
      throw new ArgumentException($"the signature has {supplied.Parameters.Count} parameter(s), the declaration {derived.Count}", nameof(supplied));
    if (!Equals(supplied.Result, result?.Image))
      throw new ArgumentException($"the signature returns {supplied.Result?.ToString() ?? "void"}, the declaration {result?.Image.ToString() ?? "void"}", nameof(supplied));

    var fitted = new List<CSharpParameterBinding>();
    for (var index = 0; index < derived.Count; ++index) {
      var declared = derived[index];
      var parameter = supplied.Parameters[index];
      if (!Equals(parameter.Type, declared.Shape.Image))
        throw new ArgumentException($"the parameter '{parameter.Name}' is {parameter.Type} in the signature, {declared.Shape.Image} in the declaration", nameof(supplied));

      var widened = declared.Mode == ParameterMode.In && parameter.Mode == ParameterMode.InOut && declared.Passing == CSharpPassing.Value
        && declared.Shape.Container == CSharpContainer.Array;
      if (parameter.Mode != declared.Mode && !widened)
        throw new ArgumentException($"the parameter '{parameter.Name}' is {Spelling(parameter.Mode)} in the signature, {Spelling(declared.Mode)} in the declaration", nameof(supplied));

      fitted.Add(declared with { Name = parameter.Name, Mode = parameter.Mode });
    }

    return fitted;
  }

  private static string Spelling(ParameterMode mode) => mode switch {
    ParameterMode.In => "in",
    ParameterMode.InOut => "inout",
    _ => "out"
  };

  private static CSharpParameterBinding Parameter(IParameterSymbol parameter) {
    var shape = Shape(parameter.Type, parameter.NullableAnnotation, $"the parameter '{parameter.Name}'");
    var (passing, mode) = parameter.RefKind switch {
      RefKind.Ref => (CSharpPassing.Ref, ParameterMode.InOut),
      RefKind.Out => (CSharpPassing.Out, ParameterMode.Out),
      RefKind.None => (CSharpPassing.Value, shape.Container == CSharpContainer.Span ? ParameterMode.InOut : ParameterMode.In),
      _ => (CSharpPassing.In, ParameterMode.In)
    };
    if (passing != CSharpPassing.Value && shape.Container is CSharpContainer.Span or CSharpContainer.ReadOnlySpan)
      throw new ArgumentException($"the parameter '{parameter.Name}' passes a span by reference, which has no derived image", nameof(parameter));

    return new(parameter.Name, shape, passing, mode);
  }

  private static CSharpShape Shape(ITypeSymbol type, NullableAnnotation annotation, string what, int depth = 0) {
    if (depth > 16)
      throw new ArgumentException($"{what} nests types deeper than 16 levels", nameof(type));

    if (annotation == NullableAnnotation.Annotated && type.IsReferenceType) {
      var inner = Shape(type, NullableAnnotation.NotAnnotated, what, depth + 1);
      return new(ImageType.Opt(inner.Image), inner.TypeName + "?", CSharpContainer.Optional, inner, []);
    }

    if (Keyword(type.SpecialType) is { } scalar)
      return scalar;
    if (type.SpecialType == SpecialType.System_String)
      return new(ImageType.Utf16, "string", CSharpContainer.String, null, []);

    var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    switch (type) {
      case IArrayTypeSymbol { Rank: 1 } array: {
        var element = Shape(array.ElementType, array.ElementNullableAnnotation, what, depth + 1);
        return new(Sequence(element), element.TypeName + "[]", CSharpContainer.Array, element, []);
      }
      case INamedTypeSymbol { TypeKind: TypeKind.Enum } enumeration: {
        var element = Keyword(enumeration.EnumUnderlyingType!.SpecialType)!;
        return new(ImageType.Enum(element.Image), name, CSharpContainer.Enum, element, []);
      }
      case INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } span
        when span.ContainingNamespace.ToDisplayString() == "System" && span.Name is "Span" or "ReadOnlySpan": {
        var element = Shape(span.TypeArguments[0], span.TypeArgumentNullableAnnotations[0], what, depth + 1);
        var container = span.Name == "Span" ? CSharpContainer.Span : CSharpContainer.ReadOnlySpan;
        return new(Sequence(element), name, container, element, []);
      }
      case INamedTypeSymbol { IsTupleType: true } tuple: {
        var members = tuple.TupleElements.Select((field, index) => new CSharpMember($"Item{index + 1}", Shape(field.Type, field.NullableAnnotation, what, depth + 1))).ToList();
        return new(ImageType.Tuple([.. members.Select(member => member.Shape.Image)]), name, CSharpContainer.Tuple, null, members);
      }
      case INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false } structure when structure.SpecialType == SpecialType.None && !IsHostIntrinsic(structure): {
        var members = structure.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsStatic && !field.IsConst)
          .Select(field => new CSharpMember(field.AssociatedSymbol?.Name ?? field.Name, Shape(field.Type, field.NullableAnnotation, what, depth + 1))).ToList();
        if (members.Count == 0)
          throw new ArgumentException($"{what} is the struct '{name}' without fields, which has no derived image", nameof(type));

        return new(ImageType.Record([.. members.Select(member => new ImageField(member.Name, member.Shape.Image))]), name, CSharpContainer.Struct, null, members);
      }
      default:
        throw new ArgumentException($"{what} has the type '{type.ToDisplayString()}', which has no derived image", nameof(type));
    }
  }

  /// <summary>Structs of the runtime that are no plain record of fields: <c>decimal</c>, <c>nint</c>, <c>Nullable&lt;T&gt;</c> and every struct of a <c>System</c> namespace.</summary>
  private static bool IsHostIntrinsic(INamedTypeSymbol type) {
    var space = type.ContainingNamespace.ToDisplayString();
    return space == "System" || space.StartsWith("System.", StringComparison.Ordinal);
  }

  /// <summary>A sequence of an element: <c>bytes</c> for <c>byte</c>, <c>seq</c> otherwise.</summary>
  private static ImageType Sequence(CSharpShape element) => element.TypeName == "byte" ? ImageType.Bytes : ImageType.Seq(element.Image);

  private static CSharpShape? Keyword(SpecialType type) => type switch {
    SpecialType.System_SByte => CSharpShape.Scalar(ImageType.I8, "sbyte"),
    SpecialType.System_Byte => CSharpShape.Scalar(ImageType.U8, "byte"),
    SpecialType.System_Int16 => CSharpShape.Scalar(ImageType.I16, "short"),
    SpecialType.System_UInt16 => CSharpShape.Scalar(ImageType.U16, "ushort"),
    SpecialType.System_Int32 => CSharpShape.Scalar(ImageType.I32, "int"),
    SpecialType.System_UInt32 => CSharpShape.Scalar(ImageType.U32, "uint"),
    SpecialType.System_Int64 => CSharpShape.Scalar(ImageType.I64, "long"),
    SpecialType.System_UInt64 => CSharpShape.Scalar(ImageType.U64, "ulong"),
    SpecialType.System_Boolean => CSharpShape.Scalar(ImageType.Bool, "bool"),
    SpecialType.System_Char => CSharpShape.Scalar(ImageType.Char16, "char"),
    SpecialType.System_Single => CSharpShape.Scalar(ImageType.F32, "float"),
    SpecialType.System_Double => CSharpShape.Scalar(ImageType.F64, "double"),
    _ => null
  };

  /// <summary>The reference assemblies the lookup binds against: the platform assemblies of the running host.</summary>
  private static readonly Lazy<IReadOnlyList<MetadataReference>> _references = new(() =>
    [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
      .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
      .Where(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(path) is "mscorlib.dll" or "netstandard.dll")
      .Select(path => MetadataReference.CreateFromFile(path))]);

}
