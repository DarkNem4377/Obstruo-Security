namespace Obstruo.Shared.Contracts;

public interface IObstrouMessage
{
    int SchemaVersion { get; }
    string Timestamp { get; }
    string MessageType { get; }
}