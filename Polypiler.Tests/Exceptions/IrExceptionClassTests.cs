using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Exceptions;

/// <summary>
/// The exception classes of the IR's library (#1323): their hierarchy, which mirrors .NET's, the class
/// every trap throws, the object an <see cref="IrTrapException"/> carries, and the trap table of
/// <c>docs/reference/exceptions.md</c>, which every trap has a row of.
/// </summary>
[TestFixture]
public sealed class IrExceptionClassTests {

  private static readonly IrTrap[] _traps = Enum.GetValues<IrTrap>();

  /// <summary>The .NET class of a library class: in <c>System</c>, <c>KeyNotFoundException</c> in <c>System.Collections.Generic</c>.</summary>
  private static Type HostType(IrClassType type)
    => typeof(Exception).Assembly.GetType((type == IrExceptionClass.KeyNotFoundException ? "System.Collections.Generic." : "System.") + type.Name)!;

  /// <summary>The classes stand as they stand in .NET, <c>SystemException</c> left out.</summary>
  [Test]
  public void Hierarchy_IsThatOfDotNet() {
    foreach (var type in IrExceptionClass.All.Where(type => type != IrExceptionClass.AssertionException)) {
      var host = HostType(type);
      Assert.That(host, Is.Not.Null, type.Name);
      // SystemException, which stands between in .NET, is no class of the IR: the nearest ancestor the IR has is the base.
      var ancestor = host.BaseType;
      while (ancestor == typeof(SystemException))
        ancestor = ancestor.BaseType;

      var expected = host == typeof(Exception) ? null : ancestor!.Name;
      Assert.That(type.Base?.Name, Is.EqualTo(expected), type.Name);
      Assert.That(type.IsLibrary && IrExceptionClass.IsLibrary(type) && IrExceptionClass.IsException(type), Is.True, type.Name);
    }
  }

  /// <summary>The one library class .NET has none of: a failed assertion of the source, derived from Exception, with a message of its own.</summary>
  [Test]
  public void AssertionException_IsALibraryClassBelowException() {
    var type = IrExceptionClass.AssertionException;
    Assert.That(type.Base, Is.SameAs(IrExceptionClass.Exception));
    Assert.That(type.IsLibrary && IrExceptionClass.IsLibrary(type) && IrExceptionClass.IsException(type), Is.True);
    Assert.That(IrExceptionClass.DefaultMessage(type), Is.EqualTo("Assertion failed."));
    Assert.That(IrExceptionClass.Parameters(IrExceptionClass.Constructor(type, 1)), Is.EqualTo(new[] { IrType.String }));
    Assert.That(IrExceptionClass.All, Does.Contain(type));
  }

  [Test]
  public void All_ListsEveryClassAfterItsBase() {
    var seen = IrExceptionClass.All.Select((type, index) => (type, index)).ToDictionary(entry => entry.type, entry => entry.index);
    foreach (var type in IrExceptionClass.All.Where(type => type.Base is not null))
      Assert.That(seen[type.Base!], Is.LessThan(seen[type]), type.Name);
  }

  [Test]
  public void Exception_HasAMessageAndAnInnerException() {
    Assert.That(IrExceptionClass.Exception.Fields, Is.EqualTo(new[] { IrExceptionClass.Message, IrExceptionClass.InnerException }));
    Assert.That(IrExceptionClass.Message.Type, Is.EqualTo(IrType.String));
    Assert.That(IrExceptionClass.InnerException.Type, Is.EqualTo(IrExceptionClass.Reference));
    Assert.That(IrExceptionClass.Message.IsReadOnly && IrExceptionClass.InnerException.IsReadOnly, Is.True);
    Assert.That(IrExceptionClass.DivideByZeroException.AllFields, Is.EqualTo(IrExceptionClass.Exception.Fields));
  }

  [Test]
  public void Constructors_TakeNothingAMessageOrAMessageAndAnInnerException() {
    foreach (var type in IrExceptionClass.All) {
      Assert.That(type.Constructors, Has.Count.EqualTo(3), type.Name);
      Assert.That(IrExceptionClass.Parameters(IrExceptionClass.Constructor(type, 0)), Is.Empty);
      Assert.That(IrExceptionClass.Parameters(IrExceptionClass.Constructor(type, 1)), Is.EqualTo(new[] { IrType.String }));
      Assert.That(IrExceptionClass.Parameters(IrExceptionClass.Constructor(type, 2)), Is.EqualTo(new[] { IrType.String, IrExceptionClass.Reference }));
      foreach (var constructor in type.Constructors) {
        Assert.That(IrExceptionClass.IsLibrary(constructor), Is.True);
        Assert.That(IrExceptionClass.ConstructorOf(constructor.Function), Is.SameAs(constructor));
      }
    }
  }

  [Test]
  public void AnotherConstructor_IsNoneOfTheLibrary() {
    var other = new IrConstructor("X..ctor");
    Assert.That(IrExceptionClass.IsLibrary(other), Is.False);
    Assert.That(IrExceptionClass.Parameters(other), Is.Null);
    Assert.That(IrExceptionClass.ConstructorOf("X..ctor"), Is.Null);
  }

