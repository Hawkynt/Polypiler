using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  /// <summary>
  /// The JavaScript class an exception class of the IR's library is: a generated error class such as
  /// <c>DivideByZeroError</c>, <c>Error</c> for <c>Exception</c>, or the engine's <c>TypeError</c> for
  /// <c>NullReferenceException</c> (<c>docs/reference/exceptions.md</c>).
  /// </summary>
  /// <param name="type">A class of <see cref="IrExceptionClass.All"/>.</param>
  /// <returns>The name of the JavaScript class.</returns>
  public static string ErrorClass(IrClassType type) => JavaScriptHelpers.ErrorClass(type);

  private sealed partial class Writer {

    /// <summary>
    /// The JavaScript class of an exception class of the IR's library, declared in the module as a
    /// generated error class where it is one (<see cref="JavaScriptHelpers.ErrorClass"/>).
    /// </summary>
    private string Error(IrClassType type) {
      var name = JavaScriptHelpers.ErrorClass(type);
      if (name is "Error" or "TypeError")
        this.Native(name);
      else
        this._used.Add(name);

      return name;
    }

  }

}
