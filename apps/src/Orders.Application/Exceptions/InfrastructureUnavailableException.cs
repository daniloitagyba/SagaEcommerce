namespace Orders.Application.Exceptions;

/// <summary>
/// Represents unavailable infrastructure.
/// </summary>
public sealed class InfrastructureUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);
