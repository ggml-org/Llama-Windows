using LlamaApp.Common;

namespace LlamaApp.Catalog;

public class Catalog(IModelSource source)
{
    async Task<ICollection<IModel>> ListModels()
    {
        return await source.GetModelsAsync<IModel>();
    }

    async Task<ICollection<IModel>> ListModelsAvailableLocally()
    {
        return await source.GetLocalModelsAsync<IModel>();
    }

    // async Task<IModel> Download(IModel model)
    // {
    //     
    // }
}