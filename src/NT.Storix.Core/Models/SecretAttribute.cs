namespace NT.Storix.Core.Models;

/// <summary>
/// Marks a string property holding sensitive data. Such values are encrypted at rest and
/// removed (or passphrase-protected) when exporting configuration.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;
