using System;
using System.Threading;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>The profile of the call running on this flow of execution, which an iterator's thread inherits; <see langword="null"/> for none.</summary>
  private static readonly AsyncLocal<LayoutProfile?> _profile = new();

  /// <summary>
  /// Calls a function of a module as <see cref="Call(IrModule, string, object[])"/> does, for a target of a
  /// layout profile (#110, #113): every layout query and field address is answered under the profile, and the
  /// bytes of memory a pointer or a view reinterprets lie in the profile's byte order.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="function">The name of the function to call.</param>
  /// <param name="arguments">One value per parameter, of the parameter's type.</param>
  /// <param name="profile">The profile of the target.</param>
  /// <returns>The value returned, or <see langword="null"/> for a function that returns nothing.</returns>
  /// <exception cref="ArgumentException">The function does not exist or the arguments do not fit it.</exception>
  /// <exception cref="IrTrapException">An operation trapped, also one of an initializer.</exception>
  public static object? Call(IrModule module, string function, object[] arguments, LayoutProfile profile) {
    ArgumentNullException.ThrowIfNull(profile);
    var outer = _profile.Value;
    _profile.Value = profile;
    try {
      return Call(module, function, arguments);
    } finally {
      _profile.Value = outer;
    }
  }

  /// <summary>
  /// The profile a layout query is answered under: the call's, or for a query no profile changes any of them;
  /// a query that depends on the profile is not answered without one.
  /// </summary>
  private static LayoutProfile ProfileFor(IrLayoutQuery query, IrType type)
    => _profile.Value ?? (RecordLayout.IsProfileIndependent(query, type) ? LayoutProfile.Lp64SysV
      : throw new InvalidOperationException($"the {query} of {type} depends on the target's layout profile; call the function with one, or lower the module for one"));

  private sealed partial class Machine {

    /// <summary>A layout query, answered under the profile, or a field address: the base moved by the field's offset.</summary>
    private object Layout(IrExpr expression, Frame frame) {
      if (expression is IrLayoutOf query)
        return RecordLayout.Answer(query.Query, query.Of, query.Field, ProfileFor(query.Query, query.Of));

      var address = (IrFieldAddress)expression;
      var record = IrType.Struct(address.Record);
      var offset = RecordLayout.OffsetOf(address.Record, address.Field, ProfileFor(IrLayoutQuery.Offset, record));
      var pointer = AsPointer(this.Evaluate(address.Base, frame));
      return pointer with { Offset = pointer.Offset + offset };
    }

  }

}
