namespace Polypiler.Core.IR;

/// <summary>What a function of a scope captures, which decides the form a backend writes it in.</summary>
public enum IrCaptureClass {

  /// <summary>
  /// It captures nothing, or only variables it never writes and that nothing writes while it runs:
  /// those became parameters, so the function stands on its own.
  /// </summary>
  Static,

  /// <summary>It writes a variable it captures, which stays one storage location shared with its scope.</summary>
  Capturing

}

/// <summary>The kinds of effect a function has, as its effect summary finds them; none is a pure function that ends.</summary>
[System.Flags]
public enum IrEffectKinds {

  /// <summary>No effect: it reads nothing that can change, writes and makes nothing, never traps and ends.</summary>
  None = 0,

  /// <summary>It reads storage outside its own variables that can change: a captured variable, a global, a field, an array element.</summary>
  Reads = 1,

  /// <summary>It writes storage outside its own variables.</summary>
  Writes = 2,

  /// <summary>It makes a new array, object or closure.</summary>
  Allocates = 4,

  /// <summary>It may trap or throw.</summary>
  Traps = 8,

  /// <summary>It may run forever: a loop without a proven bound, recursion, or a call of a function that may.</summary>
  MayNotTerminate = 16

}

/// <summary>
/// A function of a scope: a lambda, an anonymous method or a local function whose value never leaves
/// the function it is made in - it is only called there, or passed to a parameter that only calls it
/// - classified by what it captures. A <see cref="IrCaptureClass.Static"/> one is lifted into a
/// function of the module whose last parameters are the values it captured; a
/// <see cref="IrCaptureClass.Capturing"/> one passed to such a parameter is lifted too and reaches what
/// it writes through a cell, and one held in a variable of its scope stays a closure there. Where none
/// of them is inlined, a backend writes it in the closest form its target has: a local function of
/// its scope, a function of the module, a nested function declaration.
/// </summary>
/// <param name="Scope">The function the value is made in.</param>
/// <param name="Name">For a lifted one the function of the module; for one that stays a closure the variable of <paramref name="Scope"/> that holds it.</param>
/// <param name="Class">What it captures.</param>
/// <param name="IsLifted">Whether it was lifted into a function of the module.</param>
public sealed record IrScopeFunction(string Scope, string Name, IrCaptureClass Class, bool IsLifted) {

  /// <summary>
  /// What it does by its effect summary, found when it was classified; <see langword="null"/> before.
  /// Lifted, it is the summary of its function of the module; held in a variable, that of its body with
  /// the variables it captures as storage of its scope.
  /// </summary>
  public IrEffectKinds? Effects { get; init; }

}

/// <summary>
/// A copy of a function made for the function values a call passes it: in the copy the parameters
/// that received them are gone, and every call of one of them is a direct call of its function, with
/// the values it captured passed as parameters of the copy.
/// </summary>
/// <param name="Function">The name of the copy.</param>
/// <param name="Origin">The function it is a copy of.</param>
/// <param name="Key">Which function each removed parameter calls, which identifies the copy among those of <paramref name="Origin"/>.</param>
/// <param name="Size">The size of the copy when it was made, in statements and expression nodes.</param>
public sealed record IrClone(string Function, string Origin, string Key, int Size);
