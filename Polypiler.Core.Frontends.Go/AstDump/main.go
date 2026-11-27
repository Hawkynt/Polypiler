// Command polypiler-go-dump parses one Go source file with go/parser, type-checks it with go/types
// and prints the bound tree as JSON. The source is read from standard input and only parsed and
// type-checked: it is never built, linked or executed. Imports of the standard library are
// resolved from the source of the installed GOROOT (go/importer, compiler "source").
//
// Wire format 1: one JSON object with "format", "go" (runtime.Version()) and either "file" (the
// tree) or "error" (the first error of the parser or the type checker, with its position). A node
// is an object whose "_" names its go/ast type; "pos" and "end" are [line, column], columns in
// bytes from 1. Every field of the node follows under its own name. An expression carries "type",
// the type go/types gives it (types.TypeString, package-local names unqualified), "mode" ("type",
// "builtin", "value", "constant", "void", "novalue") and, for a constant, "const" with its exact
// value (constant.Value.ExactString) and "constKind" (int, float, bool, string). An identifier
// carries "obj": the kind of the object it names (var, const, func, type, builtin, pkg, nil,
// label), "decl" the position of that object's declaration, "pkg" the import path of a package
// it names, and "field" true for a struct field.
package main

import (
	"bytes"
	"encoding/json"
	"go/ast"
	"go/constant"
	"go/importer"
	"go/parser"
	"go/scanner"
	"go/token"
	"go/types"
	"io"
	"math"
	"os"
	"reflect"
	"runtime"
)

const format = 1

type dumper struct {
	fset *token.FileSet
	info *types.Info
	pkg  *types.Package
}

func (d *dumper) position(pos token.Pos) []int {
	if !pos.IsValid() {
		return nil
	}
	p := d.fset.Position(pos)
	return []int{p.Line, p.Column}
}

func (d *dumper) qualifier(other *types.Package) string {
	if other == d.pkg {
		return ""
	}
	return other.Path()
}

func objectKind(object types.Object) string {
	switch object.(type) {
	case *types.Var:
		return "var"
	case *types.Const:
		return "const"
	case *types.Func:
		return "func"
	case *types.TypeName:
		return "type"
	case *types.Builtin:
		return "builtin"
	case *types.PkgName:
		return "pkg"
	case *types.Nil:
		return "nil"
	case *types.Label:
		return "label"
	}
	return "unknown"
}

func modeOf(tv types.TypeAndValue) string {
	switch {
	case tv.IsType():
		return "type"
	case tv.IsBuiltin():
		return "builtin"
	case tv.Value != nil:
		return "constant"
	case tv.IsVoid():
		return "void"
	case tv.IsValue():
		return "value"
	}
	return "novalue"
}

func constKind(value constant.Value) string {
	switch value.Kind() {
	case constant.Int:
		return "int"
	case constant.Float:
		return "float"
	case constant.Bool:
		return "bool"
	case constant.String:
		return "string"
	case constant.Complex:
		return "complex"
	}
	return "unknown"
}

