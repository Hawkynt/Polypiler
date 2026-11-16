using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The tests as the unit tests of the module: <c>#[cfg(test)] mod tests { use super::*; … }</c>
    /// with one <c>#[test]</c> function each. A result is compared with <c>assert_eq!</c>, a
    /// floating-point one by its bits; a trap is a <c>#[should_panic(expected = "Trap")]</c> test.
    /// </summary>
    private RsInlineModule Tests(IrModule module) {
      var names = RustNames.Unique(this._options.Tests.Select(test => test.Name), name => RustNames.Snake(name, this.Edition), new HashSet<string>(StringComparer.Ordinal));
      var items = new List<RsItem> { new RsUse("super::*") };
      foreach (var (test, name) in this._options.Tests.Zip(names)) {
        this._construct = $"the test '{test.Name}'";
        var function = module.Functions.FirstOrDefault(candidate => candidate.Name == test.Function);
        if (function is null || function.Parameters.Count != test.Arguments.Count || !this._functions.TryGetValue(test.Function, out var callee)) {
          this.Refuse($"a test of the function '{test.Function}' with {test.Arguments.Count} arguments");
          continue;
        }

        RsExpression call = new RsCall(new RsPath(callee), [.. test.Arguments.Select(argument => Literal(argument))]) { Type = function.ReturnType };
        RsBlock body;
        var attributes = new List<string> { "test" };
        if (test.Trap is { } trap) {
          attributes.Add($"should_panic(expected = \"{trap}\")");
          body = new RsBlock([new RsExpressionStatement(call)], null);
        } else if (test.Expected is { } expected) {
          RsExpression wanted = Literal(expected);
          if (expected.Type.IsFloat) {
            call = this.Method(call, "to_bits", expected.Type);
            wanted = this.Method(wanted, "to_bits", expected.Type);
          }

          body = new RsBlock([new RsExpressionStatement(new RsMacro("assert_eq", [call, wanted]))], null);
        } else {
          body = new RsBlock([new RsExpressionStatement(call)], null);
        }

        items.Add(new RsFunction(name, "", [], null, body) { Attributes = attributes });
      }

      return new RsInlineModule("tests", items) { Attributes = ["cfg(test)"] };
    }

  }

}
