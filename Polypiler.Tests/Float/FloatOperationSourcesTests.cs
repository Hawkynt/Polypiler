using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The embedded source of <see cref="FloatOperations"/> is in the subset the C# frontend imports, and
/// every implementation the table names is a function of the imported module.
/// </summary>
[TestFixture]
public sealed class FloatOperationSourcesTests {

  [Test]
  public void Sources_AreEveryFileOfTheFamilies() {
    var names = FloatOperationSources.All.Select(file => file.Name).ToList();
    Assert.That(names, Does.Contain("Arithmetic.cs").And.Contain("Representation.cs"));
    Assert.That(FloatOperationSources.All.All(file => file.Text.Contains("partial class FloatOperations")), Is.True);
  }

  [Test]
  public void Sources_ImportAndVerify() {
    var module = FloatImplementations.Module;
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(module.Name, Is.EqualTo(nameof(FloatOperations)));
  }

  [Test]
  public void EveryPublicImplementation_IsAFunctionOfTheImportedModule() {
    var functions = FloatImplementations.Module.Functions.Select(function => function.Name).ToHashSet();
    var methods = typeof(FloatOperations).GetMethods(BindingFlags.Public | BindingFlags.Static).Select(method => method.Name);
    Assert.That(methods.Where(name => !functions.Contains(name)), Is.Empty);
  }

  [Test]
  public void EveryImplementationTheTableNames_Exists() {
    var named = FloatOperationTable.All.SelectMany(operation => new[] { operation.DoubleName, operation.SingleName }).OfType<string>();
    Assert.That(named.Where(name => FloatImplementations.Method(name) is null), Is.Empty);
  }

}
