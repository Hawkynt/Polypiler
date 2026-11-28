// Command oracle is the differential oracle of the Go frontend's own parser and type checker in the
// Go layer: for each request on standard input it reports what Go itself decides of a source file,
// one JSON object per line. "P <path>" asks go/parser whether the file parses; "T <path>" asks
// go/types, with the standard library imported from source, whether the file alone type-checks as
// its package, and for the package-level constants of a valid file their type and exact value
// (constant.Value.ExactString). Nothing is built or run.
package main

import (
	"bufio"
	"encoding/json"
	"go/ast"
	"go/importer"
	"go/parser"
	"go/scanner"
	"go/token"
	"go/types"
	"os"
	"strings"
)

type constantValue struct {
	Name  string `json:"name"`
	Type  string `json:"type"`
	Value string `json:"value"`
}

type verdict struct {
	Path      string          `json:"path"`
	Valid     bool            `json:"valid"`
	Error     string          `json:"error,omitempty"`
	Constants []constantValue `json:"constants,omitempty"`
}

func firstError(err error) string {
	if list, ok := err.(scanner.ErrorList); ok && len(list) > 0 {
		return list[0].Error()
	}
	return err.Error()
}

func check(fset *token.FileSet, imports types.Importer, kind, path string) verdict {
	result := verdict{Path: path}
	file, err := parser.ParseFile(fset, path, nil, parser.ParseComments|parser.SkipObjectResolution)
	if err != nil {
		result.Error = firstError(err)
		return result
	}
	if kind == "P" {
		result.Valid = true
		return result
	}
	var first error
	config := types.Config{Importer: imports, Error: func(err error) {
		if first == nil {
			first = err
		}
	}}
	pkg, _ := config.Check(file.Name.Name, fset, []*ast.File{file}, nil)
	if first != nil {
		result.Error = first.Error()
		return result
	}
	result.Valid = true
	scope := pkg.Scope()
	for _, name := range scope.Names() {
		if c, ok := scope.Lookup(name).(*types.Const); ok {
			result.Constants = append(result.Constants, constantValue{name, types.TypeString(c.Type(), types.RelativeTo(pkg)), c.Val().ExactString()})
		}
	}
	return result
}

func main() {
	fset := token.NewFileSet()
	imports := importer.ForCompiler(fset, "source", nil)
	input := bufio.NewScanner(os.Stdin)
	input.Buffer(make([]byte, 1<<20), 1<<20)
	output := json.NewEncoder(os.Stdout)
	for input.Scan() {
		line := input.Text()
		kind, path, ok := strings.Cut(line, " ")
		if !ok {
			continue
		}
		if err := output.Encode(check(fset, imports, kind, path)); err != nil {
			os.Exit(2)
		}
	}
}
