using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Kotlin.Syntax;
using Polypiler.Core.IR;
using static Polypiler.Core.Backends.Kotlin.Syntax.Kt;

namespace Polypiler.Core.Backends.Kotlin;

public static partial class KotlinEmitter {

  private sealed partial class Writer {

    /// <summary>The Kotlin name of every declared type of the module.</summary>
    private readonly Dictionary<IrTypeDeclaration, string> _typeNames = [];

    /// <summary>The exception classes of the IR's library the file declares, by the IR class.</summary>
    private readonly HashSet<IrClassType> _declaredExceptions = [];

    /// <summary>
    /// The library classes the JVM has no class of the same meaning for: each is declared in the file
    /// where it is thrown, deriving from the JVM class of the nearest meaning, so that a caller still
    /// catches an <c>ArithmeticException</c> or an <c>IllegalArgumentException</c>.
    /// </summary>
    private static readonly IReadOnlyList<(IrClassType Class, string Base)> _declarable = [
      (IrExceptionClass.DivideByZeroException, "ArithmeticException"),
      (IrExceptionClass.OverflowException, "ArithmeticException"),
      (IrExceptionClass.ArgumentOutOfRangeException, "IllegalArgumentException"),
      (IrExceptionClass.FormatException, "RuntimeException"),
      (IrExceptionClass.AssertionException, "RuntimeException")
    ];

    /// <summary>
    /// The Kotlin class of a library exception class: the JVM's own where one has the IR's meaning
    /// (<c>docs/reference/exceptions.md</c>, "Kotlin"), otherwise the class the file declares.
    /// </summary>
    private string ExceptionClass(IrClassType type) {
      if (_declarable.Any(entry => entry.Class == type)) {
        this._declaredExceptions.Add(type);
        return type.Name;
      }

      return type.Name switch {
        "ArithmeticException" => "ArithmeticException",
        "IndexOutOfRangeException" => "IndexOutOfBoundsException",
        "NullReferenceException" => "NullPointerException",
        "InvalidCastException" => "ClassCastException",
        "InvalidOperationException" => "IllegalStateException",
        "ArgumentException" => "IllegalArgumentException",
        "KeyNotFoundException" => "NoSuchElementException",
        _ => "Exception"
      };
    }

    /// <summary>The declarations of the library exception classes the file throws, in the order of the table.</summary>
    private IEnumerable<KtDeclaration> ExceptionClasses()
      => _declarable.Where(entry => this._declaredExceptions.Contains(entry.Class)).Select(entry => (KtDeclaration)new KtClass("class", entry.Class.Name,
        [new KtParameter("message", Type("String") with { Nullable = true }) { Default = Null }],
        [new KtSuperType(Type(entry.Base), [Name("message")])], []) {
        Modifiers = ["private"],
        Documentation = [$"The {entry.Class.Name} of the IR's library, which the JVM has no class of; it derives from {entry.Base}."]
      });

    /// <summary>
    /// The declared types of the module: a struct whose fields nothing writes is a <c>data class</c> of
    /// <c>val</c> properties, each with its zero as default so that the struct's default value is the
    /// constructor without arguments; an enum is its underlying integer. Every other declared type is
    /// refused with <see cref="DeclarationUnsupported"/>.
    /// </summary>
    private List<KtDeclaration> Types(IrModule module) {
      var written = module.Functions.SelectMany(function => IrClosures.Within(function.Body)).OfType<IrFieldStore>().Select(store => store.Field).ToHashSet();
      foreach (var declaration in module.Types)
        this._typeNames[declaration] = declaration is IrEnumType ? KotlinNaming.Type(declaration.Name) : this.Take(Fallback(KotlinNaming.Type(declaration.Name), "Type"));

      var types = new List<KtDeclaration>();
      foreach (var declaration in module.Types) {
        switch (declaration) {
          case IrStructType structure when structure.Layout is not null:
            this.Refuse(MemoryUnsupportedDescriptor, $"the layout of the struct '{structure.Name}' has no Kotlin form: a class of the JVM has no fields at offsets");
            break;
          case IrStructType structure when structure.Fields.Any(written.Contains):
            this.Refuse(DeclarationUnsupportedDescriptor, $"the struct '{structure.Name}' has a field that is written; a mutable value type has no Kotlin form yet");
            break;
          case IrStructType structure:
            types.Add(new KtClass("class", this._typeNames[structure], [.. structure.Fields.Select(field =>
              new KtParameter(Fallback(KotlinNaming.Camel(field.Name), "field"), this.TypeOf(field.Type)) { Property = "val", Default = this.FieldDefault(field.Type) })], [], []) {
              Modifiers = ["data"]
            });
            break;
          case IrEnumType:
            break;
          default:
            this.Refuse(DeclarationUnsupportedDescriptor, $"the {KindOf(declaration)[..^4].ToLowerInvariant()} '{declaration.Name}' has no Kotlin form yet");
            break;
        }
      }

      return types;
    }

    /// <summary>The name of a field of a struct as its data class declares it.</summary>
    private static string FieldName(IrField field) => Fallback(KotlinNaming.Camel(field.Name), "field");

    /// <summary>The default of a field: the zero of a number, <c>false</c>, the default struct; <see langword="null"/> for a type that has none.</summary>
    private KtExpression? FieldDefault(IrType type) => type switch {
      { IsStruct: true, Declaration: { } declaration } when this._typeNames.TryGetValue(declaration, out var name) => Call(name),
      _ when KotlinTypes.Scalar(type) is not null => this.Zero(type),
      _ => null
    };

  }

}
