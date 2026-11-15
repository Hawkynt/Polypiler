using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>
/// Method calls: the methods of the primitives, of <c>Option</c> and <c>Result</c>, of arrays,
/// slices and <c>Vec</c>, of iterators, and the inherent methods of the crate's own types, each found
/// through the auto-dereference of the receiver (The Rust Reference, "Method call expressions").
/// </summary>
internal sealed partial class TypeChecker {

  /// <summary>The slice, array and <c>Vec</c> methods the subset imports.</summary>
  private static readonly HashSet<string> _sliceMethods = [
    "len", "is_empty", "iter", "iter_mut", "into_iter", "swap", "reverse", "fill", "copy_from_slice", "copy_within",
    "split_at", "split_at_mut", "to_vec", "get", "first", "last", "chunks_exact", "chunks_exact_mut", "clone"
  ];

  /// <summary>The iterator adapters and consuming methods the subset imports.</summary>
  private static readonly HashSet<string> _iteratorMethods = [
    "rev", "step_by", "zip", "enumerate", "copied", "cloned", "map", "filter",
    "sum", "product", "count", "fold", "any", "all", "for_each"
  ];

  private RType Method(MethodCallExpr call, RType? expected) {
    // An integer range is an iterator only as a receiver: (1..=n).map(..).
    if (call.Receiver is RangeExpr range)
      return this.IteratorMethod(call, this.RangeIterator(range, call), expected);

    // The pair or optional value a checked_* or overflowing_* method yields is a value of its own now.
    var receiver = this.Value(call.Receiver);
    var resolved = receiver.Resolved;
    var target = receiver.Dereferenced;
    if (target.Kind == RKind.Variable) {
      if (this.IsError(target))
        return this.Skip(call);

      if (target.VariableKind == VariableKind.Any)
        throw this.Unresolved(call.Receiver is PathExpr path ? path.Text : call.Name, call.Receiver.At);

      throw this.Invalid($"E0689: can't call method `{call.Name}` on ambiguous numeric type `{target}`", call.At);
    }

    switch (target.Kind) {
      case RKind.Iterator:
        return this.IteratorMethod(call, target.IteratorInfo!, expected);
      case RKind.Struct or RKind.Enum:
        return this.InherentMethod(call, receiver, target);
      case RKind.Option:
        return this.OptionMethod(call, target, receiver);
      case RKind.Result:
        return this.ResultMethod(call, target);
      case RKind.Array or RKind.Slice or RKind.Vec:
        return this.SliceMethod(call, receiver, target, expected);
      case RKind.Integer or RKind.Float or RKind.Bool:
        return this.PrimitiveMethod(call, target);
    }

    foreach (var argument in call.Arguments)
      this.Expression(argument);

    throw this.Invalid($"E0599: no method named `{call.Name}` found for `{resolved}`", call.At);
  }

  private RType Skip(MethodCallExpr call) {
    foreach (var argument in call.Arguments)
      this.Expression(argument);

    return this.ErrorType();
  }

  private void Count(MethodCallExpr call, int count) {
    if (call.Arguments.Count != count)
      throw this.Invalid($"E0061: this method takes {count} argument{(count == 1 ? "" : "s")} but {call.Arguments.Count} were supplied", call.At);
  }

  // ---------------------------------------------------------------- primitives

  private RType PrimitiveMethod(MethodCallExpr call, RType receiver) {
    this._typed.Methods[call] = call.Name;
    switch (call.Name) {
      case "to_le_bytes" or "to_be_bytes" when receiver.IsNumber:
        this.Count(call, 0);
        return RType.Array(RType.U8, receiver.Bits / 8);
      case "to_ne_bytes":
        return this.Refused(RustFrontend.UnsupportedSliceMethod, call.Name, call.At);
      case "clone":
        this.Count(call, 0);
        return receiver;
    }

    if (Methods.Of(receiver, call.Name) is not { } method) {
      this._typed.Methods.Remove(call);
      foreach (var argument in call.Arguments)
        this.Expression(argument);

      return this.Refused(RustFrontend.Unsupported, $"{receiver}::{call.Name}", call.At);
    }

    this.Count(call, method.Arguments.Count);
    for (var index = 0; index < method.Arguments.Count; ++index)
      this.Unify(Methods.ArgumentOf(method.Arguments[index], receiver), StripShared(this.Value(call.Arguments[index])), call.Arguments[index].At);

    return Methods.ResultOf(method, receiver);
  }

