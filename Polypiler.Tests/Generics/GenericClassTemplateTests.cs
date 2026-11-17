using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Generics;

/// <summary>
/// The template of a generic class (#1421): the C# frontend keeps <c>Ring&lt;T&gt;</c> next to its
/// instantiations, every instantiation is the template instantiated with its type arguments, a class
/// declared from the template, the verifier's rules of class templates and the module format.
/// </summary>
[TestFixture]
public sealed class GenericClassTemplateTests {

  private static IReadOnlyList<string> Problems(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  private static IrModule Snippet(string source) {
    var result = CSharpFrontend.Import([new CSharpSource("Snippet.cs", "#nullable disable\n" + source)], false, []);
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  private static IrClassType Instance(IrModule module, string name) => module.Types.OfType<IrClassType>().Single(type => type.Name == name);

  [Test]
  public void Ring_KeepsItsTemplate_NextToItsInstantiations() {
    var module = GenericsFixtures.Imported("Ring");
    var (longs, doubles) = (Instance(module, "Ring<i64>"), Instance(module, "Ring<f64>"));
    var template = longs.Origin!.ClassTemplate!;
    Assert.That(doubles.Origin!.ClassTemplate, Is.SameAs(template));
    Assert.That(template.IsDefined, Is.True);
    var generic = template.Class!;
    Assert.That(generic.Name, Is.EqualTo("Ring<T>"));
    Assert.That(module.Types, Does.Not.Contain(generic));
    Assert.That(generic.Origin!.TypeArguments, Is.EqualTo(template.Parameters));
    Assert.That(generic.Fields.Select(field => field.Type), Is.EqualTo(new[] { IrType.Array(template.Parameters[0]), IrType.I32 }));
    Assert.That(template.Members.Select(member => member.Function.Name), Is.EqualTo(generic.Functions));
    Assert.That(template.Members.Select(member => member.Function.Name), Is.EquivalentTo(new[] { "Ring<T>.ctor", "Ring<T>.Push", "Ring<T>.get_Last", "Ring<T>.get_Count" }));
    Assert.That(Problems(module), Is.Empty);
  }

  [TestCase("Ring<i64>")]
  [TestCase("Ring<f64>")]
  public void EveryInstantiation_IsTheTemplateInstantiated(string name) {
    var module = GenericsFixtures.Imported("Ring");
    var instance = Instance(module, name);
    var functions = module.Functions.ToDictionary(function => function.Name);
    var instantiated = IrGenerics.Instantiate(instance.Origin!.ClassTemplate!, instance.Origin.TypeArguments, instance, module);
    Assert.That(instantiated.Select(function => function.Name), Is.EqualTo(instance.Functions));
    Assert.That(instantiated.All(function => IrGenerics.Equivalent(function, functions[function.Name])), Is.True);
  }

  [Test]
  public void ATemplateMember_CallsAnotherMember_AndMakesItsOwnClass() {
    var module = Snippet("""
      public sealed class Cell<T> {
        private T _v;
        public Cell(T v) { this.Set(v); }
        public void Set(T v) { this._v = v; }
        public T Get() => this._v;
        public static int Size() => 1;
        public Cell<T> Copy() => new Cell<T>(this.Get());
      }
      public static class Use {
        public static int F(int a) { var c = new Cell<int>(a); var d = c.Copy(); return d.Get() + Cell<int>.Size(); }
        public static bool G(bool a) { var c = new Cell<bool>(a); return c.Get(); }
      }
      """);
    var instance = Instance(module, "Cell<i32>");
    var functions = module.Functions.ToDictionary(function => function.Name);
    var instantiated = IrGenerics.Instantiate(instance.Origin!.ClassTemplate!, [IrType.I32], instance, module);
    Assert.That(instantiated.All(function => IrGenerics.Equivalent(function, functions[function.Name])), Is.True);
    Assert.That(Problems(module), Is.Empty);
  }

  [TestCase("public T[] Show() => new[] { this._v, this._v };", "[1]", TestName = "A template member makes an array of the parameter")]
  [TestCase("public T Show() => Use.Id(this._v);", "", TestName = "A template member calls a generic method with the parameter")]
  public void ATemplateMember_StatesTheConstructInTermsOfTheParameter(string member, string element) {
    var module = Snippet($$"""
      public sealed class Cell<T> {
        private T _v;
        public Cell(T v) { this._v = v; }
        {{member}}
      }
      public static class Use {
        public static int F(int a) { var c = new Cell<int>(a); return c.Show(){{element}}; }
        public static long G(long a) { var c = new Cell<long>(a); return c.Show(){{element}}; }
        internal static T Id<T>(T a) => a;
      }
      """);
    var instance = Instance(module, "Cell<i64>");
    var template = instance.Origin!.ClassTemplate!;
    Assert.That(template.IsDefined, Is.True);
    var functions = module.Functions.ToDictionary(function => function.Name);
    Assert.That(IrGenerics.Instantiate(template, [IrType.I64], instance, module).All(function => IrGenerics.Equivalent(function, functions[function.Name])), Is.True);
    var bytes = IrModuleFormat.Write(module);
    Assert.That(IrModuleFormat.Write(IrModuleFormat.Read(bytes)), Is.EqualTo(bytes));
  }

  [TestCase("public string Show() => \"v\" + this._v;", TestName = "The parameter concatenated to a string")]
  [TestCase("public string Show() => $\"{this._v}\";", TestName = "The parameter interpolated into a string")]
  public void AClassWithAConstructTheTemplateCannotState_KeepsItsInstantiationsOnly(string member) {
    var module = Snippet($$"""
      public sealed class Cell<T> {
        private T _v;
        public Cell(T v) { this._v = v; }
        {{member}}
        public T Get() => this._v;
      }
      public static class Use {
        public static int F(int a) { var c = new Cell<int>(a); return c.Get(); }
        internal static T Id<T>(T a) => a;
      }
      """);
    var template = Instance(module, "Cell<i32>").Origin!.ClassTemplate!;
    Assert.That((template.IsDefined, template.Class, template.Parameters, template.Members), Is.EqualTo((false, (IrClassType?)null, Array.Empty<IrType>(), Array.Empty<IrGenericTemplate>())));
    Assert.That(Problems(module), Is.Empty);
    var bytes = IrModuleFormat.Write(module);
    Assert.That(IrModuleFormat.Write(IrModuleFormat.Read(bytes)), Is.EqualTo(bytes));
  }

  [Test]
  public void Define_IsOnce() {
    var template = GenericsFixtures.Imported("Ring").Types.OfType<IrClassType>().First().Origin!.ClassTemplate!;
    Assert.Throws<InvalidOperationException>(() => template.Define(template.Class!, template.Parameters, template.Members));
  }

  [Test]
  public void Declare_MakesAClassOfTheTemplate_WhoseFunctionsAreItsInstantiation() {
    var module = GenericsFixtures.Imported("Ring");
    var template = Instance(module, "Ring<i64>").Origin!.ClassTemplate!;
    var declared = IrGenerics.Declare(template, [IrType.U16], "Ring<u16>");
    Assert.That(declared.Name, Is.EqualTo("Ring<u16>"));
    Assert.That(declared.Fields.Select(field => field.Type), Is.EqualTo(new[] { IrType.Array(IrType.U16), IrType.I32 }));
    Assert.That(declared.Functions, Is.EquivalentTo(new[] { "Ring<u16>.ctor", "Ring<u16>.Push", "Ring<u16>.get_Last", "Ring<u16>.get_Count" }));
    Assert.That(declared.Properties.Select(property => (property.Name, property.Type, property.Getter?.Function)),
      Is.EquivalentTo(new[] { ("Last", IrType.U16, "Ring<u16>.get_Last"), ("Count", IrType.I32, "Ring<u16>.get_Count") }));
    var functions = IrGenerics.Instantiate(template, [IrType.U16], declared, module);
    var push = functions.Single(function => function.Name == "Ring<u16>.Push");
    Assert.That(push.Parameters.Select(parameter => parameter.Type), Is.EqualTo(new[] { IrType.Object(declared), IrType.U16 }));
    Assert.That(IrTree.Nodes(push.Body).OfType<IrFieldLoad>().Select(load => load.Field).Distinct(), Is.SubsetOf(declared.Fields).Using<IrField>((a, b) => ReferenceEquals(a, b)));
    var withDeclared = module with { Types = [.. module.Types, declared], Functions = [.. module.Functions, .. functions] };
    Assert.That(Problems(withDeclared), Is.Empty);
  }

  [Test]
  public void Declare_KeepsAnAutoPropertyAndASetter() {
    var module = Snippet("""
      public sealed class Box<T> {
        public T Value { get; set; }
        public T Other { get => this.Value; set => this.Value = value; }
      }
      public static class Use {
        public static int F(int a) { var b = new Box<int>(); b.Other = a; return b.Value; }
      }
      """);
    var template = Instance(module, "Box<i32>").Origin!.ClassTemplate!;
    var declared = IrGenerics.Declare(template, [IrType.I64], "Box<i64>");
    var value = declared.Properties.Single(property => property.Name == "Value");
    var other = declared.Properties.Single(property => property.Name == "Other");
    Assert.That(value.BackingField, Is.SameAs(declared.Fields.Single()));
    Assert.That((other.Getter!.Function, other.Setter!.Function), Is.EqualTo(("Box<i64>.get_Other", "Box<i64>.set_Other")));
    Assert.That(other.Setter, Is.SameAs(declared.Methods.Single(method => method.Function == "Box<i64>.set_Other")));
  }

  [Test]
  public void InstantiateAndDeclare_RefuseWhatIsNoTemplateOfTheClass() {
    var module = GenericsFixtures.Imported("Ring");
    var instance = Instance(module, "Ring<i64>");
    var template = instance.Origin!.ClassTemplate!;
    Assert.Throws<ArgumentException>(() => IrGenerics.Instantiate(new IrGenericClassTemplate(), [IrType.I64], instance, module));
    Assert.Throws<ArgumentException>(() => IrGenerics.Declare(new IrGenericClassTemplate(), [IrType.I64], "X"));
    Assert.Throws<ArgumentException>(() => IrGenerics.Instantiate(template, [], instance, module));
    Assert.Throws<ArgumentException>(() => IrGenerics.Declare(template, [IrType.I64, IrType.I64], "X"));

    var shapes = new List<IrClassType>();
    IrClassType Shape(int fields, int constructors, int methods, bool defined = true) {
      var type = new IrClassType("Shape");
      if (defined)
        type.Define([.. Enumerable.Range(0, fields).Select(index => new IrField("f" + index, IrType.I32, false))]);
      type.DefineMembers([.. Enumerable.Range(0, constructors).Select(index => new IrConstructor("Shape.ctor" + index))],
        [.. Enumerable.Range(0, methods).Select(index => new IrMethod("M" + index, "Shape.M" + index, false))], [], []);
      return type;
    }

    foreach (var shape in new[] { Shape(2, 1, 3, false), Shape(1, 1, 3), Shape(2, 2, 3), Shape(2, 1, 2) })
      Assert.Throws<ArgumentException>(() => IrGenerics.Instantiate(template, [IrType.I64], shape, module), shape.ToString());

    var lopsided = new IrGenericClassTemplate();
    lopsided.Define(template.Class!, template.Parameters, [.. template.Members.Skip(1)]);
    Assert.Throws<ArgumentException>(() => IrGenerics.Instantiate(lopsided, [IrType.I64], instance, module));

    var stranger = new IrClassType("Odd<T>") { Origin = template.Class!.Origin };
    stranger.Define([]);
    stranger.DefineMembers([new IrConstructor("Elsewhere.ctor")], [], [], []);
    var misnamed = new IrGenericClassTemplate();
    misnamed.Define(stranger, template.Parameters, []);
    Assert.Throws<ArgumentException>(() => IrGenerics.Declare(misnamed, [IrType.I64], "X"));
  }

  [Test]
  public void Verifier_RefusesBrokenClassTemplates() {
    void Refused(IrModule module, string message) => Assert.That(Problems(module), Has.Some.Contains(message), string.Join("\n", Problems(module)));
    var module = GenericsFixtures.Imported("Ring");
    var instance = Instance(module, "Ring<i64>");
    var template = instance.Origin!.ClassTemplate!;

    Refused(module with { Types = [.. module.Types, template.Class!] }, "the class Ring<T> of the template of Ring<T> is a type of the module");

    var lopsided = new IrGenericClassTemplate();
    lopsided.Define(template.Class!, template.Parameters, [.. template.Members.Skip(1)]);
    Refused(Replaced(module, instance, lopsided), "are not one per function of it");

    var otherParameters = new IrGenericClassTemplate();
    otherParameters.Define(template.Class!, [IrType.I64], template.Members);
    Refused(Replaced(module, instance, otherParameters), "are not one per function of it");

    var bigger = new IrClassType("Ring<T>") { Origin = template.Class!.Origin };
    bigger.Define([.. template.Class!.Fields, new IrField("extra", IrType.I32, false)]);
    bigger.DefineMembers(template.Class.Constructors, template.Class.Methods, [], []);
    var widened = new IrGenericClassTemplate();
    widened.Define(bigger, template.Parameters, template.Members);
    Refused(Replaced(module, instance, widened), "are not those of the template Ring<T>");

    var leak = new IrFunction("Leak", true, IrType.Void, [new IrLocal("t", template.Parameters[0])], [], new IrBlock([]));
    Refused(module with { Functions = [.. module.Functions, leak] }, "the type T is open: a type parameter of Ring<T>");
  }

  /// <summary>The module with one instantiation's class template replaced, the class itself rebuilt around it.</summary>
  private static IrModule Replaced(IrModule module, IrClassType instance, IrGenericClassTemplate template) {
    var copy = new IrClassType(instance.Name) { Origin = instance.Origin! with { ClassTemplate = template }, IsSealed = instance.IsSealed };
    copy.Inherit(null, []);
    copy.Define(instance.Fields);
    copy.DefineMembers(instance.Constructors, instance.Methods, instance.Properties, []);
    return module with { Types = [.. module.Types.Select(type => ReferenceEquals(type, instance) ? copy : type)] };
  }

  [Test]
  public void ModuleFormat_KeepsTheClassTemplate() {
    var module = GenericsFixtures.Imported("Ring");
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    var template = Instance(read, "Ring<i64>").Origin!.ClassTemplate!;
    Assert.That(Instance(read, "Ring<f64>").Origin!.ClassTemplate, Is.SameAs(template));
    Assert.That(template.Class!.Name, Is.EqualTo("Ring<T>"));
    Assert.That(read.Types, Does.Not.Contain(template.Class));
    Assert.That(template.Members.Select(member => member.Function.Name), Is.EqualTo(template.Class.Functions));
    var instance = Instance(read, "Ring<i64>");
    var functions = read.Functions.ToDictionary(function => function.Name);
    Assert.That(IrGenerics.Instantiate(template, [IrType.I64], instance, read).All(function => IrGenerics.Equivalent(function, functions[function.Name])), Is.True);
    Assert.That(Problems(read), Is.Empty);
  }

}
