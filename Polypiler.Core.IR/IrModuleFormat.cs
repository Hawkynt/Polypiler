using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Polypiler.Core.IR;

/// <summary>
/// A module of the IR as bytes, and back. What depends only on the build - the portable
/// implementations, imported from their source - is written once when the build runs and read when a
/// translation needs it, instead of being imported again on every run.
/// <para>
/// The bytes keep the module exactly: every node, every type and every name, and also which nodes
/// are one and the same object - a local that a parameter list, a declaration and every read share,
/// a constant that its readers share with the list of the module, a struct and its fields. A SHA-256
/// of the content guards it: damaged bytes are refused, never read as another module.
/// </para>
/// </summary>
public static class IrModuleFormat {

  /// <summary>The version of the layout; bytes of another version are refused.</summary>
  public const int Version = 1;

  private static readonly byte[] _magic = "PLYIRMOD"u8.ToArray();

  private const int HashSize = 32;

  private enum Tag : byte {
    Null, Shared,
    Const, ConstRef, LocalRef, Unary, Binary, Convert, Conditional, ArrayLoad, ArrayLength, NewArray, Call, Step,
    StructNew, StructDefault, FieldLoad, StructEquals, Intrinsic,
    Block, Declare, Assign, Store, FieldStore, ExprStmt, If, While, DoWhile, For, Switch, Break, Continue, Return
  }

  /// <summary>Writes a module as bytes.</summary>
  /// <param name="module">The module; every declared type it names is one of its <see cref="IrModule.Types"/>, and every constant it reads one of its <see cref="IrModule.Constants"/>.</param>
  /// <returns>The bytes, which <see cref="Read"/> turns back into an equal module.</returns>
  /// <exception cref="ArgumentException">The module names a struct, a field or a constant that is not its own.</exception>
  public static byte[] Write(IrModule module) {
    using var content = new MemoryStream();
    using (var writer = new BinaryWriter(content, Encoding.UTF8, true))
      new Writer(writer, module).Module();

    var payload = content.ToArray();
    using var result = new MemoryStream();
    using (var writer = new BinaryWriter(result, Encoding.UTF8, true)) {
      writer.Write(_magic);
      writer.Write(Version);
      writer.Write(SHA256.HashData(payload));
      writer.Write(payload.Length);
      writer.Write(payload);
    }

    return result.ToArray();
  }

  /// <summary>Reads a module that <see cref="Write"/> wrote.</summary>
  /// <param name="bytes">The bytes.</param>
  /// <returns>The module.</returns>
  /// <exception cref="InvalidDataException">The bytes are not a module of this version, or they are damaged; the message says how.</exception>
  public static IrModule Read(ReadOnlySpan<byte> bytes) {
    var header = _magic.Length + sizeof(int) + HashSize + sizeof(int);
    if (bytes.Length < header || !bytes[.._magic.Length].SequenceEqual(_magic))
      throw new InvalidDataException("The bytes are no IR module.");

    var version = BinaryPrimitives.ReadInt32LittleEndian(bytes[_magic.Length..]);
    if (version != Version)
      throw new InvalidDataException($"The IR module has version {version}; this build reads version {Version}.");

    var hash = bytes.Slice(_magic.Length + sizeof(int), HashSize);
    var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[(header - sizeof(int))..]);
    var payload = bytes[header..];
    if (length != payload.Length || !SHA256.HashData(payload).AsSpan().SequenceEqual(hash))
      throw new InvalidDataException("The IR module is damaged: its content does not match its checksum.");

