namespace LlamaApp.Common;

public interface IModelSource
{ 
    Task<ICollection<T>> GetModelsAsync<T>() where T : IModel;
    Task<ICollection<T>> GetLocalModelsAsync<T>() where T : IModel;
}