using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Huginn.Services;

/// <summary>Serialization for <see cref="SecretVault"/>, generated so it survives trimming.</summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SecretVaultJsonContext : JsonSerializerContext;
