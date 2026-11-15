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
/// The bytes keep the module exactly: every node, every type and every declaration with all its parts,
/// and also which nodes are one and the same object - a local that a parameter list, a declaration and
/// every read share, a constant or a global that its readers share with the list of the module, a
/// struct or a class and its fields, a class and its constructors and methods, a template that several
/// instantiations of a generic method share. A class of the library (<see cref="IrExceptionClass"/>) is
/// named, never written: it is the same object in every module. A SHA-256 of the content guards the
/// bytes: damaged bytes are refused, never read as another module.
/// </para>
/// </summary>
public static class IrModuleFormat {

  /// <summary>The version of the layout; bytes of another version are refused.</summary>
  public const int Version = 5;

  /// <summary>The byte that stands for the kind of an enum type, followed by its declaration: the kind of an enum type is that of its underlying type.</summary>
  private const byte EnumType = 0xFF;

  private static readonly byte[] _magic = "PLYIRMOD"u8.ToArray();

  private const int HashSize = 32;

  private enum Tag : byte {
    Null, Shared,
    Const, ConstRef, LocalRef, Unary, Binary, Convert, Conditional, ArrayLoad, ArrayLength, NewArray, Call, Step,
    StructNew, StructDefault, FieldLoad, StructEquals, Intrinsic, GlobalRef, StrConst,
    New, NullValue, MethodCall, ReferenceEquals, TypeTest, Cast, AsCast, GenericEquals, GenericCompare,
    None, Some, IsNone, Unwrap, Coalesce, IsNull, Bind, ThrowExpression,
    Collection, Lambda, FunctionRef, BoundMethod, Invoke,
    Block, Declare, Assign, Store, FieldStore, ExprStmt, If, While, DoWhile, For, Switch, Break, Continue, Return, GlobalAssign,
    Throw, Rethrow, Try, Using, ForEachCollection
  }

  /// <summary>The kinds of declaration, as the byte after the name of a declaration says.</summary>
  private enum Declared : byte {
    Struct, Enum, Class, Interface
  }

  /// <summary>How a constructor is named: the constructor of <c>new T()</c>, one of a class of the library, or one of the module.</summary>
  private enum Construction {
    Parameterless, Library, Module
  }

  /// <summary>Writes a module as bytes.</summary>
  /// <param name="module">
  /// The module; every declared type it names is one of its <see cref="IrModule.Types"/>, a type
  /// parameter of a template or a class of the library, every constant it reads one of its
  /// <see cref="IrModule.Constants"/>, every global one of its <see cref="IrModule.Globals"/>, and
  /// every method and constructor one of a type it declares.
  /// </param>
  /// <returns>The bytes, which <see cref="Read"/> turns back into an equal module.</returns>
  /// <exception cref="ArgumentException">
  /// The module names a declaration, a field, a method, a constructor, a constant or a global that is
  /// not its own, declares a class of the library, or holds a node that has no form in bytes.
  /// </exception>
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

  /// <summary>
  /// Writes the parts of a module in the order <see cref="Reader"/> reads them: every declaration by
  /// its name and kind first, so that any type can name any of them; then what each declaration
  /// holds; the methods in an order where a method comes after those it overrides and implements; the
  /// constructors and properties; the constants and globals; the members of the classes, which list
  /// their static fields among the globals; and the functions last.
  /// </summary>
  private sealed class Writer(BinaryWriter writer, IrModule module) {

    private readonly Dictionary<IrTypeDeclaration, int> _declarations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrField, (int Declaration, int Index)> _fields = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrMethod, int> _methods = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrConstructor, int> _constructors = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrGenericTemplate, int> _templates = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrModuleConst, int> _constants = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrGlobal, int> _globals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrLocal, int> _locals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, int> _nodes = new(ReferenceEqualityComparer.Instance);

