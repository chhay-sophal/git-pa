namespace GitPa;

/// <summary>An expected failure whose message is shown to the user as-is.</summary>
public sealed class GitPaException(string message) : Exception(message);
