using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.Frontends.Perl.Syntax;

/// <summary>
/// What the parser knows of pragmas and modules without running them: the feature bundles of
/// <c>feature.pm</c> (perl 5.42.3, copied as the agreed tables they are), the pragmas that only
/// change lexical hints, and the subs a few core modules export with the prototypes that change how
/// a call of them parses.
/// </summary>
internal static class PerlPragmas {

  private static readonly string[] _default = ["apostrophe_as_package_separator", "bareword_filehandles", "indirect", "multidimensional", "smartmatch"];
  private static readonly string[] _v510 = ["apostrophe_as_package_separator", "bareword_filehandles", "indirect", "multidimensional", "say", "smartmatch", "state", "switch"];
  private static readonly string[] _v511 = [.. _v510, "unicode_strings"];
  private static readonly string[] _v515 = [.. _v511, "current_sub", "evalbytes", "fc", "unicode_eval"];
  private static readonly string[] _v523 = [.. _v515, "postderef_qq"];
  private static readonly string[] _v527 = [.. _v523, "bitwise"];
  private static readonly string[] _v535 = ["apostrophe_as_package_separator", "bareword_filehandles", "bitwise", "current_sub", "evalbytes", "fc", "isa", "postderef_qq", "say", "signatures", "smartmatch", "state", "unicode_eval", "unicode_strings"];
  private static readonly string[] _v537 = ["apostrophe_as_package_separator", "bitwise", "current_sub", "evalbytes", "fc", "isa", "module_true", "postderef_qq", "say", "signatures", "smartmatch", "state", "unicode_eval", "unicode_strings"];
  private static readonly string[] _v539 = [.. _v537, "try"];
  private static readonly string[] _v541 = ["bitwise", "current_sub", "evalbytes", "fc", "isa", "module_true", "postderef_qq", "say", "signatures", "state", "try", "unicode_eval", "unicode_strings"];
  private static readonly string[] _all = [
    "apostrophe_as_package_separator", "bareword_filehandles", "bitwise", "class", "current_sub", "declared_refs", "defer", "evalbytes",
    "extra_paired_delimiters", "fc", "indirect", "isa", "keyword_all", "keyword_any", "module_true", "multidimensional", "postderef_qq",
    "refaliasing", "say", "signatures", "smartmatch", "state", "switch", "try", "unicode_eval", "unicode_strings"
  ];

  /// <summary>The functions <c>use v5.40</c> imports from <c>builtin</c> (the <c>:5.40</c> bundle of builtin.pm).</summary>
  public static IReadOnlyList<string> BuiltinBundle { get; } = [
    "true", "false", "weaken", "unweaken", "is_weak", "blessed", "refaddr", "reftype", "ceil", "floor", "is_tainted", "trim", "indexed"
  ];

  /// <summary>The features of a fresh file.</summary>
  public static IReadOnlyCollection<string> DefaultFeatures => _default;

  /// <summary>Every feature perl 5.42 knows.</summary>
  public static IReadOnlyCollection<string> AllFeatures => _all;

  /// <summary>The bundle of a minor version of perl 5 (<c>use v5.36</c> is 36), as <c>%feature_bundle</c> maps it.</summary>
  public static IReadOnlyCollection<string> Bundle(int minor) => minor switch {
    < 10 => _default,
    10 => _v510,
    >= 11 and <= 14 => _v511,
    >= 15 and <= 22 => _v515,
    >= 23 and <= 26 => _v523,
    >= 27 and <= 34 => _v527,
    35 or 36 => _v535,
    37 or 38 => _v537,
    39 or 40 => _v539,
    _ => _v541
  };

