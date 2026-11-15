using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// Test design 1 of #1353 from the other side: every helper of the library, compiled by the C# compiler
/// as the source of the library is, computes what the import of that source computes in the interpreter
/// - the value, or the class of the exception it ends with - for the edges and random values of its
/// parameters (fixed seed). The import is the helper the backends write, so the source and what is
/// written from it cannot drift apart.
/// </summary>
[TestFixture]
public sealed class HelperSourceTests {

  private static readonly double[] _numbers = [
    0.0, -0.0, 0.5, -0.5, 1.5, -1.5, 2.5, -2.5, 0.49999999999999994, 3.7, -3.7, 1e16, -1e16, 1e300, -1e300, 4294967296.5, -2147483648.5,
    2147483647.5, 4294967295.5, 9.2233720368547758e18, -9.2233720368547758e18, 1.8446744073709552e19, double.NaN, double.PositiveInfinity,
    double.NegativeInfinity, double.Epsilon, -double.Epsilon, 123.456789, -98765.4321, 2147483647.0, -2147483649.0, 127.5, -128.5, 255.5, 65535.5
  ];

  private static IEnumerable<TestCaseData> Methods()
    => typeof(HelperOperations).GetMethods(BindingFlags.Public | BindingFlags.Static)
      .Where(method => !method.Name.StartsWith("Intrinsic", StringComparison.Ordinal))
      .OrderBy(method => method.Name, StringComparer.Ordinal)
      .Select(method => new TestCaseData(method.Name).SetArgDisplayNames(method.Name));

  [TestCaseSource(nameof(Methods))]
  public void CompiledHelper_ComputesWhatItsImportComputes(string name) {
    var method = typeof(HelperOperations).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
    var random = new Random(1353);
    var pools = method.GetParameters().Select(parameter => Pool(parameter, random)).ToList();
    var lists = new List<object[]>();
    foreach (var first in pools[0])
      lists.Add([first, .. pools.Skip(1).Select(pool => pool[random.Next(pool.Count)])]);

    for (var index = 0; index < 64; ++index)
      lists.Add([.. pools.Select(pool => pool[random.Next(pool.Count)])]);

    Assert.That(HelperHarness.Library.Functions.Any(function => function.Name == name), Is.True, $"the library imports {name}");
    foreach (var list in lists) {
      var compiled = Compiled(method, list);
      var imported = Imported(name, list);
      Assert.That(imported, Is.EqualTo(compiled), $"{name}({string.Join(", ", list.Select(Show))})");
    }
  }

  /// <summary>The values a parameter takes: by its name for a width, a size, a count or a position, by its type otherwise.</summary>
  private static List<object> Pool(ParameterInfo parameter, Random random) {
    var type = parameter.ParameterType;
    if (type == typeof(bool))
      return [false, true];

    if (type == typeof(byte[]))
      return [.. new[] { 0, 1, 4, 8, 12 }.Select(length => (object)Enumerable.Range(0, length).Select(_ => (byte)random.Next(256)).ToArray())];

    if (type == typeof(long[]))
      return [.. new[] { 0, 1, 3 }.Select(length => (object)Enumerable.Range(0, length).Select(_ => random.NextInt64(long.MinValue, long.MaxValue)).Append(long.MaxValue).Append(long.MinValue).ToArray())];

    if (type == typeof(ulong[]))
      return [.. new[] { 0, 1, 3 }.Select(length => (object)Enumerable.Range(0, length).Select(_ => (ulong)random.NextInt64(long.MinValue, long.MaxValue)).Append(ulong.MaxValue).Append(0UL).ToArray())];

    if (type == typeof(double))
      return [.. _numbers.Select(number => (object)number)];

    if (type == typeof(float))
      return [.. _numbers.Select(number => (object)(float)number)];

    if (type == typeof(int) && parameter.Name is "bits" or "width")
      return [8, 16];

    if (type == typeof(int) && parameter.Name is "size")
      return [2, 4];

    if (type == typeof(int) && parameter.Name is "count" or "position" or "start" or "length" or "index" or "shift")
      return [.. Enumerable.Range(-2, 70).Select(value => (object)value)];

    var values = new List<long> { 0, 1, -1, 2, 3, 7, 8, 0x55, 0x80, 0xFF, 0x7FFF, 0x8000, 0xFFFF, int.MaxValue, int.MinValue, uint.MaxValue, long.MaxValue, long.MinValue };
    values.AddRange(Enumerable.Range(0, 48).Select(_ => random.NextInt64(long.MinValue, long.MaxValue)));
    values.AddRange(Enumerable.Range(0, 16).Select(_ => (long)random.Next(-300, 300)));
    return [.. values.Select(value => Convert(type, value)).Distinct()];
  }

  private static object Convert(Type type, long value) => unchecked(type == typeof(int) ? (int)value
    : type == typeof(uint) ? (uint)value
    : type == typeof(long) ? value
    : type == typeof(ulong) ? (ulong)value
    : throw new ArgumentOutOfRangeException(nameof(type), type, "a type the library has no helper with"));

  private static string Compiled(MethodInfo method, object[] arguments) {
    try {
      return Text(method.Invoke(null, [.. arguments.Select(Copy)]));
    } catch (TargetInvocationException invocation) when (invocation.InnerException is { } thrown) {
      return "throws " + thrown.GetType().Name;
    }
  }

  private static string Imported(string name, object[] arguments) {
    try {
      return Text(IrInterpreter.Call(HelperHarness.Library, name, [.. arguments.Select(Copy)]));
    } catch (IrException exception) {
      return "throws " + exception.Class.Name;
    }
  }

  private static string Text(object? value) => value switch {
    null => "void",
    double number => number.ToString("R", CultureInfo.InvariantCulture),
    float number => number.ToString("R", CultureInfo.InvariantCulture),
    _ => System.Convert.ToString(value, CultureInfo.InvariantCulture)!
  };

  /// <summary>An argument as a new object: a store writes into an array, which must not reach the next call.</summary>
  private static object Copy(object argument) => argument is Array array ? array.Clone() : argument;

  private static string Show(object value) => value is Array array ? "[" + string.Join(",", array.Cast<object>().Select(Text)) + "]" : Text(value);

}
