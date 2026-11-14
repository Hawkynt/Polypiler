using System;
using NUnit.Framework;

namespace Polypiler.Tests.Acceptance;

/// <summary>
/// The CI layer that runs a test: the NUnit category <c>Layer.&lt;name&gt;</c>, which the job
/// <c>layer-&lt;name&gt;</c> of <c>.github/workflows/ci.yml</c> selects. Every test of this project
/// carries exactly one, on its class or its method; a feature file gets it as the tag
/// <c>@Layer.&lt;name&gt;</c>. <c>polypiler-tooling ci audit</c> checks both.
/// </summary>
/// <param name="name">The layer, the name of a file <c>tooling/ci/layers/&lt;name&gt;.yaml</c>.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class LayerAttribute(string name) : CategoryAttribute("Layer." + name) {

  /// <summary>The layer.</summary>
  public string Layer { get; } = name;

}