  // ---------------------------------------------------------------- Option and Result

  private RType OptionMethod(MethodCallExpr call, RType option, RType receiver) {
    this._typed.Methods[call] = "Option::" + call.Name;
    var value = option.Element!;
    switch (call.Name) {
      case "is_some" or "is_none":
        this.Count(call, 0);
        return RType.Bool;
      case "unwrap":
        this.Count(call, 0);
        return value;
      case "expect":
        this.Count(call, 1);
        this.Expectation(call);
        return value;
      case "unwrap_or":
        this.Count(call, 1);
        this.Coerce(value, this.Value(call.Arguments[0], value), call.Arguments[0].At);
        return value;
      case "ok_or": {
        this.Count(call, 1);
        var error = this.Value(call.Arguments[0]);
        return RType.Result(value, error);
      }
      case "copied" or "cloned" when value.Resolved.Kind == RKind.Reference:
        this.Count(call, 0);
        return RType.Option(value.Resolved.Element!);
      case "clone":
        this.Count(call, 0);
        return option;
    }

    this._typed.Methods.Remove(call);
    foreach (var argument in call.Arguments)
      this.Expression(argument);

    return this.Refused(RustFrontend.UnsupportedOptionMethod, $"Option::{call.Name}", call.At);
  }

  /// <summary>The message of <c>expect</c>: a string literal.</summary>
  private void Expectation(MethodCallExpr call) {
    if (call.Arguments[0] is not StringLiteral)
      this.Refuse(new Refusal(RustFrontend.Unsupported, "expect with a message that is not a string literal", call.Arguments[0].At));
  }

  private RType ResultMethod(MethodCallExpr call, RType result) {
    this._typed.Methods[call] = "Result::" + call.Name;
    switch (call.Name) {
      case "is_ok" or "is_err":
        this.Count(call, 0);
        return RType.Bool;
      case "unwrap":
        this.Count(call, 0);
        return result.Element!;
      case "expect":
        this.Count(call, 1);
        this.Expectation(call);
        return result.Element!;
      case "unwrap_or":
        this.Count(call, 1);
        this.Coerce(result.Element!, this.Value(call.Arguments[0], result.Element), call.Arguments[0].At);
        return result.Element!;
      case "ok" when result.Element!.Resolved.Kind != RKind.Unit:
        this.Count(call, 0);
        return RType.Option(result.Element!);
      case "clone":
        this.Count(call, 0);
        return result;
    }

    this._typed.Methods.Remove(call);
    foreach (var argument in call.Arguments)
      this.Expression(argument);

    return this.Refused(RustFrontend.UnsupportedOptionMethod, $"Result::{call.Name}", call.At);
  }

  // ---------------------------------------------------------------- the crate's own methods

