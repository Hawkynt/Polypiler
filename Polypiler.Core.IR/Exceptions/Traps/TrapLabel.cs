using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The name a trap has in the source language, with its kind (#115). A label is either built in - the
/// name of a kind, or one of the two other spellings of a kind, <c>ArithmeticOverflow</c> and
/// <c>DivisionByZero</c> - or declared in the trap table of its module (<see cref="IrModule.Traps"/>)
/// under the source language's own name of the exception type, spelled exactly as that language spells
/// it: <c>OverflowException</c>, <c>ZeroDivisionError</c>, <c>std::out_of_range</c>. A language whose
/// failures have no type uses the kind's name. Nothing else is a label: the verifier refuses an
/// undeclared one (<see cref="TrapLabelRule"/>, <c>PLY0178</c>).
/// </summary>
/// <param name="Name">The label.</param>
/// <param name="Kind">The kind of failure it names.</param>
/// <param name="ExceptionType">
/// The name of the source's exception type as text, such as <c>System.OverflowException</c>, or
/// <see langword="null"/>; what the type is - its base types, its fields, the native exception each
/// target raises - is not the label's business.
/// </param>
public sealed record TrapLabel(string Name, TrapKind Kind, string? ExceptionType = null) {

  /// <summary>The built-in labels: one per kind under its name, then <c>ArithmeticOverflow</c> and <c>DivisionByZero</c>.</summary>
  public static IReadOnlyList<TrapLabel> BuiltIn { get; } = [
    .. TrapKinds.All.Select(kind => new TrapLabel(kind.ToString(), kind)),
    new TrapLabel("ArithmeticOverflow", TrapKind.Overflow),
    new TrapLabel("DivisionByZero", TrapKind.DivideByZero)
  ];

  /// <summary>The built-in label that bears a kind's name.</summary>
  /// <param name="kind">The kind.</param>
  /// <returns>The label.</returns>
  public static TrapLabel Of(TrapKind kind) => BuiltIn[(int)kind];

  /// <summary>The label of a trap an operation of the IR raises, before any attribute renames it: its kind's.</summary>
  /// <param name="trap">The trap.</param>
  /// <returns>The label.</returns>
  public static TrapLabel Of(IrTrap trap) => Of(TrapKinds.Of(trap));

  /// <summary>The built-in label of a name.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The label, or <see langword="null"/> when the name is not built in.</returns>
  public static TrapLabel? BuiltInNamed(string name) => BuiltIn.FirstOrDefault(label => label.Name == name);

  /// <summary>Whether the label is one of <see cref="BuiltIn"/>, which no module declares.</summary>
  public bool IsBuiltIn => BuiltIn.Contains(this);

  /// <summary>
  /// The label a name stands for in a module: a built-in one, or the one its trap table declares.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="name">The name.</param>
  /// <returns>The label, or <see langword="null"/> when the name is neither.</returns>
  public static TrapLabel? Resolve(IrModule module, string name) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(name);
    return BuiltInNamed(name) ?? module.Traps.FirstOrDefault(label => label.Name == name);
  }

  /// <summary>Whether a label is one a module may name: built in, or an entry of its trap table.</summary>
  /// <param name="module">The module.</param>
  /// <returns><see langword="true"/> when the label is declared.</returns>
  public bool IsDeclaredIn(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return this.IsBuiltIn || module.Traps.Contains(this);
  }

  /// <summary>
  /// Whether a handler that lists this label selects a trap: the trap has this label, or this label is
  /// built in and the trap is of its kind - <c>[Overflow]</c> and <c>[ArithmeticOverflow]</c> catch every
  /// overflow, <c>[OverflowException]</c> only the overflows so labelled.
  /// </summary>
  /// <param name="trap">The label of the trap.</param>
  /// <returns><see langword="true"/> when the handler selects it.</returns>
  public bool Selects(TrapLabel trap) {
    ArgumentNullException.ThrowIfNull(trap);
    return trap == this || (this.IsBuiltIn && trap.Kind == this.Kind);
  }

  /// <summary>
  /// Why a name cannot be declared in a trap table, or <see langword="null"/> when it can: it is empty,
  /// it is built in, or it is an invented spelling of a kind - <c>Panic</c> before a kind's name, or a
  /// kind's name joined to something else by <c>_</c>.
  /// </summary>
  /// <param name="name">The name.</param>
  /// <returns>The reason, or <see langword="null"/>.</returns>
  public static string? Undeclarable(string name) {
    ArgumentNullException.ThrowIfNull(name);
    if (name.Length == 0 || name.Any(char.IsWhiteSpace))
      return $"trap label '{name}' is empty or holds white space";

    if (BuiltInNamed(name) is not null)
      return $"trap label '{name}' is built in and is not declared";

    var invented = TrapKinds.All.Select(kind => kind.ToString())
      .Any(kind => name == "Panic" + kind || name.StartsWith(kind + "_", StringComparison.Ordinal) || name.EndsWith("_" + kind, StringComparison.Ordinal));
    return invented ? $"trap label '{name}' is an invented spelling of a kind: a label is a kind's name or the source's own exception type" : null;
  }

}
