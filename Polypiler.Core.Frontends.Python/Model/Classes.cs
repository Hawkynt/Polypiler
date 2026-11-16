using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Python.Model;

/// <summary>
/// The built-in exceptions of Python the import knows, as classes of the IR (docs/reference/exceptions.md,
/// "Python"): each one the IR's library has stands for itself - <c>ValueError</c> is
/// <c>ArgumentException</c>, <c>ArithmeticError</c> is <c>ArithmeticException</c>, the base of
/// <c>ZeroDivisionError</c> and <c>OverflowError</c> as in Python - and the few the library has not,
/// such as <c>LookupError</c> and <c>NotImplementedError</c>, are declared by the module beside it,
/// derived from the library class Python derives them from. A handler of <c>LookupError</c> also
/// catches the library's <c>IndexOutOfRangeException</c> and <c>KeyNotFoundException</c>.
/// <para>An import runs on one thread; the registry of the import running is <see cref="Current"/>.</para>
/// </summary>
internal sealed class Classes {

  [System.ThreadStatic]
  private static Classes? _current;

  /// <summary>The library class every built-in exception the library has stands for.</summary>
  private static readonly Dictionary<string, IrClassType> _library = new() {
    ["BaseException"] = IrExceptionClass.Exception,
    ["Exception"] = IrExceptionClass.Exception,
    ["ArithmeticError"] = IrExceptionClass.ArithmeticException,
    ["ZeroDivisionError"] = IrExceptionClass.DivideByZeroException,
    ["OverflowError"] = IrExceptionClass.OverflowException,
    ["ValueError"] = IrExceptionClass.ArgumentException,
    ["TypeError"] = IrExceptionClass.InvalidCastException,
    ["IndexError"] = IrExceptionClass.IndexOutOfRangeException,
    ["KeyError"] = IrExceptionClass.KeyNotFoundException,
    ["AssertionError"] = IrExceptionClass.AssertionException,
    ["RuntimeError"] = IrExceptionClass.InvalidOperationException
  };

  /// <summary>The built-in exceptions the module declares, with the built-in each derives from.</summary>
  private static readonly Dictionary<string, string> _declared = new() {
    ["LookupError"] = "Exception",
    ["NotImplementedError"] = "RuntimeError",
    ["UnicodeEncodeError"] = "ValueError",
    ["AttributeError"] = "Exception",
    ["error"] = "Exception"
  };

  private readonly Dictionary<string, ClassModel> _made = [];

  /// <summary>The registry of the import running on this thread.</summary>
  public static Classes Current {
    get => _current ??= new();
    set => _current = value;
  }

  /// <summary>The classes the module declares for built-in exceptions the library has not, in the order they were first used.</summary>
  public IEnumerable<ClassModel> Declared => this._made.Values.Where(type => _declared.ContainsKey(type.Name));

  /// <summary>The name of the built-in exception of Python an IR class stands for: the first of the library's, or the class's own name.</summary>
  public static string PythonName(IrClassType type) => _library.FirstOrDefault(pair => pair.Value == type).Key ?? (type.Name == "StructError" ? "struct.error" : type.Name);

  /// <summary>Whether a name is a built-in exception of the import.</summary>
  public static bool IsBuiltinException(string name) => _library.ContainsKey(name) || (_declared.ContainsKey(name) && name != "error");

  /// <summary>A class of the module or a built-in exception by its name, or <see langword="null"/>.</summary>
  public static ClassModel? Of(ModuleModel module, string name)
    => module.Classes.TryGetValue(name, out var type) ? type : IsBuiltinException(name) ? Builtin(name) : null;

  /// <summary>The class of a built-in exception; <c>error</c> is <c>struct.error</c>.</summary>
  public static ClassModel Builtin(string name) => Current.Make(name);

  private ClassModel Make(string name) {
    if (this._made.TryGetValue(name, out var made))
      return made;

    if (_library.TryGetValue(name, out var library)) {
      made = new ClassModel(name, ClassKind.BuiltinException, null) { Base = Parent(name) is { } parent ? this.Make(parent) : null };
      made.Ir = library;
    } else {
      made = new ClassModel(name, ClassKind.BuiltinException, null) { Base = this.Make(_declared[name]) };
    }

    this._made[name] = made;
    return made;
  }

  /// <summary>The built-in the IR's library class of a built-in derives from, as Python derives it.</summary>
  private static string? Parent(string name) => name switch {
    "ZeroDivisionError" or "OverflowError" => "ArithmeticError",
    "BaseException" or "Exception" => null,
    _ => "Exception"
  };

  /// <summary>
  /// The IR classes a handler of a class catches: the class itself, and for <c>LookupError</c> the
  /// library classes of <c>IndexError</c> and <c>KeyError</c>, which do not derive from it.
  /// </summary>
  public static IReadOnlyList<IrClassType> Caught(ClassModel type)
    => type.Name == "LookupError" && type.Kind == ClassKind.BuiltinException
      ? [type.Ir!, IrExceptionClass.IndexOutOfRangeException, IrExceptionClass.KeyNotFoundException]
      : [type.Ir!];

}