  private RType InherentMethod(MethodCallExpr call, RType receiver, RType target) {
    var methods = target.Kind == RKind.Struct ? target.Struct!.Methods : target.Enum!.Methods;
    var derives = target.Kind == RKind.Struct ? target.Struct!.Derives : target.Enum!.Derives;
    if (!methods.TryGetValue(call.Name, out var method)) {
      if (call.Name == "clone" && derives.Contains("Clone")) {
        this.Count(call, 0);
        this._typed.Methods[call] = "clone";
        return target;
      }

      foreach (var argument in call.Arguments)
        this.Expression(argument);

      throw this.Invalid($"E0599: no method named `{call.Name}` found for `{target}` in the current scope", call.At);
    }

    if (method.Receiver is null)
      throw this.Invalid($"E0599: no method named `{call.Name}` found: `{target}::{call.Name}` is an associated function, not a method", call.At);

    if (!method.Item.IsVisible && !this._module.IsWithin(method.Module!))
      throw this.Invalid($"E0624: method `{call.Name}` is private", call.At);

    this._typed.Callees[call] = method;
    this.Uses(method.Module);
    var kind = method.Item.Receiver;
    this._deferred.Add(() => {
      var resolved = receiver.Resolved;
      if (kind == ReceiverKind.Exclusive && !(resolved is { Kind: RKind.Reference, Mutable: true } || (resolved.Kind != RKind.Reference && this.MutablePlace(call.Receiver)) || IsTemporary(call.Receiver)))
        throw this.Invalid($"E0596: cannot borrow `{Describe(call.Receiver)}` as mutable, as it is not declared as mutable", call.Receiver.At);

      if (kind == ReceiverKind.Value && resolved.Kind == RKind.Reference && !target.IsCopy)
        throw this.Invalid($"E0507: cannot move out of `{Describe(call.Receiver)}` which is behind a reference", call.Receiver.At);
    });
    this.Count(call, method.Parameters.Count);
    for (var index = 0; index < method.Parameters.Count; ++index)
      this.Coerce(method.Parameters[index], this.Value(call.Arguments[index], method.Parameters[index]), call.Arguments[index].At);

    return method.Result;
  }

  private static bool IsTemporary(Expr expression) => expression is CallExpr or MethodCallExpr or StructExpr or MacroExpr;

  // ---------------------------------------------------------------- arrays, slices and Vec