    public void Module() {
      writer.Write(module.Name);
      this.Count(module.Caveats.Count);
      foreach (var caveat in module.Caveats)
        writer.Write(caveat);

      // The type parameters of the templates are declarations no module holds: they follow its own.
      var declarations = module.Types.Concat(StandIns(module).Except(module.Types, ReferenceEqualityComparer.Instance).Cast<IrTypeDeclaration>()).ToList();
      this.Count(module.Types.Count);
      this.Count(declarations.Count - module.Types.Count);
      foreach (var declaration in declarations) {
        if (declaration is IrClassType { IsLibrary: true })
          throw new ArgumentException($"The class '{declaration.Name}' is of the library: a module names it, never declares it.", nameof(module));

        this._declarations.Add(declaration, this._declarations.Count);
        this.Header(declaration);
      }

      foreach (var declaration in declarations)
        this.Body(declaration);

      var classes = declarations.OfType<IrClassType>().Where(type => type.HasMembers).ToList();
      var interfaces = declarations.OfType<IrInterfaceType>().Where(type => type.HasMembers).ToList();
      this.MethodTable(classes.SelectMany(type => type.Methods).Concat(interfaces.SelectMany(type => type.Methods)));
      foreach (var type in classes) {
        this.Count(type.Constructors.Count);
        foreach (var constructor in type.Constructors) {
          this._constructors.Add(constructor, this._constructors.Count);
          writer.Write(constructor.Function);
          this.Count((int)constructor.Visibility);
        }

        this.Properties(type.Properties);
      }

      foreach (var type in interfaces)
        this.Properties(type.Properties);

      this.Count(module.Constants.Count);
      foreach (var constant in module.Constants) {
        this._constants.Add(constant, this._constants.Count);
        writer.Write(constant.Name);
        this.Type(constant.Type);
        this.Longs(constant.Values);
        this.Count(constant.Strings.Count);
        foreach (var text in constant.Strings)
          this.Text(text);
      }

      // A global is numbered before its initializer is written: no initializer names a later one.
      this.Count(module.Globals.Count);
      foreach (var global in module.Globals) {
        this._globals.Add(global, this._globals.Count);
        writer.Write(global.Name);
        this.Type(global.Type);
        this.Node(global.Initializer);
      }

      foreach (var type in classes) {
        this.Methods(type.Methods);
        this.Count(type.StaticFields.Count);
        foreach (var global in type.StaticFields)
          this.Global(global);
      }

      foreach (var type in interfaces) {
        this.Methods(type.Methods);
        this.Count(type.BaseInterfaces.Count);
        foreach (var inherited in type.BaseInterfaces)
          this.Reference(inherited);
      }

      this.Count(module.Functions.Count);
      foreach (var function in module.Functions)
        this.Function(function);
    }

    /// <summary>The declarations that stand for the type parameters of the templates the functions of a module reach.</summary>
    private static IEnumerable<IrTypeDeclaration> StandIns(IrModule module) {
      var templates = new HashSet<IrGenericTemplate>(ReferenceEqualityComparer.Instance);
      var pending = new Stack<IrFunction>(module.Functions.Reverse());
      while (pending.TryPop(out var function))
        if (function.Origin?.Template is { } template && templates.Add(template)) {
          foreach (var parameter in template.Parameters)
            if (parameter.Declaration is { } declaration)
              yield return declaration;

          pending.Push(template.Function);
        }
    }

    private void Header(IrTypeDeclaration declaration) {
      writer.Write(declaration.Name);
      switch (declaration) {
        case IrStructType structure:
          writer.Write((byte)Declared.Struct);
          writer.Write(structure.IsEquatable);
          writer.Write(structure.IsDefined);
          break;
        case IrEnumType named:
          writer.Write((byte)Declared.Enum);
          this.Type(named.Underlying);
          writer.Write(named.IsFlags);
          writer.Write(named.IsPublic);
          this.Count(named.Members.Count);
          foreach (var member in named.Members) {
            writer.Write(member.Name);
            writer.Write(member.Value);
          }

          break;
        case IrClassType type:
          writer.Write((byte)Declared.Class);
          writer.Write(type.IsSealed);
          writer.Write(type.IsAbstract);
          writer.Write(type.IsRecord);
          this.Count((int)type.Visibility);
          writer.Write(type.IsDefined);
          writer.Write(type.HasHierarchy);
          writer.Write(type.HasMembers);
          writer.Write(type.Origin is not null);
          if (type.Origin is { } origin) {
            if (origin.Template is not null)
              throw new ArgumentException($"The origin of the class '{type.Name}' has a template; only a function's has one.", nameof(module));

            writer.Write(origin.Name);
            this.Strings(origin.TypeParameters);
          }

          break;
        case IrInterfaceType type:
          writer.Write((byte)Declared.Interface);
          this.Count((int)type.Visibility);
          writer.Write(type.IsDisposable);
          writer.Write(type.HasMembers);
          break;
        default:
          throw new ArgumentException($"The declared type '{declaration.Name}' is of no kind the format knows.", nameof(module));
      }
    }

    /// <summary>What a declaration holds that may name another declaration: its fields, its base and interfaces, its type arguments.</summary>
    private void Body(IrTypeDeclaration declaration) {
      if (declaration is IrStructType { IsDefined: true } or IrClassType { IsDefined: true })
        this.Fields(declaration);

      if (declaration is not IrClassType type)
        return;

      if (type.HasHierarchy) {
        this.OptionalReference(type.Base);
        this.Count(type.Interfaces.Count);
        foreach (var implemented in type.Interfaces)
          this.Reference(implemented);
      }

      if (type.Origin is { } origin)
        this.Arguments(origin);
    }

    private void Fields(IrTypeDeclaration declaration) {
      this.Count(declaration.Fields.Count);
      for (var index = 0; index < declaration.Fields.Count; ++index) {
        var field = declaration.Fields[index];
        this._fields[field] = (this._declarations[declaration], index);
        writer.Write(field.Name);
        this.Type(field.Type);
        writer.Write(field.IsReadOnly);
        this.Count((int)field.Visibility);
      }
    }