    using var reader = new BinaryReader(new MemoryStream(payload.ToArray()), Encoding.UTF8);
    try {
      var module = new Reader(reader).Module();
      if (reader.BaseStream.Position != payload.Length)
        throw new InvalidDataException("The IR module has bytes after its end.");

      return module;
    } catch (Exception exception) when (exception is IOException or FormatException or InvalidOperationException) {
      throw new InvalidDataException($"The IR module is malformed: {exception.Message}", exception);
    }
  }

  /// <summary>Writes the parts of a module in the order <see cref="Reader"/> reads them.</summary>
  private sealed class Writer(BinaryWriter writer, IrModule module) {

    private readonly Dictionary<IrTypeDeclaration, int> _declarations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrField, (int Declaration, int Index)> _fields = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrModuleConst, int> _constants = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrLocal, int> _locals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, int> _nodes = new(ReferenceEqualityComparer.Instance);

    public void Module() {
      writer.Write(module.Name);
      this.Count(module.Types.Count);
      foreach (var declaration in module.Types) {
        if (declaration is not IrStructType structure)
          throw new ArgumentException($"The declared type '{declaration.Name}' is no struct.", nameof(module));

        this._declarations.Add(declaration, this._declarations.Count);
        writer.Write(structure.Name);
        writer.Write(structure.IsEquatable);
        writer.Write(structure.IsDefined);
      }

      foreach (var declaration in module.Types.Where(declaration => declaration.IsDefined)) {
        this.Count(declaration.Fields.Count);
        for (var index = 0; index < declaration.Fields.Count; ++index) {
          var field = declaration.Fields[index];
          this._fields[field] = (this._declarations[declaration], index);
          writer.Write(field.Name);
          this.Type(field.Type);
          writer.Write(field.IsReadOnly);
        }
      }

      this.Count(module.Constants.Count);
      foreach (var constant in module.Constants) {
        this._constants.Add(constant, this._constants.Count);
        writer.Write(constant.Name);
        this.Type(constant.Type);
        this.Longs(constant.Values);
      }

      this.Count(module.Functions.Count);
      foreach (var function in module.Functions) {
        writer.Write(function.Name);
        writer.Write(function.IsPublic);
        this.Type(function.ReturnType);
        this.Locals(function.Parameters);
        this.Locals(function.Locals);
        this.Node(function.Body);
      }
    }

    private void Count(int count) => writer.Write7BitEncodedInt(count);

    private void Longs(IReadOnlyList<long> values) {
      this.Count(values.Count);
      foreach (var value in values)
        writer.Write(value);
    }

    private void Type(IrType type) {
      writer.Write((byte)type.Kind);
      if (type.Kind is IrTypeKind.Array or IrTypeKind.Span or IrTypeKind.ReadOnlySpan)
        this.Type(type.Element!);
      else if (type.Kind == IrTypeKind.Struct)
        this.Count(this._declarations.TryGetValue(type.Declaration!, out var index) ? index : throw new ArgumentException($"The struct '{type.Declaration}' is not one of the module's types.", nameof(module)));
    }

    private void Field(IrField field)
      => this.Field(this._fields.TryGetValue(field, out var place) ? place : throw new ArgumentException($"The field '{field.Name}' is not one of a struct of the module.", nameof(module)));

    private void Field((int Declaration, int Index) place) {
      this.Count(place.Declaration);
      this.Count(place.Index);
    }

    /// <summary>A local by its number; the first time it occurs, its name and type follow.</summary>
    private void Local(IrLocal local) {
      if (this._locals.TryGetValue(local, out var known)) {
        this.Count(known);
        return;
      }

      this.Count(this._locals.Count);
      this._locals.Add(local, this._locals.Count);
      writer.Write(local.Name);
      this.Type(local.Type);
    }

    private void Locals(IReadOnlyList<IrLocal> locals) {
      this.Count(locals.Count);
      foreach (var local in locals)
        this.Local(local);
    }

    private void Nodes<T>(IReadOnlyList<T> nodes) where T : class {
      this.Count(nodes.Count);
      foreach (var node in nodes)
        this.Node(node);
    }

    /// <summary>
    /// A node, or a reference to the same node written before. A node gets its number before its
    /// children are written, so the numbers run in the order the reader meets the nodes.
    /// </summary>
    private void Node(object? node) {
      if (node is null) {
        writer.Write((byte)Tag.Null);
        return;
      }

      if (this._nodes.TryGetValue(node, out var known)) {
        writer.Write((byte)Tag.Shared);
        this.Count(known);
        return;
      }

      this._nodes.Add(node, this._nodes.Count);
      if (node is IrExpr expression) {
        writer.Write((byte)TagOf(expression));
        this.Type(expression.Type);
        this.Expression(expression);
      } else {
        writer.Write((byte)TagOf((IrStmt)node));
        this.Statement((IrStmt)node);
      }
    }

    private static Tag TagOf(IrExpr expression) => expression switch {
      IrConst => Tag.Const,
      IrConstRef => Tag.ConstRef,
      IrLocalRef => Tag.LocalRef,
      IrUnary => Tag.Unary,
      IrBinary => Tag.Binary,
      IrConvert => Tag.Convert,
      IrConditional => Tag.Conditional,
      IrArrayLoad => Tag.ArrayLoad,
      IrArrayLength => Tag.ArrayLength,
      IrNewArray => Tag.NewArray,
      IrCall => Tag.Call,
      IrStep => Tag.Step,
      IrStructNew => Tag.StructNew,
      IrStructDefault => Tag.StructDefault,
      IrFieldLoad => Tag.FieldLoad,
      IrStructEquals => Tag.StructEquals,
      IrIntrinsic => Tag.Intrinsic,
      _ => throw new ArgumentException($"The expression {expression.GetType().Name} has no form in bytes.", nameof(module))
    };

    private static Tag TagOf(IrStmt statement) => statement switch {
      IrBlock => Tag.Block,
      IrDeclare => Tag.Declare,
      IrAssign => Tag.Assign,
      IrStore => Tag.Store,
      IrFieldStore => Tag.FieldStore,
      IrExprStmt => Tag.ExprStmt,
      IrIf => Tag.If,
      IrWhile => Tag.While,
      IrDoWhile => Tag.DoWhile,
      IrFor => Tag.For,
      IrSwitch => Tag.Switch,
      IrBreak => Tag.Break,
      IrContinue => Tag.Continue,
      IrReturn => Tag.Return,
      _ => throw new ArgumentException($"The statement {statement.GetType().Name} has no form in bytes.", nameof(module))
    };

    private void Expression(IrExpr expression) {
      switch (expression) {
        case IrConst constant:
          writer.Write(constant.Value);
          break;
        case IrConstRef reference:
          this.Count(this._constants.TryGetValue(reference.Constant, out var index) ? index : throw new ArgumentException($"The constant '{reference.Constant.Name}' is not one of the module's.", nameof(module)));
          break;
        case IrLocalRef reference:
          this.Local(reference.Local);
          break;
        case IrUnary unary:
          this.Count((int)unary.Op);
          this.Node(unary.Operand);
          break;
        case IrBinary binary:
          this.Count((int)binary.Op);
          this.Node(binary.Left);
          this.Node(binary.Right);
          break;
        case IrConvert conversion:
          this.Node(conversion.Operand);
          this.Count((int)conversion.Contract);
          break;
        case IrConditional choice:
          this.Node(choice.Condition);
          this.Node(choice.WhenTrue);
          this.Node(choice.WhenFalse);
          break;
        case IrArrayLoad load:
          this.Node(load.Array);
          this.Node(load.Index);
          break;
        case IrArrayLength length:
          this.Node(length.Array);
          break;
        case IrNewArray creation:
          this.Node(creation.Length);
          break;
        case IrCall call:
          writer.Write(call.Function);
          this.Nodes(call.Arguments);
          break;
        case IrStep step:
          writer.Write(step.Increment);
          writer.Write(step.Postfix);
          writer.Write(step.Trap);
          this.Node(step.Target);
          break;
        case IrStructNew creation:
          this.Nodes(creation.Arguments);
          break;
        case IrStructDefault:
          break;
        case IrFieldLoad load:
          this.Field(load.Field);
          this.Node(load.Target);
          break;
        case IrStructEquals equality:
          this.Node(equality.Left);
          this.Node(equality.Right);
          break;
        default:
          var intrinsic = (IrIntrinsic)expression;
          this.Count((int)intrinsic.Op);
          this.Nodes(intrinsic.Arguments);
          break;
      }
    }

    private void Statement(IrStmt statement) {
      switch (statement) {
        case IrBlock block:
          this.Nodes(block.Statements);
          break;
        case IrDeclare declaration:
          this.Local(declaration.Local);
          this.Node(declaration.Initializer);
          break;
        case IrAssign assignment:
          this.Local(assignment.Local);
          this.Node(assignment.Value);
          break;
        case IrStore store:
          this.Node(store.Array);
          this.Node(store.Index);
          this.Node(store.Value);
          break;
        case IrFieldStore store:
          this.Field(store.Field);
          this.Node(store.Target);
          this.Node(store.Value);
          break;
        case IrExprStmt expression:
          this.Node(expression.Expression);
          break;
        case IrIf conditional:
          this.Node(conditional.Condition);
          this.Node(conditional.Then);
          this.Node(conditional.Else);
          break;
        case IrWhile loop:
          this.Node(loop.Condition);
          this.Node(loop.Body);
          break;
        case IrDoWhile loop:
          this.Node(loop.Body);
          this.Node(loop.Condition);
          break;
        case IrFor loop:
          this.Node(loop.Condition);
          this.Node(loop.Body);
          this.Node(loop.Step);
          break;
        case IrSwitch choice:
          this.Node(choice.Value);
          this.Count(choice.Sections.Count);
          foreach (var section in choice.Sections) {
            this.Longs(section.Labels);
            writer.Write(section.IsDefault);
            this.Node(section.Body);
          }
          break;
        case IrReturn exit:
          this.Node(exit.Value);
          break;
      }
    }

  }

  /// <summary>Reads the parts of a module in the order <see cref="Writer"/> wrote them.</summary>
  private sealed class Reader(BinaryReader reader) {

    private static readonly IrType[] _primitives = [
      IrType.Void, IrType.Bool, IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32,
      IrType.I64, IrType.U64, IrType.F32, IrType.F64
    ];

    private readonly List<IrStructType> _declarations = [];
    private readonly List<IrModuleConst> _constants = [];
    private readonly List<IrLocal> _locals = [];
    private readonly List<object?> _nodes = [];

    public IrModule Module() {
      var name = reader.ReadString();
      var defined = new List<IrStructType>();
      for (var count = this.Count(); count > 0; --count) {
        var structure = new IrStructType(reader.ReadString()) { IsEquatable = reader.ReadBoolean() };
        this._declarations.Add(structure);
        if (reader.ReadBoolean())
          defined.Add(structure);
      }

      foreach (var structure in defined)
        structure.Define(this.List(() => new IrField(reader.ReadString(), this.Type(), reader.ReadBoolean())));

      var constants = this.List(() => {
        var constant = new IrModuleConst(reader.ReadString(), this.Type(), this.Longs());
        this._constants.Add(constant);
        return constant;
      });
      var functions = this.List(() => new IrFunction(
        reader.ReadString(), reader.ReadBoolean(), this.Type(), this.List(this.Local), this.List(this.Local), this.Node<IrBlock>()));
      return new IrModule(name, functions, constants) { Types = [.. this._declarations] };
    }

    private int Count() {
      var count = reader.Read7BitEncodedInt();
      return count >= 0 ? count : throw new InvalidDataException("A count is negative.");
    }

    private IReadOnlyList<T> List<T>(Func<T> item) {
      var count = this.Count();
      var items = new List<T>(Math.Min(count, 1024));
      for (var index = 0; index < count; ++index)
        items.Add(item());

      return items;
    }

    private IReadOnlyList<long> Longs() => this.List(reader.ReadInt64);

    private static T Of<T>(IReadOnlyList<T> table, int index, string what)
      => index < table.Count ? table[index] : throw new InvalidDataException($"The {what} {index} does not exist.");

    private TEnum Enum<TEnum>() where TEnum : struct, Enum {
      var number = this.Count();
      var value = (TEnum)(object)number;
      return System.Enum.IsDefined(value) ? value : throw new InvalidDataException($"{number} is no {typeof(TEnum).Name}.");
    }

    private IrType Type() {
      var kind = (IrTypeKind)reader.ReadByte();
      return kind switch {
        IrTypeKind.Array => IrType.Array(this.Type()),
        IrTypeKind.Struct => IrType.Struct(Of(this._declarations, this.Count(), "struct")),
        IrTypeKind.Span => IrType.Span(this.Type()),
        IrTypeKind.ReadOnlySpan => IrType.ReadOnlySpan(this.Type()),
        < IrTypeKind.Array => _primitives[(int)kind],
        _ => throw new InvalidDataException($"{(int)kind} is no kind of type.")
      };
    }

    private IrField Field() {
      var structure = Of(this._declarations, this.Count(), "struct");
      return Of(structure.Fields, this.Count(), $"field of '{structure.Name}'");
    }

    private IrLocal Local() {
      var index = this.Count();
      if (index != this._locals.Count)
        return Of(this._locals, index, "local");

      var local = new IrLocal(reader.ReadString(), this.Type());
      this._locals.Add(local);
      return local;
    }

    private IReadOnlyList<T> Nodes<T>() where T : class => this.List(this.Node<T>);

    private T Node<T>() where T : class => this.OptionalNode<T>() ?? throw new InvalidDataException($"A {typeof(T).Name} is missing.");

    private T? OptionalNode<T>() where T : class {
      var tag = (Tag)reader.ReadByte();
      if (tag == Tag.Null)
        return null;

      if (tag == Tag.Shared) {
        var index = this.Count();
        return Of(this._nodes, index, "node") as T ?? throw new InvalidDataException($"The node {index} is no {typeof(T).Name}.");
      }

      if (tag > Tag.Return)
        throw new InvalidDataException($"{(int)tag} is no kind of node.");

      var slot = this._nodes.Count;
      this._nodes.Add(null);
      object node = tag >= Tag.Block ? this.Statement(tag) : this.Expression(tag);
      this._nodes[slot] = node;
      return node as T ?? throw new InvalidDataException($"A {node.GetType().Name} stands where a {typeof(T).Name} belongs.");
    }

    private IrExpr Expression(Tag tag) {
      var type = this.Type();
      IrExpr expression = tag switch {
        Tag.Const => new IrConst(type, reader.ReadInt64()),
        Tag.ConstRef => new IrConstRef(Of(this._constants, this.Count(), "constant")),
        Tag.LocalRef => new IrLocalRef(this.Local()),
        Tag.Unary => new IrUnary(this.Enum<IrUnaryOp>(), this.Node<IrExpr>()),
        Tag.Binary => new IrBinary(this.Enum<IrBinaryOp>(), this.Node<IrExpr>(), this.Node<IrExpr>()),
        Tag.Convert => new IrConvert(type, this.Node<IrExpr>(), this.Enum<IrConversion>()),
        Tag.Conditional => new IrConditional(this.Node<IrExpr>(), this.Node<IrExpr>(), this.Node<IrExpr>()),
        Tag.ArrayLoad => new IrArrayLoad(this.Node<IrExpr>(), this.Node<IrExpr>()),
        Tag.ArrayLength => new IrArrayLength(this.Node<IrExpr>()),
        Tag.NewArray => new IrNewArray(type, this.Node<IrExpr>()),
        Tag.Call => new IrCall(reader.ReadString(), type, this.Nodes<IrExpr>()),
        Tag.Step => this.Step(),
        Tag.StructNew => new IrStructNew(type, this.Nodes<IrExpr>()),
        Tag.StructDefault => new IrStructDefault(type),
        Tag.FieldLoad => this.FieldLoad(),
        Tag.StructEquals => new IrStructEquals(this.Node<IrExpr>(), this.Node<IrExpr>()),
        _ => new IrIntrinsic(this.Enum<IrIntrinsicOp>(), type, this.Nodes<IrExpr>())
      };
      return expression.Type == type ? expression : expression with { Type = type };
    }

    private IrStep Step() {
      bool increment = reader.ReadBoolean(), postfix = reader.ReadBoolean(), trap = reader.ReadBoolean();
      return new IrStep(this.Node<IrExpr>(), increment, postfix, trap);
    }

    private IrFieldLoad FieldLoad() {
      var field = this.Field();
      return new IrFieldLoad(this.Node<IrExpr>(), field);
    }

    private IrStmt Statement(Tag tag) => tag switch {
      Tag.Block => new IrBlock(this.Nodes<IrStmt>()),
      Tag.Declare => new IrDeclare(this.Local(), this.OptionalNode<IrExpr>()),
      Tag.Assign => new IrAssign(this.Local(), this.Node<IrExpr>()),
      Tag.Store => new IrStore(this.Node<IrExpr>(), this.Node<IrExpr>(), this.Node<IrExpr>()),
      Tag.FieldStore => this.FieldStore(),
      Tag.ExprStmt => new IrExprStmt(this.Node<IrExpr>()),
      Tag.If => new IrIf(this.Node<IrExpr>(), this.Node<IrBlock>(), this.OptionalNode<IrBlock>()),
      Tag.While => new IrWhile(this.Node<IrExpr>(), this.Node<IrBlock>()),
      Tag.DoWhile => new IrDoWhile(this.Node<IrBlock>(), this.Node<IrExpr>()),
      Tag.For => new IrFor(this.Node<IrExpr>(), this.Node<IrBlock>(), this.Node<IrBlock>()),
      Tag.Switch => new IrSwitch(this.Node<IrExpr>(), this.List(() => new IrSwitchSection(this.Longs(), reader.ReadBoolean(), this.Node<IrBlock>()))),
      Tag.Break => new IrBreak(),
      Tag.Continue => new IrContinue(),
      _ => new IrReturn(this.OptionalNode<IrExpr>())
    };

    private IrFieldStore FieldStore() {
      var field = this.Field();
      return new IrFieldStore(this.Node<IrExpr>(), field, this.Node<IrExpr>());
    }

  }

}
