using System;
using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// A module the interpreter has loaded: its globals, initialised once, which every call of the
/// instance reads and writes. <see cref="IrInterpreter.Instantiate"/> makes one.
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
  public static IrModuleInstance Instantiate(IrModule module) {
    var program = _programs.GetValue(module, Index);
    var globals = new Dictionary<IrGlobal, object>(ReferenceEqualityComparer.Instance);
    var machine = new Machine(program, globals);
    foreach (var global in module.Globals)
      globals[global] = global.Initializer is { } initializer ? machine.Initial(initializer) : Constant(global.Type, 0);

    return new IrModuleInstance(module, (function, arguments) => Call(program, globals, module, function, arguments));
  }

  private sealed partial class Machine {

    /// <summary>The value of the initializer of a global, which reads no variable.</summary>
    public object Initial(IrExpr initializer) => this.Evaluate(initializer, new Frame());

  }

}
