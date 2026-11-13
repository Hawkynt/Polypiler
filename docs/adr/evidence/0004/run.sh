#!/bin/bash
# Reproduces outputs.txt: builds the three prototypes of ADR 0004 and the library probe in a
# temporary directory, runs the check suite, publishes each prototype the way
# .github/workflows/_build.yml does and measures size and start-up time.
#   bash docs/adr/evidence/0004/run.sh > docs/adr/evidence/0004/outputs.txt
# The projects are generated here and not committed: every *.csproj of the repository is subject to
# the module rules, and these are not modules.
export LC_ALL=C DOTNET_CLI_UI_LANGUAGE=en DOTNET_NOLOGO=1
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../../.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
publish="-c Release --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -r linux-x64"

cat > "$tmp/Directory.Build.props" <<'XML'
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
    <OutputType>Exe</OutputType>
    <AssemblyName>polypiler</AssemblyName>
    <Version>0.1.0</Version>
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
  </PropertyGroup>
</Project>
XML

# project <name> <package@version or empty> <source file>...
project() {
  local name="$1" package="$2"; shift 2
  mkdir "$tmp/$name"
  for source in "$@"; do cp "$here/$source" "$tmp/$name/"; done
  {
    echo '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>'
    [ -n "$package" ] && echo "<PackageReference Include=\"${package%@*}\" Version=\"${package#*@}\" />"
    echo '</ItemGroup></Project>'
  } > "$tmp/$name/$name.csproj"
}
project hand-written "" Grammar.cs Checks.cs HandWritten.cs
project system-commandline "System.CommandLine@2.0.15" Grammar.cs Checks.cs SystemCommandLine.cs
project spectre "Spectre.Console.Cli@0.57.2" SpectreConsoleCli.cs
project library-probe "System.CommandLine@2.0.15" LibraryProbe.cs

echo "### dotnet-sdk $(dotnet --version), $(uname -sm), $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs)"
for name in hand-written system-commandline spectre library-probe; do
  dotnet build "$tmp/$name" -c Release -v q 2>&1 | grep -E 'warning|error' | sort -u
done

for name in hand-written system-commandline; do
  echo "### check suite - $name"
  dotnet "$tmp/$name/bin/Release/net10.0/polypiler.dll" --selftest
done

echo "### System.CommandLine 2.0.15 with the settings of the ADR and without the foundation's corrections"
dotnet "$tmp/library-probe/bin/Release/net10.0/polypiler.dll" "$here/library-probe-lines.txt" | sed 's/[[:space:]]*$//'

echo "### Spectre.Console.Cli 0.57.2 - stdout, stderr and exit code of each line"
while IFS= read -r line; do
  echo "> $line"
  # shellcheck disable=SC2086
  dotnet "$tmp/spectre/bin/Release/net10.0/polypiler.dll" $line > "$tmp/out.txt" 2> "$tmp/err.txt"; status=$?
  grep -v '^ *$' "$tmp/out.txt" | head -4 | sed 's/^/  stdout: /'
  grep -v '^ *$' "$tmp/err.txt" | head -4 | sed 's/^/  stderr: /'
  echo "  exit $status"
done <<'LINES'
--version
translate a.cs --from csharp --to javascript --output out
translate a.cs --nope
translate a.cs --opt speed --opt size
translate a.cs --force=true
translate a.cs --from csharp --output:out
translate a.cs --from csharp --output --force
frobnicate
LINES

echo "### published size in bytes - dotnet publish $publish"
mkdir -p "$tmp/single" "$tmp/shared"
for name in hand-written system-commandline spectre; do
  dotnet publish "$tmp/$name" $publish -o "$tmp/single/$name" -v q 2>&1 | grep -oE 'warning IL[0-9]+' | sort -u | sed "s/^/$name: /"
  dotnet publish "$tmp/$name" -c Release -o "$tmp/shared/$name" -v q > /dev/null 2>&1
done
dotnet publish "$repo/Polypiler.Cli/Polypiler.Cli.csproj" $publish -o "$tmp/single/current" -v q > /dev/null 2>&1
dotnet publish "$repo/Polypiler.Cli/Polypiler.Cli.csproj" -c Release -o "$tmp/shared/current" -v q > /dev/null 2>&1
for name in current hand-written system-commandline spectre; do
  printf '%-20s %s\n' "$name" "$(stat -c %s "$tmp/single/$name/polypiler")"
done

echo "### lines of parser code"
wc -l "$here/HandWritten.cs" "$here/SystemCommandLine.cs" | head -2 | sed "s|$here/||"

echo "### start-up time"
python3 "$here/measure.py" "$tmp"
