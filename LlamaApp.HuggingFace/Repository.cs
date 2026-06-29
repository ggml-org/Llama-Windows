using LlamaApp.Common;

namespace LlamaApp.HuggingFace;

public record Repository(string Name, string Description, string License, ulong Parameters, ulong Size) : IModel
{
    public string Name { get; } = Name;
    public string Description { get; } = Description;
    public string License { get; } = License;
    public ulong Parameters { get; } = Parameters;
    public ulong Size { get; } = Size;
}