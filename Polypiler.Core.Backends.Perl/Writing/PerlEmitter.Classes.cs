using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Analysis.Range;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Perl;

public static partial class PerlEmitter {

  private sealed partial class Writer {

    private readonly Dictionary<IrClassType, string> _classPackages = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrInterfaceType, string> _interfacePackages = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrMethod, string> _methodNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrConstructor, string> _ctorNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IrClassType> _classFunctionOwners = new(StringComparer.Ordinal);
    private readonly HashSet<string> _interfaceFunctionNames = new(StringComparer.Ordinal);

    private string _package = "";
    private IrClassType? _currentClass;

    private void PrepareClassesAndInterfaces(IrModule module, string package) {
      this._package = package;
      this._classPackages.Clear();
      this._interfacePackages.Clear();
      this._methodNames.Clear();
      this._ctorNames.Clear();
      this._classFunctionOwners.Clear();
      this._interfaceFunctionNames.Clear();

      foreach (var iface in module.Types.OfType<IrInterfaceType>()) {
        this._interfacePackages[iface] = $"{package}::{PerlNaming.Package(iface.Name)}";
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in iface.Methods) {
          this._interfaceFunctionNames.Add(method.Function);
          var name = PerlNaming.Free(MethodBaseName(method), taken.Contains);
          taken.Add(name);
          this._methodNames[method] = name;
        }
      }

      var classes = OrderClassesByHierarchy(module.Types.OfType<IrClassType>().Where(IsUserClass).ToList());
      foreach (var cls in classes) {
        this._classPackages[cls] = $"{package}::{PerlNaming.Package(cls.Name)}";
        for (var i = 0; i < cls.Constructors.Count; ++i) {
          var ctor = cls.Constructors[i];
          this._classFunctionOwners[ctor.Function] = cls;
          this._ctorNames[ctor] = $"_ctor_{i.ToString(CultureInfo.InvariantCulture)}";
        }

        var taken = new HashSet<string>(StringComparer.Ordinal) { "new", "DESTROY", "meta" };
        if (this._profile.ObjectModel == PerlObjectModel.Moose) {
          foreach (var field in cls.AllFields) {
            var attr = this.MooseAttrName(field);
            taken.Add(attr);
            if (field.IsReadOnly)
              taken.Add("_set_" + attr);
          }
        } else if (this._profile.ObjectModel == PerlObjectModel.InsideOut) {
          foreach (var field in cls.AllFields)
            if (field.Visibility is IrVisibility.Public or IrVisibility.Internal)
              taken.Add(FieldPerlIdentifier(field));
        }

        foreach (var method in cls.Methods) {
          this._classFunctionOwners[method.Function] = cls;
          if (method.Overrides is { } overridden) {
            var slotName = this._methodNames[overridden.Root];
            this._methodNames[method] = slotName;
            taken.Add(slotName);
            continue;
          }
          if (method.Implements.Count > 0) {
            var ifaceName = this._methodNames[method.Implements[0]];
            this._methodNames[method] = ifaceName;
            taken.Add(ifaceName);
            continue;
          }

          var baseName = MethodBaseName(method);
          var assigned = PerlNaming.Free(baseName, taken.Contains);
          taken.Add(assigned);
          this._methodNames[method] = assigned;
        }
      }
    }

    private static bool IsUserClass(IrClassType cls)
      => !cls.IsLibrary && !IrExceptionClass.IsException(cls);

    private static List<IrClassType> OrderClassesByHierarchy(List<IrClassType> classes) {
      var ordered = new List<IrClassType>(classes.Count);
      var visited = new HashSet<IrClassType>(ReferenceEqualityComparer.Instance);
      void Visit(IrClassType cls) {
        if (!visited.Add(cls))
          return;
        if (cls.Base is { } b)
          Visit(b);
        ordered.Add(cls);
      }
      foreach (var cls in classes)
        Visit(cls);
      return ordered;
    }

    private static string MethodBaseName(IrMethod method) {
      var raw = method.Kind switch {
        IrMethodKind.Getter => "get_" + method.Name,
        IrMethodKind.Setter => "set_" + method.Name,
        _ => method.Name
      };
      var snake = PerlNaming.Snake(raw).Trim('_');
      return method.Visibility == IrVisibility.Private ? "_" + snake : PerlNaming.Usable(snake);
    }

