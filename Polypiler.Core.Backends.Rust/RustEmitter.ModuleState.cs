using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Emit;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>The name of the function that runs the module's initializer once, before the module's state is first used.</summary>
  private const string LoadFunction = "ply_load";

  /// <summary>The static of each thread that says whether the initializer ran.</summary>
  private const string LoadedFlag = "PLY_LOADED";

  /// <summary>Whether a global of a type is held in a static of each thread: an object, a collection or a function value, which hold an <c>Rc</c> no <c>static</c> may.</summary>
  private static bool IsThreadLocalType(IrType type) => type.IsObject || type.IsCollection || type.IsFunction;

  private sealed partial class Writer {

    /// <summary>Whether a global is held in a static of each thread.</summary>
    private bool IsThreadLocal(IrGlobal global) => IsThreadLocalType(global.Type);

    /// <summary>The functions that read or write module state and so run the module's initializer first.</summary>
    private HashSet<string> _loading = new(System.StringComparer.Ordinal);

    /// <summary>The readers of the module state held in statics of each thread, by global.</summary>
    private readonly Dictionary<string, string> _readers = new(System.StringComparer.Ordinal);

    /// <summary>
    /// Finds the functions that touch module state where the module has an initializer: each runs it
    /// first (<see cref="LoadFunction"/>), as .NET runs a class's static constructor before its state is
    /// first used and JavaScript a module's top level before anything it exports. The initializer itself
    /// does not, and neither does a function only it calls.
    /// </summary>
    private void Loading(IrModule module) {
      this._loading = new(System.StringComparer.Ordinal);
      if (module.Initializer is not { } initializer)
        return;

      foreach (var function in module.Functions.Where(function => function.Name != initializer && !IrSequences.IsIterator(function)))
        if (IrTree.Nodes(function.Body).Any(node => node is IrGlobalRef) || ArrayOwnership.Statements(function.Body).Any(statement => statement is IrGlobalAssign))
          this._loading.Add(function.Name);
    }

    /// <summary><c>ply_load();</c> where the function being written touches module state.</summary>
    private IEnumerable<RsStatement> Load() {
      if (this._loading.Contains(this._function.Name))
        yield return new RsExpressionStatement(new RsCall(new RsPath(LoadFunction), []));
    }

    /// <summary>
    /// A global held in a static of each thread (#1422): <c>thread_local! { static NAME: RefCell&lt;T&gt; = RefCell::new(v); }</c>,
    /// the value made on its first use in a thread - an object as <c>Option&lt;Rc&lt;T&gt;&gt;</c>, a
    /// collection as the shared <c>Rc</c> it always is, a function value as its <c>Rc</c>. A static of
    /// the process cannot hold an <c>Rc</c>, which no other thread may touch: each thread has the
    /// module state of its own, where .NET has one for the process.
    /// </summary>
    private RsConst ThreadLocalGlobal(IrGlobal global) {
      this._uses.Add("std::cell::RefCell");
      var saved = this._function;
      this._function = new IrFunction("$" + global.Name, false, IrType.Void, [], [], new IrBlock([]));
      string type;
      RsExpression initial;
      if (global.Type.IsObject) {
        type = this.ObjectType(global.Type, true);
        initial = global.Initializer is null or IrNull ? new RsPath("None") : this.ObjectValue(global.Initializer, true, global.Type);
      } else if (global.Type.IsCollection) {
        type = this.CollectionType(global.Type);
        initial = global.Initializer is null ? this.SharedCollection(new RsCall(new RsPath("Vec::new"), []), global.Type) : this.CollectionValue(global.Initializer);
      } else {
        type = this.FunctionType(global.Type);
        initial = global.Initializer is null ? this.Refuse($"a global of the type {global.Type} without a first value") : this.FunctionValue(global.Initializer);
      }

      this._function = saved;
      var constant = initial is RsPath { Name: "None" } && this.Has(RustCapabilities.ConstThreadLocal);
      return new RsConst(this._globals[global.Name], "", $"RefCell<{type}>", new RsCall(new RsPath("RefCell::new"), [initial])) { IsStatic = true, IsThreadLocal = true, IsConstInitialized = constant };
    }

    /// <summary>The value of a global held in a static of each thread: a copy of its <c>Rc</c>, <c>NAME.with(|name| name.borrow().clone())</c>.</summary>
    private RsExpression ThreadLocalValue(IrGlobal global)
      => this.WithThreadLocal(global, value => new RsMethodCall(new RsMethodCall(value, "borrow", []), "clone", []) { Type = global.Type });

    /// <summary>A global held in a static of each thread written: its value first, then <c>NAME.with(|name| *name.borrow_mut() = value)</c>.</summary>
    private void ThreadLocalAssign(IrGlobalAssign assign, List<RsStatement> into) {
      var value = this.Settled(assign.Value, "value", into, this.ThreadLocalEntry);
      into.Add(new RsExpressionStatement(this.WithThreadLocal(assign.Global, place => new RsAssign(new RsUnary("*", new RsMethodCall(place, "borrow_mut", [])), "=", value))));
    }

    /// <summary>A value as a global held in a static of each thread takes it.</summary>
    private RsExpression ThreadLocalEntry(IrExpr value)
      => value.Type.IsObject ? this.ObjectValue(value, true, value.Type) : value.Type.IsCollection ? this.CollectionValue(value) : this.FunctionValue(value);

    /// <summary><c>NAME.with(|name| access)</c>.</summary>
    private RsExpression WithThreadLocal(IrGlobal global, System.Func<RsExpression, RsExpression> access) {
      var own = global.Name[(global.Name.LastIndexOf('.') + 1)..].TrimStart('_');
      var name = this.Fresh(own.Length == 0 ? "value" : RustNames.Snake(own, this.Edition).Replace("r#", "", System.StringComparison.Ordinal));
      return new RsMethodCall(new RsPath(this._globals[global.Name]), "with", [new RsClosure(false, [new RsParameter(name, "")], access(new RsPath(name) { Type = global.Type }))]) { Type = global.Type };
    }

    /// <summary>
    /// The items of the module state held in statics of each thread: the flag and the function that run
    /// the initializer once (<c>PLY_LOADED</c>, <c>ply_load</c>), and a public reader per such global,
    /// <c>pub fn algorithms() -&gt; Vec&lt;Rc&lt;dyn Algorithm&gt;&gt;</c>, which gives a copy of a
    /// collection's elements, or the value - what a caller of the crate reads of the module's state.
    /// </summary>
    private IEnumerable<RsItem> ModuleStateItems(IrModule module) {
      if (module.Initializer is { } initializer && this._functions.TryGetValue(initializer, out var run)) {
        this._uses.Add("std::cell::Cell");
        if (this._throwing.Contains(initializer))
          this.Refuse("a module initializer that lets an exception out");
        yield return new RsConst(LoadedFlag, "", "Cell<bool>", new RsCall(new RsPath("Cell::new"), [new RsLiteral("false")])) {
          IsStatic = true, IsThreadLocal = true, IsConstInitialized = this.Has(RustCapabilities.ConstThreadLocal)
        };
        var loaded = new RsMethodCall(new RsPath(LoadedFlag), "with", [new RsClosure(false, [new RsParameter("loaded", "")], new RsMethodCall(new RsPath("loaded"), "replace", [new RsLiteral("true")]))]);
        yield return new RsFunction(LoadFunction, "", [], null, new RsBlock([
          new RsExpressionStatement(new RsIf(new RsUnary("!", loaded), new RsBlock([new RsExpressionStatement(new RsCall(new RsPath(run), []))], null), null))
        ], null)) { Docs = ["Runs the module's initializer once, before its state is first used."] };
      }

      foreach (var global in module.Globals.Where(this.IsThreadLocal)) {
        this._construct = $"the global '{global.Name}'";
        var reader = this._readers[global.Name];
        var copied = global.Type.IsCollection
          ? this.WithThreadLocal(global, value => new RsMethodCall(new RsMethodCall(new RsMethodCall(value, "borrow", []), "borrow", []), "clone", []))
          : this.ThreadLocalValue(global);
        var statements = new List<RsStatement>();
        if (module.Initializer is not null)
          statements.Add(new RsExpressionStatement(new RsCall(new RsPath(LoadFunction), [])));
        yield return new RsFunction(reader, "pub", [], global.Type.IsCollection ? this.VectorType(global.Type) : global.Type.IsObject ? this.ObjectType(global.Type, true) : this.FunctionType(global.Type),
          new RsBlock(statements, copied)) { Docs = [$"The module state '{global.Name[(global.Name.LastIndexOf('.') + 1)..]}' as it is now."] };
      }
    }

    /// <summary>Names the readers of the module state held in statics of each thread, apart from the functions.</summary>
    private void NameReaders(IrModule module) {
      var taken = new HashSet<string>(this._functions.Values, System.StringComparer.Ordinal) { LoadFunction };
      var globals = module.Globals.Where(this.IsThreadLocal).Select(global => global.Name).ToList();
      foreach (var (global, name) in globals.Zip(RustNames.Unique(globals, name => RustNames.Snake(name[(name.LastIndexOf('.') + 1)..].TrimStart('_'), this.Edition), taken)))
        this._readers[global] = name;
    }

  }

}