  /// <summary>The features of a bundle named in <c>use feature ':5.10'</c>; <see langword="null"/> for no bundle.</summary>
  public static IReadOnlyCollection<string>? NamedBundle(string name) {
    if (name == "all")
      return _all;
    if (name == "default")
      return _default;
    var parts = name.Split('.');
    if (parts.Length is 2 or 3 && parts[0] == "5" && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
      return Bundle(minor == 9 ? 10 : minor);
    return null;
  }

  /// <summary>The minor version of perl 5 a <c>use VERSION</c> asks for (<c>5.036</c>, <c>v5.36</c>, <c>5.36.0</c>); -1 if it is not perl 5.</summary>
  public static int MinorVersion(string version) {
    var text = version.TrimStart('v').Replace("_", "", StringComparison.Ordinal);
    var parts = text.Split('.');
    if (parts[0] != "5")
      return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major < 5 ? 0 : -1;
    if (parts.Length == 1)
      return 0;
    if (version.StartsWith('v') || parts.Length > 2)
      return int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var vminor) ? vminor : -1;
    var fraction = (parts[1] + "000").AsSpan(0, Math.Max(3, parts[1].Length));
    return int.TryParse(fraction[..3], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ? minor : -1;
  }

  /// <summary>Pragmas and modules that export nothing the parser needs to know of.</summary>
  private static readonly HashSet<string> _quiet = new(StringComparer.Ordinal) {
    "strict", "warnings", "utf8", "integer", "bytes", "less", "lib", "locale", "open", "sort", "vars", "subs", "re", "mro",
    "overload", "overloading", "parent", "base", "fields", "attributes", "autouse", "bigint", "bignum", "bigrat", "bigfloat",
    "blib", "charnames", "deprecate", "diagnostics", "encoding", "encoding::warnings", "filetest", "if", "sigtrap", "threads",
    "threads::shared", "vmsish", "warnings::register", "experimental", "feature", "builtin", "autodie", "constant", "version",
    "Exporter", "Carp::Heavy", "DynaLoader", "XSLoader", "Config", "English", "FindBin", "Symbol", "SelectSaver", "IO::Handle",
    "IO::File", "File::Spec", "File::Spec::Functions", "Tie::Hash", "Tie::Array", "Tie::Scalar", "Tie::Handle",
    "ok", "stable"
  };

  /// <summary>The subs a core module exports without an import list, where the parser knows them.</summary>
  private static readonly Dictionary<string, string[]> _defaults = new(StringComparer.Ordinal) {
    ["Carp"] = ["croak", "carp", "confess"],
    ["Moose"] = ["has", "extends", "with", "before", "after", "around", "override", "super", "augment", "inner", "blessed", "confess", "meta"],
    ["Moose::Role"] = ["has", "with", "requires", "excludes", "before", "after", "around", "override", "super", "augment", "inner", "meta"],
    ["Test::More"] = [
      "ok", "use_ok", "require_ok", "is", "isnt", "like", "unlike", "is_deeply", "cmp_ok", "skip", "todo", "todo_skip", "pass", "fail",
      "eq_array", "eq_hash", "eq_set", "plan", "done_testing", "can_ok", "isa_ok", "new_ok", "diag", "note", "explain", "subtest", "BAIL_OUT"
    ],
    ["Test::Simple"] = ["ok"],
    ["Data::Dumper"] = ["Dumper"],
    ["Cwd"] = ["cwd", "getcwd", "fastcwd", "fastgetcwd"],
    ["File::Basename"] = ["basename", "dirname", "fileparse", "fileparse_set_fstype"],
    ["File::Temp"] = ["tempfile", "tempdir"],
    ["File::Path"] = ["mkpath", "rmtree"],
    ["File::Copy"] = ["copy", "move"],
    ["Scalar::Util"] = [],
    ["List::Util"] = [],
    ["Hash::Util::FieldHash"] = [],
    ["POSIX"] = [],
    ["Storable"] = ["store", "retrieve", "nstore"],
    ["Time::HiRes"] = [],
    ["Getopt::Long"] = ["GetOptions"],
    ["Text::Wrap"] = ["wrap", "fill"],
    ["Benchmark"] = ["timeit", "timethis", "timethese", "timediff", "timestr"]
  };

  /// <summary>Prototypes of exported subs that change how a call of them parses.</summary>
  private static readonly Dictionary<string, string> _prototypes = new(StringComparer.Ordinal) {
    ["List::Util::first"] = "&@", ["List::Util::any"] = "&@", ["List::Util::all"] = "&@", ["List::Util::none"] = "&@",
    ["List::Util::notall"] = "&@", ["List::Util::reduce"] = "&@", ["List::Util::reductions"] = "&@", ["List::Util::pairmap"] = "&@",
    ["List::Util::pairgrep"] = "&@", ["List::Util::pairfirst"] = "&@",
    ["Scalar::Util::blessed"] = "$", ["Scalar::Util::reftype"] = "$", ["Scalar::Util::looks_like_number"] = "$",
    ["Scalar::Util::refaddr"] = "$", ["Scalar::Util::weaken"] = "$", ["Scalar::Util::unweaken"] = "$", ["Scalar::Util::isweak"] = "$",
    ["Scalar::Util::readonly"] = "$", ["Scalar::Util::openhandle"] = "$", ["Scalar::Util::isvstring"] = "$", ["Scalar::Util::tainted"] = "$",
    ["Scalar::Util::isdual"] = "$", ["Scalar::Util::dualvar"] = "$$",
    ["Hash::Util::FieldHash::fieldhash"] = "\\[%$]",
    ["Test::More::ok"] = "$;$", ["Test::More::is"] = "$$;$", ["Test::More::isnt"] = "$$;$", ["Test::More::like"] = "$$;$",
    ["Test::More::unlike"] = "$$;$", ["Test::More::cmp_ok"] = "$$$;$", ["Test::More::pass"] = ";$", ["Test::More::fail"] = ";$",
    ["Try::Tiny::try"] = "&;@", ["Try::Tiny::catch"] = "&;@",
    ["Try::Tiny::finally"] = "&;@", ["Test::Exception::lives_ok"] = "&;$", ["Test::Exception::dies_ok"] = "&;$",
    ["Test::Exception::throws_ok"] = "&$;$", ["Test::Exception::lives_and"] = "&;$", ["Test::Fatal::exception"] = "&"
  };

  /// <summary>Modules whose import changes the grammar of what follows, which the parser does not follow.</summary>
  private static readonly HashSet<string> _grammarChanging = new(StringComparer.Ordinal) {
    "Switch", "Spiffy", "Filter::Simple", "Filter::Util::Call", "Filter::cpp", "Filter::exec", "Filter::sh", "Filter::decrypt", "Filter::tee",
    "Acme::Bleach", "Lingua::Romana::Perligata", "Function::Parameters", "Method::Signatures", "Method::Signatures::Simple", "MooseX::Declare",
    "Devel::Declare", "Keyword::Simple", "Keyword::Declare", "Kavorka", "Moops", "Zydeco", "Object::Pad", "Syntax::Feature::Junction", "Dios",
    "TryCatch", "Feature::Compat::Try", "Feature::Compat::Class", "Feature::Compat::Defer", "Future::AsyncAwait", "Syntax::Keyword::Try",
    "Syntax::Keyword::Defer", "Syntax::Keyword::Match", "Syntax::Keyword::Dynamically", "Syntax::Keyword::Gather", "Syntax::Keyword::Junction",
    "Babble", "Perl6::Say", "Perl6::Junction", "Perl6::Form", "Perl6::Rules", "Perl6::Gather", "Perl6::Variables", "Smart::Comments",
    "Text::Balanced::Filter", "Sub::Signatures", "signatures", "Attribute::Handlers", "Exporter::Declare", "PerlX::Maybe::XS", "Tie::Cycle::Sinewave",
    "Babble::Plugin", "B::Hooks::Parser", "B::Hooks::EndOfScope", "Devel::BeginLift", "Devel::CallParser", "XS::Parse::Keyword", "XS::Parse::Sublike"
  };

  /// <summary>
  /// The subs of the test harness of perl's own test suite (<c>t/test.pl</c>, <c>t/loc_tools.pl</c>,
  /// <c>t/charset_tools.pl</c>), which its tests <c>require</c> in a BEGIN block, with the prototypes
  /// they declare: the names are those the files define in perl 5.42.3.
  /// </summary>
  private static readonly Dictionary<string, Dictionary<string, string?>> _harness = new(StringComparer.Ordinal) {
    ["test.pl"] = new(StringComparer.Ordinal) {
      ["_print"] = null, ["_print_stderr"] = null, ["_diag"] = null, ["_comment"] = null, ["_have_dynamic_extension"] = null, ["_ok"] = null,
      ["_where"] = null, ["_q"] = null, ["_qq"] = null, ["_quote_args"] = null, ["_create_runperl"] = null, ["_num_to_alpha"] = null,
      ["_fresh_perl"] = null, ["_setup_one_file"] = null, ["__capture"] = null, ["_fail_excess_warnings"] = null,
      ["plan"] = null, ["done_testing"] = null, ["diag"] = null, ["note"] = null, ["is_miniperl"] = null, ["set_up_inc"] = null,
      ["skip_all"] = null, ["skip_all_if_miniperl"] = null, ["skip_all_without_dynamic_extension"] = null, ["skip_all_without_perlio"] = null,
      ["skip_all_without_config"] = null, ["skip_all_without_unicode_tables"] = null, ["find_git_or_skip"] = null, ["BAIL_OUT"] = null,
      ["ok"] = "$@", ["display"] = null, ["display_rx"] = null, ["is"] = "$$@", ["isnt"] = "$$@", ["cmp_ok"] = "$$$@", ["within"] = "$$$@",
      ["like"] = "$$@", ["unlike"] = "$$@", ["like_yn"] = "$$$@", ["refcount_is"] = null, ["pass"] = null, ["fail"] = null,
      ["curr_test"] = null, ["next_test"] = null, ["skip"] = null, ["skip_if_miniperl"] = null, ["skip_without_dynamic_extension"] = null,
      ["todo_skip"] = null, ["eq_array"] = null, ["eq_hash"] = null, ["require_ok"] = "$", ["use_ok"] = "$", ["untaint_path"] = null,
      ["runperl"] = null, ["run_perl"] = null, ["runperl_and_capture"] = null, ["DIE"] = null, ["which_perl"] = null, ["unlink_all"] = null,
      ["unlink_tempfiles"] = null, ["tempfile"] = null, ["register_tempfile"] = null, ["fresh_perl"] = null, ["fresh_perl_is"] = null,
      ["fresh_perl_like"] = null, ["setup_multiple_progs"] = null, ["run_multiple_progs"] = null, ["can_ok"] = "$@", ["new_ok"] = null,
      ["isa_ok"] = "$$;$", ["class_ok"] = null, ["object_ok"] = null, ["capture_warnings"] = null, ["warnings_like"] = null,
      ["warning_is"] = null, ["warning_like"] = null, ["watchdog"] = "$;$", ["is_linux_container"] = null
    },
    ["loc_tools.pl"] = new(StringComparer.Ordinal) {
      ["category_excluded"] = "$", ["platform_locale_categories"] = "", ["valid_locale_categories"] = "", ["is_category_valid"] = "$",
      ["locales_enabled"] = ";$", ["find_locales"] = "$;$", ["is_locale_utf8"] = "$", ["classify_locales_wrt_utf8ness"] = "$",
      ["find_utf8_ctype_locales"] = ";$", ["find_utf8_ctype_locale"] = ";$", ["find_utf8_turkic_locales"] = ";$", ["find_utf8_turkic_locale"] = ";$"
    },
    ["charset_tools.pl"] = new(StringComparer.Ordinal) {
      ["native_to_uni"] = null, ["uni_to_native"] = null, ["byte_utf8a_to_utf8n"] = null, ["I8_to_native"] = null, ["native_to_I8"] = null
    }
  };

  /// <summary>
  /// The functions perl itself defines in packages of its own before any code runs (universal.c,
  /// mro_core.c, builtin.c), with their prototypes as <c>prototype()</c> of perl 5.42.3 gives them.
  /// </summary>
  private static readonly Dictionary<string, string?> _core = new(StringComparer.Ordinal) {
    ["Internals::SvREADONLY"] = "\\[$%@];$", ["Internals::SvREFCNT"] = "\\[$%@];$", ["Internals::hv_clear_placeholders"] = "\\%",
    ["Internals::stack_refcounted"] = null, ["Internals::V"] = null, ["PerlIO::get_layers"] = "*;@", ["re::is_regexp"] = "$",
    ["re::regexp_pattern"] = "$", ["re::regname"] = ";$$", ["re::regnames"] = ";$", ["re::regnames_count"] = "",
    ["mro::get_linear_isa"] = null, ["mro::set_mro"] = null, ["mro::get_mro"] = null, ["mro::get_isarev"] = null, ["mro::is_universal"] = null,
    ["mro::invalidate_all_method_caches"] = null, ["mro::method_changed_in"] = "$", ["mro::get_pkg_gen"] = null,
    ["utf8::decode"] = null, ["utf8::downgrade"] = null, ["utf8::encode"] = null, ["utf8::is_utf8"] = null, ["utf8::native_to_unicode"] = null,
    ["utf8::unicode_to_native"] = null, ["utf8::upgrade"] = null, ["utf8::valid"] = null, ["UNIVERSAL::can"] = null, ["UNIVERSAL::DOES"] = null,
    ["UNIVERSAL::isa"] = null, ["UNIVERSAL::VERSION"] = null, ["version::new"] = null, ["version::parse"] = null, ["version::qv"] = null,
    ["version::declare"] = null, ["constant::_make_const"] = "\\[$@]",
    ["builtin::blessed"] = "$", ["builtin::ceil"] = "$", ["builtin::created_as_number"] = "$", ["builtin::created_as_string"] = "$",
    ["builtin::export_lexically"] = null, ["builtin::false"] = "", ["builtin::floor"] = "$", ["builtin::indexed"] = "@", ["builtin::inf"] = "",
    ["builtin::is_bool"] = "$", ["builtin::is_tainted"] = "$", ["builtin::is_weak"] = "$", ["builtin::load_module"] = "$", ["builtin::nan"] = "",
    ["builtin::refaddr"] = "$", ["builtin::reftype"] = "$", ["builtin::stringify"] = "$", ["builtin::trim"] = "$", ["builtin::true"] = "",
    ["builtin::unweaken"] = "$", ["builtin::weaken"] = "$"
  };

  /// <summary>The functions perl defines before any code runs, by qualified name, with their prototypes.</summary>
  public static IReadOnlyDictionary<string, string?> CoreFunctions => _core;

  /// <summary>The subs a file of perl's test harness defines, by the file's name; <see langword="null"/> for any other file.</summary>
  public static IReadOnlyDictionary<string, string?>? Harness(string path) {
    var slash = path.LastIndexOfAny(['/', '\\']);
    return _harness.GetValueOrDefault(slash < 0 ? path : path[(slash + 1)..]);
  }

  /// <summary>Whether a module's import list is known to export nothing the parser needs to know of.</summary>
  public static bool IsQuiet(string module) => _quiet.Contains(module);

  /// <summary>Whether importing a module changes the grammar of the rest of the file.</summary>
  public static bool ChangesGrammar(string module)
    => _grammarChanging.Contains(module) || module.StartsWith("Filter::", StringComparison.Ordinal)
      || module.StartsWith("Syntax::Keyword::", StringComparison.Ordinal) || module.StartsWith("Syntax::Feature::", StringComparison.Ordinal);

  /// <summary>The subs a module exports by default, where the parser knows them; <see langword="null"/> where it does not.</summary>
  public static IReadOnlyList<string>? DefaultExports(string module) => _defaults.GetValueOrDefault(module);

  /// <summary>The prototype of a sub a module exports, where it changes the parse; <see langword="null"/> where it does not or is not known.</summary>
  public static string? ExportedPrototype(string module, string name) => _prototypes.GetValueOrDefault(module + "::" + name);

  /// <summary>Whether the parser knows every prototype that matters among a module's exports.</summary>
  public static bool KnowsPrototypes(string module) => _defaults.ContainsKey(module) || _quiet.Contains(module) || _prototypes.Keys.Any(k => k.StartsWith(module + "::", StringComparison.Ordinal) && k.LastIndexOf("::", StringComparison.Ordinal) == module.Length);

}
