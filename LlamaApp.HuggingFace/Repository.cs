using LlamaApp.Common;

namespace LlamaApp.HuggingFace;

public record Repository : IModel
{
    public required string Name { get; init; }
    public required string Description { get; init;  }
    public required string License { get; init;  }
    public required ulong Parameters { get; init; }
    public required ulong Size { get; init;  }
}