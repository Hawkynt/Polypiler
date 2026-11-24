using System;
using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// A module the interpreter has loaded: its globals, initialised once, which every call of the
/// instance reads and writes. <see cref="IrInterpreter.Instantiate(IrModule)"/> makes one.
/// </summary>
public sealed class IrModuleInstance {

  private readonly Func<string, object[], object?> _call;

  internal IrModuleInstance(IrModule module, Func<string, object[], object?> call) {
    this.Module = module;
    this._call = call;
  }

  /// <summary>The module.</summary>
  public IrModule Module { get; }

  /// <summary>Calls a function of the module in this instance; what it does to the globals the next call sees.</summary>
  /// <param name="function">The name of the function to call.</param>
  /// <param name="arguments">One value per parameter, of the parameter's type.</param>
  /// <returns>The value returned, or <see langword="null"/> for a function that returns nothing.</returns>
  /// <exception cref="ArgumentException">The function does not exist or the arguments do not fit it.</exception>
  /// <exception cref="IrTrapException">An operation trapped.</exception>
  /// <exception cref="InvalidOperationException">A write ran without an output sink.</exception>
  public object? Call(string function, params object[] arguments) => this._call(function, arguments);

}

public static partial class IrInterpreter {

  /// <summary>
  /// An instance of a module that <see cref="IrVerifier"/> accepted: its globals initialised once, in
  /// order, and shared by every call of the instance, as a loaded module or class shares them.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <returns>The instance.</returns>
  /// <exception cref="IrTrapException">An initializer trapped.</exception>
  public static IrModuleInstance Instantiate(IrModule module) => InstantiateCore(module, null);

  /// <summary>
  /// Instantiates a module with a standard output sink shared by its initializers and all calls.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="standardOutput">Receives each written line without a terminator. A null string arrives as empty text.</param>
  /// <returns>The instance.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="standardOutput"/> is <see langword="null"/>.</exception>
  /// <exception cref="IrTrapException">An initializer trapped.</exception>
  public static IrModuleInstance Instantiate(IrModule module, Action<string> standardOutput) {
    ArgumentNullException.ThrowIfNull(standardOutput);
    return InstantiateCore(module, standardOutput);
  }

  private static IrModuleInstance InstantiateCore(IrModule module, Action<string>? standardOutput) {
    var program = _programs.GetValue(module, Index);
    var globals = new Dictionary<IrGlobal, object>(ReferenceEqualityComparer.Instance);
    var machine = new Machine(program, globals, standardOutput);
    foreach (var global in module.Globals)
      globals[global] = global.Initializer is { } initializer ? machine.Initial(initializer)
        : global.Type.IsObject || global.Type.IsCollection ? NullReference.Value
        : Constant(global.Type, 0);

    // The statements of the module run once, when it is loaded, after its globals have their values.
    if (module.Initializer is { } start)
      Call(program, globals, module, start, [], standardOutput: standardOutput);

    return new IrModuleInstance(module, (function, arguments) => Call(program, globals, module, function, arguments, standardOutput: standardOutput));
  }

  private sealed partial class Machine {

    /// <summary>The value of the initializer of a global, which reads no variable.</summary>
    public object Initial(IrExpr initializer) => this.Evaluate(initializer, new Frame());

  }

}
