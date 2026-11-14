using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.CSharp.Printing;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.JavaScript.Printing;
using Polypiler.Core.Backends.JavaScript.Syntax;

namespace Polypiler.Tests.Backends.Syntax;

/// <summary>
/// The walkers and rewriters of both trees on a tree that has every kind of node, each with the name
/// <c>a</c> below it: a rewrite that changes nothing keeps the tree itself, and one that renames
/// <c>a</c> rebuilds every node on the way up and nothing else.
/// </summary>
[TestFixture]
public sealed class SyntaxRewriterTests {

  private static CsName A => new("a");

  private static CsCompilationUnit CSharpTree() {
    CsExpression[] expressions = [
      new CsMemberAccess(A, "M"), new CsInvocation(A, [A]), new CsInvocation(new CsName("f"), [A]), new CsOutArgument(A), new CsElementAccess(A, A), new CsElementAccess(new CsName("t"), A),
      new CsCast(new CsType("int"), A), new CsPrefixUnary("-", A), new CsPostfixUnary(A, "++"), new CsBinary(A, "+", A), new CsBinary(new CsName("b"), "+", A),
      new CsIsType(A, new CsType("int")), new CsAssignment(A, "=", A), new CsAssignment(new CsName("b"), "=", A),
      new CsConditional(A, A, A), new CsConditional(new CsName("b"), new CsName("b"), A), new CsConditional(new CsName("b"), A, new CsName("b")),
      new CsParenthesized(A), new CsCheckedExpression(true, A), new CsObjectCreation(new CsType("T"), [A]), new CsObjectCreation(new CsType("T"), []),
      new CsArrayCreation(new CsType("int"), A, null), new CsArrayCreation(new CsType("int"), null, new CsArrayInitializer([A])), new CsArrayCreation(new CsType("int"), new CsIntegerLiteral(1), null),
      new CsArrayInitializer([A]), new CsArrayInitializer([new CsIntegerLiteral(1)]), new CsDefaultExpression(new CsType("int")), new CsDefaultLiteral(), new CsStringLiteral("s"),
      new CsBooleanLiteral(true), new CsRealLiteral(1.5)
    ];
    CsStatement[] statements = [
      .. expressions.Select(expression => new CsExpressionStatement(expression)),
      new CsBlock([new CsReturn(A)]), new CsLocalDeclaration(new CsType("int"), "x", A), new CsLocalDeclaration(new CsType("int"), "y", null),
      new CsIf(A, new CsReturn(A), null), new CsIf(A, new CsReturn(A), new CsReturn(A)), new CsIf(new CsName("b"), new CsReturn(null), new CsReturn(A)),
      new CsWhile(A, new CsBlock([])), new CsDo(new CsBlock([]), A),
      new CsFor(new CsLocalDeclaration(new CsType("int"), "i", A), A, [A], new CsBlock([])), new CsFor(null, null, [], new CsBlock([new CsReturn(A)])),
      new CsSwitch(A, [new CsSwitchSection([A, null], [new CsBreak()]), new CsSwitchSection([new CsIntegerLiteral(1)], [new CsContinue()])]),
      new CsSwitch(new CsName("b"), [new CsSwitchSection([null], [new CsReturn(A)])]),
      new CsReturn(null), new CsThrow(A), new CsCheckedStatement(false, new CsBlock([new CsReturn(A)]))
    ];
    return new CsCompilationUnit(["h"], [new CsNamespace("N", true, [
      new CsTypeDeclaration(["public"], "record struct", "R", [new CsParameter(new CsType("int"), "X")], [], null),
      new CsTypeDeclaration(["public", "static"], "class", "C", null, [], [
        new CsField(["private", "const"], new CsType("int"), "F", A), new CsField(["public"], new CsType("int"), "G", null),
        new CsMethod(["static"], CsType.Void, "M", [], [], new CsBlock(statements), null),
        new CsMethod(["static"], new CsType("int"), "E", [], [], null, A),
        new CsMethod(["static"], new CsType("int"), "Z", [], [], null, new CsName("z")),
        new CsConstructor(["public"], "C", [], new CsBlock([new CsReturn(A)]))
      ])
    ])]);
  }

