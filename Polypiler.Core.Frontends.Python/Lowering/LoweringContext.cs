using System.Collections.Generic;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>What the lowering of the module found that the import has to announce as a known difference.</summary>
internal sealed class LoweringNotes {

  /// <summary>An <c>int</c> is computed: beyond 64 bits it raises <c>OverflowError</c>.</summary>
  public bool UsesInt { get; set; }

  /// <summary>A narrower number is stored where a wider one is held, and converted.</summary>
  public bool Widens { get; set; }

  /// <summary>An <c>int</c> is raised to an <c>int</c> power whose sign is not known.</summary>
  public bool PowersBySignUnknown { get; set; }

  /// <summary>A <c>float</c> is raised to a power, which CPython can answer with a complex.</summary>
  public bool PowersFloats { get; set; }

  /// <summary>Floats are sorted, whose order with a NaN among them is that of the sort algorithm.</summary>
  public bool SortsFloats { get; set; }

  /// <summary>A float is searched for in a sequence, where CPython also finds the very same NaN object.</summary>
  public bool SearchesFloats { get; set; }

  /// <summary>A character is made with <c>chr</c>, which can make a lone surrogate.</summary>
  public bool Characters { get; set; }

  /// <summary>The contracts the annotations of locals state, as <c>f.x: T</c>.</summary>
  public List<string> Contracts { get; } = [];

}

/// <summary>What every function lowering of an import shares: the names taken, the helpers, the notes and the options.</summary>
internal sealed class LoweringContext(IReadOnlyDictionary<PyNode, Variable> bound, PythonHelpers helpers, LoweringNotes notes, PythonImportOptions options) {

  /// <summary>The variables of comprehensions and lambdas, by the name nodes that read them.</summary>
  public IReadOnlyDictionary<PyNode, Variable> Bound { get; } = bound;

  /// <summary>The operations of Python's values that no single IR operation computes.</summary>
  public PythonHelpers Helpers { get; } = helpers;

  /// <summary>The known differences found so far.</summary>
  public LoweringNotes Notes { get; } = notes;

  /// <summary>How the source is imported.</summary>
  public PythonImportOptions Options { get; } = options;

  /// <summary>The functions lowered outside the functions of the source: synthesised constructors and equality.</summary>
  public List<IrFunction> Made { get; } = [];

  /// <summary>The tables of the module the lowering made, global arrays nothing writes: module-level lists of numbers and the member lists of enums.</summary>
  public List<IrGlobal> Tables { get; } = [];

  /// <summary>The types the module declares, in the order they were declared: classes, enums and the exceptions declared beside the library's.</summary>
  public List<IrTypeDeclaration> Types { get; } = [];

  /// <summary>The exception classes declared for the built-ins the library has not, by their names.</summary>
  public Dictionary<string, DeclaredException> Exceptions { get; } = [];

}
