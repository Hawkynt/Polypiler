using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Polypiler.Core.Verification.Images;

/// <summary>How a parameter carries its value: stated by the signature, never inferred from the language.</summary>
public enum ParameterMode {

  /// <summary><c>in</c>: the value goes into the call; part of the input image.</summary>
  In,

  /// <summary><c>inout</c>: the value goes in and its content after the call is part of the contract; in both images.</summary>
  InOut,

  /// <summary><c>out</c>: the call sets the value; part of the output image.</summary>
  Out

}

/// <summary>A parameter of a call signature.</summary>
/// <param name="Name">The parameter's name, unique in its signature.</param>
/// <param name="Type">The image type of its value.</param>
/// <param name="Mode">How it carries the value.</param>
public sealed record CallParameter(string Name, ImageType Type, ParameterMode Mode) {

  /// <summary>Whether the value is part of the input image.</summary>
  public bool IsInput => this.Mode != ParameterMode.Out;

  /// <summary>Whether the value after the call is part of the output image.</summary>
  public bool IsOutput => this.Mode != ParameterMode.In;

  /// <inheritdoc />
  public override string ToString() => $"{this.Name}: {this.Type} {Spelling(this.Mode)}";

  internal static string Spelling(ParameterMode mode) => mode switch {
    ParameterMode.In => "in",
    ParameterMode.InOut => "inout",
    _ => "out"
  };

}

/// <summary>
/// The typed call signature of a vector file: the image type and mode of every parameter and the
/// result type. It says how a function's arguments and outcome become the input and output images
/// of a vector, so that one vector file drives a width-less language and a typed one alike.
/// </summary>
public sealed class CallSignature {

  /// <summary>Creates a signature.</summary>
  /// <param name="parameters">The parameters, in order; names are unique.</param>
  /// <param name="result">The result type; <see langword="null"/> for <c>void</c>.</param>
  /// <exception cref="ImageException">A name is used twice, or a type is <see cref="ImageType.Null"/>.</exception>
  public CallSignature(IReadOnlyList<CallParameter> parameters, ImageType? result) {
    ArgumentNullException.ThrowIfNull(parameters);
    foreach (var parameter in parameters) {
      ArgumentNullException.ThrowIfNull(parameter, nameof(parameters));
      if (parameter.Type.Kind == ImageKind.Null)
        throw new ImageException($"signature: parameter '{parameter.Name}' has the type 'null', which only a union holds");
    }

    if (parameters.GroupBy(parameter => parameter.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice)
      throw new ImageException($"signature: parameter '{twice.Key}' is declared twice");
    if (result?.Kind == ImageKind.Null)
      throw new ImageException("signature: the result has the type 'null', which only a union holds");

    this.Parameters = [.. parameters];
    this.Result = result;
  }

  /// <summary>The parameters, in order.</summary>
  public IReadOnlyList<CallParameter> Parameters { get; }

  /// <summary>The result type; <see langword="null"/> for <c>void</c>.</summary>
  public ImageType? Result { get; }

  /// <summary>The parameters of the input image: <c>in</c> and <c>inout</c>, in order.</summary>
  public IEnumerable<CallParameter> Inputs => this.Parameters.Where(parameter => parameter.IsInput);

  /// <summary>The parameters of the output image after the result: <c>inout</c> and <c>out</c>, in order.</summary>
  public IEnumerable<CallParameter> Outputs => this.Parameters.Where(parameter => parameter.IsOutput);

  /// <summary>
  /// Reads the JSON form of a signature:
  /// <c>{"parameters": [{"name", "type", "mode"}], "result": "&lt;type&gt;" | "void"}</c>, the mode
  /// <c>in</c> (the default), <c>inout</c> or <c>out</c>.
  /// </summary>
  /// <param name="node">The JSON.</param>
  /// <returns>The signature.</returns>
  /// <exception cref="ImageException">The JSON is not of that form or names an unknown image type; the message starts with <c>signature: </c>.</exception>
  public static CallSignature Parse(JsonNode? node) {
    if (node is not JsonObject root)
      throw Invalid("must be an object");

    Only(root, "signature", "parameters", "result");
    if (root["parameters"] is not JsonArray list)
      throw Invalid("'parameters' must be an array");

    var parameters = new List<CallParameter>();
    foreach (var item in list) {
      var at = $"parameters[{parameters.Count}]";
      if (item is not JsonObject entry)
        throw Invalid($"'{at}' must be an object");

      Only(entry, at, "name", "type", "mode");
      var name = Text(entry, "name", at);
      if (!IsName(name))
        throw Invalid($"'{at}.name' '{name}' is not a name");

      var mode = entry.ContainsKey("mode") ? Text(entry, "mode", at) switch {
        "in" => ParameterMode.In,
        "inout" => ParameterMode.InOut,
        "out" => ParameterMode.Out,
        var other => throw Invalid($"parameter '{name}' has the unknown mode '{other}'")
      } : ParameterMode.In;
      var type = Text(entry, "type", at);
      if (type == "void")
        throw Invalid($"parameter '{name}' has mode '{CallParameter.Spelling(mode)}' and type 'void'");

      parameters.Add(new(name, Type(type), mode));
    }

    var result = Text(root, "result", null);
    return new(parameters, result == "void" ? null : Type(result));
  }

  /// <summary>The signature in the notation <c>(a: u32 in, buf: seq&lt;u8&gt; inout) -&gt; bool</c>.</summary>
  public override string ToString() => $"({string.Join(", ", this.Parameters)}) -> {this.Result?.ToString() ?? "void"}";

  private static ImageType Type(string text) {
    try {
      return ImageType.Parse(text);
    } catch (ImageException exception) {
      throw Invalid(exception.Message);
    }
  }

  private static void Only(JsonObject node, string at, params string[] allowed) {
    foreach (var (key, _) in node)
      if (!allowed.Contains(key))
        throw Invalid($"'{(at == "signature" ? key : $"{at}.{key}")}' is not a property of a signature");
  }

  private static string Text(JsonObject node, string property, string? at) {
    var name = at is null ? property : $"{at}.{property}";
    return node[property] is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } text
      ? text
      : throw Invalid($"'{name}' must be a non-empty string");
  }

  private static bool IsName(string text) => (char.IsAsciiLetter(text[0]) || text[0] == '_') && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

  private static ImageException Invalid(string problem) => new($"signature: {problem}");

}