    private static string FieldPerlIdentifier(IrField field)
      => PerlNaming.Usable(PerlNaming.Snake(field.Name).Trim('_'));

    private static string InsideOutHashName(IrField field)
      => "%_f_" + FieldPerlIdentifier(field);

    private static string InsideOutHashElement(IrField field, string keyExpr)
      => "$_f_" + FieldPerlIdentifier(field) + "{" + keyExpr + "}";

    private string InsideOutKey(string objExpr) => this._profile.HasFieldHash
      ? objExpr
      : this._profile.HasScalarUtil ? $"Scalar::Util::refaddr({objExpr})" : $"\"{objExpr}\"";

    private string MooseAttrName(IrField field)
      => field.Visibility == IrVisibility.Private ? "_priv_" + FieldPerlIdentifier(field) : FieldPerlIdentifier(field);

    private string ClassPackage(IrClassType cls) => this._classPackages[cls];

    private string InterfacePackage(IrInterfaceType iface) => this._interfacePackages[iface];

    private string MethodPerlName(IrMethod method) => this._methodNames[method];

    private string EmitClassAndInterfacePackages(IrModule module) {
      var text = new StringBuilder();
      var functionMap = module.Functions.ToDictionary(f => f.Name, StringComparer.Ordinal);

      if (this._profile.ObjectModel == PerlObjectModel.Moose) {
        foreach (var iface in module.Types.OfType<IrInterfaceType>())
          if (!iface.IsDisposable)
            this.EmitMooseRolePackage(iface, text);
      }

      var classes = OrderClassesByHierarchy(module.Types.OfType<IrClassType>().Where(IsUserClass).ToList());
      foreach (var cls in classes) {
        switch (this._profile.ObjectModel) {
          case PerlObjectModel.Moose:
            this.EmitMooseClassPackage(cls, functionMap, text);
            break;
          case PerlObjectModel.InsideOut:
            this.EmitInsideOutClassPackage(cls, functionMap, text);
            break;
          default:
            this.EmitBlessedClassPackage(cls, functionMap, text);
            break;
        }
      }

      return text.ToString();
    }

    private void EmitMooseRolePackage(IrInterfaceType iface, StringBuilder text) {
      var pkg = this.InterfacePackage(iface);
      text.Append("{\n");
      text.Append("    package ").Append(pkg).Append(";\n");
      text.Append("    use Moose::Role;\n");
      foreach (var baseIface in iface.BaseInterfaces)
        text.Append("    with '").Append(this.InterfacePackage(baseIface)).Append("';\n");
      foreach (var method in iface.Methods)
        text.Append("    requires '").Append(this.MethodPerlName(method)).Append("';\n");
      text.Append("}\n\n");
    }

    private void EmitBlessedClassPackage(IrClassType cls, Dictionary<string, IrFunction> functionMap, StringBuilder text) {
      var pkg = this.ClassPackage(cls);
      this._currentClass = cls;
      text.Append("{\n");
      text.Append("    package ").Append(pkg).Append(";\n");
      text.Append("    use strict;\n    use warnings;\n");
      if (cls.Base is { } baseCls)
        text.Append("    our @ISA = ('").Append(this.ClassPackage(baseCls)).Append("');\n");
      text.Append('\n');

      foreach (var iface in cls.AllInterfaces)
        text.Append("    sub _ply_does_").Append(PerlNaming.Snake(iface.Name)).Append(" { return 1; }\n");

      // Emit per-constructor init subs and public `new` factory
      foreach (var ctor in cls.Constructors)
        this.EmitClassConstructor(cls, ctor, functionMap[ctor.Function], text);
      this.EmitPublicNew(cls, functionMap, text);

      foreach (var method in cls.Methods)
        if (method.Dispatch != IrDispatch.Abstract)
          this.EmitClassMethod(cls, method, functionMap[method.Function], text);

      if (cls.IsRecord)
        this.EmitClassEqualsMethod(cls, text);

      text.Append("}\n\n");
      this._currentClass = null;
    }

