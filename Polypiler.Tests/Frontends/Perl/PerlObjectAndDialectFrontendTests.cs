using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Perl;

/// <summary>Tests for Perl 5.6/5.8 dialect imports and blessed-hash, inside-out and Moose object models.</summary>
[TestFixture]
public sealed class PerlObjectAndDialectFrontendTests {

  private static IrModule Import(string file, string source, string contracts = "") {
    var result = PerlFrontend.Import(file, source, new PerlImportOptions { Contracts = contracts });
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  [Test]
  public void Perl56And58Modules_WithExporterIsaAndConstants_ImportAndInterpret() {
    const string src56 = """
      package Legacy56;
      use 5.006;
      use strict;
      use warnings;
      use Exporter ();
      our @ISA = qw(Exporter);
      our @EXPORT_OK = qw(bump);
      use constant STEP => 7;

      sub bump {
        my ($x) = @_;
        return $x + STEP;
      }
      1;
      """;
    var mod56 = Import("Legacy56.pm", src56, "Legacy56::bump.x: i32\nLegacy56::bump.return: i32");
    Assert.That(IrInterpreter.Call(mod56, "bump", [35]), Is.EqualTo(42));

    const string src58 = """
      package Legacy58;
      use 5.008;
      use strict;
      use warnings;
      use base 'Exporter';
      our @EXPORT_OK = qw(combine);
      use constant { BASE => 10, MULT => 3 };

      sub combine {
        my ($x) = @_;
        return ($x + BASE) * MULT;
      }
      1;
      """;
    var mod58 = Import("Legacy58.pm", src58, "Legacy58::combine.x: i32\nLegacy58::combine.return: i32");
    Assert.That(IrInterpreter.Call(mod58, "combine", [4]), Is.EqualTo(42));
  }

  [Test]
  public void BlessedHashHierarchy_WithInheritanceSuperAndIsa_ImportsAndInterprets() {
    const string src = """
      package Base;
      use strict;
      use warnings;
      sub new {
        my ($class, $val) = @_;
        return bless { val => $val }, $class;
      }
      sub score {
        my ($self) = @_;
        return $self->{val};
      }

      package Derived;
      use strict;
      use warnings;
      our @ISA = ('Base');
      sub new {
        my ($class, $val, $bonus) = @_;
        my $self = bless { val => $val, bonus => $bonus }, $class;
        return $self;
      }
      sub score {
        my ($self) = @_;
        return $self->SUPER::score() + $self->{bonus};
      }

      package main;
      sub run_blessed {
        my ($a, $b) = @_;
        my $d = Derived->new($a, $b);
        my $is_base = $d->isa('Base') ? 1 : 0;
        return $d->score() + $is_base;
      }
      1;
      """;
    const string contracts = """
      Base::new.val: i32
      Base::field.val: i32
      Base::score.return: i32
      Derived::new.val: i32
      Derived::new.bonus: i32
      Derived::field.bonus: i32
      Derived::score.return: i32
      run_blessed.a: i32
      run_blessed.b: i32
      run_blessed.return: i32
      """;
    var mod = Import("blessed.pl", src, contracts);
    var derived = mod.Types.OfType<IrClassType>().Single(c => c.Name == "Derived");
    Assert.That(derived.Base?.Name, Is.EqualTo("Base"));
    Assert.That(IrInterpreter.Call(mod, "run_blessed", [30, 11]), Is.EqualTo(42));
  }

  [Test]
  public void InsideOutObjects_In56_58_AndFieldHash_KeepPrivateFieldsEncapsulated() {
    const string src58 = """
      package Vault;
      use strict;
      use warnings;
      use Scalar::Util qw(refaddr);

      my %_secret;
      my %label;

      sub new {
        my ($class, $secret, $label) = @_;
        my $self = bless \(my $o), $class;
        $_secret{refaddr($self)} = $secret;
        $label{refaddr($self)} = $label;
        return $self;
      }
      sub reveal {
        my ($self, $mult) = @_;
        return $_secret{refaddr($self)} * $mult;
      }
      sub set_secret {
        my ($self, $v) = @_;
        $_secret{refaddr($self)} = $v;
        return;
      }
      sub DESTROY {
        my ($self) = @_;
        delete $_secret{refaddr($self)};
        delete $label{refaddr($self)};
      }

      package main;
      sub test_vault {
        my ($v) = @_;
        my $box = Vault->new($v, 1);
        $box->set_secret($v + 1);
        return $box->reveal(2);
      }
      1;
      """;
    const string contracts58 = """
      Vault::new.secret: i32
      Vault::new.label: i32
      Vault::field._secret: i32
      Vault::field.label: i32
      Vault::reveal.mult: i32
      Vault::reveal.return: i32
      Vault::set_secret.v: i32
      Vault::set_secret.return: void
      test_vault.v: i32
      test_vault.return: i32
      """;
    var mod58 = Import("vault58.pl", src58, contracts58);
    var vaultCls = mod58.Types.OfType<IrClassType>().Single(c => c.Name == "Vault");
    Assert.That(vaultCls.Field("_secret")!.Visibility, Is.EqualTo(IrVisibility.Private));
    Assert.That(vaultCls.Field("label")!.Visibility, Is.EqualTo(IrVisibility.Public));
    Assert.That(IrInterpreter.Call(mod58, "test_vault", [20]), Is.EqualTo(42));

    // Also test 5.6 `"$self"` style and 5.10+ `Hash::Util::FieldHash` style
    const string src56AndFh = """
      package Box56;
      use 5.006;
      use strict;
      use warnings;
      my %_pin;
      sub new {
        my ($class, $pin) = @_;
        my $self = bless \(my $o), $class;
        $_pin{"$self"} = $pin;
        return $self;
      }
      sub check {
        my ($self) = @_;
        return $_pin{"$self"};
      }
      sub DESTROY { my ($self) = @_; delete $_pin{"$self"}; }

      package BoxFH;
      use strict;
      use warnings;
      use Hash::Util::FieldHash qw(fieldhash);
      fieldhash my %_code;
      sub new {
        my ($class, $code) = @_;
        my $self = bless \(my $o), $class;
        $_code{$self} = $code;
        return $self;
      }
      sub get_code {
        my ($self) = @_;
        return $_code{$self};
      }

      package main;
      sub sum_boxes {
        my ($a, $b) = @_;
        my $b1 = Box56->new($a);
        my $b2 = BoxFH->new($b);
        return $b1->check() + $b2->get_code();
      }
      1;
      """;
    const string contracts56 = """
      Box56::new.pin: i32
      Box56::field._pin: i32
      Box56::check.return: i32
      BoxFH::new.code: i32
      BoxFH::field._code: i32
      BoxFH::get_code.return: i32
      sum_boxes.a: i32
      sum_boxes.b: i32
      sum_boxes.return: i32
      """;
    var modFh = Import("vault_fh.pl", src56AndFh, contracts56);
    Assert.That(IrInterpreter.Call(modFh, "sum_boxes", [19, 23]), Is.EqualTo(42));
  }

  [Test]
  public void MooseClassesAndRoles_ImportWithAttributesInheritanceAndDoes() {
    const string src = """
      package Measurable;
      use Moose::Role;
      requires 'measure';

      package BaseItem;
      use Moose;
      has 'weight' => (is => 'ro', isa => 'Int', default => 0);
      __PACKAGE__->meta->make_immutable;

      package Parcel;
      use Moose;
      extends 'BaseItem';
      has '_secret_fee' => (is => 'rw', isa => 'Int', default => 5);
      has 'extra' => (is => 'rw', isa => 'Int', default => 0);

      sub measure {
        use integer;
        my ($self) = @_;
        return $self->weight() + $self->_secret_fee() + $self->extra();
      }

      sub bump_extra {
        use integer;
        my ($self, $delta) = @_;
        $self->extra($self->extra() + $delta);
        return;
      }

      with 'Measurable';
      __PACKAGE__->meta->make_immutable;

      package main;
      sub run_moose {
        use integer;
        my ($w, $e) = @_;
        my $p = Parcel->new(weight => $w, _secret_fee => 10, extra => $e);
        $p->bump_extra(2);
        my $d = $p->does('Measurable') ? 1 : 0;
        return $p->measure() + $d;
      }
      1;
      """;
    const string contracts = """
      Measurable::measure.return: i64
      Parcel::measure.return: i64
      Parcel::bump_extra.delta: i64
      Parcel::bump_extra.return: void
      run_moose.w: i64
      run_moose.e: i64
      run_moose.return: i64
      """;
    var mod = Import("moose.pl", src, contracts);
    var role = mod.Types.OfType<IrInterfaceType>().Single(i => i.Name == "Measurable");
    var parcel = mod.Types.OfType<IrClassType>().Single(c => c.Name == "Parcel");
    Assert.That(parcel.Base?.Name, Is.EqualTo("BaseItem"));
    Assert.That(parcel.Interfaces, Does.Contain(role));
    Assert.That(parcel.Field("_secret_fee")!.Visibility, Is.EqualTo(IrVisibility.Private));
    Assert.That(parcel.Base!.Field("weight")!.IsReadOnly, Is.True);
    Assert.That(IrInterpreter.Call(mod, "run_moose", [20L, 9L]), Is.EqualTo(42L));
  }

  [Test]
  public void MooseAttributeKinds_RoleDispatch_AndDerivedInheritedCtor_ImportAndInterpret() {
    const string src = """
      package BaseRole;
      use Moose::Role;
      requires 'base_val';

      package ExtendedRole;
      use Moose::Role;
      with 'BaseRole';
      requires 'ext_val';

      package RichItem;
      use Moose;
      has 'ratio' => (is => 'rw', isa => 'Num', default => 1.5);
      has 'tag' => (is => 'ro', isa => 'Str', reader => 'get_tag', writer => '_set_tag', default => 'ok');
      has 'flag' => (is => 'rw', isa => 'Bool', default => 1);
      has 'inferred_f' => (is => 'rw', default => 2.5);
      has 'inferred_s' => (is => 'rw', default => 'hi');
      has 'inferred_i' => (is => 'bare', default => 3);
      sub _const_zero { return 0; }
      sub touch { my ($self) = @_; $self->flag(1); }
      sub base_val { my ($self) = @_; return $self->flag() ? 10 : 0; }
      sub ext_val { my ($self) = @_; $self->_set_tag('done'); return 32; }
      with 'ExtendedRole';
      __PACKAGE__->meta->make_immutable;

      package BaseBox;
      use strict;
      use warnings;
      sub new { my ($class, $x) = @_; return bless { x => $x, _hidden => 1 }, $class; }
      sub get_x { my ($self) = @_; return $self->{x}; }

      package SubBox;
      use strict;
      use warnings;
      our @ISA = ('BaseBox');
      sub doubled { my ($self) = @_; return $self->get_x() * 2; }

      package BoxQual;
      use strict;
      use warnings;
      use Scalar::Util ();
      my %_f_pin;
      sub new {
        my ($class, $pin) = @_;
        my $self = bless \(my $o), $class;
        $_f_pin{Scalar::Util::refaddr($self)} = $pin;
        return $self;
      }
      sub pin {
        my ($self) = @_;
        return $_f_pin{Scalar::Util::refaddr($self)};
      }
      sub DESTROY { my ($self) = @_; delete $_f_pin{Scalar::Util::refaddr($self)}; }

      package main;
      sub call_role {
        my ($r) = @_;
        return $r->base_val() + $r->ext_val();
      }
      sub run_all {
        my ($x) = @_;
        my $item = RichItem->new(1.0, 'a', 1, 2.0, 'b', 3);
        $item->touch();
        my $item2 = RichItem->new(flag => 1);
        my $sub = SubBox->new($x);
        my $bq = BoxQual->new(10);
        return call_role($item) + $sub->doubled() + $bq->pin() + ($item2->get_tag() eq '' ? 0 : 0);
      }
      1;
      """;
    const string contracts = """
      BaseRole::base_val.return: i32
      ExtendedRole::ext_val.return: i32
      RichItem::field.inferred_i: i32
      RichItem::_const_zero.return: i32
      RichItem::touch.return: void
      RichItem::base_val.return: i32
      RichItem::ext_val.return: i32
      BaseBox::new.x: i32
      BaseBox::field.x: i32
      BaseBox::get_x.return: i32
      SubBox::doubled.return: i32
      BoxQual::new.pin: i32
      BoxQual::pin.return: i32
      call_role.r: ExtendedRole
      call_role.return: i32
      run_all.x: i32
      run_all.return: i32
      """;
    var mod = Import("moose_full.pl", src, contracts);
    Assert.That(IrInterpreter.Call(mod, "run_all", [5]), Is.EqualTo(62));
  }

  [Test]
  public void UnsupportedObjectPatterns_ReportPLY1740() {
    var cases = new (string Source, string Contracts)[] {
      ("package A; use Moose; extends 'NoSuchBase';", ""),
      ("package A; use Moose; with 'NoSuchRole';", ""),
      ("package B1; use Moose; package B2; use Moose; package C; use Moose; extends 'B1', 'B2';", ""),
      ("package R; use Moose::Role; with 'MissingRole';", ""),
      ("package X; use parent -norequire, 'ExternalBase'; sub f { return 1; }", ""),
      ("package B; use Moose; package D; use Moose; extends 'B'; sub f { my ($s) = @_; return $s->SUPER::nope(); }", ""),
      ("package R; use Moose::Role; requires 'a'; package main; sub f { my ($r) = @_; return $r->missing(); }", "f.r: R"),
      ("package A; use Moose; has 'x' => (is => 'rw'); package main; sub f { my $a = A->new(); return $a->unknown_m(); }", ""),
      ("package main; sub f { my ($o, $m) = @_; return $o->$m(); }", ""),
    };
    foreach (var (src, c) in cases) {
      var res = PerlFrontend.Import("bad_oo.pl", src, new PerlImportOptions { Contracts = c });
      Assert.That(res.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1740"),
        $"Source: {src}\nActual: {string.Join("; ", res.Diagnostics)}");
    }
  }

}
