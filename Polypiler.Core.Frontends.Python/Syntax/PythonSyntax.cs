using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>What parsing a source gave: the tree, or CPython's error, and whether a verdict is given at all.</summary>
/// <param name="Module">The <c>Module</c>, or <see langword="null"/> where the source is refused.</param>
/// <param name="Error">The error of <c>ast.parse</c> or of <c>compile</c>.</param>
/// <param name="Parsed">Whether <c>ast.parse</c> accepts the source.</param>
/// <param name="Undecided">Whether the frontend gives no verdict: the source declares an encoding it has no decoder for.</param>
internal sealed record PySyntaxResult(PyNode? Module, PyError? Error, bool Parsed, bool Undecided);

/// <summary>
/// Parses Python source as <c>ast.parse</c> does and checks it as <c>compile</c> does, with the
/// frontend's own tokenizer and parser; nothing is started or run.
/// </summary>
internal static class PythonSyntax {

  /// <summary>The stack of the thread a parse runs on: deep nesting recurses as deeply as CPython's parser allows.</summary>
  private const int StackSize = 64 * 1024 * 1024;

  /// <summary>Parses a source given as text, as <c>ast.parse(str, feature_version=(3, minor))</c>.</summary>
  public static PySyntaxResult Parse(string source, int minor) => Run(PySource.FromText(source), minor);

  /// <summary>Parses a source given as bytes, as <c>ast.parse(bytes, feature_version=(3, minor))</c>: its encoding declaration counts.</summary>
  public static PySyntaxResult Parse(byte[] source, int minor) => Run(PySource.FromBytes(source), minor);

  private static PySyntaxResult Run(PySource.Prepared prepared, int minor) {
    if (prepared.Utf8 is null)
      return new(null, prepared.Error, false, prepared.Undecided);

    PySyntaxResult? result = null;
    ExceptionDispatchInfo? failure = null;
    var thread = new Thread(() => {
      try {
        result = Analyse(prepared.Utf8, minor);
      } catch (Exception exception) {
        failure = ExceptionDispatchInfo.Capture(exception);
      }
    }, StackSize);
    thread.Start();
    thread.Join();
    failure?.Throw();
    return result!;
  }

  private static PySyntaxResult Analyse(byte[] utf8, int minor) {
    PyNode module;
    try {
      module = new PyParser(utf8, minor).ParseModule();
      PyFuture.Check(module);
    } catch (PyError error) {
      return new(null, error, false, false);
    }

    if (PyCompileCheck.Check(module) is { } compileError)
      return new(module, compileError, true, false);

    return new(module, null, true, false);
  }

}
