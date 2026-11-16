using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  /// <summary>The type of the field of a regular expression, which every profile has.</summary>
  private const string RegexType = "global::System.Text.RegularExpressions.Regex";

  private sealed partial class Writer {

    /// <summary>The field of each regular expression of the module, by pattern and flags, in the order the module first uses them.</summary>
    private Dictionary<(string Pattern, string Flags), string> _regexes = [];

    /// <summary>
    /// Names a field for every regular expression the module tests (#1399), each pattern and its flags
    /// once, and keeps the names from every variable and helper.
    /// </summary>
    private void NameRegexes(IrModule module) {
      var tests = module.Functions.SelectMany(function => IrTree.Nodes(function.Body))
        .Concat(module.Globals.Select(global => global.Initializer).OfType<IrExpr>().SelectMany(IrTree.Nodes))
        .OfType<IrIntrinsic>().Where(intrinsic => RegexOperations.IsRegex(intrinsic.Op)).Select(RegexOperations.Of).Distinct().ToList();
      var names = Names(tests.Select((_, index) => $"Pattern{index + 1}"), this._taken);
      this._regexes = tests.Select((test, index) => (test, names[$"Pattern{index + 1}"])).ToDictionary(pair => pair.test, pair => pair.Item2);
      this._taken.UnionWith(this._regexes.Values);
    }

    /// <summary>
    /// The test of a regular expression: <c>IsMatch</c> of its field, a <c>Regex</c> of the pattern
    /// <see cref="RegexOperations.ToDotNet"/> converted, which matches what ECMAScript matches.
    /// </summary>
    private (CsExpression Node, int Binding) RegexTest(IrIntrinsic intrinsic)
      => (new CsInvocation(new CsMemberAccess(this.Qualified(this._regexes[RegexOperations.Of(intrinsic)]), "IsMatch"), [this.Text(intrinsic.Arguments[0])]), Primary);

    /// <summary>The fields of the regular expressions, each compiled once with the options of <see cref="RegexOperations.Options"/>.</summary>
    private IEnumerable<CsField> RegexFields() => this._regexes.Select(pair => new CsField(["private", "static", "readonly"], new CsType(RegexType), pair.Value,
      new CsObjectCreation(new CsType(RegexType), [
        new CsStringLiteral(RegexOperations.ToDotNet(pair.Key.Pattern, pair.Key.Flags)) { Type = IrType.String },
        new CsMemberAccess(new CsName("global::System.Text.RegularExpressions.RegexOptions"), RegexOperations.Options.ToString())
      ])));

  }

}
