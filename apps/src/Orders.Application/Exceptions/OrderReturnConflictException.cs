namespace Orders.Application.Exceptions;

public sealed class OrderReturnConflictException(string message, Exception innerException)
    : Exception(message, innerException);