    /// <summary>The methods of the declarations, each after the methods it overrides and implements.</summary>
    private void MethodTable(IEnumerable<IrMethod> owned) {
      var declared = owned.ToList();
      var methods = new HashSet<IrMethod>(declared, ReferenceEqualityComparer.Instance);
      var ordered = new List<IrMethod>();
      void Visit(IrMethod method) {
        if (this._methods.ContainsKey(method))
          return;

        if (!methods.Contains(method))
          throw new ArgumentException($"The method '{method.Name}' is not one of a type of the module.", nameof(module));

        this._methods.Add(method, -1);
        if (method.Overrides is { } overridden)
          Visit(overridden);

        foreach (var implemented in method.Implements)
          Visit(implemented);

        this._methods[method] = ordered.Count;
        ordered.Add(method);
      }

      foreach (var method in declared)
        Visit(method);

      this.Count(ordered.Count);
      foreach (var method in ordered) {
        writer.Write(method.Name);
        writer.Write(method.Function);
        writer.Write(method.IsStatic);
        this.Count((int)method.Visibility);
        this.Count((int)method.Kind);
        this.Count((int)method.Dispatch);
        writer.Write(method.IsSealedOverride);
        this.OptionalMethod(method.Overrides);
        this.Methods(method.Implements);
      }
    }

    private void Properties(IReadOnlyList<IrProperty> properties) {
      this.Count(properties.Count);
      foreach (var property in properties) {
        writer.Write(property.Name);
        this.Type(property.Type);
        writer.Write(property.BackingField is not null);
        if (property.BackingField is { } field)
          this.Field(field);

        this.OptionalMethod(property.Getter);
        this.OptionalMethod(property.Setter);
        writer.Write(property.HasSetter);
        writer.Write(property.IsInitOnly);
        this.Count((int)property.Visibility);
      }
    }

    private void Function(IrFunction function) {
      writer.Write(function.Name);
      writer.Write(function.IsPublic);
      this.Type(function.ReturnType);
      this.Locals(function.Parameters);
      this.Locals(function.Locals);
      this.Node(function.Body);
      this.Count((int)function.Absent);
      writer.Write(function.Origin is not null);
      if (function.Origin is not { } origin)
        return;

      writer.Write(origin.Name);
      this.Strings(origin.TypeParameters);
      this.Arguments(origin);
      this.Template(origin.Template);
    }

    /// <summary>The type arguments and the constraints of an instantiation.</summary>
    private void Arguments(IrGenericOrigin origin) {
      this.Types(origin.TypeArguments);
      this.Count(origin.Constraints.Count);
      foreach (var constraint in origin.Constraints) {
        writer.Write(constraint.Parameter);
        this.Count((int)constraint.Kind);
        writer.Write(constraint.Interface is not null);
        if (constraint.Interface is { } type)
          this.Type(type);
      }
    }

    /// <summary>
    /// A template by its number plus one, 0 for none. The first time it occurs, the number of the next
    /// template stands for it and its function, type parameters and calls follow; it is numbered after
    /// them, as the reader numbers it, so that a template its function names comes first.
    /// </summary>
    private void Template(IrGenericTemplate? template) {
      if (template is null) {
        this.Count(0);
        return;
      }

      if (this._templates.TryGetValue(template, out var known)) {
        this.Count(known + 1);
        return;
      }

      this.Count(this._templates.Count + 1);
      this.Function(template.Function);
      this.Types(template.Parameters);
      this.Count(template.Calls.Count);
      foreach (var (callee, call) in template.Calls) {
        writer.Write(callee);
        writer.Write(call.Origin);
        this.Types(call.Arguments);
      }

      this._templates.Add(template, this._templates.Count);
    }

    private void Count(int count) => writer.Write7BitEncodedInt(count);

    private void Strings(IReadOnlyList<string> texts) {
      this.Count(texts.Count);
      foreach (var text in texts)
        writer.Write(text);
    }

    private void Longs(IReadOnlyList<long> values) {
      this.Count(values.Count);
      foreach (var value in values)
        writer.Write(value);
    }

    // A string as its UTF-16 code units, so that an unpaired surrogate is kept as it is.
    private void Text(string text) {
      this.Count(text.Length);
      foreach (var unit in text)
        writer.Write((ushort)unit);
    }

    private void Types(IReadOnlyList<IrType> types) {
      this.Count(types.Count);
      foreach (var type in types)
        this.Type(type);
    }

    private void Type(IrType type) {
      if (type.IsEnum) {
        writer.Write(EnumType);
        this.Reference(type.Declaration!);
        return;
      }

      writer.Write((byte)type.Kind);
      switch (type.Kind) {
        case IrTypeKind.Array or IrTypeKind.Span or IrTypeKind.ReadOnlySpan or IrTypeKind.Memory or IrTypeKind.ReadOnlyMemory or IrTypeKind.List or IrTypeKind.Pointer:
          this.Type(type.Element!);
          break;
        case IrTypeKind.Map:
          this.Type(type.Key!);
          this.Type(type.Element!);
          this.Count((int)type.Order);
          break;
        case IrTypeKind.Set:
          this.Type(type.Element!);
          this.Count((int)type.Order);
          break;
        case IrTypeKind.Struct or IrTypeKind.Object:
          this.Reference(type.Declaration!);
          break;
        case IrTypeKind.Optional:
          this.Type(type.Underlying!);
          break;
        case IrTypeKind.Function:
          this.Types(type.Signature!.Parameters);
          this.Type(type.Signature.Result);
          break;
      }
    }

