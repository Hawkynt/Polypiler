using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>What the step assembly holds, read by reflection: its bindings and the tests Reqnroll generated.</summary>
public static class AssemblyBindings {

  /// <summary>Every step binding of an assembly, once per feature it is scoped to.</summary>
  public static IReadOnlyList<StepBinding> Of(Assembly assembly) {
    var bindings = new List<StepBinding>();
    foreach (var type in assembly.GetTypes().Where(type => type.IsDefined(typeof(BindingAttribute), false)))
      foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
        var scopes = method.GetCustomAttributes<ScopeAttribute>().ToList() is { Count: > 0 } own ? own : [.. type.GetCustomAttributes<ScopeAttribute>()];
        string?[] features = scopes.Count > 0 ? [.. scopes.Select(scope => scope.Feature)] : [null];
        foreach (var attribute in method.GetCustomAttributes<StepDefinitionBaseAttribute>())
          foreach (var feature in features)
            bindings.Add(new StepBinding(KindOf(attribute), attribute.Expression, type.Name + "." + method.Name, feature));
      }

    return bindings;
  }

  /// <summary>The kind of step an attribute binds.</summary>
  public static StepKind KindOf(StepDefinitionBaseAttribute attribute) => attribute switch {
    GivenAttribute => StepKind.Given,
    WhenAttribute => StepKind.When,
    ThenAttribute => StepKind.Then,
    _ => StepKind.Any
  };

  /// <summary>The test classes Reqnroll generated from the feature files of an assembly.</summary>
  public static IEnumerable<Type> GeneratedFeatures(Assembly assembly)
    => assembly.GetTypes().Where(type => type.GetCustomAttribute<GeneratedCodeAttribute>()?.Tool == "Reqnroll" && type.IsDefined(typeof(TestFixtureAttribute), false));

  /// <summary>The title of the feature of a generated test class.</summary>
  public static string TitleOf(Type feature) => feature.GetCustomAttribute<DescriptionAttribute>()?.Properties.Get("Description") as string ?? "";

  /// <summary>The NUnit test cases of a generated test class: one per test method, or one per <c>TestCase</c> of a method that has them.</summary>
  public static int TestCasesOf(Type feature)
    => feature.GetMethods().Where(method => method.IsDefined(typeof(TestAttribute), false))
      .Sum(method => Math.Max(1, method.GetCustomAttributes<TestCaseAttribute>(false).Count()));

  /// <summary>The NUnit test cases of every generated test class of an assembly.</summary>
  public static int GeneratedTestCases(Assembly assembly) => GeneratedFeatures(assembly).Sum(TestCasesOf);

}