  private RType SliceMethod(MethodCallExpr call, RType receiver, RType container, RType? expected) {
    var element = container.Element!;
    var name = call.Name;
    var isVec = container.Kind == RKind.Vec;
    if (isVec && name is "push" or "pop" or "clear") {
      this._typed.Methods[call] = "Vec::" + name;
      this.RequireMutable(call, receiver);
      if (name == "push") {
        this.Count(call, 1);
        this.Coerce(element, this.Value(call.Arguments[0], element), call.Arguments[0].At);
        return RType.Unit;
      }

      this.Count(call, 0);
      return name == "pop" ? RType.Option(element) : RType.Unit;
    }

    if (!_sliceMethods.Contains(name)) {
      foreach (var argument in call.Arguments)
        this.Expression(argument);

      return this.Refused(RustFrontend.UnsupportedSliceMethod, name, call.At);
    }

    this._typed.Methods[call] = name;
    switch (name) {
      case "len":
        this.Count(call, 0);
        return RType.Usize;
      case "is_empty":
        this.Count(call, 0);
        return RType.Bool;
      case "iter" or "iter_mut" or "into_iter": {
        this.Count(call, 0);
        var access = name switch {
          "iter" => ElementAccess.Shared,
          "iter_mut" => ElementAccess.Exclusive,
          // into_iter of a reference iterates by reference, of a Vec or an array by value.
          _ => receiver.Resolved.Kind == RKind.Reference ? receiver.Resolved.Mutable ? ElementAccess.Exclusive : ElementAccess.Shared : ElementAccess.Value
        };
        if (access == ElementAccess.Exclusive)
          this.RequireMutable(call, receiver);

        var source = new ElementSource(call.Receiver, element, access);
        return RType.Iterator(new IteratorInfo(SourceKind.Elements, null, source, [], ItemOf(element, access)));
      }
      case "chunks_exact" or "chunks_exact_mut": {
        this.Count(call, 1);
        this.Unify(RType.Usize, this.Value(call.Arguments[0], RType.Usize), call.Arguments[0].At);
        var mutable = name == "chunks_exact_mut";
        if (mutable)
          this.RequireMutable(call, receiver);

        var source = new ElementSource(call.Receiver, element, mutable ? ElementAccess.Exclusive : ElementAccess.Shared) { ChunkSize = call.Arguments[0] };
        return RType.Iterator(new IteratorInfo(SourceKind.Chunks, null, source, [], RType.Reference(RType.Slice(element), mutable)));
      }
      case "swap":
        this.Count(call, 2);
        this.RequireMutable(call, receiver);
        foreach (var argument in call.Arguments)
          this.Unify(RType.Usize, this.Value(argument, RType.Usize), argument.At);

        return RType.Unit;
      case "reverse":
        this.Count(call, 0);
        this.RequireMutable(call, receiver);
        return RType.Unit;
      case "fill":
        this.Count(call, 1);
        this.RequireMutable(call, receiver);
        this.Coerce(element, this.Value(call.Arguments[0], element), call.Arguments[0].At);
        return RType.Unit;
      case "copy_from_slice": {
        this.Count(call, 1);
        this.RequireMutable(call, receiver);
        var source = RType.Reference(RType.Slice(element), false);
        this.Coerce(source, this.Value(call.Arguments[0], source), call.Arguments[0].At);
        return RType.Unit;
      }
      case "copy_within": {
        this.Count(call, 2);
        this.RequireMutable(call, receiver);
        if (call.Arguments[0] is not RangeExpr range)
          return this.Refused(RustFrontend.UnsupportedSliceMethod, "copy_within with a range that is no range expression", call.Arguments[0].At);

        foreach (var bound in new[] { range.Start, range.End })
          if (bound is not null)
            this.Unify(RType.Usize, this.Value(bound, RType.Usize), bound.At);

        this._typed.Types[range] = RType.Unit;
        this.Unify(RType.Usize, this.Value(call.Arguments[1], RType.Usize), call.Arguments[1].At);
        return RType.Unit;
      }
      case "split_at" or "split_at_mut": {
        this.Count(call, 1);
        var mutable = name == "split_at_mut";
        if (mutable)
          this.RequireMutable(call, receiver);

        this.Unify(RType.Usize, this.Value(call.Arguments[0], RType.Usize), call.Arguments[0].At);
        if (!ReferenceEquals(this._splitInitializer, call))
          return this.Refused(RustFrontend.UnsupportedSliceMethod, $"{name} other than as the initializer of a let of two names", call.At);

        var half = RType.Reference(RType.Slice(element), mutable);
        return RType.Tuple([half, half]);
      }
      case "to_vec":
        this.Count(call, 0);
        this.CopyElements(call, element);
        return RType.Vec(element);
      case "clone":
        this.Count(call, 0);
        if (container.Kind == RKind.Slice)
          throw this.Invalid("E0599: the method `clone` exists for `[T]`, but its trait bounds were not satisfied", call.At);

        this.CopyElements(call, element);
        return container;
      case "get": {
        this.Count(call, 1);
        if (call.Arguments[0] is RangeExpr)
          return this.Refused(RustFrontend.UnsupportedSliceMethod, "get with a range", call.Arguments[0].At);

        this.Unify(RType.Usize, this.Value(call.Arguments[0], RType.Usize), call.Arguments[0].At);
        return RType.Option(RType.Reference(element, false));
      }
      default:
        // first, last.
        this.Count(call, 0);
        return RType.Option(RType.Reference(element, false));
    }
  }

  /// <summary>The <c>split_at</c> call a tuple <c>let</c> is being checked for, which alone may make the pair of views.</summary>
  private MethodCallExpr? _splitInitializer;

  /// <summary>The elements a copy makes must be <c>Clone</c>; the subset's are <c>Copy</c> or derive <c>Clone</c>.</summary>
  private void CopyElements(MethodCallExpr call, RType element) {
    this._deferred.Add(() => {
      var resolved = element.Resolved;
      if (resolved.Kind == RKind.Struct && !resolved.Struct!.Derives.Contains("Clone"))
        throw this.Invalid($"E0277: the trait bound `{resolved}: Clone` is not satisfied", call.At);
    });
  }