    private void EmitMooseClassPackage(IrClassType cls, Dictionary<string, IrFunction> functionMap, StringBuilder text) {
      var pkg = this.ClassPackage(cls);
      this._currentClass = cls;
      text.Append("{\n");
      text.Append("    package ").Append(pkg).Append(";\n");
      text.Append("    use Moose;\n");
      if (cls.Base is { } baseCls)
        text.Append("    extends '").Append(this.ClassPackage(baseCls)).Append("';\n");
      text.Append('\n');

      foreach (var field in cls.Fields) {
        var attr = this.MooseAttrName(field);
        var def = this.DefaultValue(field.Type);
        if (field.IsReadOnly)
          text.Append("    has '").Append(attr).Append("' => (is => 'ro', writer => '_set_").Append(attr)
            .Append("', default => sub { ").Append(def).Append(" });\n");
        else
          text.Append("    has '").Append(attr).Append("' => (is => 'rw', default => sub { ").Append(def).Append(" });\n");
      }
      if (cls.Fields.Count > 0)
        text.Append('\n');

      foreach (var ctor in cls.Constructors)
        this.EmitClassConstructor(cls, ctor, functionMap[ctor.Function], text);

      foreach (var method in cls.Methods)
        if (method.Dispatch != IrDispatch.Abstract)
          this.EmitClassMethod(cls, method, functionMap[method.Function], text);

      if (cls.IsRecord)
        this.EmitClassEqualsMethod(cls, text);

      foreach (var iface in cls.Interfaces)
        if (!iface.IsDisposable)
          text.Append("    with '").Append(this.InterfacePackage(iface)).Append("';\n");
      text.Append("    __PACKAGE__->meta->make_immutable;\n");
      text.Append("}\n\n");
      this._currentClass = null;
    }

    private void EmitInsideOutClassPackage(IrClassType cls, Dictionary<string, IrFunction> functionMap, StringBuilder text) {
      var pkg = this.ClassPackage(cls);
      this._currentClass = cls;
      text.Append("{\n");
      text.Append("    package ").Append(pkg).Append(";\n");
      text.Append("    use strict;\n    use warnings;\n");
      if (this._profile.HasFieldHash)
        text.Append("    use Hash::Util::FieldHash qw(fieldhash);\n");
      else if (this._profile.HasScalarUtil)
        text.Append("    use Scalar::Util ();\n");
      if (cls.Fields.Any(f => f.Visibility == IrVisibility.Protected || f.IsReadOnly))
        text.Append("    use Carp ();\n");
      if (cls.Base is { } baseCls)
        text.Append("    our @ISA = ('").Append(this.ClassPackage(baseCls)).Append("');\n");
      text.Append('\n');

      // Lexical storage hashes strictly scoped to this package block: external code cannot see or touch them!
      foreach (var field in cls.Fields) {
        if (this._profile.HasFieldHash)
          text.Append("    fieldhash my ").Append(InsideOutHashName(field)).Append(";\n");
        else
          text.Append("    my ").Append(InsideOutHashName(field)).Append(";\n");
      }
      if (cls.Fields.Count > 0)
        text.Append('\n');

      foreach (var iface in cls.AllInterfaces)
        text.Append("    sub _ply_does_").Append(PerlNaming.Snake(iface.Name)).Append(" { return 1; }\n");

      // Emit own-field initializer called during construction (for both this class and derived classes)
      text.Append("    sub _ply_init_fields {\n");
      text.Append("        my ($self) = @_;\n");
      if (cls.Base is { } initBase)
        text.Append("        ").Append(this.ClassPackage(initBase)).Append("::_ply_init_fields($self);\n");
      if (cls.Fields.Count > 0) {
        text.Append("        my $_id = ").Append(this.InsideOutKey("$self")).Append(";\n");
        foreach (var field in cls.Fields)
          text.Append("        ").Append(InsideOutHashElement(field, "$_id")).Append(" = ").Append(this.DefaultValue(field.Type)).Append(";\n");
      }
      text.Append("        return;\n    }\n\n");

      // Emit accessors strictly according to field visibility:
      // - Private: NO accessor emitted at all! Only methods inside this lexical block can read/write %_f_<field>.
      // - Protected: caller-checked accessor enforcing `caller->isa(pkg)`.
      // - Public / Internal: accessor method (read-only enforces croak on write).
      foreach (var field in cls.Fields) {
        if (field.Visibility == IrVisibility.Private)
          continue;

        var id = FieldPerlIdentifier(field);
        var keyExpr = this.InsideOutKey("$self");
        if (field.Visibility == IrVisibility.Protected) {
          text.Append("    sub _prot_").Append(id).Append(" {\n");
          text.Append("        my $self = shift;\n");
          text.Append("        Carp::croak('Protected field access denied') unless caller->isa('").Append(pkg).Append("');\n");
          if (!field.IsReadOnly)
            text.Append("        ").Append(InsideOutHashElement(field, keyExpr)).Append(" = shift if @_;\n");
          text.Append("        return ").Append(InsideOutHashElement(field, keyExpr)).Append(";\n    }\n");
          continue;
        }

        text.Append("    sub ").Append(id).Append(" {\n");
        text.Append("        my $self = shift;\n");
        if (field.IsReadOnly)
          text.Append("        Carp::croak('Cannot write read-only field ").Append(id).Append("') if @_;\n");
        else
          text.Append("        ").Append(InsideOutHashElement(field, keyExpr)).Append(" = shift if @_;\n");
        text.Append("        return ").Append(InsideOutHashElement(field, keyExpr)).Append(";\n    }\n");
      }

      // Emit DESTROY cleanup for Perl 5.6 and 5.8 (Perl 5.10+ uses Hash::Util::FieldHash automatic GC)
      if (!this._profile.HasFieldHash) {
        text.Append("    sub DESTROY {\n");
        text.Append("        my ($self) = @_;\n");
        if (cls.Fields.Count > 0) {
          text.Append("        my $_id = ").Append(this.InsideOutKey("$self")).Append(";\n");
          foreach (var field in cls.Fields)
            text.Append("        delete ").Append(InsideOutHashElement(field, "$_id")).Append(";\n");
        }
        if (cls.Base is { } destroyBase)
          text.Append("        ").Append(this.ClassPackage(destroyBase)).Append("::DESTROY($self);\n");
        text.Append("        return;\n    }\n\n");
      }

      foreach (var ctor in cls.Constructors)
        this.EmitClassConstructor(cls, ctor, functionMap[ctor.Function], text);
      this.EmitPublicNew(cls, functionMap, text);

      foreach (var method in cls.Methods)
        if (method.Dispatch != IrDispatch.Abstract)
          this.EmitClassMethod(cls, method, functionMap[method.Function], text);

      if (cls.IsRecord)
        this.EmitClassEqualsMethod(cls, text);

      text.Append("}\n\n");
      this._currentClass = null;
    }

