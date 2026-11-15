using System;
using System.IO;
using System.Reflection;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/interop</c> (#1332). <c>LibC</c> exists for Linux (glibc,
/// <c>libc.so.6</c>) and for Windows (<c>msvcrt</c>); a test takes the variant of the operating system it runs on, whose
/// C library .NET calls in the oracle: the fixture compiled into this assembly.
/// </summary>
internal static class InteropFixtures {

  /// <summary>The variant of <c>LibC</c> of the running operating system: <c>Windows</c> or <c>Linux</c>.</summary>
  public static string Variant => OperatingSystem.IsWindows() ? "Windows" : "Linux";

  /// <summary>The library of the variant, as the fixture names it.</summary>
  public static string Library => OperatingSystem.IsWindows() ? "msvcrt" : "libc.so.6";

  /// <summary>The file of <c>LibC</c> for the running operating system.</summary>
  public static string LibC => $"LibC.{Variant}.cs";

  /// <summary>The path of a fixture file of the folder.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "interop", file);

  /// <summary>The text of a fixture file of the folder.</summary>
  public static string SourceOf(string file) => File.ReadAllText(PathOf(file));

  /// <summary>A fixture of the folder imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Imported(string file) {
    var result = CSharpFrontend.Import(file, SourceOf(file));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>The class <c>LibC</c> of the running operating system as the C# compiler built it into this assembly.</summary>
  public static Type CompiledLibC => typeof(InteropFixtures).Assembly.GetType($"Polypiler.Fixtures.Prototype.Interop.{Variant}.LibC", true)!;

  /// <summary>A public static method of a compiled class.</summary>
  public static MethodInfo Method(Type type, string name)
    => type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(type.Name, name);

  /// <summary>Calls a method and gives what it returns, the exception it throws unwrapped.</summary>
  public static object? Invoke(MethodInfo method, params object?[] arguments) {
    try {
      return method.Invoke(null, arguments);
    } catch (TargetInvocationException thrown) when (thrown.InnerException is { } inner) {
      System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
      throw;
    }
  }

}