    /// <summary>A declaration of the module by its number plus one, or a class of the library as 0 and its name.</summary>
    private void Reference(IrTypeDeclaration declaration) {
      if (this._declarations.TryGetValue(declaration, out var index)) {
        this.Count(index + 1);
        return;
      }

      if (declaration is not IrClassType { IsLibrary: true } || !IrExceptionClass.All.Contains(declaration))
        throw new ArgumentException($"The {KindOf(declaration)} '{declaration.Name}' is not one of the module's types.", nameof(module));

      this.Count(0);
      writer.Write(declaration.Name);
    }

    private static string KindOf(IrTypeDeclaration declaration) => declaration switch {
      IrStructType => "struct",
      IrEnumType => "enum",
      IrClassType => "class",
      _ => "interface"
    };

    private void OptionalReference(IrTypeDeclaration? declaration) {
      writer.Write(declaration is not null);
      if (declaration is not null)
        this.Reference(declaration);
    }

    private void Field(IrField field) {
      if (this._fields.TryGetValue(field, out var place)) {
        this.Count(place.Declaration + 1);
        this.Count(place.Index);
        return;
      }

      var owner = IrExceptionClass.All.FirstOrDefault(type => type.Owns(field))
        ?? throw new ArgumentException($"The field '{field.Name}' is not one of a type of the module.", nameof(module));
      this.Reference(owner);
      this.Count(owner.Fields.ToList().FindIndex(candidate => ReferenceEquals(candidate, field)));
    }

    private void Global(IrGlobal global)
      => this.Count(this._globals.TryGetValue(global, out var index) ? index : throw new ArgumentException($"The global '{global.Name}' is not one of the module's.", nameof(module)));

    private void Method(IrMethod method)
      => this.Count(this._methods.TryGetValue(method, out var index) ? index : throw new ArgumentException($"The method '{method.Name}' is not one of a type of the module.", nameof(module)));

    private void OptionalMethod(IrMethod? method) {
      writer.Write(method is not null);
      if (method is not null)
        this.Method(method);
    }

    private void Methods(IReadOnlyList<IrMethod> methods) {
      this.Count(methods.Count);
      foreach (var method in methods)
        this.Method(method);
    }