    private void EmitPublicNew(IrClassType cls, Dictionary<string, IrFunction> functionMap, StringBuilder text) {
      if (cls.IsAbstract || cls.Constructors.Count == 0)
        return;

      text.Append("    sub new {\n");
      text.Append("        my $class = shift;\n");
      if (cls.Constructors.Count == 1) {
        var ctorName = this._ctorNames[cls.Constructors[0]];
        text.Append("        return $class->").Append(ctorName).Append("(@_);\n");
      } else {
        for (var i = 0; i < cls.Constructors.Count; ++i) {
          var ctor = cls.Constructors[i];
          var arity = functionMap[ctor.Function].Parameters.Count - 1;
          var kw = i == 0 ? "if" : "elsif";
          text.Append("        ").Append(kw).Append(" (@_ == ").Append(arity.ToString(CultureInfo.InvariantCulture))
            .Append(") { return $class->").Append(this._ctorNames[ctor]).Append("(@_); }\n");
        }
        text.Append("        die \"ContractViolation: no matching constructor for ").Append(cls.Name).Append("\\n\";\n");
      }
      text.Append("    }\n\n");
    }

    private void EmitClassConstructor(IrClassType cls, IrConstructor ctor, IrFunction function, StringBuilder text) {
      var ctorSub = this._ctorNames[ctor];
      var initSub = "_init" + ctorSub;

      // 1. Factory `$class->_ctor_N(@args)` that allocates/blesses the instance and invokes `_init_ctor_N`
      text.Append("    sub ").Append(ctorSub).Append(" {\n");
      text.Append("        my $class = shift;\n");
      if (this._profile.ObjectModel == PerlObjectModel.Moose) {
        text.Append("        my $self = $class->new();\n");
      } else if (this._profile.ObjectModel == PerlObjectModel.InsideOut) {
        text.Append("        my $self = bless \\(my $_id), $class;\n");
        text.Append("        $self->_ply_init_fields();\n");
      } else {
        var fields = cls.AllFields.Select(f => $"{PerlNaming.HashKey(f.Name)} => {this.DefaultValue(f.Type)}");
        text.Append("        my $self = bless { ").Append(string.Join(", ", fields)).Append(" }, $class;\n");
      }
      text.Append("        ").Append(this.ClassPackage(cls)).Append("::").Append(initSub).Append("($self, @_);\n");
      text.Append("        return $self;\n");
      text.Append("    }\n\n");

      // 2. Initializer body `_init_ctor_N($self, @args)` (also callable by derived constructors / `this(...)` chaining!)
      this.EmitClassSubBody(initSub, function, text);
    }