  [Test]
  public void AClassOfAModule_IsNoLibraryClass() {
    var plain = new IrClassType("Plain");
    plain.Inherit(null, []);
    var derived = new IrClassType("X");
    derived.Inherit(IrExceptionClass.Exception, []);
    Assert.That(IrExceptionClass.IsLibrary(plain) || IrExceptionClass.IsException(plain), Is.False);
    Assert.That(IrExceptionClass.IsLibrary(derived), Is.False);
    Assert.That(IrExceptionClass.IsException(derived), Is.True);
  }

  /// <summary>The message of a parameterless constructor is .NET 10's own text.</summary>
  [Test]
  public void DefaultMessage_IsThatOfDotNet() {
    foreach (var type in IrExceptionClass.All.Where(type => type.Name is not ("ArgumentOutOfRangeException" or "AssertionException"))) {
      var host = (Exception)Activator.CreateInstance(HostType(type))!;
      Assert.That(IrExceptionClass.DefaultMessage(type), Is.EqualTo(host.Message), type.Name);
    }

    Assert.That(IrExceptionClass.DefaultMessage(IrExceptionClass.ArgumentOutOfRangeException), Is.EqualTo(new ArgumentOutOfRangeException().Message));
    var user = new IrClassType("X");
    Assert.That(IrExceptionClass.DefaultMessage(user), Is.EqualTo("Exception of type 'X' was thrown."));
  }

  /// <summary>Test design 2: every trap is the throw of an object of its class, with the trap's message.</summary>
  [Test]
  public void EveryTrap_ThrowsAnObjectOfItsClass() {
    foreach (var trap in _traps) {
      var thrown = new IrTrapException(trap);
      var type = IrExceptionClass.Of(trap);
      Assert.That(thrown.Class, Is.SameAs(type), trap.ToString());
      Assert.That(thrown.Message, Is.EqualTo(IrTrapException.Describe(trap)));
      Assert.That(thrown.Exception, Is.Not.Null);
      Assert.That(IrExceptionClass.IsException(type), Is.True);
    }
  }

  [Test]
  public void ATrapOutsideTheEnumeration_HasNoClass()
    => Assert.That(() => IrExceptionClass.Of((IrTrap)99), Throws.TypeOf<ArgumentOutOfRangeException>());

  /// <summary>The C# class each trap throws is the .NET class of the same name.</summary>
  [TestCase(IrTrap.DivideByZero, typeof(DivideByZeroException))]
  [TestCase(IrTrap.IntegerOverflow, typeof(OverflowException))]
  [TestCase(IrTrap.InvalidOperation, typeof(ArithmeticException))]
  [TestCase(IrTrap.IndexOutOfRange, typeof(IndexOutOfRangeException))]
  [TestCase(IrTrap.ArgumentOutOfRange, typeof(ArgumentOutOfRangeException))]
  [TestCase(IrTrap.NullReference, typeof(NullReferenceException))]
  [TestCase(IrTrap.InvalidCast, typeof(InvalidCastException))]
  [TestCase(IrTrap.MissingValue, typeof(InvalidOperationException))]
  public void Trap_HasTheClassDotNetThrows(IrTrap trap, Type expected)
    => Assert.That(IrExceptionClass.Of(trap).Name, Is.EqualTo(expected.Name));

  /// <summary>The oracle: .NET throws the class of the table for the operations of the traps.</summary>
  [Test]
  public void DotNet_ThrowsTheClassesOfTheTable() {
    var zero = 0;
    int[] empty = [];
    string? none = null;
    int? missing = null;
    object text = "x";
    Assert.That(() => 1 / zero, Throws.TypeOf<DivideByZeroException>());
    Assert.That(() => checked(int.MaxValue + (zero + 1)), Throws.TypeOf<OverflowException>());
    Assert.That(() => Math.Sign(double.NaN), Throws.TypeOf<ArithmeticException>());
    Assert.That(() => empty[zero], Throws.TypeOf<IndexOutOfRangeException>());
    Assert.That(() => Math.Round(1.5, 16), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(() => none!.Length, Throws.TypeOf<NullReferenceException>());
    Assert.That(() => (int[])text, Throws.TypeOf<InvalidCastException>());
    Assert.That(() => missing!.Value, Throws.TypeOf<InvalidOperationException>());
  }

  /// <summary>Completeness: every trap has its row in the table of the reference, with the class of <see cref="IrExceptionClass.Of"/>.</summary>
  [Test]
  public void EveryTrap_HasItsRowInTheReference() {
    var text = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "docs", "reference", "exceptions.md"));
    var rows = Regex.Matches(text, @"^\| `(?<class>\w+)` \(`(?<trap>\w+)`\) \| `System\.(?:Collections\.Generic\.)?(?<csharp>\w+)` \| [^|]+ \| `(?<js>\w+)`", RegexOptions.Multiline)
      .ToDictionary(match => match.Groups["trap"].Value, match => (Class: match.Groups["class"].Value, CSharp: match.Groups["csharp"].Value, JavaScript: match.Groups["js"].Value));
    foreach (var trap in _traps) {
      Assert.That(rows, Contains.Key(trap.ToString()), $"the trap {trap} has no row");
      var row = rows[trap.ToString()];
      Assert.That(row.Class, Is.EqualTo(IrExceptionClass.Of(trap).Name));
      Assert.That(row.CSharp, Is.EqualTo(row.Class));
      Assert.That(row.JavaScript, Is.EqualTo(JavaScriptEmitter.ErrorClass(IrExceptionClass.Of(trap))));
    }
  }

}
