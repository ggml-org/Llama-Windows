using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LlamaApp.Views;

/// <summary>
/// Page hosting a WebView2 that renders the local chat server at http://127.0.0.1:8080.
/// </summary>
/// <remarks>
/// Initialization is driven entirely by the <c>Source</c> attribute set in
/// ChatPage.xaml (see the comment there for why EnsureCoreWebView2Async is not
/// called from code-behind). The control auto-initializes with the default
/// WebView2 environment and navigates to the local chat server on its own.
///
/// This requires the app to run with a native platform that matches an installed
/// WebView2 runtime. The csproj coerces "Any CPU" builds to x64 (PlatformTarget)
/// for that reason: an ARM64-native apphost on ARM64 Windows cannot load the
/// x64/ARM64EC WebView2 runtime and init hangs (blank page); the x64 apphost runs
/// natively on x64 Windows and emulated on ARM64 Windows, matching the runtime.
/// </remarks>
public sealed partial class ChatPage : Page
{
    // The local server the WebView2 loads. Use 127.0.0.1 explicitly because the
    // server binds to IPv4 only; "localhost" can resolve to ::1 (IPv6) and fail
    // to connect inside WebView2. Kept in sync with the Source in ChatPage.xaml.
    private const string ChatUrl = "http://127.0.0.1:8080";

    public ChatPage()
    {
        InitializeComponent();
    }
}