  /// <summary>A method that writes the elements needs a mutable place or a <c>&amp;mut</c> receiver.</summary>
  private void RequireMutable(MethodCallExpr call, RType receiver) {
    this._deferred.Add(() => {
      var resolved = receiver.Resolved;
      var ok = resolved.Kind == RKind.Reference ? resolved.Mutable : this.MutablePlace(call.Receiver) || IsTemporary(call.Receiver) || call.Receiver is IndexExpr { Index: RangeExpr } && this.ThroughMutable(((IndexExpr)call.Receiver).Target);
      if (!ok)
        throw this.Invalid($"E0596: cannot borrow `{Describe(call.Receiver)}` as mutable, as it is not declared as mutable", call.Receiver.At);
    });
  }

  /// <summary>The item type of a source of elements.</summary>
  private static RType ItemOf(RType element, ElementAccess access) => access switch {
    ElementAccess.Shared => RType.Reference(element, false),
    ElementAccess.Exclusive => RType.Reference(element, true),
    _ => element
  };

  // ---------------------------------------------------------------- iterators

  /// <summary>An integer range as an iterator source.</summary>
  private IteratorInfo RangeIterator(RangeExpr range, Expr owner) {
    if (range.Start is null || range.End is null) {
      this.Refuse(new Refusal(RustFrontend.UnsupportedIterable, range.Start is null ? ".." : "a..", range.At));
      throw new CheckAbort();
    }

    var element = this.Value(range.Start);
    this.Unify(element, this.Value(range.End), range.End.At);
    this._typed.Types[range] = RType.Unit;
    this._deferred.Add(() => {
      if (element.Resolved.Kind != RKind.Integer && !this.IsError(element))
        throw this.Invalid($"E0277: the trait bound `{element}: Step` is not satisfied", range.At);
    });
    var loop = new RangeLoop(range.Start, range.End, range.Inclusive, false, null, false);
    return new IteratorInfo(SourceKind.Range, loop, null, [], element) { RangeElement = element };
  }

