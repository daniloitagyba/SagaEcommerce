namespace Orders.Application.Exceptions;

/// <summary>
/// Represents a concurrent return update.
/// </summary>
public sealed class OrderReturnConflictException(string message, Exception innerException)
    : Exception(message, innerException);
