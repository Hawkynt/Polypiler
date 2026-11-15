using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The exception classes of the IR's library, arranged as .NET arranges them:
/// <c>Exception</c> ← <c>ArithmeticException</c> ← {<c>DivideByZeroException</c>, <c>OverflowException</c>};
/// <c>Exception</c> ← <c>IndexOutOfRangeException</c>, <c>NullReferenceException</c>,
/// <c>InvalidCastException</c>, <c>InvalidOperationException</c>;
/// <c>Exception</c> ← <c>ArgumentException</c> ← <c>ArgumentOutOfRangeException</c>;
/// <c>Exception</c> ← <c>KeyNotFoundException</c>, <c>FormatException</c>.
/// <para>
/// They are classes (<see cref="IrClassType"/>) that no module declares and every module may name: a
/// class of a module derives from one of them, a catch clause names one, and every trap of the IR is
/// the throw of an object of one (<see cref="Of(IrTrap)"/>). An exception has a
/// <see cref="Message"/> and an <see cref="InnerException"/>, both fields of <see cref="Exception"/>
/// that only its constructors write. A library class has three constructors - none, the message, the
/// message and the inner exception - which are no functions of a module: <see cref="Parameters"/>
/// gives their parameters, and an implementation of the IR knows what they do.
/// </para>
/// </summary>
public static class IrExceptionClass {

  private static readonly Dictionary<IrConstructor, (IrClassType Owner, IReadOnlyList<IrType> Parameters)> _constructors = new(ReferenceEqualityComparer.Instance);

  static IrExceptionClass() {
    Exception = Library("Exception", null, "Exception of type 'System.Exception' was thrown.");
    Message = new IrField("Message", IrType.String, true);
    InnerException = new IrField("InnerException", IrType.Object(Exception), true);
    Exception.Define([Message, InnerException]);
    ArithmeticException = Library("ArithmeticException", Exception, "Overflow or underflow in the arithmetic operation.");
    DivideByZeroException = Library("DivideByZeroException", ArithmeticException, "Attempted to divide by zero.");
    OverflowException = Library("OverflowException", ArithmeticException, "Arithmetic operation resulted in an overflow.");
    IndexOutOfRangeException = Library("IndexOutOfRangeException", Exception, "Index was outside the bounds of the array.");
    NullReferenceException = Library("NullReferenceException", Exception, "Object reference not set to an instance of an object.");
    InvalidCastException = Library("InvalidCastException", Exception, "Specified cast is not valid.");
    InvalidOperationException = Library("InvalidOperationException", Exception, "Operation is not valid due to the current state of the object.");
    ArgumentException = Library("ArgumentException", Exception, "Value does not fall within the expected range.");
    ArgumentOutOfRangeException = Library("ArgumentOutOfRangeException", ArgumentException, "Specified argument was out of the range of valid values.");
    KeyNotFoundException = Library("KeyNotFoundException", Exception, "The given key was not present in the dictionary.");
    FormatException = Library("FormatException", Exception, "One of the identified items was in an invalid format.");
    All = [
      Exception, ArithmeticException, DivideByZeroException, OverflowException, IndexOutOfRangeException, NullReferenceException,
      InvalidCastException, InvalidOperationException, ArgumentException, ArgumentOutOfRangeException, KeyNotFoundException, FormatException
    ];
  }

  /// <summary>The base class of every exception.</summary>
  public static IrClassType Exception { get; }

  /// <summary>An arithmetic failure; the class of <see cref="IrTrap.InvalidOperation"/>.</summary>
  public static IrClassType ArithmeticException { get; }

  /// <summary>The class of <see cref="IrTrap.DivideByZero"/>.</summary>
  public static IrClassType DivideByZeroException { get; }

  /// <summary>The class of <see cref="IrTrap.IntegerOverflow"/>.</summary>
  public static IrClassType OverflowException { get; }

  /// <summary>The class of <see cref="IrTrap.IndexOutOfRange"/>.</summary>
  public static IrClassType IndexOutOfRangeException { get; }

  /// <summary>The class of <see cref="IrTrap.NullReference"/>.</summary>
  public static IrClassType NullReferenceException { get; }

  /// <summary>The class of <see cref="IrTrap.InvalidCast"/>.</summary>
  public static IrClassType InvalidCastException { get; }

  /// <summary>The class of <see cref="IrTrap.MissingValue"/> and <see cref="IrTrap.CollectionModified"/>.</summary>
  public static IrClassType InvalidOperationException { get; }

  /// <summary>The class of <see cref="IrTrap.KeyNotFound"/>.</summary>
  public static IrClassType KeyNotFoundException { get; }

  /// <summary>The class of <see cref="IrTrap.InvalidFormat"/>: a text that is no number of the grammar it is parsed by.</summary>
  public static IrClassType FormatException { get; }

  /// <summary>An argument a method does not accept; the class of <see cref="IrTrap.DuplicateKey"/>.</summary>
  public static IrClassType ArgumentException { get; }