  /// <summary>An adapter or a consuming method of an iterator.</summary>
  private RType IteratorMethod(MethodCallExpr call, IteratorInfo iterator, RType? expected) {
    var name = call.Name;
    if (!_iteratorMethods.Contains(name)) {
      foreach (var argument in call.Arguments)
        this.Expression(argument);

      return this.Refused(RustFrontend.UnsupportedIteratorMethod, name, call.At);
    }

    var item = iterator.Item;
    switch (name) {
      case "rev" or "step_by": {
        this.Count(call, name == "rev" ? 0 : 1);
        if (name == "step_by")
          this.Unify(RType.Usize, this.Value(call.Arguments[0], RType.Usize), call.Arguments[0].At);

        // rev() after map and filter: they keep the order and run item by item, so reversing the
        // positions before them is the same sequence with their functions called in the same order
        // as Rust's next_back calls them.
        var streamed = iterator.Adapters.ToList().FindIndex(adapter => adapter.Name is "map" or "filter");
        if (name == "rev" && streamed >= 0 && iterator.Adapters.Skip(streamed).All(adapter => adapter.Name is "map" or "filter")) {
          var before = iterator with { Adapters = [.. iterator.Adapters.Take(streamed)] };
          if (before.Adapters.Any(adapter => adapter.Name == "rev") || before.Kind == SourceKind.Range && before.Adapters.Any(adapter => adapter.Name != "step_by"))
            return this.Refused(RustFrontend.UnsupportedIteratorMethod, "rev after rev", call.At);

          if (before.Kind == SourceKind.Range)
            before = before with { Range = this.RangeAdapter(call, before) };

          return RType.Iterator(iterator with { Range = before.Range, Adapters = [.. before.Adapters, new Adapter(name, call, null, null), .. iterator.Adapters.Skip(streamed)] });
        }

        if (!iterator.Positional || iterator.Adapters.Any(adapter => adapter.Name == name)
            || (iterator.Kind == SourceKind.Range && iterator.Adapters.Any(adapter => adapter.Name is not ("rev" or "step_by"))))
          return this.Refused(RustFrontend.UnsupportedIteratorMethod, $"{name} after {string.Join(", ", iterator.Adapters.Select(adapter => adapter.Name))}", call.At);

        if (iterator.Kind == SourceKind.Range)
          iterator = iterator with { Range = this.RangeAdapter(call, iterator) };

        return Extend(iterator, new Adapter(name, call, null, null), item);
      }
      case "zip": {
        this.Count(call, 1);
        if (!iterator.Positional || iterator.Kind == SourceKind.Range)
          return this.Refused(RustFrontend.UnsupportedIteratorMethod, "zip of a range or after map or filter", call.At);

        var other = this.ZipSource(call.Arguments[0]);
        if (other is null)
          return this.Refused(RustFrontend.UnsupportedIteratorMethod, "zip with anything but a slice, an array or a Vec, or their iter() or iter_mut()", call.Arguments[0].At);

        return Extend(iterator, new Adapter(name, call, other, null), RType.Tuple([item, ItemOf(other.Element, other.Access)]));
      }
      case "enumerate":
        this.Count(call, 0);
        return Extend(iterator, new Adapter(name, call, null, null), RType.Tuple([RType.Usize, item]));
      case "copied" or "cloned": {
        this.Count(call, 0);
        if (item.Resolved.Kind != RKind.Reference)
          throw this.Invalid($"E0271: expected an iterator of references for `{name}`, found items of `{item}`", call.At);

        var value = item.Resolved.Element!;
        if (name == "copied")
          this._deferred.Add(() => {
            if (!value.IsCopy && !this.IsError(value))
              throw this.Invalid($"E0277: the trait bound `{value}: Copy` is not satisfied", call.At);
          });

        return Extend(iterator, new Adapter(name, call, null, null), value);
      }
      case "map": {
        this.Count(call, 1);
        var result = this.Fresh(VariableKind.Any);
        this.FunctionArgument(call.Arguments[0], [item], result);
        return Extend(iterator, new Adapter(name, call, null, call.Arguments[0]), result);
      }
      case "filter":
        this.Count(call, 1);
        this.FunctionArgument(call.Arguments[0], [RType.Reference(item, false)], RType.Bool);
        return Extend(iterator, new Adapter(name, call, null, call.Arguments[0]), item);
    }

    // The consuming methods.
    RType type;
    switch (name) {
      case "sum" or "product": {
        this.Count(call, 0);
        // Sum<T> and Sum<&T> for T: the result is the item's number type.
        type = StripShared(item);
        if (call.Generics.Count == 1 && this.Resolve(call.Generics[0], false) is { } generic)
          this.Unify(generic, type, call.At);

        this._deferred.Add(() => {
          var resolved = type.Resolved;
          if (resolved.Kind is not (RKind.Integer or RKind.Float) && !this.IsError(resolved))
            throw this.Invalid($"E0277: a value of type `{resolved}` cannot be made by summing an iterator", call.At);
        });
        break;
      }
      case "count":
        this.Count(call, 0);
        type = RType.Usize;
        break;
      case "fold": {
        this.Count(call, 2);
        type = this.Value(call.Arguments[0], expected);
        this.FunctionArgument(call.Arguments[1], [type, item], type);
        break;
      }
      case "any" or "all":
        this.Count(call, 1);
        type = RType.Bool;
        this.FunctionArgument(call.Arguments[0], [item], RType.Bool);
        break;
      default:
        this.Count(call, 1);
        type = RType.Unit;
        this.FunctionArgument(call.Arguments[0], [item], RType.Unit);
        break;
    }

    var function = name is "fold" ? call.Arguments[1] : name is "any" or "all" or "for_each" ? call.Arguments[0] : null;
    this._typed.Iterations[call] = new Iteration(iterator.Kind, iterator.Range, iterator.Source, iterator.Adapters, item) {
      Terminal = name, TerminalFunction = function, TerminalCall = call, RangeElement = iterator.RangeElement
    };
    return type;
  }

