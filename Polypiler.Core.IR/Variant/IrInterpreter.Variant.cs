using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>A value of the dynamic type as the interpreter carries it: the tag and the payload as the interpreter carries a value of the tag's type.</summary>
  private sealed record Tagged(IrVariantTag Tag, object? Payload);

  /// <summary>A value of the dynamic type taken across the boundary: an <see cref="IrVariant"/> whose payload is a value of its tag's type, or none for a tag without one.</summary>
  private static object? AcceptedVariant(object? value) {
    if (value is not IrVariant variant)
      return null;

    if (IrVariants.PayloadType(variant.Tag) is not { } payload)
      return variant.Value is null ? new Tagged(variant.Tag, null) : null;

    return Accepted(payload, variant.Value) is { } accepted ? new Tagged(variant.Tag, accepted) : null;
  }

  /// <summary>The value a new element of an array of the dynamic type holds: <c>Empty</c>.</summary>
  private static readonly Tagged EmptyVariant = new(IrVariantTag.Empty, null);

  /// <summary>An array of the dynamic type taken across the boundary: an <see cref="IrVariant"/> array whose every element is accepted, or <see langword="null"/> for a null reference.</summary>
  private static object? AcceptedVariants(object? value) {
    if (value is null)
      return NullReference.Value;
    if (value is not IrVariant[] variants)
      return null;

    var elements = new object[variants.Length];
    for (var index = 0; index < variants.Length; ++index)
      if (AcceptedVariant(variants[index]) is { } element)
        elements[index] = element;
      else
        return null;

    return elements;
  }

  /// <summary>An array of the dynamic type given back across the boundary as an <see cref="IrVariant"/> array.</summary>
  private static IrVariant[] VariantsToHost(object[] elements) => [.. elements.Select(element => VariantToHost((Tagged)element))];

  /// <summary>A value of the dynamic type given back across the boundary, its payload as a .NET value of its tag's type.</summary>
  private static IrVariant VariantToHost(Tagged value)
    => new(value.Tag, IrVariants.PayloadType(value.Tag) is { } payload ? ToHost(payload, value.Payload) : null);

  private sealed partial class Machine {

    /// <summary>The nodes of the dynamic type: a value made from a tag and a payload, its tag, and its payload, which traps on another tag.</summary>
    private object Variant(IrExpr expression, Frame frame) {
      switch (expression) {
        case IrVariantOf made:
          return new Tagged(made.Tag, made.Value is null ? null : this.Evaluate(made.Value, frame));
        case IrVariantTagOf tag:
          return (long)((Tagged)this.Evaluate(tag.Value, frame)).Tag;
        default:
          var value = (IrVariantValue)expression;
          var held = (Tagged)this.Evaluate(value.Value, frame);
          return held.Tag == value.Tag ? held.Payload! : throw new IrTrapException(IrTrap.InvalidCast);
      }
    }

  }

}
