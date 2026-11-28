using System;
using System.Runtime.CompilerServices;

[assembly: CLSCompliant(false)]
[assembly: InternalsVisibleTo("Polypiler.Tests")]
// The parser benchmark of polypiler-tooling (bench parsers) runs the parsers themselves.
[assembly: InternalsVisibleTo("polypiler-tooling")]