  [Test]
  public void CSharpRewrite_KeepsAnUnchangedTree_AndRebuildsAChangedOne() {
    var tree = CSharpTree();
    Assert.That(CSharpSyntaxRewriter.Rewrite(tree, node => node), Is.SameAs(tree));
    var renamed = CSharpSyntaxRewriter.Rewrite(tree, node => node is CsName { Identifier: "a" } ? new CsName("q") : node);
    int Count(CsNode root, string name) => CSharpSyntaxRewriter.DescendantsAndSelf(root).Count(node => node is CsName { Identifier: var identifier } && identifier == name);
    Assert.That((Count(renamed, "a"), Count(renamed, "q")), Is.EqualTo((0, Count(tree, "a"))));
    Assert.That(CSharpSyntaxRewriter.DescendantsAndSelf(renamed).Count(), Is.EqualTo(CSharpSyntaxRewriter.DescendantsAndSelf(tree).Count()));
    Assert.That(CSharpPrinter.Print(renamed, CSharpProfile.Default), Does.Contain("return q;").And.Not.Contain("return a;"));
  }

  private static JsIdentifier X => new("a");

  private static JsProgram JavaScriptTree() {
    JsExpression[] expressions = [
      new JsMember(X, "m"), new JsIndex(X, X), new JsIndex(new JsIdentifier("t"), X), new JsCall(X, [X]), new JsCall(new JsIdentifier("f"), [X]), new JsNew(X, [X]),
      new JsNew(new JsIdentifier("T"), [X]), new JsUnary("-", X), new JsUpdate("++", true, X), new JsBinary(X, "+", X), new JsBinary(new JsIdentifier("b"), "+", X),
      new JsAssignment(X, "=", X), new JsAssignment(new JsIdentifier("b"), "=", X), new JsConditional(X, X, X), new JsConditional(new JsIdentifier("b"), new JsIdentifier("b"), X),
      new JsSequence([X, X]), new JsParenthesized(X), new JsArray([X]), new JsObject([new JsProperty("k", X)]), new JsObject([new JsProperty("k", new JsNumber(1))]),
      new JsFunctionExpression(null, [], new JsBlock([new JsReturn(X)])), new JsString("s"), new JsBoolean(true), new JsBigInt(1)
    ];
    JsStatement[] statements = [
      .. expressions.Select(expression => new JsExpressionStatement(expression)),
      new JsBlock([new JsReturn(X)]), new JsVariableDeclaration("let", [new JsDeclarator("x", X), new JsDeclarator("y", null)]),
      new JsIf(X, new JsReturn(X), null), new JsIf(X, new JsReturn(X), new JsReturn(X)), new JsIf(new JsIdentifier("b"), new JsReturn(null), new JsReturn(X)),
      new JsWhile(X, new JsBlock([])), new JsDoWhile(new JsBlock([]), X),
      new JsFor(new JsVariableDeclaration("let", [new JsDeclarator("i", X)]), X, X, new JsBlock([])), new JsFor(null, null, null, new JsBlock([new JsReturn(X)])),
      new JsSwitch(X, [new JsSwitchCase(X, []), new JsSwitchCase(null, [new JsBreak()]), new JsSwitchCase(new JsNumber(1), [new JsContinue()])]),
      new JsSwitch(new JsIdentifier("b"), [new JsSwitchCase(null, [new JsReturn(X)])]),
      new JsReturn(null), new JsThrow(X)
    ];
    return new JsProgram(["h"], [
      new JsFunctionDeclaration("m", [], new JsBlock(statements)),
      new JsExport(new JsFunctionDeclaration("e", [], new JsBlock([new JsReturn(X)]))),
      new JsFunctionDeclaration("z", [], new JsBlock([new JsReturn(new JsIdentifier("z"))]))
    ], true);
  }

  [Test]
  public void JavaScriptRewrite_KeepsAnUnchangedTree_AndRebuildsAChangedOne() {
    var tree = JavaScriptTree();
    Assert.That(JavaScriptSyntaxRewriter.Rewrite(tree, node => node), Is.SameAs(tree));
    var renamed = JavaScriptSyntaxRewriter.Rewrite(tree, node => node is JsIdentifier { Name: "a" } ? new JsIdentifier("q") : node);
    int Count(JsNode root, string name) => JavaScriptSyntaxRewriter.DescendantsAndSelf(root).Count(node => node is JsIdentifier { Name: var identifier } && identifier == name);
    Assert.That((Count(renamed, "a"), Count(renamed, "q")), Is.EqualTo((0, Count(tree, "a"))));
    Assert.That(JavaScriptSyntaxRewriter.DescendantsAndSelf(renamed).Count(), Is.EqualTo(JavaScriptSyntaxRewriter.DescendantsAndSelf(tree).Count()));
    Assert.That(JavaScriptPrinter.Print(renamed, JavaScriptProfile.Default), Does.Contain("return q;").And.Not.Contain("return a;"));
  }

}
