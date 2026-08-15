namespace Orders.Application.Exceptions;

/// <summary>
/// Raised when a return's save loses the race OrderLine's xmin
/// concurrency token exists to catch - another return (or any other write)
/// changed the same line between this request's read and its write. The
/// request is safe to retry: a fresh read will see the line's current
/// ReturnableQuantity and validate against it correctly.
/// </summary>
public sealed class OrderReturnConflictException(string message, Exception innerException)
    : Exception(message, innerException);
