using System;
using System.Collections.Generic;
using System.Globalization;
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
        if (this.Results && this._throwing.Contains(test.Function)) {
          items.Add(new RsFunction(name, "", [], null, new RsBlock([new RsExpressionStatement(this.FallibleTest(test, call))], null)) { Attributes = attributes });
          continue;
        }

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

    /// <summary>
    /// The assertion of a test of a function that returns a <c>Result</c> under the trap policy
    /// <c>result</c>: <c>assert_eq!(f(1, 0), Err(Error::DivideByZero));</c>, <c>Ok(3)</c> for a value,
    /// <c>Ok(())</c> for none; a floating-point value compared by its bits; an exception compared by
    /// the number of its class, <c>f(1, 0).map_err(|error| error.class)</c>.
    /// </summary>
    private RsMacro FallibleTest(RustTestCase test, RsExpression call) {
      if (this.PlyErrors)
        call = new RsMethodCall(call, "map_err", [new RsClosure(false, [new RsParameter("error", "")], new RsField(new RsPath("error"), "class"))]);

      RsExpression wanted;
      if (test.Trap is { } trap)
        wanted = new RsCall(new RsPath("Err"), [this.PlyErrors ? new RsLiteral(this.ErrorClass(IrExceptionClass.Of(trap)).ToString(CultureInfo.InvariantCulture)) : this.TrapError(trap)]);
      else if (test.Expected is { Type.IsFloat: true } number) {
        call = new RsMethodCall(call, "map", [new RsPath(TypeName(number.Type) + "::to_bits")]);
        wanted = new RsCall(new RsPath("Ok"), [this.Method(Literal(number), "to_bits", number.Type)]);
      } else
        wanted = new RsCall(new RsPath("Ok"), [test.Expected is { } expected ? Literal(expected) : new RsTuple([])]);

      return new RsMacro("assert_eq", [call, wanted]);
    }

  }

}
