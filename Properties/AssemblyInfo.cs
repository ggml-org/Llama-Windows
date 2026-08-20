using System.Runtime.CompilerServices;

// Unit tests exercise pure managed internals (e.g. the ViewModel's
// FitEvaluation hook) without XAML — expose them to the test assembly,
// mirroring LlamaApp.HuggingFace and LlamaApp.LlamaCpp.
[assembly: InternalsVisibleTo("LlamaApp.Tests")]
