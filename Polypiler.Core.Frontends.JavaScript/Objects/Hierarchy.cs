using System;
using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// What a call reaches of the class a class derives from: its constructor, <c>super(…)</c> or
/// <c>A.call(this, …)</c>, or one of its methods, <c>super.m(…)</c> or
/// <c>A.prototype.m.call(this, …)</c>.
/// </summary>
/// <param name="Owner">The class whose constructor or method it is.</param>
/// <param name="Member">The constructor or the method; <see langword="null"/> for a class without a constructor or a method the class does not have.</param>
/// <param name="IsConstructor">Whether the constructor is called.</param>
/// <param name="Key">The key of the method called.</param>
/// <param name="Arguments">The arguments, without the <c>this</c> of <c>call</c>.</param>
internal sealed record BaseReference(ClassModel Owner, Candidate? Member, bool IsConstructor, string Key, IReadOnlyList<Node> Arguments);

/// <summary>The forms in which a member of a class reaches the class it derives from.</summary>
internal static class Inheritance {

  /// <summary>What a node calls of a base class of the class whose member it stands in, or <see langword="null"/> for anything else.</summary>
  /// <param name="node">The node.</param>
  /// <param name="current">The class whose member the node stands in, or <see langword="null"/>.</param>
  /// <param name="classNamed">The class of the file a name names.</param>
  /// <returns>The reference.</returns>
  public static BaseReference? Of(Node node, ClassModel? current, Func<Node, ClassModel?> classNamed) {
    if (current?.Base is not { } @base || node is not CallExpression call)
      return null;

    switch (call.Callee) {
      case Super:
        return new(@base, @base.Constructor, true, "constructor", call.Arguments);
      case MemberExpression { Object: Super, Computed: false } method when Binder.KeyOf(method) is { } key:
        return new(@base, @base.Member(MemberKind.Method, key), false, key, call.Arguments);
      case MemberExpression { Computed: false, Property: Identifier { Name: "call" }, Object: var named } when call.Arguments is [ThisExpression, ..]:
        if (classNamed(named) == @base)
          return new(@base, @base.Constructor, true, "constructor", [.. call.Arguments.Skip(1)]);

        if (named is MemberExpression { Computed: false, Object: MemberExpression { Object: var owner, Property: Identifier { Name: "prototype" } } } prototyped
            && classNamed(owner) is { } ancestor && @base.Lineage.Contains(ancestor) && Binder.KeyOf(prototyped) is { } inherited)
          return new(ancestor, ancestor.Member(MemberKind.Method, inherited), false, inherited, [.. call.Arguments.Skip(1)]);

        return null;
      default:
        return null;
    }
  }

  /// <summary>The method a method overrides first of all: itself where it overrides none.</summary>
  public static Candidate Root(Candidate method) => method.Overrides is { } replaced ? Root(replaced) : method;

  /// <summary>The slots of the hierarchies of the file: each method that is overridden with every override of it.</summary>
  public static List<List<Candidate>> Slots(IEnumerable<ClassModel> classes)
    => [.. classes.SelectMany(owner => owner.Members).Where(member => member.Overrides is not null).GroupBy(Root).Select(group => group.Prepend(group.Key).ToList())];

}
