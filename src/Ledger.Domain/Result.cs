namespace Ledger.Domain;

/// <summary>Categoria do erro. A camada HTTP traduz cada categoria para um status code.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    BusinessRule,
    Conflict,
}

/// <summary>Erro esperado de negócio. Falhas de infraestrutura são exceções; regras de negócio não.</summary>
public sealed record DomainError(ErrorKind Kind, string Code, string Message);

/// <summary>Resultado de uma operação que pode falhar por motivo de negócio.</summary>
public sealed class Result<T>
{
    private readonly T? _value;

    internal Result(T value)
    {
        _value = value;
        IsSuccess = true;
    }

    internal Result(DomainError error)
    {
        Error = error;
    }

    public bool IsSuccess { get; }

    public DomainError? Error { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Resultado com falha não possui valor: {Error!.Code}.");
}

public static class Result
{
    public static Result<T> Success<T>(T value) => new(value);

    public static Result<T> Failure<T>(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(error);
    }
}
