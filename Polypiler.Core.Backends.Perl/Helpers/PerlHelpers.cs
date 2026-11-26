using System.Collections.Generic;
using Polypiler.Core;

namespace Polypiler.Core.Backends.Perl;

/// <summary>
/// Private runtime support subs emitted into a Perl module only when the emitted code references them.
/// All helpers rely solely on Perl 5 core modules and include Perl 5.6/5.8-compatible implementations.
/// </summary>
internal static class PerlHelpers {

  public const string OriginFolder = "Polypiler.Core.Backends.Perl/Helpers";

  public const string FloatDivide = "_ply_fdiv_f64";
  public const string FloatDivide56 = "_ply_fdiv_f64_56";
  public const string FloatRemainder = "_ply_frem_f64";
  public const string FloatRemainder56 = "_ply_frem_f64_56";
  public const string FloatSignBit = "_ply_signbit";
  public const string FloatTrunc = "_ply_trunc";
  public const string FloatRoundAway = "_ply_round";
  public const string FloatRoundEven = "_ply_rint";
  public const string FloatCopySign = "_ply_copysign";
  public const string FloatIsNormal = "_ply_isnormal";
  public const string SwapEndian = "_ply_endian_bytes";
  public const string RoundF32 = "_ply_round_f32";
  public const string IntToF32 = "_ply_int_to_f32";
  public const string ArrayCopy = "_ply_array_copy";
  public const string ArrayEq = "_ply_array_eq";
  public const string ViewSlice = "_ply_view_slice";
  public const string ViewCopy = "_ply_view_copy";
  public const string ViewEqual = "_ply_view_equal";
  public const string TrapKind = "_trap_kind";
  public const string TrapMessage = "_trap_message";
  public const string TrapMatches = "_trap_matches";
  public const string StrTrim = "_ply_str_trim";
  public const string StrPad = "_ply_str_pad";
  public const string TaskFromResult = "_ply_task_from_result";
  public const string TaskAwait = "_ply_await";
  public const string TaskWhenAll = "_ply_when_all";

  public static IReadOnlyList<string> Order { get; } = [
    FloatSignBit, FloatDivide, FloatDivide56, FloatRemainder, FloatRemainder56,
    FloatTrunc, FloatRoundAway, FloatRoundEven, FloatCopySign, FloatIsNormal,
    SwapEndian, RoundF32, IntToF32, ArrayCopy, ArrayEq,
    ViewSlice, ViewCopy, ViewEqual, TrapKind, TrapMessage, TrapMatches,
    StrTrim, StrPad, TaskFromResult, TaskAwait, TaskWhenAll
  ];

