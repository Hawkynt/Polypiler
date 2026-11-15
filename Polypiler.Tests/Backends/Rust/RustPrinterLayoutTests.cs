using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The printer's rules one kind of node at a time, beyond what the emitted fixtures reach: documentation
/// lines, signatures too long for a line, every expression that cannot be flat inside one that could,
/// the forms rustfmt gives a broken assignment, break and argument list, the precedence table, and a
/// node the printer does not know. The texts of whole forms are what rustfmt 1.99 writes for them.
/// </summary>
[TestFixture]
public sealed class RustPrinterLayoutTests {

  private static RsPath P(string name) => new(name);

  private static RsLiteral L(string text) => new(text);

  /// <summary>A block expression with a statement, which never stands on one line.</summary>
  private static RsBlockExpression Block() => new(new RsBlock([new RsLet("z", false, null, L("1"))], P("z")));

  private static string Body(params RsStatement[] statements) => RustPrinter.Print(new RsFunction("f", "", [], null, new RsBlock(statements, null)));

  private sealed record UnknownItem : RsItem;

  private sealed record UnknownStatement : RsStatement;

  private sealed record UnknownExpression : RsExpression;

  [Test]
  public void EmptyDocumentationLines_HaveNoTrailingSpace() {
    var file = new RsFile(["Title.", ""], [], [new RsConst("C", "", "u32", L("1")) { Docs = ["", "One."] }]);
    Assert.That(RustPrinter.Print(file), Is.EqualTo("//! Title.\n//!\n\n///\n/// One.\nconst C: u32 = 1;\n"));
  }

  [Test]
  public void EmptyBody_IsBraces_AlsoAfterABrokenSignature() {
    Assert.That(RustPrinter.Print(new RsFunction("f", "", [], null, RsBlock.Empty)), Is.EqualTo("fn f() {}\n"));
    var parameters = Enumerable.Range(0, 6).Select(index => new RsParameter("parameter_" + index, "u64")).ToList();
    Assert.That(RustPrinter.Print(new RsFunction("f", "pub", parameters, "u64", RsBlock.Empty)),
      Is.EqualTo("pub fn f(\n" + string.Concat(parameters.Select(parameter => $"    {parameter.Name}: u64,\n")) + ") -> u64 {}\n"));
  }

  [Test]
  public void ExpressionThatCannotBeFlat_IsBrokenAlsoAlone()
    => Assert.That(RustPrinter.Print(Block()), Is.EqualTo("{\n    let z = 1;\n    z\n}"));

  [Test]
  public void NodeInsideAFlatForm_ThatIsNoFlat_BreaksTheForm() {
    var text = Body(
      new RsLet("a", false, null, new RsUnary("-", Block())),
      new RsLet("b", false, null, new RsBinary(Block(), "+", L("1"))),
      new RsLet("c", false, null, new RsIndex(P("T"), Block())),
      new RsLet("d", false, null, new RsArray([L("1"), Block()])),
      new RsLet("e", false, null, new RsIf(Block(), new RsBlock([], L("1")), new RsBlockExpression(new RsBlock([], L("2"))))),
      new RsExpressionStatement(new RsAssign(P("a"), "=", Block())),
      new RsExpressionStatement(new RsReturn(Block())),
      new RsExpressionStatement(new RsLoop("l", new RsBlock([new RsExpressionStatement(new RsBreak("l", Block()))], null))),
      new RsExpressionStatement(new RsFor(null, "i", new RsRange(L("0"), Block(), false), RsBlock.Empty)));
    Assert.That(text, Does.Contain("let a = -{\n        let z = 1;\n        z\n    };"));
    Assert.That(text, Does.Contain("let b = {\n        let z = 1;\n        z\n    }"));
    Assert.That(text, Does.Contain("let c = T[{\n        let z = 1;\n        z\n    }];"));
    Assert.That(text, Does.Contain("let d = [\n        1,\n        {\n            let z = 1;\n            z\n        },\n    ];"));
    Assert.That(text, Does.Contain("let e = if {\n        let z = 1;\n        z\n    } {\n        1\n    } else {\n        2\n    };"));
    Assert.That(text, Does.Contain("a = {\n        let z = 1;\n        z\n    };"));
    Assert.That(text, Does.Contain("return {\n        let z = 1;\n        z\n    };"));
    Assert.That(text, Does.Contain("break 'l {\n            let z = 1;\n            z\n        };"));
    Assert.That(text, Does.Contain("for i in 0..{\n        let z = 1;\n        z\n    } {}"));
  }

  [Test]
  public void AssignmentAsAnExpression_IsFlatOrBroken() {
    Assert.That(RustPrinter.Print(new RsAssign(P("a"), "+=", L("1"))), Is.EqualTo("a += 1"));
    Assert.That(RustPrinter.Print(new RsAssign(P("a"), "=", Block())), Is.EqualTo("a = {\n    let z = 1;\n    z\n}"));
  }

