namespace ReapOps.Domain.Common;

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IDomainEvent
{
    DateTime OccurredAt { get; }
}

public abstract class AggregateRoot
{
    readonly List<IDomainEvent> _events = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _events;

    protected void Raise(IDomainEvent e) => _events.Add(e);

    public void ClearDomainEvents() => _events.Clear();
}
