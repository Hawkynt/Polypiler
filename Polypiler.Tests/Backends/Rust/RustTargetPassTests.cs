using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Passes;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The target passes of the Rust backend on hand-built crates: the parentheses an inlined body leaves
/// are dropped wherever rustc's <c>unused_parens</c> would warn and kept where they are needed, and a
/// call is inlined only where its arguments match the helper's parameters.
/// </summary>
[TestFixture]
public sealed class RustTargetPassTests {

  private static RsPath P(string name) => new(name);

  private static RsLiteral L(string text) => new(text);

  private static RsParenthesized Wrapped(RsExpression inner) => new(inner);

  private static RsBinary Sum() => new(P("a"), "+", P("b"));

  [Test]
  public void Unparenthesized_DropsTheParenthesesOfEveryPlaceThatTakesAnyExpression() {
    var body = new RsBlock([
      new RsLet("x", false, null, Wrapped(Wrapped(Sum()))),
      new RsLet("y", false, null, null),
      new RsExpressionStatement(new RsCall(P("g"), [Wrapped(Sum()), P("a")])),
      new RsExpressionStatement(new RsMethodCall(P("a"), "max", [Wrapped(Sum())])),
      new RsExpressionStatement(new RsMacro("assert_eq", [Wrapped(Sum()), P("a")])),
      new RsExpressionStatement(new RsAssign(P("x"), "=", Wrapped(Sum()))),
      new RsExpressionStatement(new RsIf(Wrapped(new RsBinary(P("a"), "<", P("b"))), RsBlock.Empty, null)),
      new RsExpressionStatement(new RsWhile(null, Wrapped(new RsBinary(P("a"), "<", P("b"))), RsBlock.Empty)),
      new RsExpressionStatement(new RsMatch(Wrapped(Sum()), [new RsMatchArm(["_"], new RsBlockExpression(RsBlock.Empty))])),
      new RsExpressionStatement(new RsIndex(P("T"), Wrapped(Sum()))),
      new RsExpressionStatement(new RsArray([Wrapped(Sum()), P("a")])),
      new RsExpressionStatement(new RsReturn(Wrapped(Sum())))
    ], Wrapped(Sum()));
    var text = RustPrinter.Print(new RsFunction("f", "", [], "u32", RustTargetPasses.Unparenthesized(body)));
    Assert.That(text, Does.Not.Contain("((").And.Not.Contain("= (").And.Not.Contain("return ("));
    Assert.That(text, Does.Contain("let x = a + b;").And.Contain("g(a + b, a);").And.Contain("a.max(a + b);").And.Contain("assert_eq!(a + b, a);")
      .And.Contain("x = a + b;").And.Contain("if a < b {}").And.Contain("while a < b {}").And.Contain("match a + b {").And.Contain("T[a + b];")
      .And.Contain("[a + b, a];").And.Contain("return a + b;").And.Contain("    a + b\n"));
  }

  [Test]
  public void Unparenthesized_KeepsAConditionThatEndsInABlock_AndATreeWithoutParentheses() {
    // `if (if c { a } else { b }) {}` needs them: without, the block of the inner if would be read as the outer's.
    var condition = Wrapped(new RsIf(P("c"), new RsBlock([], P("a")), new RsBlockExpression(new RsBlock([], P("b")))));
    var kept = RustTargetPasses.Unparenthesized(new RsIf(condition, RsBlock.Empty, null));
    Assert.That(kept.Condition, Is.SameAs(condition));
    var plain = new RsBlock([new RsLet("x", false, null, Sum()), new RsExpressionStatement(new RsReturn(null))], P("x"));
    Assert.That(RustTargetPasses.Unparenthesized(plain), Is.SameAs(plain));
  }

  private static RustCrate Crate(params RsItem[] items) => new("p", "", new RsFile([], [], []), "m", new RsFile([], [], items));

  private static RsFunction Helper(string name, int parameters, RsExpression body)
    => new(name, "", [.. Enumerable.Range(0, parameters).Select(index => new RsParameter("p" + index, "u32"))], "u32", new RsBlock([], body)) { IsHelper = true };

  [Test]
  public void Inlining_NeedsAsManyArgumentsAsParameters() {
    var helper = Helper("h", 2, new RsBinary(P("p0"), "+", P("p1")));
    var caller = new RsFunction("f", "pub", [new RsParameter("a", "u32")], "u32", new RsBlock([], new RsCall(P("h"), [P("a")])));
    var applied = new List<string>();
    var crate = Crate(helper, caller);
    Assert.That(new RustHelperInlining().Run(crate, RustProfile.Default, applied.Add), Is.SameAs(crate));
    Assert.That(applied, Is.Empty);
  }

  [Test]
  public void Inlining_AnAtomicBody_NeedsNoParentheses() {
    var helper = Helper("h", 1, new RsMethodCall(P("p0"), "count_ones", []));
    var caller = new RsFunction("f", "pub", [new RsParameter("a", "u32")], "u32", new RsBlock([], new RsCall(P("h"), [P("a")])));
    var applied = new List<string>();
    var inlined = new RustHelperInlining().Run(Crate(helper, caller), RustProfile.Default, applied.Add);
    Assert.That(RustPrinter.Print(inlined.Module.Items.OfType<RsFunction>().Single(function => function.Name == "f")), Is.EqualTo("pub fn f(a: u32) -> u32 {\n    a.count_ones()\n}\n"));
    Assert.That(applied, Is.EqualTo(new[] { "'h' into 'f'" }));
  }

  [Test]
  public void Passes_RefuseNoTree()
    => Assert.Multiple(() => {
      Assert.That(() => new RustHelperInlining().Run(null!, RustProfile.Default, _ => { }), Throws.ArgumentNullException);
      Assert.That(() => new RustHelperInlining().Run(Crate(), RustProfile.Default, null!), Throws.ArgumentNullException);
      Assert.That(() => new RustUnusedDeclarations().Run(null!, RustProfile.Default, _ => { }), Throws.ArgumentNullException);
      Assert.That(() => new RustUnusedDeclarations().Run(Crate(), RustProfile.Default, null!), Throws.ArgumentNullException);
      Assert.That(new RustHelperInlining().Name, Is.EqualTo("helper inlining"));
      Assert.That(new RustUnusedDeclarations().Name, Is.EqualTo("unused declaration removal"));
    });

}