  [Test]
  public void BreakWithoutALabel_TakesItsValue() {
    Assert.That(RustPrinter.Print(new RsBreak(null, L("1"))), Is.EqualTo("break 1"));
    // A value that is no flat expression breaks the break; it is never dropped.
    Assert.That(RustPrinter.Print(new RsBreak(null, Block())), Is.EqualTo("break {\n    let z = 1;\n    z\n}"));
    Assert.That(RustPrinter.Print(new RsReturn(null)), Is.EqualTo("return"));
  }

  [Test]
  public void EmptyBlockAndEmptyLoop_AreFlat() {
    Assert.That(RustPrinter.Print(new RsBlockExpression(RsBlock.Empty)), Is.EqualTo("{}"));
    Assert.That(RustPrinter.Print(new RsLoop(null, RsBlock.Empty)), Is.EqualTo("loop {}"));
    Assert.That(RustPrinter.Print(new RsLoop("l", RsBlock.Empty)), Is.EqualTo("'l: loop {}"));
  }

  [Test]
  public void CallsThatCannotOverflow_PutOneArgumentOnEachLine() {
    var x = P("x_" + new string('a', 50));
    // No arguments at all, but a callee too long for the line: the parentheses stay empty.
    Assert.That(RustPrinter.Print(new RsCall(new RsCall(P("g"), [Block()]), [])), Is.EqualTo("g({\n    let z = 1;\n    z\n})()"));
    // The last argument ends in a block, but one before it is no flat expression.
    Assert.That(RustPrinter.Print(new RsCall(P("g"), [Block(), new RsIf(x, new RsBlock([], L("1")), null)])),
      Is.EqualTo($"g(\n    {{\n        let z = 1;\n        z\n    }},\n    if {x.Name} {{\n        1\n    }},\n)"));
    // A chain on a short root whose first call has no flat arguments: the block overflows the call, and
    // the calls after it stand at the root's indentation.
    Assert.That(RustPrinter.Print(new RsMethodCall(new RsMethodCall(P("a"), "min", [Block()]), "max", [L("1")])),
      Is.EqualTo("a.min({\n    let z = 1;\n    z\n})\n.max(1)"));
    var arguments = Enumerable.Range(0, 3).Select(index => (RsExpression)P("argument_number_" + index + "_long_name")).ToList();
    Assert.That(RustPrinter.Print(new RsMethodCall(new RsMethodCall(P("a"), "min", arguments), "max", [L("1")])),
      Is.EqualTo("a.min(\n" + string.Concat(arguments.Select(argument => $"    {((RsPath)argument).Name},\n")) + ")\n.max(1)"));
  }

  [TestCase("*", 11)]
  [TestCase("/", 11)]
  [TestCase("%", 11)]
  [TestCase("+", 10)]
  [TestCase("-", 10)]
  [TestCase("<<", 9)]
  [TestCase(">>", 9)]
  [TestCase("&", 8)]
  [TestCase("^", 7)]
  [TestCase("|", 6)]
  [TestCase("==", 5)]
  [TestCase("!=", 5)]
  [TestCase("<", 5)]
  [TestCase(">", 5)]
  [TestCase("<=", 5)]
  [TestCase(">=", 5)]
  [TestCase("&&", 4)]
  [TestCase("||", 3)]
  [TestCase("..", 2)]
  [TestCase("..=", 2)]
  [TestCase("=", 1)]
  [TestCase("+=", 1)]
  public void Precedence_FollowsTheReference(string op, int expected) => Assert.That(RustPrinter.Precedence(op), Is.EqualTo(expected));

  [Test]
  public void UnknownNodes_AreRefused() {
    Assert.That(() => RustPrinter.Print(new UnknownItem()), Throws.ArgumentException.With.Message.Contains("There is no Rust item 'UnknownItem'"));
    Assert.That(() => RustPrinter.Print(new RsFunction("f", "", [], null, new RsBlock([new UnknownStatement()], null))),
      Throws.ArgumentException.With.Message.Contains("There is no Rust statement 'UnknownStatement'"));
    Assert.That(() => RustPrinter.Print(new UnknownExpression()), Throws.ArgumentException.With.Message.Contains("There is no Rust expression 'UnknownExpression'"));
  }

  [Test]
  public void Arguments_AreRequired() {
    Assert.That(() => RustPrinter.Print((RsFile)null!), Throws.ArgumentNullException);
    Assert.That(() => RustPrinter.Print((RsItem)null!), Throws.ArgumentNullException);
    Assert.That(() => RustPrinter.Print((RsExpression)null!), Throws.ArgumentNullException);
  }

}