    private void EmitClassMethod(IrClassType cls, IrMethod method, IrFunction function, StringBuilder text) {
      _ = cls;
      var subName = this.MethodPerlName(method);
      this.EmitClassSubBody(subName, function, text);
    }

    private void EmitClassSubBody(string subName, IrFunction function, StringBuilder text) {
      this._currentFunction = function;
      this._locals.Clear();
      this._loops.Clear();
      this._tryFrames.Clear();
      this._isIterator = false;
      this._asyncResultVar = null;
      this._seqReturnFrame = null;
      this._tempCounter = 0;
      this._labelCounter = 0;

      var taken = new HashSet<string>(StringComparer.Ordinal);
      foreach (var parameter in function.Parameters) {
        var name = PerlNaming.Free(PerlNaming.Local(parameter.Name), taken.Contains);
        taken.Add(name);
        this._locals[parameter] = "$" + name;
      }
      foreach (var local in function.Locals) {
        var name = PerlNaming.Free(PerlNaming.Local(local.Name), taken.Contains);
        taken.Add(name);
        this._locals[local] = "$" + name;
      }

      this._integerFunction = IsIntegerOnlyFunction(function);
      this._ranges = new RangeAnalysis(function);

      text.Append("    sub ").Append(subName).Append(" {\n");
      if (this._integerFunction)
        text.Append("        use integer;\n");
      if (function.Parameters.Count > 0)
        text.Append("        my (").AppendJoin(", ", function.Parameters.Select(p => this._locals[p])).Append(") = @_;\n");

      this.EmitFunctionBody(function, text, 2);

      text.Append("    }\n\n");
      this._currentFunction = null;
      this._isIterator = false;
      this._asyncResultVar = null;
      this._seqReturnFrame = null;
      this._ranges = null;
    }

    private string EmitClassFieldLoad(IrFieldLoad load) {
      var target = this.EmitExpr(load.Target);
      if (load.Field == IrExceptionClass.Message)
        return $"{this.HelperCall(PerlHelpers.TrapMessage)}({target})";

      var cls = load.Target.Type.ClassType!;
      var owner = cls.OwnerOf(load.Field)!;
      var access = this.FormatObjectFieldRead(owner, load.Field, "$_o");
      return $"do {{ my $_o = {target}; die \"NullReference: null object\\n\" unless defined $_o; {this.CopyIfStruct(load.Type, access)} }}";
    }

    private void EmitClassFieldStore(IrFieldStore store, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var target = this.EmitExpr(store.Target);
      var value = this.CopyIfStruct(store.Field.Type, this.EmitExpr(store.Value));
      var cls = store.Target.Type.ClassType!;
      var owner = cls.OwnerOf(store.Field)!;
      var write = this.FormatObjectFieldWrite(owner, store.Field, "$_o", "$_v");
      text.Append(pad).Append("do { my ($_o, $_v) = (").Append(target).Append(", ").Append(value)
        .Append("); die \"NullReference: null object\\n\" unless defined $_o; ").Append(write).Append("; };\n");
    }

