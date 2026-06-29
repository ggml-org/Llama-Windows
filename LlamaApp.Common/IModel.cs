namespace LlamaApp.Common;

public interface IModel
{
    string Name { get; }
    string Description { get; }
    string License { get; }
    ulong Parameters { get; }
    ulong Size { get; }
}