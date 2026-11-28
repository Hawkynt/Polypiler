using System.Collections.Generic;
using System.IO;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.VisualBasic;

/// <summary>The fixture <c>Variants.bas</c> and the calls of it whose arguments and results every target prints alike.</summary>
internal static class VisualBasicVariantCalls {

  /// <summary>The fixture, imported.</summary>
  public static IrModule Module() => VisualBasicFrontend.Import("Variants.bas",
    File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "visualbasic", "classic", "Variants.bas"))).Module!;

  /// <summary>Every call of <c>Variants.cases.txt</c> but those that take or give a Variant or raise error 94, whose spelling differs per target.</summary>
  public static IReadOnlyList<PrototypeCall> Calls(System.Func<string, string>? name = null) {
    name ??= function => function;
    (string Function, object[] Arguments)[] calls = [
      ("Doubled", [21]), ("Promoted", []), ("IntegerProduct", [(short)200, (short)200]), ("ByteBelowZero", []), ("LongBeyond", []),
      ("Mixed", [(short)1]), ("Mixed", [(short)2]), ("Mixed", [(short)3]), ("Mixed", [(short)4]), ("Mixed", [(short)5]),
      ("EmptyRules", []), ("NullRules", []), ("NullConcatenated", []), ("NullIsNotTrue", []), ("NullLogic", []),
      ("Accumulate", [(short)3]), ("Joined", [(short)7]), ("Compared", [(short)1]), ("Compared", [(short)2]),
      ("Divided", [(short)7, (short)2]), ("Divided", [(short)-32768, (short)-1]), ("Divided", [(short)5, (short)0]), ("Counted", [5])
    ];
    var result = new List<PrototypeCall>();
    foreach (var (function, arguments) in calls)
      result.Add(new PrototypeCall(name(function), arguments));
    return result;
  }

}
