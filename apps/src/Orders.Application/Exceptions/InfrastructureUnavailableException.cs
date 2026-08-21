namespace Orders.Application.Exceptions;

public sealed class InfrastructureUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);
