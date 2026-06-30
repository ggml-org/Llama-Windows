namespace LlamaApp.HuggingFace;

public class HubClient(string? token)
{
    private static string HUGGINGFACE_HUB_BASE_URL = "https://huggingface.co/api";
    
    public sealed class HubModelsClient(string baseUrl, string? token)
    {
        string Url { get; } = $"{baseUrl}/models";

        public async Task<Repository?> GetRepository(string id)
        {
            await Task.Delay(500);
            return null;
        }
        
        public async Task<Stream> GetModel(Repository repository, CancellationToken cancel, IProgress<float> progress)
        {
            using (HttpClient client = new())
            {
                if(token is not null)
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    
                var response = await client.GetAsync($"{Url}/{repository.Name}", cancel);
                if (response.EnsureSuccessStatusCode().IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStreamAsync(cancel);
                }
            }
            return await Task.FromResult(new MemoryStream());
        }
    }
    
    public HubModelsClient Models { get; } = new (HUGGINGFACE_HUB_BASE_URL, token);
}