using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Emit;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>The fields every function reads or writes, itself or through the functions it calls; <see langword="null"/> where a call that selects its method by the object may reach any.</summary>
    private Dictionary<string, HashSet<IrField>?>? _fieldsTouched;

    /// <summary>Whether a variable names the array a field of an object holds (<c>var s = this._s;</c>).</summary>
    private bool IsFieldAlias(IrLocal local) => this._arrayFacts.FieldAliases.ContainsKey(local);

    /// <summary>
    /// The fields a function touches, a fixed point over the calls of the module: a call that selects
    /// its method by its object, or calls a function value, may touch any.
    /// </summary>
    private HashSet<IrField>? FieldsTouched(string function) {
      if (this._fieldsTouched is null) {
        var touched = this._module.Functions.ToDictionary(candidate => candidate.Name, candidate => (HashSet<IrField>?)[.. Direct(candidate)], StringComparer.Ordinal);
        foreach (var candidate in this._module.Functions.Where(candidate => IrTree.Nodes(candidate.Body).Any(node => node is IrMethodCall or IrInvoke)))
          touched[candidate.Name] = null;

        for (var changed = true; changed;) {
          changed = false;
          foreach (var candidate in this._module.Functions) {
            if (touched[candidate.Name] is not { } own)
              continue;

            foreach (var call in IrTree.Nodes(candidate.Body).OfType<IrCall>().Where(call => touched.ContainsKey(call.Function))) {
              if (touched[call.Function] is not { } callee) {
                touched[candidate.Name] = null;
                changed = true;
                break;
              }

              var before = own.Count;
              own.UnionWith(callee);
              changed |= own.Count != before;
            }
          }
        }

        this._fieldsTouched = touched;
      }

      return this._fieldsTouched.GetValueOrDefault(function, []);

      static IEnumerable<IrField> Direct(IrFunction function)
        => IrTree.Nodes(function.Body).OfType<IrFieldLoad>().Select(load => load.Field)
          .Concat(ArrayOwnership.Statements(function.Body).OfType<IrFieldStore>().Select(store => store.Field));
    }

    /// <summary>
    /// Checks the aliases of field arrays of the function being written: each holds the field's
    /// <c>RefCell</c> borrowed for as long as it lives, so nothing else in the function may touch the
    /// field - no other read or write of it, no call that may reach it - and the variable is never
    /// given another array. Otherwise the borrow would panic where .NET runs on.
    /// </summary>
    private void FieldAliases(IrFunction function) {
      foreach (var (alias, load) in this._arrayFacts.FieldAliases) {
        // The borrow lives from the declaration to the end of its block: only what follows it there matters.
        // A plain borrow, of no cell, ends where the variable is read for the last time.
        var direct = !load.Target.Type.IsObject || this.IsPlain(load.Target.Type);
        var following = Following(function.Body, alias);
        if (direct && following is not null) {
          var last = following.Select((statement, index) => (statement, index)).LastOrDefault(pair => IrTree.Nodes(pair.statement).Any(node => node is IrLocalRef reference && reference.Local == alias));
          following = [.. following.Take(last.statement is null ? 0 : last.index + 1)];
        }

        var block = new IrBlock(following ?? []);
        var touches = IrTree.Nodes(block).Any(node => node is IrFieldLoad other && other.Field == load.Field || node is IrMethodCall or IrInvoke
            || node is IrCall call && (this.FieldsTouched(call.Function) is not { } fields || fields.Contains(load.Field)))
          || ArrayOwnership.Statements(block).Any(statement => statement is IrFieldStore store && store.Field == load.Field);
        var assigned = ArrayOwnership.Statements(function.Body).Any(statement => statement is IrAssign assign && assign.Local == alias);
        // The borrow of a plain struct's field holds the struct: nothing may take the struct as a whole while it lives.
        touches |= direct && load.Target is IrLocalRef { Local: var owner }
          && IrTree.Nodes(block).Any(node => node is IrLocalRef reference && reference.Local == owner)
          && !IrTree.Nodes(block).All(node => node is not IrLocalRef reference || reference.Local != owner || IsFieldTarget(block, node));
        if (following is null || touches || assigned || load.Target is not IrLocalRef)
          this.Refuse("a variable that names a field's array where the function touches the field otherwise while it lives");
      }
    }

    /// <summary>Whether a node is the object or struct of a field read or written in a block, not a value of its own.</summary>
    private static bool IsFieldTarget(IrBlock block, IrExpr node)
      => IrTree.Nodes(block).Any(other => other is IrFieldLoad load && ReferenceEquals(load.Target, node))
        || ArrayOwnership.Statements(block).Any(statement => statement is IrFieldStore store && ReferenceEquals(store.Target, node));

    /// <summary>The statements after the declaration of a variable with its value, in the block that declares it; <see langword="null"/> where there is no such declaration.</summary>
    private static IReadOnlyList<IrStmt>? Following(IrBlock block, IrLocal local) {
      for (var index = 0; index < block.Statements.Count; ++index) {
        var statement = block.Statements[index];
        if (statement is IrDeclare { Initializer: IrFieldLoad } declare && declare.Local == local)
          return [.. block.Statements.Skip(index + 1)];

        var inner = statement is IrBlock nested ? [nested] : IrTree.Blocks(statement);
        foreach (var child in inner)
          if (Following(child, local) is { } found)
            return found;
      }

      return null;
    }

    /// <summary>The value an alias of a field array is declared with: the field's cell borrowed, <c>self.s.borrow_mut()</c> where the function writes through it.</summary>
    private RsExpression FieldAlias(IrLocal alias) {
      var load = this._arrayFacts.FieldAliases[alias];
      var mutable = this._arrayFacts.MutableLocals.Contains(alias);
      // The array of a plain struct or of a struct value is borrowed from the struct itself, no cell between them.
      return load.Target.Type.IsStruct ? new RsReference(mutable, this.Place(load, mutable)) { Type = alias.Type }
        : this.IsPlain(load.Target.Type) ? new RsReference(mutable, this.FieldArray(load, mutable)) { Type = alias.Type }
        : this.FieldArray(load, mutable);
    }

    /// <summary>An alias of a field array as a slice, <c>&amp;s[..]</c> or <c>&amp;mut s[..]</c>, which its guard derefs to.</summary>
    private RsExpression FieldAliasSlice(IrLocal alias, bool mutable)
      => new RsReference(mutable, new RsIndex(new RsPath(this._locals[alias]), new RsRange(null, null, false))) { Type = alias.Type };

  }

}
