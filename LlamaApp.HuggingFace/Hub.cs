namespace LlamaApp.HuggingFace;

public class Hub
{
    private static string DEFAULT_REMOTE_URL = "https://huggingface.co";
    private static string DEFAULT_PRESET_REPO = "mfuntowicz/llamaapp-preset";
    private static string DEFAULT_PRESET_FILE = "preset.json";
    
    public async void Download(Repository repo, string destination)
    {
        
    }

    public async Task<List<Repository>> Search(string query)
    {
        return [];
    }
}