func (d *dumper) node(value reflect.Value) any {
	switch value.Kind() {
	case reflect.Interface, reflect.Pointer:
		if value.IsNil() {
			return nil
		}
		if value.Kind() == reflect.Interface {
			return d.node(value.Elem())
		}
	case reflect.Slice:
		list := make([]any, 0, value.Len())
		for index := 0; index < value.Len(); index++ {
			list = append(list, d.node(value.Index(index)))
		}
		return list
	case reflect.String:
		return value.String()
	case reflect.Bool:
		return value.Bool()
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return value.Int()
	default:
		return nil
	}
	astNode, ok := value.Interface().(ast.Node)
	if !ok {
		return nil
	}
	result := map[string]any{"_": value.Elem().Type().Name()}
	result["pos"] = d.position(astNode.Pos())
	result["end"] = d.position(astNode.End())
	element := value.Elem()
	for index := 0; index < element.NumField(); index++ {
		field := element.Type().Field(index)
		if !field.IsExported() || field.Type == reflect.TypeOf(token.Pos(0)) || field.Name == "Obj" || field.Name == "Scope" || field.Name == "Comments" || field.Name == "Doc" || field.Name == "Comment" || field.Name == "Imports" || field.Name == "Unresolved" || field.Name == "FileStart" || field.Name == "FileEnd" {
			continue
		}
		if field.Type == reflect.TypeOf(token.Token(0)) {
			result[field.Name] = element.Field(index).Interface().(token.Token).String()
			continue
		}
		if field.Type == reflect.TypeOf(ast.ChanDir(0)) {
			result[field.Name] = int64(element.Field(index).Interface().(ast.ChanDir))
			continue
		}
		result[field.Name] = d.node(element.Field(index))
	}
	if expression, ok := astNode.(ast.Expr); ok {
		if tv, ok := d.info.Types[expression]; ok {
			result["mode"] = modeOf(tv)
			if tv.Type != nil {
				result["type"] = types.TypeString(tv.Type, d.qualifier)
				result["underlying"] = types.TypeString(tv.Type.Underlying(), d.qualifier)
			}
			if tv.Value != nil {
				result["const"] = tv.Value.ExactString()
				result["constKind"] = constKind(tv.Value)
				if tv.Value.Kind() == constant.Float {
					if f, _ := constant.Float64Val(tv.Value); !math.IsInf(f, 0) {
						result["float64"] = math.Float64bits(f)
					}
				}
			}
		}
	}
	if ident, ok := astNode.(*ast.Ident); ok {
		object := d.info.Uses[ident]
		if object == nil {
			object = d.info.Defs[ident]
		}
		if object != nil {
			result["obj"] = objectKind(object)
			result["decl"] = d.position(object.Pos())
			if name, ok := object.(*types.PkgName); ok {
				result["pkg"] = name.Imported().Path()
			}
			if variable, ok := object.(*types.Var); ok && variable.IsField() {
				result["field"] = true
			}
			if object.Type() != nil && result["type"] == nil {
				result["type"] = types.TypeString(object.Type(), d.qualifier)
				result["underlying"] = types.TypeString(object.Type().Underlying(), d.qualifier)
			}
			if object.Pkg() != nil && object.Pkg() != d.pkg {
				result["from"] = object.Pkg().Path()
			}
		}
	}
	if spec, ok := astNode.(*ast.TypeSpec); ok && spec.Assign.IsValid() {
		result["alias"] = true
	}
	if call, ok := astNode.(*ast.CallExpr); ok && call.Ellipsis.IsValid() {
		result["spread"] = true
	}
	if selector, ok := astNode.(*ast.SelectorExpr); ok {
		if selection, ok := d.info.Selections[selector]; ok {
			kinds := map[types.SelectionKind]string{types.FieldVal: "field", types.MethodVal: "method", types.MethodExpr: "methodExpr"}
			result["selection"] = kinds[selection.Kind()]
		}
	}
	return result
}

func write(document map[string]any) {
	document["format"] = format
	document["go"] = runtime.Version()
	output, err := json.Marshal(document)
	if err != nil {
		os.Stderr.WriteString(err.Error())
		os.Exit(2)
	}
	os.Stdout.Write(output)
}

func failure(fset *token.FileSet, pos token.Pos, message string, kind string) map[string]any {
	position := fset.Position(pos)
	return map[string]any{"error": map[string]any{"kind": kind, "msg": message, "line": position.Line, "col": position.Column}}
}

func main() {
	name := "source.go"
	if len(os.Args) > 1 {
		name = os.Args[1]
	}
	source, err := io.ReadAll(os.Stdin)
	if err != nil {
		os.Stderr.WriteString(err.Error())
		os.Exit(2)
	}
	fset := token.NewFileSet()
	file, err := parser.ParseFile(fset, name, bytes.NewReader(source), parser.ParseComments|parser.SkipObjectResolution)
	if err != nil {
		line, column, message := 1, 1, err.Error()
		if list, ok := err.(scanner.ErrorList); ok && len(list) > 0 {
			line, column, message = list[0].Pos.Line, list[0].Pos.Column, list[0].Msg
		}
		write(map[string]any{"error": map[string]any{"kind": "syntax", "msg": message, "line": line, "col": column}})
		return
	}
	info := &types.Info{
		Types:      map[ast.Expr]types.TypeAndValue{},
		Defs:       map[*ast.Ident]types.Object{},
		Uses:       map[*ast.Ident]types.Object{},
		Selections: map[*ast.SelectorExpr]*types.Selection{},
	}
	var first *types.Error
	config := types.Config{
		Importer: importer.ForCompiler(fset, "source", nil),
		Error: func(err error) {
			if typed, ok := err.(types.Error); ok && first == nil {
				first = &typed
			}
		},
	}
	pkg, _ := config.Check(file.Name.Name, fset, []*ast.File{file}, info)
	if first != nil {
		write(failure(fset, first.Pos, first.Msg, "type"))
		return
	}
	d := &dumper{fset: fset, info: info, pkg: pkg}
	write(map[string]any{"file": d.node(reflect.ValueOf(file))})
}