  public static IReadOnlyDictionary<string, string> Bodies { get; } = new Dictionary<string, string> {
    [FloatSignBit] = """
      sub _ply_signbit {
          return (unpack('Q', pack('d', $_[0])) >> 63) & 1;
      }
      """,
    [FloatDivide] = """
      sub _ply_fdiv_f64 {
          my ($left, $right) = @_;
          return $left / $right if $right != 0;
          return $left if POSIX::isnan($left);
          return POSIX::NAN() if $left == 0;
          return POSIX::signbit($left) != POSIX::signbit($right) ? -POSIX::HUGE_VAL() : POSIX::HUGE_VAL();
      }
      """,
    [FloatDivide56] = """
      sub _ply_fdiv_f64_56 {
          my ($left, $right) = @_;
          return $left / $right if $right != 0;
          return $left if $left != $left;
          return unpack('d', pack('Q', 9221120237041090560)) if $left == 0;
          return _ply_signbit($left) != _ply_signbit($right) ? -(9**9**9) : (9**9**9);
      }
      """,
    [FloatRemainder] = """
      sub _ply_frem_f64 {
          my ($left, $right) = @_;
          return POSIX::NAN() if POSIX::isnan($left) || POSIX::isnan($right) || $right == 0 || POSIX::isinf($left);
          return $left if POSIX::isinf($right);
          return POSIX::fmod($left, $right);
      }
      """,
    [FloatRemainder56] = """
      sub _ply_frem_f64_56 {
          my ($left, $right) = @_;
          return unpack('d', pack('Q', 9221120237041090560))
              if $left != $left || $right != $right || $right == 0 || $left == 9**9**9 || $left == -9**9**9;
          return $left if $right == 9**9**9 || $right == -9**9**9;
          return POSIX::fmod($left, $right);
      }
      """,
    [FloatTrunc] = """
      sub _ply_trunc {
          my ($v) = @_;
          return $v if $v != $v || $v == 9**9**9 || $v == -9**9**9 || $v == 0;
          return $v > 0 ? POSIX::floor($v) : POSIX::ceil($v);
      }
      """,
    [FloatRoundAway] = """
      sub _ply_round {
          my ($v) = @_;
          return $v if $v != $v || $v == 9**9**9 || $v == -9**9**9 || $v == 0;
          return $v > 0 ? POSIX::floor($v + 0.5) : POSIX::ceil($v - 0.5);
      }
      """,
    [FloatRoundEven] = """
      sub _ply_rint {
          my ($v) = @_;
          return $v if $v != $v || $v == 9**9**9 || $v == -9**9**9 || $v == 0;
          my $f = POSIX::floor($v);
          my $diff = $v - $f;
          return $f if $diff < 0.5;
          return $f + 1.0 if $diff > 0.5;
          return (POSIX::fmod($f, 2.0) == 0) ? $f : $f + 1.0;
      }
      """,
    [FloatCopySign] = """
      sub _ply_copysign {
          my ($mag, $sgn) = @_;
          my $mb = unpack('Q', pack('d', $mag)) & 9223372036854775807;
          my $sb = unpack('Q', pack('d', $sgn)) & 9223372036854775808;
          return unpack('d', pack('Q', $mb | $sb));
      }
      """,
    [FloatIsNormal] = """
      sub _ply_isnormal {
          my $e = (unpack('Q', pack('d', $_[0])) >> 52) & 0x7FF;
          return ($e > 0 && $e < 2047) ? 1 : 0;
      }
      """,
    [SwapEndian] = """
      sub _ply_endian_bytes {
          my ($bytes, $want_little) = @_;
          my $host_little = (unpack('C', pack('S', 1)) == 1) ? 1 : 0;
          return ($host_little == $want_little) ? $bytes : scalar reverse($bytes);
      }
      """,
    [RoundF32] = """
      sub _ply_round_f32 {
          return unpack('f', pack('f', $_[0]));
      }
      """,
    [IntToF32] = """
      sub _ply_int_to_f32 {
          my ($value) = @_;
          my $negative = $value < 0;
          my $magnitude = $negative ? -$value : $value;
          my $scale = 1;
          if ($magnitude >= 9007199254740992) {
              $magnitude = ($magnitude >> 11) | (($magnitude & 2047) != 0 ? 1 : 0);
              $scale = 2048;
          }
          my $rounded = unpack('f', pack('f', $magnitude)) * $scale;
          return unpack('f', pack('f', $negative ? -$rounded : $rounded));
      }
      """,
    [ArrayCopy] = """
      sub _ply_array_copy {
          my ($src, $si, $dst, $di, $len) = @_;
          die "IndexOutOfRange: invalid copy range\n"
              if $len < 0 || $si < 0 || $di < 0 || $si + $len > scalar(@$src) || $di + $len > scalar(@$dst);
          return if $len == 0;
          @$dst[$di .. $di + $len - 1] = @$src[$si .. $si + $len - 1];
          return;
      }
      """,
    [ArrayEq] = """
      sub _ply_array_eq {
          my ($left, $right) = @_;
          return 0 if scalar(@$left) != scalar(@$right);
          for my $i (0 .. $#$left) {
              return 0 if $left->[$i] != $right->[$i];
          }
          return 1;
      }
      """,
    [ViewSlice] = """
      sub _ply_view_slice {
          my ($view, $start, $len) = @_;
          my ($buf, $off, $vlen) = @$view;
          $len = $vlen - $start unless defined $len;
          die "ContractViolation: slice out of range\n" if $start < 0 || $len < 0 || $start + $len > $vlen;
          return [$buf, $off + $start, $len];
      }
      """,
    [ViewCopy] = """
      sub _ply_view_copy {
          my ($src, $dst, $try_only) = @_;
          my ($sb, $so, $sl) = @$src;
          my ($db, $do, $dl) = @$dst;
          if ($sl > $dl) {
              return 0 if $try_only;
              die "ContractViolation: destination too short\n";
          }
          if ($sl > 0) {
              @$db[$do .. $do + $sl - 1] = @$sb[$so .. $so + $sl - 1];
          }
          return 1;
      }
      """,
    [ViewEqual] = """
      sub _ply_view_equal {
          my ($left, $right) = @_;
          my ($lb, $lo, $ll) = @$left;
          my ($rb, $ro, $rl) = @$right;
          return 0 if $ll != $rl;
          for my $i (0 .. $ll - 1) {
              my ($x, $y) = ($lb->[$lo + $i], $rb->[$ro + $i]);
              if ($x != $x && $y != $y) { next; }
              return 0 if $x != $y;
          }
          return 1;
      }
      """,
    [TrapKind] = """
      sub _trap_kind {
          my ($err) = @_;
          return $err->{kind} if ref($err) eq 'HASH' && defined $err->{kind};
          return 'DivideByZero' if $err =~ /\AIllegal (?:division|modulus) by zero\b/;
          return $1 if $err =~ /\A([A-Za-z_][A-Za-z0-9_]*):/;
          return 'User';
      }
      """,
    [TrapMessage] = """
      sub _trap_message {
          my ($err) = @_;
          return (defined $err->{message} ? $err->{message} : '') if ref($err) eq 'HASH';
          if ($err =~ /\A[A-Za-z_][A-Za-z0-9_]*:\s*(.*)\z/s) {
              my $msg = $1;
              $msg =~ s/\r?\n\z//;
              return $msg;
          }
          my $msg = "$err";
          $msg =~ s/\r?\n\z//;
          return $msg;
      }
      """,
    [TrapMatches] = """
      sub _trap_matches {
          my ($err, $target) = @_;
          return 1 if $target eq 'Exception';
          my $kind = _trap_kind($err);
          return 1 if $kind eq $target;
          return 1 if $target eq 'ArithmeticException' && ($kind eq 'DivideByZero' || $kind eq 'Overflow' || $kind eq 'ArithmeticException');
          return 1 if $target eq 'ArgumentException' && ($kind eq 'ContractViolation' || $kind eq 'ArgumentException');
          return 0;
      }
      """,
    [StrTrim] = """
      sub _ply_str_trim {
          my ($s, $mode) = @_;
          $s =~ s/\A\s+// if $mode == 0 || $mode == 1;
          $s =~ s/\s+\z// if $mode == 0 || $mode == 2;
          return $s;
      }
      """,
    [StrPad] = """
      sub _ply_str_pad {
          my ($s, $width, $fill, $at_start) = @_;
          my $need = $width - length($s);
          return $s if $need <= 0 || length($fill) == 0;
          my $pad = substr($fill x (int(($need + length($fill) - 1) / length($fill))), 0, $need);
          return $at_start ? $pad . $s : $s . $pad;
      }
      """,
    [TaskFromResult] = """
      sub _ply_task_from_result {
          my ($val) = @_;
          return { done => 1, value => $val };
      }
      """,
    [TaskAwait] = """
      sub _ply_await {
          my ($task) = @_;
          die $task->{error} if exists $task->{error};
          return $task->{value};
      }
      """,
    [TaskWhenAll] = """
      sub _ply_when_all {
          my ($is_void, @tasks) = @_;
          my @results;
          for my $t (@tasks) {
              return { done => 1, error => $t->{error} } if exists $t->{error};
              push @results, $t->{value} unless $is_void;
          }
          return $is_void ? { done => 1, value => undef } : { done => 1, value => \@results };
      }
      """
  };

  public static HelperText Descriptor(string name)
    => new(name, name, EmittedHelper.OwnLicence, EmittedHelper.OriginOf(OriginFolder), Bodies[name]);

}