    private string FormatObjectFieldRead(IrClassType owner, IrField field, string objVar) {
      if (this._profile.ObjectModel == PerlObjectModel.Moose)
        return $"{objVar}->{this.MooseAttrName(field)}()";

      if (this._profile.ObjectModel == PerlObjectModel.InsideOut) {
        if (ReferenceEquals(this._currentClass, owner))
          return InsideOutHashElement(field, this.InsideOutKey(objVar));
        if (field.Visibility == IrVisibility.Protected)
          return $"{objVar}->_prot_{FieldPerlIdentifier(field)}()";
        return $"{objVar}->{FieldPerlIdentifier(field)}()";
      }

      return $"{objVar}->{{{PerlNaming.HashKey(field.Name)}}}";
    }

    private string FormatObjectFieldWrite(IrClassType owner, IrField field, string objVar, string valVar) {
      if (this._profile.ObjectModel == PerlObjectModel.Moose) {
        var attr = this.MooseAttrName(field);
        var writer = field.IsReadOnly ? $"_set_{attr}" : attr;
        return $"{objVar}->{writer}({valVar})";
      }

      if (this._profile.ObjectModel == PerlObjectModel.InsideOut) {
        if (ReferenceEquals(this._currentClass, owner))
          return $"{InsideOutHashElement(field, this.InsideOutKey(objVar))} = {valVar}";
        if (field.Visibility == IrVisibility.Protected)
          return $"{objVar}->_prot_{FieldPerlIdentifier(field)}({valVar})";
        return $"{objVar}->{FieldPerlIdentifier(field)}({valVar})";
      }

      return $"{objVar}->{{{PerlNaming.HashKey(field.Name)}}} = {valVar}";
    }

    private string EmitClassObjectNew(IrNew objNew) {
      var cls = objNew.Type.ClassType!;
      if (IrExceptionClass.IsException(cls))
        return this.EmitExceptionConstruction(cls, objNew.Arguments);

      var pkg = this.ClassPackage(cls);
      var ctorSub = this._ctorNames[objNew.Constructor];
      var args = string.Join(", ", objNew.Arguments.Select(this.EmitExpr));
      return $"{pkg}->{ctorSub}({args})";
    }

    private string EmitClassMethodCall(IrMethodCall call) {
      var methodName = this.MethodPerlName(call.Method);
      var evalArgs = call.Arguments.Select(this.EmitExpr).ToList();

      if (call.Target is null) {
        var owner = this.ClassPackage(this._classFunctionOwners[call.Method.Function]);
        return $"{owner}::{methodName}({string.Join(", ", evalArgs)})";
      }

      var receiver = this.EmitExpr(call.Target);
      var argsJoined = string.Join(", ", evalArgs);
      if (call.Kind == IrMethodCallKind.Base || (call.Kind == IrMethodCallKind.Direct && call.Method.Visibility == IrVisibility.Private)) {
        var owner = this.ClassPackage(this._classFunctionOwners[call.Method.Function]);
        var allArgs = string.Join(", ", evalArgs.Prepend("$_r"));
        return $"do {{ my $_r = {receiver}; die \"NullReference: null receiver\\n\" unless defined $_r; {owner}::{methodName}({allArgs}) }}";
      }

      return $"do {{ my $_r = {receiver}; die \"NullReference: null receiver\\n\" unless defined $_r; $_r->{methodName}({argsJoined}) }}";
    }

    private string EmitInstanceCheck(string varName, IrType targetType) {
      if (targetType.ClassType is { } targetCls) {
        if (IrExceptionClass.IsException(targetCls))
          return $"{this.HelperCall(PerlHelpers.TrapMatches)}({varName}, '{ExceptionKindName(targetCls)}')";
        return $"(ref({varName}) && UNIVERSAL::isa({varName}, '{this.ClassPackage(targetCls)}'))";
      }
      var targetIface = targetType.InterfaceType!;
      return this._profile.ObjectModel == PerlObjectModel.Moose
        ? $"(ref({varName}) && UNIVERSAL::can({varName}, 'does') && {varName}->does('{this.InterfacePackage(targetIface)}'))"
        : $"(ref({varName}) && UNIVERSAL::can({varName}, '_ply_does_{PerlNaming.Snake(targetIface.Name)}'))";
    }

    private string EmitTypeTest(IrTypeTest test) {
      var val = this.EmitExpr(test.Value);
      var check = this.EmitInstanceCheck("$_t", test.Target);
      return $"do {{ my $_t = {val}; ({check}) ? 1 : 0 }}";
    }

