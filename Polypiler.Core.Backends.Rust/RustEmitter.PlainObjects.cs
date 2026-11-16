using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Emit;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>Which classes are single-owner and which functions write their objects (#1393).</summary>
    private ObjectOwnership _ownership = ObjectOwnership.None;

    /// <summary>The fields some function of the module reads.</summary>
    private HashSet<IrField>? _readFields;

    /// <summary>Finds the single-owner classes, whose members then take <c>&amp;self</c> or <c>&amp;mut self</c> and never the <c>Rc</c>.</summary>
    private void Ownership(IrModule module) {
      this._ownership = ObjectOwnership.Of(module, this._classes.Where(type => !this._hierarchy.Contains(type)), this._nullness, this._arrays);
      this._sharedSelf.RemoveWhere(name => this._members.TryGetValue(name, out var member) && this._ownership.Single.Contains(member.Class));
    }

    /// <summary>Whether values of a type are objects of a single-owner class, a plain struct.</summary>
    private bool IsPlain(IrType type) => this._ownership.IsSingle(type);

    /// <summary>Whether the function being written writes the object a variable of a single-owner class holds.</summary>
    private bool IsMutatedObject(IrLocal local) => local.Type.IsObject && this._ownership.Mutates(this._function.Name, local);

    /// <summary>
    /// The fields of a single-owner class the struct keeps: those some function reads. A field only
    /// written is never seen again, and rustc would warn about it as dead code; its stores keep only
    /// the effects of their values.
    /// </summary>
    private IReadOnlyList<IrField> PlainFields(IrClassType type) {
      this._readFields ??= [.. this._module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrFieldLoad>().Select(load => load.Field)];
      return [.. type.AllFields.Where(this._readFields.Contains)];
    }

    /// <summary>The Rust type of a field of a plain struct: a value as it is, an array as the vector it owns.</summary>
    private string PlainFieldType(IrField field) => field.Type.IsArray ? $"Vec<{this.ElementType(field.Type)}>" : this.Type(field.Type);

    /// <summary>The object of a single-owner class where a field or a method takes it: <c>self</c>, a variable, a new object.</summary>
    private RsExpression PlainObject(IrExpr value) => value switch {
      IrLocalRef reference when this.CurrentMember is { Kind: MemberKind.Instance } && this._function.Parameters[0] == reference.Local => new RsPath("self") { Type = value.Type },
      IrLocalRef reference => new RsPath(this._locals[reference.Local]) { Type = value.Type },
      _ => this.Expression(value)
    };

    /// <summary>
    /// An object of a single-owner class where a function takes it: a parameter passed on as the
    /// reference it is, a variable or a new object borrowed - exclusively where the callee writes it.
    /// </summary>
    private RsExpression PlainArgument(string callee, int index, IrExpr value) {
      var function = this._module.Functions.FirstOrDefault(function => function.Name == callee);
      var mutable = function is not null && this._ownership.MutatesParameter(function, index);
      if (value is IrLocalRef { Local: var local } && this._function.Parameters.Contains(local))
        return this.PlainObject(value);

      return new RsReference(mutable, this.PlainObject(value)) { Type = value.Type };
    }

    /// <summary>The type of a parameter of a single-owner class: <c>&amp;Name</c>, or <c>&amp;mut Name</c> where the function writes the object.</summary>
    private string PlainParameterType(IrFunction function, int index)
      => (this._ownership.MutatesParameter(function, index) ? "&mut " : "&") + this.ClassName(function.Parameters[index].Type);

    /// <summary>A field of a plain struct read: a value as it is, an array copied.</summary>
    private RsExpression PlainField(IrFieldLoad load) {
      var field = new RsField(this.Receiver(this.PlainObject(load.Target)), this.FieldName(load.Field)) { Type = load.Type };
      return IsValue(load.Field.Type) ? field : new RsMethodCall(field, "clone", []) { Type = load.Type };
    }

    /// <summary>A field of a plain struct written, <c>o.f = v;</c>; of a field the struct leaves out only what the value does.</summary>
    private void PlainFieldStore(IrFieldStore store, List<RsStatement> into) {
      var value = store.Field.Type.IsArray ? this.Vector(store.Value, false) : this.Expression(store.Value);
      if (!this.PlainFields(store.Target.Type.ClassType!).Contains(store.Field)) {
        // A value that may trap or do something is still evaluated; a variable or a constant is not.
        if (store.Value is not (IrConst or IrLocalRef))
          into.Add(new RsLet("_", false, null, value));
        return;
      }

      into.Add(new RsExpressionStatement(new RsAssign(new RsField(this.Receiver(this.PlainObject(store.Target)), this.FieldName(store.Field)), "=", value)));
    }

    /// <summary>
    /// The items of a single-owner class: <c>#[derive(Default)] pub struct Name { field: T }</c> and an
    /// <c>impl</c> with <c>new</c> per constructor, which makes the object and runs the constructor's
    /// body on it, and the methods with <c>&amp;self</c>, or <c>&amp;mut self</c> where they write it.
    /// </summary>
    private IEnumerable<RsItem> PlainClass(IrClassType type, IReadOnlyDictionary<string, RsFunction> written) {
      var name = this._types[type];
      var fields = this.PlainFields(type).Select(field => new RsFieldDeclaration(this.FieldName(field), "", this.PlainFieldType(field))).ToList();
      yield return new RsStruct(name, "pub", fields) { Attributes = ["derive(Default)"] };
      var members = this._members.Where(pair => pair.Value.Class == type && written.ContainsKey(pair.Key)).ToList();
      var items = new List<RsItem>();
      foreach (var (function, member) in members.Where(pair => pair.Value.Kind == MemberKind.Maker))
        items.Add(this.PlainMaker(written[function], member));
      foreach (var (function, member) in members.Where(pair => pair.Value.Kind != MemberKind.Maker))
        items.Add(written[function]);

      if (items.Count > 0)
        yield return new RsImpl(null, name, items);
    }

    /// <summary><c>pub fn new(..) -&gt; Self</c>: the object made with every field at its default, the constructor's body run on it, the object moved out.</summary>
    private RsFunction PlainMaker(RsFunction written, Member member) {
      var (init, source) = this._members.Where(pair => pair.Value.Kind == MemberKind.Instance && pair.Value.Constructor == member.Constructor)
        .Select(pair => (pair.Value, Function: this._module.Functions.First(function => function.Name == pair.Key))).First();
      // A struct without fields is a unit struct, which is its own value (clippy's default_constructed_unit_structs).
      RsExpression empty = this.PlainFields(member.Class).Count == 0 ? new RsPath("Self") : new RsCall(new RsPath("Self::default"), []);
      var make = new RsLet("this", this._ownership.MutatesParameter(source, 0), null, empty);
      var arguments = written.Parameters.Select(parameter => (RsExpression)new RsPath(parameter.Name)).ToList();
      var run = new RsExpressionStatement(new RsMethodCall(new RsPath("this"), init.Name, arguments));
      return written with { Visibility = "pub", Returns = "Self", Body = new RsBlock([make, run], new RsPath("this")) };
    }

  }

}
