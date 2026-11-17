using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Rust.Emit;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>The regions of the pointers of the function being written.</summary>
    private PointerRegions _pointers = PointerRegions.Of(new IrFunction("", false, IrType.Void, [], [], new IrBlock([])));

    /// <summary>The Rust name of the array of a <c>stackalloc</c> and of the slice a pointer parameter brings, by the region's owner.</summary>
    private readonly Dictionary<IrLocal, string> _regionNames = [];

    /// <summary>
    /// The pointers of a function before its body is written: their regions, each problem refused, the arrays of
    /// <c>stackalloc</c> and the slices of pointer parameters named, and a local whose address is written
    /// through made <c>let mut</c> (a parameter bound again).
    /// </summary>
    private void PointerFacts(IrFunction function) {
      this._pointers = PointerRegions.Of(function);
      foreach (var problem in this._pointers.Problems)
        this.Refuse(problem);

      this._regionNames.Clear();
      foreach (var region in this._pointers.Regions.Values.Where(region => region.Kind is RegionKind.Stack or RegionKind.Parameter).Distinct()) {
        var name = this.Temporary(this._locals[region.Owner].TrimStart('_') + (region.Kind == RegionKind.Stack ? "_memory" : "_region"));
        this._taken.Add(name);
        this._regionNames[region.Owner] = name;
      }

      // A variable Rust never names: a pinned array no pointer reads, a pointer only compared with null.
      var nodes = ArrayOwnership.Statements(function.Body).SelectMany(IrTree.Expressions).SelectMany(IrTree.Nodes).ToList();
      var pinned = nodes.OfType<IrIntrinsic>().Where(node => node.Op == IrIntrinsicOp.Pin).Select(pin => pin.Arguments[0]).OfType<IrLocalRef>().ToHashSet(ReferenceEqualityComparer.Instance);
      var nulled = nodes.OfType<IrIntrinsic>().Where(node => IrPointers.IsComparison(node.Op) && node.Arguments.Any(argument => argument is IrNull))
        .SelectMany(comparison => comparison.Arguments).OfType<IrLocalRef>().ToHashSet(ReferenceEqualityComparer.Instance);
      foreach (var region in this._pointers.Regions.Values.Where(region => region.Kind == RegionKind.Variable).Distinct())
        if (!this._pointers.Accessed.Concat(this._pointers.Compared).Any(accessed => PointerRegions.Same(accessed, region)) && nodes.OfType<IrLocalRef>().All(reference => reference.Local != region.Owner || pinned.Contains(reference)))
          this.Unnamed(region.Owner);
      foreach (var pointer in this._pointers.Regions.Keys.Where(pointer => !function.Parameters.Contains(pointer)))
        if (nodes.OfType<IrLocalRef>().All(reference => reference.Local != pointer || nulled.Contains(reference)))
          this.Unnamed(pointer);

      foreach (var region in this._pointers.Written.Where(region => region.Kind == RegionKind.Address)) {
        if (function.Parameters.Contains(region.Owner))
          this._reassigned.Add(region.Owner);
        else if (ArrayOwnership.Statements(function.Body).OfType<IrDeclare>().FirstOrDefault(declare => declare.Local == region.Owner) is { } declaration)
          this._mutable.Add(declaration);
      }
    }

    /// <summary>The number of elements of a new array a pin makes its region of, where it is a constant.</summary>
    private static long? KnownLength(IrExpr made) => made switch {
      IrNewArray { Length: IrConst { Value: >= 0 and < 4096 } count } => count.Value,
      IrIntrinsic { Op: IrIntrinsicOp.ArrayOf } literal => literal.Arguments.Count,
      _ => null
    };

    /// <summary>The constant length of the new array the declaration of a pointer pins, or <see langword="null"/> where it is known at run time only.</summary>
    private long? MadeLength(IrLocal pointer)
      => KnownLength(((IrIntrinsic)ArrayOwnership.Statements(this._function.Body).OfType<IrDeclare>().First(declare => declare.Local == pointer).Initializer!).Arguments[0]);

    /// <summary>An array of a constant number of zeros of a type, <c>[0i64; 2]</c>: memory that lives where the function does.</summary>
    private RsExpression Zeros(IrType element, long count)
      => new RsArray([Suffixed(Literal(new IrConst(element, 0)))]) { Count = new RsLiteral(count.ToString(CultureInfo.InvariantCulture)) { Type = IrType.U64 } };

    /// <summary>Gives a variable Rust never reads the leading underscore rustc does not warn about.</summary>
    private void Unnamed(IrLocal local) {
      if (!this._locals[local].StartsWith('_'))
        this._locals[local] = "_" + this._locals[local];
    }

    /// <summary>The Rust parameters of a pointer parameter: the slice of its region, <c>&amp;mut</c> where the function writes through it, and its position.</summary>
    private IEnumerable<RsParameter> PointerParameters(IrLocal parameter) {
      if (!this._pointers.Regions.TryGetValue(parameter, out var region)) {
        // A pointer parameter with no region is refused; its stand-in keeps the signature whole.
        yield return new RsParameter(this._locals[parameter], "i64");
        yield break;
      }

      var written = this._pointers.Written.Any(candidate => PointerRegions.Same(candidate, region)) || this._arrayFacts.MutablePointers.Contains(this._function.Parameters.ToList().IndexOf(parameter));
      yield return new RsParameter(this._regionNames[parameter], $"&{(written ? "mut " : "")}[{this.ElementType(IrType.Array(region.Element))}]");
      yield return new RsParameter(this._locals[parameter], "i64");
    }

    /// <summary>
    /// The declaration of a pointer variable: its position in its region as an <c>i64</c>, 0 for a pin of a whole
    /// variable and for a <c>stackalloc</c>, whose array of zeros is declared first - an array for a constant
    /// length, a vector otherwise, a negative length trapping with <c>IntegerOverflow</c>; the element of a pin of
    /// one element checked by <c>ply_pointer</c>. A pointer to the address of a local has no variable: it stands
    /// for the local.
    /// </summary>
    private void PointerDeclaration(IrDeclare declare, List<RsStatement> into) {
      if (!this._pointers.Regions.TryGetValue(declare.Local, out var region) || region.Kind == RegionKind.Address)
        return;

      RsExpression position;
      switch (declare.Initializer) {
        case IrIntrinsic { Op: IrIntrinsicOp.Pin, Arguments: [IrLocalRef pinned] } when this.IsNullableArray(pinned.Local):
          position = this.Refuse("a pin of an array that may be null");
          break;
        case IrIntrinsic { Op: IrIntrinsicOp.Pin, Arguments: [var made] } when region.Kind == RegionKind.Stack:
          // A new array of a known length is an array, not a vector (clippy's useless_vec); one nothing reads is left out.
          if (!this._pointers.Accessed.Contains(region) && KnownLength(made) is not null) {
            position = new RsLiteral("0") { Type = IrType.I64 };
            break;
          }

          into.Add(new RsLet(this._regionNames[region.Owner], this._pointers.Written.Contains(region), null, made switch {
            IrNewArray { Length: IrConst { Value: >= 0 and < 4096 } known } => this.Zeros(region.Element, known.Value),
            IrIntrinsic { Op: IrIntrinsicOp.ArrayOf } literal => new RsArray([.. literal.Arguments.Select(argument => Suffixed(this.Expression(argument)))]),
            _ => this.Vector(made)
          }));
          position = new RsLiteral("0") { Type = IrType.I64 };
          break;
        case IrIntrinsic { Op: IrIntrinsicOp.Pin, Arguments: [_] }:
          position = new RsLiteral("0") { Type = IrType.I64 };
          break;
        case IrIntrinsic { Op: IrIntrinsicOp.Pin, Arguments: [_, var at] }:
          position = new RsCast(new RsField(this.PointerRange(this.Widened(at), 1, this.RegionSlice(region, false), false), "start"), "i64") { Type = IrType.I64 };
          break;
        case IrIntrinsic { Op: IrIntrinsicOp.StackAlloc, Arguments: [var length] }:
          var memory = length is IrConst { Value: >= 0 and < 4096 } count ? this.Zeros(region.Element, count.Value)
            : new RsMacro("vec", [new RsArray([Suffixed(Literal(new IrConst(region.Element, 0)))]) { Count = this.Length(length) }]) { Bracketed = true };
          into.Add(new RsLet(this._regionNames[region.Owner], this._pointers.Written.Contains(region), null, memory));
          position = new RsLiteral("0") { Type = IrType.I64 };
          break;
        default:
          position = this.PointerIndex(declare.Initializer!);
          break;
      }

      into.Add(new RsLet(this._locals[declare.Local], this._mutable.Contains(declare), "i64", position));
    }

    /// <summary>
    /// An operation on pointers: a load and a store through one, arithmetic and its difference, a comparison.
    /// A pointer as a value is its position in its region; its region is known statically.
    /// </summary>
    private RsExpression PointerOperation(IrIntrinsic operation) {
      var arguments = operation.Arguments;
      switch (operation.Op) {
        case IrIntrinsicOp.PointerLoad:
          return this.PointerRead(arguments[0], operation.Type);
        case IrIntrinsicOp.PointerStore:
          return this.PointerWrite(arguments[0], arguments[1]);
        case IrIntrinsicOp.PointerDifference: {
          var scale = this.Scale(arguments[0]);
          this.SameRegion(arguments[0], arguments[1], "difference");
          var difference = new RsMethodCall(this.Operand(this.PointerIndex(arguments[0]), 13), "wrapping_sub", [this.PointerIndex(arguments[1])]) { Type = IrType.I64 };
          return scale == 1 ? difference : this.Binary(difference, "/", Literal(new IrConst(IrType.I64, scale)), IrType.I64);
        }
        case var comparison when IrPointers.IsComparison(comparison):
          return this.PointerComparison(operation);
        default:
          // PointerAdd, PointerCast; any other operation as a value, which PointerIndex refuses by its form.
          return this.PointerIndex(operation);
      }
    }

    /// <summary>
    /// A comparison of two pointers: of the positions in one region; with the null pointer the emptiness of a
    /// region that is null when it is empty, <c>false</c> of any other. Pointers into two regions are refused:
    /// their order is no value, and safe Rust has no address to compare.
    /// </summary>
    private RsExpression PointerComparison(IrIntrinsic comparison) {
      var (left, right) = (comparison.Arguments[0], comparison.Arguments[1]);
      var op = comparison.Op switch {
        IrIntrinsicOp.PointerEqual => "==",
        IrIntrinsicOp.PointerNotEqual => "!=",
        IrIntrinsicOp.PointerLess => "<",
        IrIntrinsicOp.PointerLessOrEqual => "<=",
        IrIntrinsicOp.PointerGreater => ">",
        _ => ">="
      };
      if (left is IrNull || right is IrNull) {
        var other = left is IrNull ? right : left;
        if (op is not ("==" or "!="))
          return this.Refuse("an ordering of a pointer and the null pointer");

        if (this._pointers.Of(other) is not { } region)
          return this.Refuse($"a comparison of a pointer of the form {other.GetType().Name[2..]} with the null pointer");
        // A region known to be empty is the null pointer, one known to have elements never is.
        // A Stack region that may be null is a pin of a new array.
        var known = region is { Kind: RegionKind.Stack, Nullable: true } ? this.MadeLength(region.Owner) : null;
        if (!region.Nullable || known is not null)
          return new RsLiteral((op == "==") == (region.Nullable && known == 0) ? "true" : "false") { Type = IrType.Bool };

        var empty = new RsMethodCall(this.RegionSlice(region, false), "is_empty", []) { Type = IrType.Bool };
        return op == "==" ? empty : new RsUnary("!", empty) { Type = IrType.Bool };
      }

      this.SameRegion(left, right, "comparison");
      return this.Binary(this.PointerIndex(left), op, this.PointerIndex(right), IrType.Bool);
    }

    /// <summary>Refuses two pointers into regions that are not one.</summary>
    private void SameRegion(IrExpr first, IrExpr second, string what) {
      if (this._pointers.Of(first) is { } a && this._pointers.Of(second) is { } b && !PointerRegions.Same(a, b))
        this.Refuse($"a pointer {what} across two regions");
    }

    /// <summary>
    /// The position a pointer expression stands at, in elements of its region, as an <c>i64</c>: a variable as it
    /// is, an addition scaled from the pointer's elements to the region's (<c>p.wrapping_add(i64::from(n).wrapping_mul(4))</c>
    /// for an <c>u32*</c> into bytes), a cast as its operand.
    /// </summary>
    private RsExpression PointerIndex(IrExpr pointer) {
      switch (pointer) {
        case IrLocalRef reference:
          return new RsPath(this._locals[reference.Local]) { Type = IrType.I64 };
        case IrIntrinsic { Op: IrIntrinsicOp.PointerCast, Arguments: [var inner] }:
          return this.PointerIndex(inner);
        case IrIntrinsic { Op: IrIntrinsicOp.PointerAdd, Arguments: [var inner, var offset] }:
          var scale = this.Scale(inner);
          var step = this.Widened(offset);
          if (scale != 1)
            step = new RsMethodCall(this.Operand(step, 13), "wrapping_mul", [Literal(new IrConst(IrType.I64, scale))]) { Type = IrType.I64 };
          return new RsMethodCall(this.Operand(this.PointerIndex(inner), 13), "wrapping_add", [step]) { Type = IrType.I64 };
        default:
          return this.Refuse($"a pointer of the form {pointer.GetType().Name[2..]}");
      }
    }

    /// <summary>How many elements of its region one element of a pointer spans: 1, or the width of a wider number over bytes.</summary>
    private long Scale(IrExpr pointer) {
      var region = this._pointers.Of(pointer);
      var element = pointer.Type.Element;
      if (region is null || element is null || !element.IsNumeric || Width(element) == Width(region.Element))
        return 1;
      if (region.Element == IrType.U8)
        return Width(element);

      this.Refuse($"a pointer of {element} into the memory of {region.Element}");
      return 1;
    }

    /// <summary>An integer offset as an <c>i64</c>.</summary>
    private RsExpression Widened(IrExpr offset)
      => offset is IrConst constant ? new RsLiteral(IrOperators.Number(offset.Type, constant.Value).ToString(CultureInfo.InvariantCulture)) { Type = IrType.I64 }
        : offset.Type == IrType.I64 ? this.Expression(offset)
        : offset.Type == IrType.U64 ? this.Cast(this.Expression(offset), IrType.I64)
        : new RsCall(new RsPath("i64::from"), [this.Expression(offset)]) { Type = IrType.I64 };

    /// <summary>The slice a region is: the pinned variable, the array of a <c>stackalloc</c>, the slice of a pointer parameter.</summary>
    private RsExpression RegionSlice(PointerRegion region, bool borrowed, bool mutable = false) {
      if (region.Kind != RegionKind.Variable)
        return borrowed && region.Kind == RegionKind.Stack ? new RsReference(mutable, new RsPath(this._regionNames[region.Owner])) : new RsPath(this._regionNames[region.Owner]) { Type = IrType.Array(region.Element) };

      var variable = new IrLocalRef(region.Owner);
      return !borrowed ? new RsPath(this._locals[region.Owner]) { Type = region.Owner.Type }
        : region.Owner.Type.IsView ? this.ViewSource(variable, mutable) : this.Borrow(variable, mutable);
    }

    /// <summary>
    /// The element a pointer reads as a place of its region: <c>data[p as usize]</c>, or through <c>ply_pointer</c>
    /// where the region is the null pointer when it is empty.
    /// </summary>
    private RsExpression Element(PointerRegion region, RsExpression index) {
      var slice = this.RegionSlice(region, false);
      var at = region.Nullable ? (RsExpression)new RsField(this.PointerRange(index, 1, slice, true), "start") : new RsCast(this.Operand(index, 12), "usize") { Type = IrType.U64 };
      return new RsIndex(slice, at) { Type = region.Element };
    }

    /// <summary>
    /// <c>ply_pointer(at, width, size, nullable)</c>: the range of <c>width</c> elements at a position of a region of
    /// <c>size</c> elements, which panics with <c>NullReference</c> for an empty region that is the null pointer and
    /// with <c>IndexOutOfRange</c> unless the range lies inside.
    /// </summary>
    private RsExpression PointerRange(RsExpression at, int width, RsExpression slice, bool nullable) {
      const string name = "ply_pointer";
      this.Spell(name, () => {
        var (start, size, count) = (new RsPath("start"), new RsPath("size"), new RsPath("width"));
        var outside = new RsBinary(new RsBinary(count, ">", size), "||", new RsBinary(start, ">", new RsBinary(size, "-", count)));
        return new RsFunction(name, "", [new RsParameter("at", "i64"), new RsParameter("width", "usize"), new RsParameter("size", "usize"), new RsParameter("nullable", "bool")], "std::ops::Range<usize>", new RsBlock([
          new RsExpressionStatement(new RsIf(new RsBinary(new RsPath("nullable"), "&&", new RsBinary(size, "==", new RsLiteral("0"))), new RsBlock([new RsExpressionStatement(Panic(IrTrap.NullReference))], null), null)),
          new RsLet("start", false, null, Expect(new RsCall(new RsPath("usize::try_from"), [new RsPath("at")]), IrTrap.IndexOutOfRange)),
          new RsExpressionStatement(new RsIf(outside, new RsBlock([new RsExpressionStatement(Panic(IrTrap.IndexOutOfRange))], null), null))
        ], new RsRange(start, new RsBinary(start, "+", count), false)));
      });
      return new RsCall(new RsPath(name), [at, new RsLiteral(width.ToString(CultureInfo.InvariantCulture)), new RsMethodCall(slice, "len", []), new RsLiteral(nullable ? "true" : "false")]) { Type = IrType.U64 };
    }

    /// <summary>
    /// A load through a pointer: the element of its region where the pointer reads the region's own type, the
    /// bytes of a wider number from a region of bytes (<c>u32::from_le_bytes(d[ply_pointer(…)].try_into().expect("4 bytes"))</c>,
    /// the lowest byte first on every host, as the IR reads them), the bits of an element of the same width
    /// (<c>to_bits</c>, <c>from_bits</c>, <c>as</c>); through the address of a local the local itself.
    /// </summary>
    private RsExpression PointerRead(IrExpr pointer, IrType type) {
      if (this._pointers.Of(pointer) is not { } region)
        return this.Refuse("a load through the null pointer");

      if (region.Kind == RegionKind.Address)
        return Reinterpreted(new RsPath(this._locals[region.Owner]) { Type = region.Owner.Type }, region.Owner.Type, type);

      var index = this.PointerIndex(pointer);
      if (Width(type) == Width(region.Element))
        return Reinterpreted(this.Element(region, index), region.Element, type);
      if (region.Element != IrType.U8)
        return this.Refuse($"a pointer of {type} into the memory of {region.Element}");

      var bytes = new RsIndex(this.RegionSlice(region, false), this.PointerRange(index, Width(type), this.RegionSlice(region, false), region.Nullable));
      var array = new RsMethodCall(new RsMethodCall(bytes, "try_into", []), "expect", [new RsString($"{Width(type)} bytes")]);
      return new RsCall(new RsPath(TypeName(type) + "::from_le_bytes"), [array]) { Type = type };
    }

    /// <summary>A store through a pointer, the forms of <see cref="PointerRead"/>; the value is evaluated before the place is checked, as the IR does.</summary>
    private RsExpression PointerWrite(IrExpr pointer, IrExpr value) {
      if (this._pointers.Of(pointer) is not { } region)
        return this.Refuse("a store through the null pointer");

      var type = value.Type;
      if (region.Kind == RegionKind.Address)
        return new RsAssign(new RsPath(this._locals[region.Owner]) { Type = region.Owner.Type }, "=", Reinterpreted(this.Expression(value), type, region.Owner.Type));

      var index = this.PointerIndex(pointer);
      if (Width(type) == Width(region.Element)) {
        // Rust evaluates the value of an assignment before its place: the IR's order.
        return new RsAssign(this.Element(region, index), "=", Reinterpreted(this.Expression(value), type, region.Element));
      }

      if (region.Element != IrType.U8)
        return this.Refuse($"a pointer of {type} into the memory of {region.Element}");

      // The value, then its place, bound before the slice is borrowed exclusively to be written.
      var name = this.Temporary("value");
      var at = this.Temporary("at");
      return new RsBlockExpression(new RsBlock([
        new RsLet(name, false, null, this.Expression(value)),
        new RsLet(at, false, null, this.PointerRange(index, Width(type), this.RegionSlice(region, false), region.Nullable)),
        new RsExpressionStatement(new RsMethodCall(new RsIndex(this.RegionSlice(region, false), new RsPath(at)), "copy_from_slice", [new RsReference(false, new RsMethodCall(new RsPath(name), "to_le_bytes", []))]))
      ], null));
    }

    /// <summary>
    /// The bits of a number as another of the same width: <c>as</c> between integers, <c>to_bits</c> and
    /// <c>from_bits</c> to and from floating point; the value itself for the same type.
    /// </summary>
    private static RsExpression Reinterpreted(RsExpression value, IrType from, IrType to) {
      if (from == to)
        return value;

      var bits = from.IsFloat ? new RsMethodCall(Operand13(value), "to_bits", []) { Type = from == IrType.F32 ? IrType.U32 : IrType.U64 } : value;
      var unsigned = Width(to) == 4 ? IrType.U32 : Width(to) == 8 ? IrType.U64 : null;
      if (to.IsFloat) {
        // A literal takes the type from_bits wants: a cast of it would be clippy's unnecessary_cast.
        var raw = bits.Type == unsigned || bits is RsLiteral { Text: [not '-', ..] } ? bits : new RsCast(Operand12(bits), TypeName(unsigned!)!) { Type = unsigned };
        return new RsCall(new RsPath(TypeName(to) + "::from_bits"), [raw]) { Type = to };
      }

      return bits.Type == to ? bits : new RsCast(Operand12(bits), TypeName(to)!) { Type = to };
    }

    private static RsExpression Operand13(RsExpression value) => value is RsPath or RsIndex or RsMethodCall or RsCall or RsLiteral ? value : new RsParenthesized(value);

    private static RsExpression Operand12(RsExpression value) => value is RsPath or RsIndex or RsMethodCall or RsCall or RsLiteral or RsField ? value : new RsParenthesized(value);

    /// <summary>A pointer passed to a function: the slice of its region and its position.</summary>
    private IEnumerable<RsExpression> PointerArgument(IrExpr pointer, bool written) {
      var region = this._pointers.Of(pointer);
      if (region is null or { Kind: RegionKind.Address }) {
        yield return this.Refuse(region is null ? "the null pointer passed to a function" : "the address of a local passed to a function");
        yield break;
      }

      yield return region.Kind == RegionKind.Parameter ? this.RegionSlice(region, false) : this.RegionSlice(region, true, written);
      yield return this.PointerIndex(pointer);
    }

  }

}