    private string EmitAsCast(IrAsCast cast) {
      var val = this.EmitExpr(cast.Value);
      var check = this.EmitInstanceCheck("$_t", cast.Type);
      return $"do {{ my $_t = {val}; defined($_t) && ({check}) ? $_t : undef }}";
    }

    private string EmitCast(IrCast cast) {
      var val = this.EmitExpr(cast.Value);
      var check = this.EmitInstanceCheck("$_t", cast.Type);
      return $"do {{ my $_t = {val}; (!defined($_t) || ({check})) ? $_t : die(\"InvalidCast: cannot cast to {cast.Type}\\n\") }}";
    }

    private string EmitBoundMethod(IrBoundMethod bound) {
      var methodName = this.MethodPerlName(bound.Method);
      if (bound.Receiver is null) {
        var owner = this.ClassPackage(this._classFunctionOwners[bound.Method.Function]);
        return $"\\&{owner}::{methodName}";
      }
      var recv = this.EmitExpr(bound.Receiver);
      return $"do {{ my $_r = {recv}; die \"NullReference: null receiver\\n\" unless defined $_r; sub {{ $_r->{methodName}(@_) }} }}";
    }

    private void EmitUsing(IrUsing @using, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var resName = this.LocalName(@using.Resource);
      text.Append(pad).Append("my ").Append(resName).Append(" = ").Append(this.EmitExpr(@using.Initializer)).Append(";\n");
      var disposeCall = new IrBlock([
        new IrIf(
          new IrUnary(IrUnaryOp.LogicalNot, new IrIsNull(new IrLocalRef(@using.Resource))),
          new([new IrExprStmt(new IrMethodCall(IrType.Void, @using.Dispose, new IrLocalRef(@using.Resource), []) { Kind = IrMethodCallKind.Virtual })]),
          null)
      ]);
      this.EmitTry(new IrTry(@using.Body, [], disposeCall), text, indent);
    }

    private string EmitClassRecordEquals(IrStructEquals equals) {
      var l = this.EmitExpr(equals.Left);
      var r = this.EmitExpr(equals.Right);
      return $"do {{ my ($_p, $_q) = ({l}, {r}); (!defined($_p) && !defined($_q)) || (defined($_p) && defined($_q) && $_p->_ply_equals($_q)) ? 1 : 0 }}";
    }

    private void EmitClassEqualsMethod(IrClassType cls, StringBuilder text) {
      var pkg = this.ClassPackage(cls);
      text.Append("    sub _ply_equals {\n");
      text.Append("        my ($_p, $_q) = @_;\n");
      text.Append("        return 0 unless ref($_p) eq ref($_q) && UNIVERSAL::isa($_q, '").Append(pkg).Append("');\n");
      foreach (var field in cls.AllFields) {
        var owner = cls.OwnerOf(field)!;
        var pVal = this.FormatObjectFieldRead(owner, field, "$_p");
        var qVal = this.FormatObjectFieldRead(owner, field, "$_q");
        if (field.Type == IrType.String)
          text.Append("        return 0 if ").Append(pVal).Append(" ne ").Append(qVal).Append(";\n");
        else if (field.Type.IsFloat)
          text.Append("        if (").Append(pVal).Append(" == ").Append(pVal).Append(" || ").Append(qVal).Append(" == ").Append(qVal).Append(") { return 0 if ").Append(pVal).Append(" != ").Append(qVal).Append("; }\n");
        else
          text.Append("        return 0 if ").Append(pVal).Append(" != ").Append(qVal).Append(";\n");
      }
      text.Append("        return 1;\n    }\n\n");
    }

    private string EmitQualifyCall(string functionName) {
      // Inside a class constructor/method, calls to base/chained constructors or module functions need proper qualification!
      if (this._classFunctionOwners.TryGetValue(functionName, out var owner)) {
        var ctor = owner.Constructors.FirstOrDefault(c => c.Function == functionName);
        if (ctor is not null)
          return $"{this.ClassPackage(owner)}::_init{this._ctorNames[ctor]}";
        var method = owner.Methods.First(m => m.Function == functionName);
        return $"{this.ClassPackage(owner)}::{this.MethodPerlName(method)}";
      }
      var name = this.FunctionPerlName(functionName);
      return this._currentClass is not null ? $"{this._package}::{name}" : name;
    }

  }

}
