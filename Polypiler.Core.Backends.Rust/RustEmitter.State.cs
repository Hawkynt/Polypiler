using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>The number of elements of every global array, which never changes.</summary>
    private readonly Dictionary<string, int> _globalLengths = new(System.StringComparer.Ordinal);

    /// <summary>Whether a global has no atomic type and is held in a <c>Mutex</c>.</summary>
    private static bool IsLocked(IrGlobal global) => Atomic(global.Type) is null;

    /// <summary>
    /// A global that has no atomic form, as a static of the process behind a <c>Mutex</c> (Rust 1.63 made
    /// <c>Mutex::new</c> const): an array as the fixed array it always is, <c>Mutex&lt;[u32; 4]&gt;</c>, a
    /// string as <c>Mutex&lt;Vec&lt;u16&gt;&gt;</c>, a floating-point number as itself. A first value
    /// Rust cannot make in a constant - a string that is not empty - is made on first use by a
    /// <c>LazyLock</c> (Rust 1.80). A <c>thread_local!</c> would give each thread a global of its own,
    /// where .NET has one for the process.
    /// </summary>
    private RsConst? LockedGlobal(IrGlobal global) {
      string type;
      RsExpression initial;
      var lazy = false;
      if (global.Type.IsArray && global.Type.Element is { } element && (IsValue(element) || element.IsString)) {
        var elementType = this.Type(element);
        List<RsExpression> values;
        switch (global.Initializer) {
          case IrNewArray { Length: IrConst { Value: >= 0 } length } when !element.IsString:
            this._globalLengths[global.Name] = (int)length.Value;
            type = $"[{elementType}; {length.Value.ToString(CultureInfo.InvariantCulture)}]";
            initial = new RsArray([this.Zero(element)]) { Count = new RsLiteral(length.Value.ToString(CultureInfo.InvariantCulture)) };
            break;
          case IrIntrinsic { Op: IrIntrinsicOp.ArrayOf } listed when listed.Arguments.All(argument => argument is IrConst || argument is IrStrConst):
            values = [.. listed.Arguments.Select(argument => argument is IrStrConst { Value.Length: 0 } ? new RsCall(new RsPath("Vec::new"), []) : argument is IrStrConst ? this.StringValue(argument) : Literal((IrConst)argument))];
            lazy = listed.Arguments.Any(argument => argument is IrStrConst { Value.Length: > 0 });
            this._globalLengths[global.Name] = values.Count;
            type = $"[{elementType}; {values.Count.ToString(CultureInfo.InvariantCulture)}]";
            initial = new RsArray(values);
            break;
          default:
            this.Refuse($"a global array of the type {global.Type} without a first value of a known length");
            return null;
        }
      } else if (global.Type.IsString) {
        type = OwnedString;
        lazy = global.Initializer is IrStrConst { Value.Length: > 0 };
        initial = global.Initializer is null or IrStrConst { Value.Length: 0 } ? new RsCall(new RsPath("Vec::new"), []) : this.StringValue(global.Initializer);
      } else if (global.Type.IsFloat) {
        type = TypeName(global.Type)!;
        initial = global.Initializer switch {
          null => Literal(new IrConst(global.Type, 0)),
          IrConst constant => Literal(constant),
          _ => this.Refuse("a global with a computed first value")
        };
      } else {
        this.Refuse($"a global of the type {global.Type}");
        return null;
      }

      if (!this.Has(RustCapabilities.ConstMutex)) {
        this.Refuse($"a global of the type {global.Type} (a Mutex in a static, Rust 1.63)");
        return null;
      }

      if (lazy && !this.Has(RustCapabilities.LazyLock)) {
        this.Refuse($"a global of the type {global.Type} with a first value made at run time (LazyLock, Rust 1.80)");
        return null;
      }

      this._uses.Add("std::sync::Mutex");
      this._uses.Add("std::sync::PoisonError");
      this.StringHelper("ply_with");
      RsExpression locked = new RsCall(new RsPath("Mutex::new"), [initial]);
      if (!lazy)
        return new RsConst(this._globals[global.Name], "", $"Mutex<{type}>", locked) { IsStatic = true };

      this._uses.Add("std::sync::LazyLock");
      return new RsConst(this._globals[global.Name], "", $"LazyLock<Mutex<{type}>>", new RsCall(new RsPath("LazyLock::new"), [new RsClosure(false, [], locked)])) { IsStatic = true };
    }

    /// <summary>
    /// An access of a locked global: <c>ply_with(&amp;NAME, |name| access)</c>, which holds the lock for
    /// the access alone - every operand that may take a lock itself is evaluated before.
    /// </summary>
    private RsExpression WithLocked(IrGlobal global, System.Func<RsExpression, RsExpression> access) {
      var own = global.Name[(global.Name.LastIndexOf('.') + 1)..].TrimStart('_');
      var name = this.Fresh(own.Length == 0 ? "value" : RustNames.Snake(own, this.Edition).Replace("r#", "", System.StringComparison.Ordinal));
      var value = new RsPath(name) { Type = global.Type };
      return new RsCall(new RsPath(this.StringHelper("ply_with")), [new RsReference(false, new RsPath(this._globals[global.Name])), new RsClosure(false, [new RsParameter(name, "")], access(value))]);
    }

    /// <summary>A name for a closure's parameter that no variable of the function has.</summary>
    private string Fresh(string stem) {
      var name = stem;
      for (var suffix = 1; this._taken.Contains(name); ++suffix)
        name = stem + suffix.ToString(CultureInfo.InvariantCulture);

      return name;
    }

    /// <summary>Whether evaluating an expression may take a lock or have an effect: a call, a step, a bind, a locked global.</summary>
    private static bool MayLock(IrExpr expression)
      => IrTree.Nodes(expression).Any(node => node is IrCall or IrInvoke or IrMethodCall or IrStep or IrBind || node is IrGlobalRef { Global: var global } && IsLocked(global));

    /// <summary>A value, bound first into a statement where it may take a lock: the binding's name, or the value as it is.</summary>
    private RsExpression Settled(IrExpr value, string stem, List<RsStatement> before, System.Func<IrExpr, RsExpression> write) {
      if (!MayLock(value))
        return write(value);

      var name = this.Temporary(stem);
      before.Add(new RsLet(name, false, null, write(value)));
      return new RsPath(name) { Type = value.Type };
    }

    /// <summary>The value of a locked global that is no array: a copy, <c>ply_with(&amp;TEXT, |text| text.clone())</c>.</summary>
    private RsExpression LockedValue(IrGlobal global)
      => this.WithLocked(global, value => global.Type.IsString ? new RsMethodCall(value, "clone", []) { Type = global.Type } : new RsUnary("*", value) { Type = global.Type });

    /// <summary>An element of a locked global array: <c>ply_with(&amp;TABLE, |table| table[i])</c>, a string cloned.</summary>
    private RsExpression LockedElement(IrGlobal global, IrExpr index) {
      var before = new List<RsStatement>();
      var at = this.Settled(index, "index", before, this.Index);
      var read = this.WithLocked(global, value => {
        RsExpression element = new RsIndex(value, at) { Type = global.Type.Element };
        return global.Type.Element!.IsString ? new RsMethodCall(element, "clone", []) { Type = global.Type.Element } : element;
      });
      return before.Count == 0 ? read : new RsBlockExpression(new RsBlock(before, read)) { Type = global.Type.Element };
    }

    /// <summary>A global that is no array written: its value first, then <c>ply_with(&amp;TEXT, |text| *text = value)</c>.</summary>
    private void LockedAssign(IrGlobalAssign assign, List<RsStatement> into) {
      var value = this.Settled(assign.Value, "value", into, write => assign.Global.Type.IsString ? this.StringValue(write) : this.Expression(write));
      into.Add(new RsExpressionStatement(this.WithLocked(assign.Global, place => new RsAssign(new RsUnary("*", place), "=", value))));
    }

    /// <summary>An element of a global array written: the index, then the value, then <c>ply_with(&amp;TABLE, |table| table[i] = v)</c>.</summary>
    private void LockedStore(IrStore store, IrGlobal global, List<RsStatement> into) {
      var at = this.Settled(store.Index, "index", into, this.Index);
      var value = this.Settled(store.Value, "value", into, this.ElementValue);
      into.Add(new RsExpressionStatement(this.WithLocked(global, place => new RsAssign(new RsIndex(place, at) { Type = store.Value.Type }, "=", value))));
    }

    /// <summary>A step of an element of a global array, under one lock: as a statement the element written back, as a value a block that gives the old or the new one.</summary>
    private RsExpression LockedStep(IrStep step, IrArrayLoad load, IrGlobal global) {
      var before = new List<RsStatement>();
      var stepped = this.LockedStep(step, load, global, false, before);
      return before.Count == 0 ? stepped : new RsBlockExpression(new RsBlock(before, stepped)) { Type = load.Type };
    }

    private RsExpression LockedStep(IrStep step, IrArrayLoad load, IrGlobal global, bool statement, List<RsStatement> before) {
      var at = this.Settled(load.Index, "index", before, this.Index);
      var stepped = this.WithLocked(global, place => {
        var element = new RsIndex(place, at) { Type = load.Type };
        var write = new RsExpressionStatement(new RsAssign(element, "=", this.Stepped(element, step)));
        if (statement)
          return new RsBlockExpression(new RsBlock([write], null));

        if (!step.Postfix)
          return new RsBlockExpression(new RsBlock([write], element));

        var old = this.Temporary("old");
        return new RsBlockExpression(new RsBlock([new RsLet(old, false, null, element), write], new RsPath(old) { Type = load.Type }));
      });
      return stepped;
    }

  }

}
