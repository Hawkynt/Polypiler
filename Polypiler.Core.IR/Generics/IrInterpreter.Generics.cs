using System.Globalization;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>The equality of a generic definition (<see cref="IrGenericEquals"/>): a reference by identity or a record by its fields, anything else as .NET's <c>Equals</c> of its type.</summary>
  private static bool GenericEquals(IrType type, object left, object right) => type.IsObject
    ? type.ClassType is { IsRecord: true } ? SameObject(left, right) : ReferenceEquals(left, right)
    : left.Equals(right);

  /// <summary>
  /// The order of a generic definition (<see cref="IrGenericCompare"/>) as .NET computes it: the
  /// difference for the integers narrower than 32 bits, else -1, 0 or 1 with a NaN below every number.
  /// </summary>
  private static long GenericCompare(IrType type, object left, object right) {
    if (type.IsFloat) {
      var (a, b) = (System.Convert.ToDouble(left, CultureInfo.InvariantCulture), System.Convert.ToDouble(right, CultureInfo.InvariantCulture));
      return a < b ? -1 : a > b ? 1 : a == b ? 0 : double.IsNaN(a) ? (double.IsNaN(b) ? 0 : -1) : 1;
    }

    if (type == IrType.Bool)
      return ((bool)left).CompareTo((bool)right);

    var (first, second) = (IrOperators.Number(type, (long)left), IrOperators.Number(type, (long)right));
    return type.Bits < 32 ? (long)(first - second) : first.CompareTo(second);
  }

}