  /// <summary>The class of <see cref="IrTrap.ArgumentOutOfRange"/>.</summary>
  public static IrClassType ArgumentOutOfRangeException { get; }

  /// <summary>Every library class, each after its base class.</summary>
  public static IReadOnlyList<IrClassType> All { get; }

  /// <summary>The message of an exception, a field of <see cref="Exception"/>.</summary>
  public static IrField Message { get; }

  /// <summary>The exception an exception was thrown because of, or <c>null</c>: a field of <see cref="Exception"/>.</summary>
  public static IrField InnerException { get; }

  /// <summary>The type of a reference to any exception.</summary>
  public static IrType Reference => IrType.Object(Exception);

  /// <summary>The class whose object a trap throws.</summary>
  /// <param name="trap">The trap.</param>
  /// <returns>The library class, as the table of <c>docs/reference/exceptions.md</c> gives it.</returns>
  public static IrClassType Of(IrTrap trap) => trap switch {
    IrTrap.DivideByZero => DivideByZeroException,
    IrTrap.IndexOutOfRange => IndexOutOfRangeException,
    IrTrap.IntegerOverflow => OverflowException,
    IrTrap.InvalidOperation => ArithmeticException,
    IrTrap.ArgumentOutOfRange => ArgumentOutOfRangeException,
    IrTrap.NullReference => NullReferenceException,
    IrTrap.InvalidCast => InvalidCastException,
    IrTrap.MissingValue or IrTrap.CollectionModified => InvalidOperationException,
    IrTrap.KeyNotFound => KeyNotFoundException,
    IrTrap.DuplicateKey => ArgumentException,
    IrTrap.InvalidFormat => FormatException,
    _ => throw new ArgumentOutOfRangeException(nameof(trap), trap, "The trap has no exception class.")
  };

  /// <summary>Whether a class is one of the library's, which no module declares.</summary>
  /// <param name="type">The class.</param>
  /// <returns><see langword="true"/> for a class of <see cref="All"/>.</returns>
  public static bool IsLibrary(IrClassType type) => type.IsLibrary;

  /// <summary>Whether a class is an exception class: <see cref="Exception"/> or a class derived from it.</summary>
  /// <param name="type">The class.</param>
  /// <returns><see langword="true"/> when an object of the class can be thrown.</returns>
  public static bool IsException(IrClassType type) => type.DerivesFrom(Exception);

  /// <summary>Whether a constructor is one of a library class.</summary>
  /// <param name="constructor">The constructor.</param>
  /// <returns><see langword="true"/> when no function of a module is its body.</returns>
  public static bool IsLibrary(IrConstructor constructor) => _constructors.ContainsKey(constructor);

  /// <summary>The parameters of a constructor of a library class, or <see langword="null"/> for any other constructor.</summary>
  /// <param name="constructor">The constructor.</param>
  /// <returns>None, the message, or the message and the inner exception.</returns>
  public static IReadOnlyList<IrType>? Parameters(IrConstructor constructor) => _constructors.TryGetValue(constructor, out var entry) ? entry.Parameters : null;

  /// <summary>The library constructor a function name stands for, or <see langword="null"/>.</summary>
  /// <param name="function">The name a call of the constructor by name gives, as a derived class's constructor calls its base's.</param>
  /// <returns>The constructor.</returns>
  public static IrConstructor? ConstructorOf(string function) => _constructors.Keys.FirstOrDefault(constructor => constructor.Function == function);

  /// <summary>The constructor of a library class with a number of parameters, 0 to 2.</summary>
  /// <param name="type">The library class.</param>
  /// <param name="arity">How many parameters: none, the message, the message and the inner exception.</param>
  /// <returns>The constructor.</returns>
  public static IrConstructor Constructor(IrClassType type, int arity) => type.Constructors[arity];

  /// <summary>
  /// The message an exception of a class has when its constructor is given none, as .NET 10 words it:
  /// the library classes have their own, every other class "Exception of type 'X' was thrown.".
  /// </summary>
  /// <param name="type">The class of the exception.</param>
  /// <returns>The message.</returns>
  public static string DefaultMessage(IrClassType type)
    => _defaults.TryGetValue(type, out var message) ? message : $"Exception of type '{type.Name}' was thrown.";

  private static readonly Dictionary<IrClassType, string> _defaults = new(ReferenceEqualityComparer.Instance);

  private static IrClassType Library(string name, IrClassType? @base, string message) {
    var type = new IrClassType(name) { IsLibrary = true };
    if (@base is not null)
      type.Define([]);

    type.Inherit(@base, []);
    IrConstructor[] constructors = [new($"{name}..ctor()"), new($"{name}..ctor(str)"), new($"{name}..ctor(str,Exception)")];
    IReadOnlyList<IrType>[] parameters = [[], [IrType.String], [IrType.String, IrType.Object(@base?.Lineage.Last() ?? type)]];
    for (var index = 0; index < constructors.Length; ++index)
      _constructors[constructors[index]] = (type, parameters[index]);

    type.DefineMembers(constructors, [], [], []);
    _defaults[type] = message;
    return type;
  }

}
