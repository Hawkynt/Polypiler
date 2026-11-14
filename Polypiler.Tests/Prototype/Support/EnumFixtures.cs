using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// A module of every operation on enums over every underlying type, imported from C#, and the calls
/// every backend is compared on: the values of the issue, the boundaries of every underlying type, a
/// value between members, and every class of text a parse distinguishes.
/// </summary>
internal static class EnumFixtures {

  /// <summary>The source: one enum per underlying type, a flags enum with and one without a zero member, and functions over plain numbers and text.</summary>
  public const string Source = """
    using System;

    [Flags] public enum Perm { None = 0, Read = 1, Write = 2, Exec = 4, ReadWrite = 3 }
    [Flags] public enum NoZero : uint { A = 1, B = 2, C = 8, Top = 2147483648 }
    public enum Color : byte { Red, Green = 5, Blue }
    public enum S { B = 2, Neg = -1, A = 0, C = 1 }
    public enum Small : sbyte { Low = -128, Minus = -1, High = 127 }
    public enum Half : short { Low = -32768, High = 2 }
    public enum Word : ushort { A = 65535, B = 0 }
    [Flags] internal enum Long : long { Low = 1, High = -9223372036854775808 }
    internal enum Big : ulong { Max = 18446744073709551615, One = 1 }
    public enum None { }

    public static class Enums {

      public static string Describe(int v) => ((Perm)v).ToString();

      public static string Flags(long v) => ((NoZero)v).ToString();

      public static string Shift(int c, int n) => ((Color)c + (byte)n).ToString();

      public static string Cast(int v) => "" + (Color)v;

      public static string Text(long v) => $"{(S)v}|{(Small)v}|{(Half)v}|{(Word)v}|{(Long)v}|{(Big)v}";

      public static string Defined(int v) => Enum.IsDefined(typeof(Perm), (Perm)v) + "|" + Enum.IsDefined(typeof(Color), (byte)v) + "|" + Enum.IsDefined<S>((S)v) + "|" + Enum.IsDefined(typeof(Big), (Big)v);

      public static int Parse(string s) => (int)(Perm)Enum.Parse(typeof(Perm), s);

      public static int ParseIgnoringCase(string s) => (int)Enum.Parse<Perm>(s, true);

      public static string ParseEach(string s) => (int)(Color)Enum.Parse(typeof(Color), s) + "|" + (Small)Enum.Parse(typeof(Small), s);

      public static string ParseWide(string s) => ((Big)Enum.Parse(typeof(Big), s)).ToString() + "|" + Enum.Parse<Long>(s, true);

      public static string Has(int x, int f) => ((Perm)x).HasFlag((Perm)f) + "|" + ((Perm)x).HasFlag(Perm.Exec) + "|" + ((Long)x).HasFlag(Long.High) + "|" + ((Big)x).HasFlag((Big)f) + "|" + ((Color)x).HasFlag(Color.Green);

      public static string Names() => string.Join(",", Enum.GetNames(typeof(S))) + "|" + string.Join(",", Enum.GetNames<Big>()) + "|" + Enum.GetNames(typeof(None)).Length;

      public static string Values() {
        var text = "";
        foreach (var v in (S[])Enum.GetValues(typeof(S)))
          text += (int)v + ",";
        foreach (var v in Enum.GetValues<Big>())
          text += (ulong)v + ",";
        foreach (var v in Enum.GetValues<Color>())
          text += v + ",";
        return text + Enum.GetValues<None>().Length;
      }

      public static int Choose(int v) {
        switch ((Color)v) {
          case Color.Red:
            return 10;
          case Color.Green:
          case Color.Blue:
            return 20;
          default:
            return 30;
        }
      }

      public static string Combine(int a, int b) {
        var p = (Perm)a;
        var q = (Perm)b;
        var r = (p & q) | (p ^ Perm.Exec);
        r |= Perm.Read;
        r &= ~Perm.Write;
        var c = (Color)b;
        c += 3;
        c++;
        var d = c - Color.Green;
        var big = (Big)a | Big.One;
        return (int)r + "," + (int)~p + "," + (int)c + "," + d + "," + (p == q) + (p != q) + (p < q) + (p >= q) + "," + (ulong)big + "," + (Color)a + (Color)b;
      }

      static Color current = Color.Green;

      public static string State(int v) {
        current = (Color)v;
        Color[] pair = { current, Color.Blue };
        return pair[0] + "|" + pair[1];
      }

    }
    """;

  private static IrModule? _module;

  /// <summary>The module, imported once.</summary>
  public static IrModule Module => _module ??= PrototypeFixture.Import("Enums.cs", Source);

  /// <summary>The integers every function of numbers is called with: the boundaries of every width, the members and values between them.</summary>
  public static readonly long[] Numbers = [0, 1, 2, 3, 4, 5, 6, 7, 8, 11, 250, 255, 256, -1, -2, -128, 127, 32767, -32768, 65535, int.MaxValue, int.MinValue, 2147483648, long.MinValue, long.MaxValue];

  /// <summary>The texts every parse is given: the classes of test design 6.</summary>
  public static readonly string[] Texts = [
    "Read", " Read ", "Read, Write", "Read , Exec", "read", "READ,exec", "", " ", "5", " -1 ", "+3", "-0", "Read,", ",Read", "Read,,Exec", "300", "1x",
    "\tRead\n", "\u00A0Read", "5 ", "5\u00A0", "-", "0x5", "None", "Read|Write", "99999999999", "1, 2", "Read, 4", "Unknown", "Red", "blue", "Green, Blue",
    "Low", "Minus", "-128", "128", "Max", "One, Max", "18446744073709551615", "-9223372036854775808", "high", "Low, High", "\u0085Read", "\uFEFFRead"
  ];

  /// <summary>Every call: each function of numbers with each number, each parse with each text.</summary>
  public static IEnumerable<PrototypeCall> Calls() {
    foreach (var value in Numbers) {
      yield return new PrototypeCall("Describe", [(int)value]);
      yield return new PrototypeCall("Flags", [value]);
      yield return new PrototypeCall("Cast", [(int)value]);
      yield return new PrototypeCall("Text", [value]);
      yield return new PrototypeCall("Defined", [(int)value]);
      yield return new PrototypeCall("Choose", [(int)value]);
      yield return new PrototypeCall("State", [(int)value]);
      foreach (var other in new long[] { 0, 1, 5, 6, 250, -1 }) {
        yield return new PrototypeCall("Shift", [(int)value, (int)other]);
        yield return new PrototypeCall("Has", [(int)value, (int)other]);
        yield return new PrototypeCall("Combine", [(int)value, (int)other]);
      }
    }

    foreach (var text in Texts)
      foreach (var method in new[] { "Parse", "ParseIgnoringCase", "ParseEach", "ParseWide" })
        yield return new PrototypeCall(method, [text]);

    yield return new PrototypeCall("Names", []);
    yield return new PrototypeCall("Values", []);
  }

  /// <summary>
  /// What an implementation does for a call, where .NET's <c>ArgumentException</c> of <c>Enum.Parse</c>
  /// is the trap <see cref="IrTrap.InvalidFormat"/> the IR names for it.
  /// </summary>
  public static string Normalized(string outcome) => outcome == PrototypeFixture.Trap(IrTrap.IndexOutOfRange) ? PrototypeFixture.Trap(IrTrap.InvalidFormat) : outcome;

}
