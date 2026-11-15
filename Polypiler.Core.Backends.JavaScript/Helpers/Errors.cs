using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The error classes of generated code: one per exception class of the IR's library
/// (<see cref="IrExceptionClass"/>), which a trap throws and a catch clause tests for, declared in a
/// module only where it is used. A class keeps <c>RangeError</c> in its chain where the code generated
/// before them threw a <c>RangeError</c>, so a caller testing <c>instanceof RangeError</c> still
/// catches it; <c>InvalidCastError</c> extends <c>TypeError</c>, which the cast helper threw. From
/// ECMAScript 2015 a class is a <c>class … extends</c>; below, a constructor function on an
/// <c>Object.create</c> chain that sets <c>name</c>, <c>message</c> and the <c>stack</c> of a native
/// error of its root, so that <c>String(e)</c> is <c>"Name: message"</c> at every level.
/// </summary>
internal static partial class JavaScriptHelpers {

  /// <summary>Each generated error class with the class it extends, every one after its base.</summary>
  private static readonly (string Name, string Base)[] _errors = [
    ("ArithmeticError", "RangeError"),
    ("DivideByZeroError", "ArithmeticError"),
    ("OverflowError", "ArithmeticError"),
    ("IndexOutOfRangeError", "RangeError"),
    ("ArgumentError", "Error"),
    ("ArgumentOutOfRangeError", "RangeError"),
    ("InvalidCastError", "TypeError"),
    ("InvalidOperationError", "Error")
  ];

  /// <summary>The names of the error classes, in the order a module declares them; no name of the module may take one.</summary>
  public static IEnumerable<string> ErrorOrder => _errors.Select(error => error.Name);

  /// <summary>The helpers of exceptions, in their order.</summary>
  private static IEnumerable<string> ExceptionOrder => ["$throw", "$cause", "$caused"];

  /// <summary>
  /// The JavaScript class an exception class of the IR's library is: a generated class, or
  /// <c>Error</c> for <c>Exception</c> and <c>TypeError</c> for <c>NullReferenceException</c>, which the
  /// engine throws itself.
  /// </summary>
  /// <param name="type">A library class.</param>
  /// <returns>The name of the JavaScript class.</returns>
  public static string ErrorClass(IrClassType type) => type.Name switch {
    "Exception" => "Error",
    "NullReferenceException" => "TypeError",
    var name => name[..^"Exception".Length] + "Error"
  };

  /// <summary>The native error a generated error class derives from at the root of its chain.</summary>
  /// <param name="name">The name of a generated error class.</param>
  /// <returns><c>RangeError</c>, <c>TypeError</c> or <c>Error</c>.</returns>
  public static string NativeRoot(string name) {
    var current = name;
    while (_errors.FirstOrDefault(error => error.Name == current) is { Name: not null } entry)
      current = entry.Base;

    return current;
  }

  /// <summary>The text of an error class for the profile; <see langword="null"/> for any other name.</summary>
  private static string? ErrorSource(string name, System.Func<string, bool> native) {
    switch (name) {
      case "$throw":
        return Lines("function $throw(error) {", "  throw error;", "}");
      case "$cause":
        return Lines("function $cause(error) {", "  return error.cause === void 0 ? null : error.cause;", "}");
      case "$caused":
        return Lines("function $caused(error, inner) {", "  error.cause = inner;", "  return error;", "}");
    }

    if (_errors.FirstOrDefault(error => error.Name == name) is not { Name: not null } entry)
      return null;

    if (native("class"))
      return Lines(
        $"class {name} extends {entry.Base} {{",
        "  constructor(message) {",
        "    super(message);",
        $"    this.name = \"{name}\";",
        "  }",
        "}");

    return Lines(
      $"function {name}(message) {{",
      $"  this.name = \"{name}\";",
      "  this.message = message;",
      $"  this.stack = new {NativeRoot(name)}(message).stack;",
      "}",
      $"{name}.prototype = Object.create({entry.Base}.prototype);",
      $"{name}.prototype.constructor = {name};");
  }

}
