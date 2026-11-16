using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The forms and refusals of the Rust backend that the other emission tests leave out, one rule of
/// the emitter at a time: each test names the rule, gives the source that reaches it and the Rust it
/// must give, or the diagnostic it must refuse with. The parts are split by the area of the emitter.
/// </summary>
[TestFixture]
public sealed partial class RustCoverageEmitTests {

  /// <summary>The exception classes the exception tests throw: <c>X</c>, with the three constructors of <c>Exception</c>, and <c>Y</c> below it.</summary>
  private const string Exceptions = "using System; public class X : Exception { public X() { } public X(string m) : base(m) { } public X(string m, Exception inner) : base(m, inner) { } } "
    + "public sealed class Y : X { public Y(string m) : base(m) { } } ";

  /// <summary>The module's file of C# methods of the class <c>Snippet</c>.</summary>
  private static string Emitted(string methods, OptimizationLevel level = OptimizationLevel.Default) => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), level: level));

  /// <summary>The module's file of a whole C# file.</summary>
  private static string EmittedFile(string source) => RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", source)));

  /// <summary>The module's file of C# methods next to the exception classes.</summary>
  private static string EmittedWithExceptions(string methods, string types = "")
    => EmittedFile(Exceptions + types + PrototypeFixture.Wrap(methods));

  /// <summary>The diagnostics of a whole C# file, as code and message.</summary>
  private static string[] ProblemsOfFile(string source)
    => [.. RustCrates.Check(PrototypeFixture.Import("Snippet.cs", source)).Select(problem => $"{problem.Code} {problem.Message}")];

  /// <summary>The diagnostics of C# methods next to the exception classes.</summary>
  private static string[] ProblemsWithExceptions(string methods, string types = "") => ProblemsOfFile(Exceptions + types + PrototypeFixture.Wrap(methods));

  /// <summary>The diagnostics of C# methods of the class <c>Snippet</c>.</summary>
  private static string[] Problems(string methods) => [.. RustCrates.Check(RustCrates.Import(methods)).Select(problem => $"{problem.Code} {problem.Message}")];

  /// <summary>The module's file of a hand-built module, emitted without the optimizer.</summary>
  private static string EmittedModule(IrModule module) => RustCrates.ModuleText(RustCrates.Emit(module, level: OptimizationLevel.None));

  /// <summary>The diagnostics of a hand-built module.</summary>
  private static string[] ProblemsOfModule(IrModule module) => [.. RustCrates.Check(module).Select(problem => $"{problem.Code} {problem.Message}")];

}