  private static RType Extend(IteratorInfo iterator, Adapter adapter, RType item)
    => RType.Iterator(iterator with { Adapters = [.. iterator.Adapters, adapter], Item = item });

  /// <summary>Records <c>rev()</c> or <c>step_by(n)</c> of a range in its <see cref="RangeLoop"/>.</summary>
  private RangeLoop RangeAdapter(MethodCallExpr call, IteratorInfo iterator) {
    var range = iterator.Range!;
    if (call.Name == "rev") {
      var stepFirst = range.Step is not null;
      if (stepFirst) {
        var element = iterator.RangeElement!;
        // Reversing a StepBy needs an ExactSizeIterator: Range of the types up to 32 bits and pointer width, RangeInclusive of 8 and 16 bits.
        this._deferred.Add(() => {
          var type = element.Resolved;
          var exact = range.Inclusive ? type.Bits <= 16 && type.Name is not ("isize" or "usize") : type.Bits <= 32 || type.Name is "isize" or "usize";
          if (!exact)
            throw this.Invalid($"E0277: the trait bound `{(range.Inclusive ? "RangeInclusive" : "Range")}<{type}>: ExactSizeIterator` is not satisfied", call.At);
        });
      }

      return range with { Reversed = true, StepBeforeReverse = stepFirst };
    }

    return range with { Step = call.Arguments[0] };
  }

  /// <summary>The other side of a <c>zip</c>: a container, a reference to one, or its <c>iter()</c> / <c>iter_mut()</c>.</summary>
  private ElementSource? ZipSource(Expr argument) {
    var type = this.Value(argument);
    var resolved = type.Resolved;
    if (resolved.Kind == RKind.Iterator) {
      var info = resolved.IteratorInfo!;
      return info is { Kind: SourceKind.Elements, Adapters.Count: 0 } ? info.Source : null;
    }

    var target = resolved.Dereferenced;
    if (target.Kind is not (RKind.Array or RKind.Slice or RKind.Vec))
      return null;

    var access = resolved.Kind == RKind.Reference ? resolved.Mutable ? ElementAccess.Exclusive : ElementAccess.Shared : ElementAccess.Value;
    return argument is ReferenceExpr reference ? new ElementSource(reference.Operand, target.Element!, access) : new ElementSource(argument, target.Element!, access);
  }

  /// <summary>
  /// The function an iterator method calls: a closure written there, a closure variable or a function
  /// of the crate, of the parameter and result types the method gives.
  /// </summary>
  private void FunctionArgument(Expr argument, IReadOnlyList<RType> parameters, RType result) {
    var signature = RType.Function(parameters, result);
    var saved = this._closureArgument;
    this._closureArgument = true;
    RType type;
    try {
      type = this.Value(argument, signature);
    } finally {
      this._closureArgument = saved;
    }

    var resolved = type.Resolved;
    if (resolved.Kind is RKind.Closure or RKind.Function) {
      if (resolved.Elements.Count != parameters.Count)
        throw this.Invalid($"E0593: closure is expected to take {parameters.Count} argument{(parameters.Count == 1 ? "" : "s")}, but it takes {resolved.Elements.Count}", argument.At);

      for (var index = 0; index < parameters.Count; ++index)
        this.Unify(resolved.Elements[index], parameters[index], argument.At);

      this.Unify(result, resolved.Element!, argument.At);
      if (argument is not (ClosureExpr or PathExpr))
        this.Refuse(new Refusal(RustFrontend.EscapingClosure, "function computed by an expression", argument.At));

      return;
    }

    if (!this.IsError(resolved))
      throw this.Invalid($"E0277: expected a `FnMut` closure, found `{type}`", argument.At);
  }

}