    private void Constructor(IrConstructor constructor) {
      if (ReferenceEquals(constructor, IrGenerics.Parameterless)) {
        this.Count((int)Construction.Parameterless);
        return;
      }

      if (this._constructors.TryGetValue(constructor, out var index)) {
        this.Count((int)Construction.Module);
        this.Count(index);
        return;
      }

      var owner = IrExceptionClass.All.FirstOrDefault(type => type.Constructors.Contains(constructor))
        ?? throw new ArgumentException($"The constructor '{constructor.Function}' is not one of a class of the module.", nameof(module));
      this.Count((int)Construction.Library);
      writer.Write(owner.Name);
      this.Count(owner.Constructors.ToList().IndexOf(constructor));
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

    private void OptionalLocal(IrLocal? local) {
      writer.Write(local is not null);
      if (local is not null)
        this.Local(local);
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
      IrGlobalRef => Tag.GlobalRef,
      IrStrConst => Tag.StrConst,
      IrNew => Tag.New,
      IrNull => Tag.NullValue,
      IrMethodCall => Tag.MethodCall,
      IrReferenceEquals => Tag.ReferenceEquals,
      IrTypeTest => Tag.TypeTest,
      IrCast => Tag.Cast,
      IrAsCast => Tag.AsCast,
      IrGenericEquals => Tag.GenericEquals,
      IrGenericCompare => Tag.GenericCompare,
      IrNone => Tag.None,
      IrSome => Tag.Some,
      IrIsNone => Tag.IsNone,
      IrUnwrap => Tag.Unwrap,
      IrCoalesce => Tag.Coalesce,
      IrIsNull => Tag.IsNull,
      IrBind => Tag.Bind,
      IrThrowExpression => Tag.ThrowExpression,
      IrCollection => Tag.Collection,
      IrLambda => Tag.Lambda,
      IrFunctionRef => Tag.FunctionRef,
      IrBoundMethod => Tag.BoundMethod,
      IrInvoke => Tag.Invoke,
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
      IrGlobalAssign => Tag.GlobalAssign,
      IrThrow => Tag.Throw,
      IrRethrow => Tag.Rethrow,
      IrTry => Tag.Try,
      IrUsing => Tag.Using,
      IrForEachCollection => Tag.ForEachCollection,
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
        case IrGlobalRef reference:
          this.Global(reference.Global);
          break;
        case IrStrConst text:
          this.Text(text.Value);
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
        case IrStructDefault or IrNull or IrNone:
          break;
        case IrFieldLoad load:
          this.Field(load.Field);
          this.Node(load.Target);
          break;
        case IrStructEquals equality:
          this.Node(equality.Left);
          this.Node(equality.Right);
          break;
        case IrIntrinsic intrinsic:
          this.Count((int)intrinsic.Op);
          this.Nodes(intrinsic.Arguments);
          break;
        case IrNew creation:
          this.Constructor(creation.Constructor);
          this.Nodes(creation.Arguments);
          break;
        case IrMethodCall call:
          this.Method(call.Method);
          this.Node(call.Target);
          this.Nodes(call.Arguments);
          this.Count((int)call.Kind);
          break;
        case IrReferenceEquals equality:
          this.Node(equality.Left);
          this.Node(equality.Right);
          break;
        case IrTypeTest test:
          this.Node(test.Value);
          this.Type(test.Target);
          break;
        case IrCast cast:
          this.Node(cast.Value);
          break;
        case IrAsCast cast:
          this.Node(cast.Value);
          break;
        case IrGenericEquals equality:
          this.Node(equality.Left);
          this.Node(equality.Right);
          break;
        case IrGenericCompare order:
          this.Node(order.Left);
          this.Node(order.Right);
          break;
        case IrSome some:
          this.Node(some.Value);
          break;
        case IrIsNone test:
          this.Node(test.Value);
          break;
        case IrUnwrap unwrap:
          this.Node(unwrap.Value);
          writer.Write(unwrap.IsTested);
          break;
        case IrCoalesce coalesce:
          this.Node(coalesce.Value);
          this.Node(coalesce.Fallback);
          break;
        case IrIsNull test:
          this.Node(test.Value);
          break;
        case IrBind bind:
          this.Local(bind.Local);
          this.Node(bind.Value);
          break;
        case IrThrowExpression thrown:
          this.Node(thrown.Exception);
          break;
        case IrCollection operation:
          this.Count((int)operation.Op);
          this.Nodes(operation.Arguments);
          break;
        case IrLambda lambda:
          this.Locals(lambda.Captures);
          this.Locals(lambda.Parameters);
          this.Locals(lambda.Locals);
          this.Node(lambda.Body);
          break;
        case IrFunctionRef reference:
          writer.Write(reference.Function);
          break;
        case IrBoundMethod bound:
          this.Node(bound.Receiver);
          this.Method(bound.Method);
          this.Count((int)bound.Kind);
          break;
        default:
          var invocation = (IrInvoke)expression;
          this.Node(invocation.Callee);
          this.Nodes(invocation.Arguments);
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
        case IrGlobalAssign assignment:
          this.Global(assignment.Global);
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
          this.Locals(loop.PerIteration);
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
        case IrThrow thrown:
          this.Node(thrown.Exception);
          break;
        case IrTry attempt:
          this.Node(attempt.Body);
          this.Count(attempt.Catches.Count);
          foreach (var clause in attempt.Catches) {
            this.Reference(clause.Class);
            this.OptionalLocal(clause.Local);
            this.Node(clause.Filter);
            this.Node(clause.Body);
          }

          this.Node(attempt.Finally);
          break;
        case IrUsing scope:
          this.Local(scope.Resource);
          this.Node(scope.Initializer);
          this.Method(scope.Dispose);
          this.Node(scope.Body);
          writer.Write(scope.IsDeclaration);
          break;
        case IrForEachCollection loop:
          this.Node(loop.Collection);
          this.Count((int)loop.Mode);
          this.OptionalLocal(loop.Element);
          this.OptionalLocal(loop.Value);
          this.Node(loop.Body);
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

    private readonly List<IrTypeDeclaration> _declarations = [];
    private readonly List<IrMethod> _methods = [];
    private readonly List<IrConstructor> _constructors = [];
    private readonly List<IrGenericTemplate> _templates = [];
    private readonly List<IrModuleConst> _constants = [];
    private readonly List<IrGlobal> _globals = [];
    private readonly List<IrLocal> _locals = [];
    private readonly List<object?> _nodes = [];

    /// <summary>The type arguments and constraints of the origin of a class, which are read after every declaration is known.</summary>
    private readonly Dictionary<IrClassType, (List<IrType> Arguments, List<IrGenericConstraint> Constraints)> _origins = new(ReferenceEqualityComparer.Instance);

    public IrModule Module() {
      var name = reader.ReadString();
      var caveats = this.Strings();
      var owned = this.Count();
      var standIns = this.Count();
      var defined = new List<IrTypeDeclaration>();
      var hierarchies = new List<IrClassType>();
      var classes = new List<IrClassType>();
      var interfaces = new List<IrInterfaceType>();
      for (var index = 0; index < owned + standIns; ++index)
        this._declarations.Add(this.Header(defined, hierarchies, classes, interfaces));

      foreach (var declaration in this._declarations)
        this.Body(declaration, defined, hierarchies);

      // A method is in the table as soon as it is read: a later one names it as the method it overrides or implements.
      this.List(this.TableMethod);
      var properties = new Dictionary<IrTypeDeclaration, IReadOnlyList<IrProperty>>(ReferenceEqualityComparer.Instance);
      var constructors = new Dictionary<IrClassType, IReadOnlyList<IrConstructor>>(ReferenceEqualityComparer.Instance);
      foreach (var type in classes) {
        constructors[type] = this.List(() => {
          var constructor = new IrConstructor(reader.ReadString()) { Visibility = this.Enum<IrVisibility>() };
          this._constructors.Add(constructor);
          return constructor;
        });
        properties[type] = this.Properties();
      }

      foreach (var type in interfaces)
        properties[type] = this.Properties();

      var constants = this.List(() => {
        var constant = new IrModuleConst(reader.ReadString(), this.Type(), this.Longs()) { Strings = this.List(this.Text) };
        this._constants.Add(constant);
        return constant;
      });
      for (var count = this.Count(); count > 0; --count)
        this._globals.Add(new IrGlobal(reader.ReadString(), this.Type(), this.OptionalNode<IrExpr>()));

      foreach (var type in classes)
        type.DefineMembers(constructors[type], this.Methods(), properties[type], this.List(() => Of(this._globals, this.Count(), "global")));

      foreach (var type in interfaces)
        type.DefineMembers(this.Methods(), properties[type], this.List(this.Interface));

      var functions = this.List(this.Function);
      return new IrModule(name, functions, constants) { Types = [.. this._declarations.Take(owned)], Globals = [.. this._globals], Caveats = caveats };
    }

    private IrTypeDeclaration Header(List<IrTypeDeclaration> defined, List<IrClassType> hierarchies, List<IrClassType> classes, List<IrInterfaceType> interfaces) {
      var declared = reader.ReadString();
      switch (reader.ReadByte()) {
        case (byte)Declared.Struct:
          var structure = new IrStructType(declared) { IsEquatable = reader.ReadBoolean() };
          if (reader.ReadBoolean())
            defined.Add(structure);

          return structure;
        case (byte)Declared.Enum:
          var underlying = this.Type();
          var flags = reader.ReadBoolean();
          var visible = reader.ReadBoolean();
          return new IrEnumType(declared, underlying, this.List(() => new IrEnumMember(reader.ReadString(), reader.ReadInt64())), flags) { IsPublic = visible };
        case (byte)Declared.Class:
          bool isSealed = reader.ReadBoolean(), isAbstract = reader.ReadBoolean(), isRecord = reader.ReadBoolean();
          var visibility = this.Enum<IrVisibility>();
          bool isDefined = reader.ReadBoolean(), hasHierarchy = reader.ReadBoolean(), hasMembers = reader.ReadBoolean();
          List<IrType> arguments = [];
          List<IrGenericConstraint> constraints = [];
          var origin = reader.ReadBoolean() ? new IrGenericOrigin(reader.ReadString(), this.Strings(), arguments, constraints) : null;
          var type = new IrClassType(declared) { IsSealed = isSealed, IsAbstract = isAbstract, IsRecord = isRecord, Visibility = visibility, Origin = origin };
          if (isDefined)
            defined.Add(type);

          if (hasHierarchy)
            hierarchies.Add(type);

          if (hasMembers)
            classes.Add(type);

          if (origin is not null)
            this._origins.Add(type, (arguments, constraints));

          return type;
        case (byte)Declared.Interface:
          var contract = new IrInterfaceType(declared) { Visibility = this.Enum<IrVisibility>(), IsDisposable = reader.ReadBoolean() };
          if (reader.ReadBoolean())
            interfaces.Add(contract);

          return contract;
        default:
          throw new InvalidDataException($"The declaration '{declared}' is of no known kind.");
      }
    }

    private void Body(IrTypeDeclaration declaration, List<IrTypeDeclaration> defined, List<IrClassType> hierarchies) {
      if (defined.Contains(declaration))
        declaration.Define(this.List(() => new IrField(reader.ReadString(), this.Type(), reader.ReadBoolean()) { Visibility = this.Enum<IrVisibility>() }));

      if (declaration is not IrClassType type)
        return;

      if (hierarchies.Contains(type))
        type.Inherit(reader.ReadBoolean() ? this.Class() : null, this.List(this.Interface));

      if (this._origins.TryGetValue(type, out var origin)) {
        origin.Arguments.AddRange(this.Types());
        origin.Constraints.AddRange(this.Constraints());
      }
    }

    private IrMethod TableMethod() {
      var method = new IrMethod(reader.ReadString(), reader.ReadString(), reader.ReadBoolean()) {
        Visibility = this.Enum<IrVisibility>(),
        Kind = this.Enum<IrMethodKind>(),
        Dispatch = this.Enum<IrDispatch>(),
        IsSealedOverride = reader.ReadBoolean(),
        Overrides = this.OptionalMethod(),
        Implements = this.Methods()
      };
      this._methods.Add(method);
      return method;
    }

    private IReadOnlyList<IrProperty> Properties() => this.List(() => new IrProperty(reader.ReadString(), this.Type()) {
      BackingField = reader.ReadBoolean() ? this.Field() : null,
      Getter = this.OptionalMethod(),
      Setter = this.OptionalMethod(),
      HasSetter = reader.ReadBoolean(),
      IsInitOnly = reader.ReadBoolean(),
      Visibility = this.Enum<IrVisibility>()
    });

    private IrFunction Function() {
      var function = new IrFunction(reader.ReadString(), reader.ReadBoolean(), this.Type(), this.List(this.Local), this.List(this.Local), this.Node<IrBlock>()) {
        Absent = this.Enum<IrAbsent>()
      };
      if (!reader.ReadBoolean())
        return function;

      var origin = new IrGenericOrigin(reader.ReadString(), this.Strings(), this.Types(), this.Constraints()) { Template = this.Template() };
      return function with { Origin = origin };
    }

    private IReadOnlyList<IrGenericConstraint> Constraints()
      => this.List(() => new IrGenericConstraint(reader.ReadString(), this.Enum<IrGenericConstraintKind>()) { Interface = reader.ReadBoolean() ? this.Type() : null });

    private IrGenericTemplate? Template() {
      var number = this.Count();
      if (number == 0)
        return null;

      if (number - 1 != this._templates.Count)
        return Of(this._templates, number - 1, "template");

      var function = this.Function();
      var parameters = this.Types();
      var calls = new Dictionary<string, IrGenericCall>();
      for (var count = this.Count(); count > 0; --count)
        calls[reader.ReadString()] = new IrGenericCall(reader.ReadString(), this.Types());

      var template = new IrGenericTemplate(function, parameters) { Calls = calls };
      this._templates.Add(template);
      return template;
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

    private IReadOnlyList<string> Strings() => this.List(reader.ReadString);

    private IReadOnlyList<long> Longs() => this.List(reader.ReadInt64);

    private string Text() {
      var units = new char[this.Count()];
      for (var index = 0; index < units.Length; ++index)
        units[index] = (char)reader.ReadUInt16();

      return new string(units);
    }

    private static T Of<T>(IReadOnlyList<T> table, int index, string what)
      => index < table.Count ? table[index] : throw new InvalidDataException($"The {what} {index} does not exist.");

    private TEnum Enum<TEnum>() where TEnum : struct, Enum {
      var number = this.Count();
      var value = (TEnum)(object)number;
      return System.Enum.IsDefined(value) ? value : throw new InvalidDataException($"{number} is no {typeof(TEnum).Name}.");
    }

    private IReadOnlyList<IrType> Types() => this.List(this.Type);

    private IrType Type() {
      var marker = reader.ReadByte();
      if (marker == EnumType)
        return this.Declaration("type") is IrEnumType named ? named.Type : throw new InvalidDataException("An enum type names no enum.");

      var kind = (IrTypeKind)marker;
      return kind switch {
        IrTypeKind.Array => IrType.Array(this.Type()),
        IrTypeKind.Struct => IrType.Struct(this.Struct()),
        IrTypeKind.Span => IrType.Span(this.Type()),
        IrTypeKind.ReadOnlySpan => IrType.ReadOnlySpan(this.Type()),
        IrTypeKind.Memory => IrType.Memory(this.Type()),
        IrTypeKind.ReadOnlyMemory => IrType.ReadOnlyMemory(this.Type()),
        IrTypeKind.String => IrType.String,
        IrTypeKind.Object => this.Declaration("type") switch {
          IrClassType type => IrType.Object(type),
          IrInterfaceType type => IrType.Object(type),
          _ => throw new InvalidDataException("An object type names neither a class nor an interface.")
        },
        IrTypeKind.Optional => IrType.Optional(this.Type()),
        IrTypeKind.List => IrType.List(this.Type()),
        IrTypeKind.Map => IrType.Map(this.Type(), this.Type(), this.Enum<MapOrder>()),
        IrTypeKind.Set => IrType.Set(this.Type(), this.Enum<MapOrder>()),
        IrTypeKind.Function => IrType.Function(new IrFunctionType(this.Types(), this.Type())),
        IrTypeKind.Pointer => IrType.Pointer(this.Type()),
        < IrTypeKind.Array => _primitives[(int)kind],
        _ => throw new InvalidDataException($"{(int)kind} is no kind of type.")
      };
    }

    /// <summary>A declaration of the module by its number plus one, or a class of the library by its name after a 0.</summary>
    private IrTypeDeclaration Declaration(string what) {
      var number = this.Count();
      if (number > 0)
        return Of(this._declarations, number - 1, what);

      var name = reader.ReadString();
      return IrExceptionClass.All.FirstOrDefault(type => type.Name == name) ?? throw new InvalidDataException($"No class of the library is named '{name}'.");
    }

    private IrStructType Struct()
      => this.Declaration("struct") as IrStructType ?? throw new InvalidDataException("A struct type names no struct.");

    private IrClassType Class()
      => this.Declaration("class") as IrClassType ?? throw new InvalidDataException("A class is expected where another declaration is named.");

    private IrInterfaceType Interface()
      => this.Declaration("interface") as IrInterfaceType ?? throw new InvalidDataException("An interface is expected where another declaration is named.");

    private IrField Field() {
      var owner = this.Declaration("declaration");
      return Of(owner.Fields, this.Count(), $"field of '{owner.Name}'");
    }

    private IrMethod Method() => Of(this._methods, this.Count(), "method");

    private IrMethod? OptionalMethod() => reader.ReadBoolean() ? this.Method() : null;

    private IReadOnlyList<IrMethod> Methods() => this.List(this.Method);

    private IrConstructor Constructor() {
      switch (this.Enum<Construction>()) {
        case Construction.Parameterless:
          return IrGenerics.Parameterless;
        case Construction.Module:
          return Of(this._constructors, this.Count(), "constructor");
        default:
          var name = reader.ReadString();
          var owner = IrExceptionClass.All.FirstOrDefault(type => type.Name == name) ?? throw new InvalidDataException($"No class of the library is named '{name}'.");
          return Of(owner.Constructors, this.Count(), $"constructor of '{name}'");
      }
    }

    private IrLocal Local() {
      var index = this.Count();
      if (index != this._locals.Count)
        return Of(this._locals, index, "local");

      var local = new IrLocal(reader.ReadString(), this.Type());
      this._locals.Add(local);
      return local;
    }

    private IrLocal? OptionalLocal() => reader.ReadBoolean() ? this.Local() : null;

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

      if (tag > Tag.ForEachCollection)
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
        Tag.Intrinsic => new IrIntrinsic(this.Enum<IrIntrinsicOp>(), type, this.Nodes<IrExpr>()),
        Tag.GlobalRef => new IrGlobalRef(Of(this._globals, this.Count(), "global")),
        Tag.StrConst => new IrStrConst(this.Text()),
        Tag.New => new IrNew(type, this.Constructor(), this.Nodes<IrExpr>()),
        Tag.NullValue => new IrNull(type),
        Tag.MethodCall => new IrMethodCall(type, this.Method(), this.OptionalNode<IrExpr>(), this.Nodes<IrExpr>()) { Kind = this.Enum<IrMethodCallKind>() },
        Tag.ReferenceEquals => new IrReferenceEquals(this.Node<IrExpr>(), this.Node<IrExpr>()),
        Tag.TypeTest => new IrTypeTest(this.Node<IrExpr>(), this.Type()),
        Tag.Cast => new IrCast(type, this.Node<IrExpr>()),
        Tag.AsCast => new IrAsCast(type, this.Node<IrExpr>()),
        Tag.GenericEquals => new IrGenericEquals(this.Node<IrExpr>(), this.Node<IrExpr>()),
        Tag.GenericCompare => new IrGenericCompare(this.Node<IrExpr>(), this.Node<IrExpr>()),
        Tag.None => new IrNone(type),
        Tag.Some => new IrSome(this.Node<IrExpr>()),
        Tag.IsNone => new IrIsNone(this.Node<IrExpr>()),
        Tag.Unwrap => new IrUnwrap(this.Node<IrExpr>()) { IsTested = reader.ReadBoolean() },
        Tag.Coalesce => new IrCoalesce(this.Node<IrExpr>(), this.Node<IrExpr>()),
        Tag.IsNull => new IrIsNull(this.Node<IrExpr>()),
        Tag.Bind => new IrBind(this.Local(), this.Node<IrExpr>()),
        Tag.ThrowExpression => new IrThrowExpression(type, this.Node<IrExpr>()),
        Tag.Collection => new IrCollection(this.Enum<IrCollectionOp>(), type, this.Nodes<IrExpr>()),
        Tag.Lambda => new IrLambda(type, this.List(this.Local), this.List(this.Local), this.List(this.Local), this.Node<IrBlock>()),
        Tag.FunctionRef => new IrFunctionRef(type, reader.ReadString()),
        Tag.BoundMethod => this.BoundMethod(type),
        _ => new IrInvoke(type, this.Node<IrExpr>(), this.Nodes<IrExpr>())
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

    private IrBoundMethod BoundMethod(IrType type) {
      var receiver = this.OptionalNode<IrExpr>();
      return new IrBoundMethod(type, receiver, this.Method()) { Kind = this.Enum<IrMethodCallKind>() };
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
      Tag.For => new IrFor(this.Node<IrExpr>(), this.Node<IrBlock>(), this.Node<IrBlock>()) { PerIteration = this.List(this.Local) },
      Tag.Switch => new IrSwitch(this.Node<IrExpr>(), this.List(() => new IrSwitchSection(this.Longs(), reader.ReadBoolean(), this.Node<IrBlock>()))),
      Tag.Break => new IrBreak(),
      Tag.Continue => new IrContinue(),
      Tag.Return => new IrReturn(this.OptionalNode<IrExpr>()),
      Tag.GlobalAssign => new IrGlobalAssign(Of(this._globals, this.Count(), "global"), this.Node<IrExpr>()),
      Tag.Throw => new IrThrow(this.Node<IrExpr>()),
      Tag.Rethrow => new IrRethrow(),
      Tag.Try => new IrTry(this.Node<IrBlock>(), this.List(this.Catch), this.OptionalNode<IrBlock>()),
      Tag.Using => new IrUsing(this.Local(), this.Node<IrExpr>(), this.Method(), this.Node<IrBlock>()) { IsDeclaration = reader.ReadBoolean() },
      _ => new IrForEachCollection(this.Node<IrExpr>(), this.Enum<EnumerationMode>(), this.OptionalLocal(), this.OptionalLocal(), this.Node<IrBlock>())
    };

    private IrCatch Catch() => new(this.Class(), this.OptionalLocal(), this.OptionalNode<IrExpr>(), this.Node<IrBlock>());

    private IrFieldStore FieldStore() {
      var field = this.Field();
      return new IrFieldStore(this.Node<IrExpr>(), field, this.Node<IrExpr>());
    }

  }

}
