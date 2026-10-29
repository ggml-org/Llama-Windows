namespace LlamaApp
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private MainWindow? _window;
        private TrayIconManager? _trayIcon;
        private OverlayWindow? _overlay;
        private GlobalHotkey? _hotkey;
        private Llama.RuntimeUpdateScheduler? _runtimeUpdates;

        /// <summary>
        /// In-flight <c>llama://install</c> downloads by server model id
        /// (<c>repo:quant</c>), for the install progress toast's Cancel
        /// button — these downloads have no MainWindow row to own a
        /// cancellation source until the poller adds one.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>
            InstallDownloads = new();

        /// <summary>
        /// At most one deep-link install download at a time — N rapid links
        /// must not start N parallel multi-GB downloads.
        /// </summary>
        private static readonly System.Threading.SemaphoreSlim InstallGate = new(1, 1);

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // Move the pre-rename data folder (%LOCALAPPDATA%\LlamaApp) to its
            // new home (%LOCALAPPDATA%\Llama) before logging or settings touch it.
            Common.AppData.MigrateLegacyFolder();

            // Initialize the global logger first, before anything that can
            // fail: a debug build logs at Debug level, a release build at Info.
            // The log lives at %LOCALAPPDATA%\Llama\logs\Llama-YYYYMMDD.log
            // (rolled daily, old files swept to a 7-day retention).
            Common.Log.Initialize(level:
#if DEBUG
                Common.LogLevel.Debug
#else
                Common.LogLevel.Info
#endif
            );
            Common.Log.Info("Llama starting");

            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                          ?? throw new InvalidOperationException("App must initialize on the UI thread.");

            // XAML-domain unhandled exceptions (including failures inside the
            // async void OnLaunched continuation) are otherwise SWALLOWED by
            // the framework: the process keeps pumping with no windows, tray
            // icon or hotkey — the app silently "doesn't start". Surface every
            // one in the log so the failure is diagnosable.
            UnhandledException += (_, e) =>
            {
                Common.Log.Error(e.Exception, "unhandled XAML exception");
            };

            InitializeComponent();
        }

        private Microsoft.UI.Dispatching.DispatcherQueue _dispatcher = null!;

        /// <summary>
        /// Invoked when the end user launches the application normally.  Other entry points
        /// will be used, such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Single-instance: a second launch redirects its activation to the
            // already-running instance (which re-opens the flyout — see the
            // Activated handler below) and exits, instead of creating a
            // duplicate tray icon and a second /models poller fighting over
            // the same server port.
            var instance = Microsoft.Windows.AppLifecycle.AppInstance
                .FindOrRegisterForKey("Llama");
            if (!instance.IsCurrent)
            {
                Common.Log.Info("another instance is already running; redirecting activation and exiting");
                try
                {
                    // The running instance must learn about this launch. Its
                    // AppInstance.Activated callback never fires on this setup
                    // (verified), so the request travels via the handoff file +
                    // named event instead: an "Open With" .gguf, a Windows-share
                    // payload, or nothing at all (the plain "reopen the flyout"
                    // second launch). A share's DataPackageView cannot cross a
                    // process boundary, so the payload is extracted HERE — in the
                    // process that received it — and only the distilled request
                    // (a path, or the text) is handed off.
                    var activatedArgs = instance.GetActivatedEventArgs();
                    var handoff = await ExtractHandoffFromActivationAsync(activatedArgs);
                    switch (handoff.Kind)
                    {
                        case Common.HandoffKind.ModelPath:
                            Common.InstanceHandoff.WriteModel(handoff.ModelPath!);
                            break;
                        case Common.HandoffKind.Text:
                            Common.InstanceHandoff.WriteText(handoff.Text!);
                            break;
                        case Common.HandoffKind.Image:
                            Common.InstanceHandoff.WriteImage(handoff.ModelPath!);
                            break;
                        case Common.HandoffKind.ModelId:
                            Common.InstanceHandoff.WriteModelId(handoff.ModelId!);
                            break;
                        case Common.HandoffKind.Install:
                            Common.InstanceHandoff.WriteInstall(handoff.Repo!);
                            break;
                    }
                    Common.InstanceHandoff.Signal();

                    // Donate this process's foreground right to the running
                    // instance: the shell launched US from the user's click,
                    // so we may set the foreground window — but the running
                    // instance receives no activation for a protocol/share
                    // handoff (the redirect below is skipped for them), and
                    // without a foreground right its SetForegroundWindow is
                    // denied: the flyout it shows for this request stays
                    // hidden behind the caller's window.
                    AllowSetForegroundToRunningInstance();

                    // Redirect the activation to the running instance — the
                    // documented pattern for launch/file activations — EXCEPT
                    // for shares and protocol (deep-link) activations:
                    //
                    // - RedirectActivationToAsync does not support ShareTarget
                    //   activations (the ShareOperation cannot cross processes,
                    //   which is exactly why the payload travels via the handoff
                    //   above). Redirecting a share anyway leaves the OS share
                    //   machinery with an activation it cannot deliver, and in
                    //   practice this failed fast the second process
                    //   (0xC0000409 from the native share host).
                    // - A redirected PROTOCOL activation is delivered into the
                    //   running instance over RPC — and that delivery raises a
                    //   native exception inside the Windows App Runtime's
                    //   activation-marshalling code which fail-fast KILLS the
                    //   running instance (0xC0000409 /
                    //   FAST_FAIL_FATAL_APP_EXIT in ucrtbase — verified from
                    //   the crash dump: the faulting thread is an RPC/COM
                    //   thread whose stack is all Microsoft.WindowsAppRuntime
                    //   frames, with no managed frames to catch it). The deep
                    //   link's full payload already travels via the handoff
                    //   file + named event above, so the redirect is redundant
                    //   for it — and it is the crash vector.
                    var isShareActivation =
                        activatedArgs.Data is Windows.ApplicationModel.Activation.ShareTargetActivatedEventArgs;
                    var isProtocolActivation =
                        activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol;
                    if (!isShareActivation && !isProtocolActivation)
                        await Task.Run(() => instance.RedirectActivationToAsync(activatedArgs));
                }
                catch (Exception ex)
                {
                    Common.Log.Warn(ex, "activation redirect failed");
                }
                Environment.Exit(0);
                return;
            }

            // The startup shortcut was renamed with the app ("LlamaApp.lnk" →
            // "Llama.lnk") — sweep any leftover from a pre-rename install.
            StartupHelper.RemoveLegacyShortcut();

            // "Open With → Llama" on a .gguf and Windows shares both deliver
            // through this instance's activation arguments (OnLaunched is the
            // one startup hook WinUI 3 desktop has — there is no
            // OnFileActivated/OnShareTargetActivated); an unpackaged
            // `LlamaApp.exe <file.gguf>` launch delivers it on the command
            // line (LaunchActivatedEventArgs.Arguments is empty for
            // unpackaged launches — verified). Captured here, handled once
            // startup completes below. A cold-start share is ours to report
            // (the share sheet is waiting on this process).
            var activationArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            var startupHandoff = await ExtractHandoffFromActivationAsync(activationArgs);
            var openModelFile = startupHandoff.Kind == Common.HandoffKind.ModelPath ? startupHandoff.ModelPath : null;
            var sharedPrompt = startupHandoff.Kind == Common.HandoffKind.Text ? startupHandoff.Text : null;
            var openModelId = startupHandoff.Kind == Common.HandoffKind.ModelId ? startupHandoff.ModelId : null;
            var installRepo = startupHandoff.Kind == Common.HandoffKind.Install ? startupHandoff.Repo : null;
            openModelFile ??= FirstGgufOnCommandLine();

            // Create the llama-server manager singleton with the configured
            // port and listen address BEFORE anything else can touch
            // LlamaManager.Shared — the MainWindow constructor below already
            // subscribes to its events. Both are baked into the manager at
            // construction, so values changed in Settings only take effect on
            // the next launch.
            var serverPort = Settings.Current.ServerPort;
            if (serverPort is < 1 or > 65535)
            {
                Common.Log.Warn($"configured server port {serverPort} is out of range; falling back to {Llama.LlamaManager.DefaultServerPort}");
                serverPort = Llama.LlamaManager.DefaultServerPort;
            }
            // The saved listen address may no longer be assigned to this
            // machine (DHCP change, a different network, or a hand-edited
            // settings.json). Resolve it against the live interfaces so the
            // app never targets — or adopts a server from — a foreign host;
            // fall back to loopback and warn when it can't be used.
            var resolvedListen = Common.ListenAddresses.Resolve(
                Settings.Current.ListenAddress, Common.ListenAddresses.List());
            if (resolvedListen.FellBack)
                Common.Log.Warn(
                    $"listen address '{Settings.Current.ListenAddress}' is not assigned to this machine; using {resolvedListen.Address}");

            // Non-loopback binds get an app-generated API key so the server
            // isn't an open control API on the network. Generated once and
            // persisted, so an adopted server (and a later app start) agree.
            string? apiKey = null;
            if (Common.ServerAuth.RequiresApiKey(resolvedListen.Address))
            {
                if (string.IsNullOrWhiteSpace(Settings.Current.ServerApiKey))
                {
                    Settings.Current.ServerApiKey = Common.ServerAuth.GenerateApiKey();
                    Settings.Current.Save();
                }
                apiKey = Settings.Current.ServerApiKey;
            }
            Common.Log.Info(
                $"listen address: {resolvedListen.Address}{(apiKey is null ? "" : " (API key required)")}");
            Llama.LlamaManager.Initialize(serverPort, resolvedListen.Address, apiKey);

            // The app is tray-only: the main window is created but never shown
            // on launch. It is revealed on demand as a flyout anchored to the
            // system-tray icon (see TrayIconManager / MainWindow.ShowAsFlyout),
            // mirroring the macOS menu-bar app on Windows 11.
            _window = new MainWindow();

            // The footer's Quit button asks the tray manager to tear the app
            // down (dispose the icon, close the window, exit).
            _window.ExitRequested += () => _trayIcon?.RequestExit();

            // Add the system-tray icon with its right-click context menu.
            _trayIcon = new TrayIconManager(_window);

            // Listen for a second instance's signal (Open With handoff, a
            // Windows share, or a plain second launch): the named event fires,
            // we consume any pending request and act on it. Replaces the
            // AppInstance.Activated path that never fires on this setup.
            var openWithEvent = Common.InstanceHandoff.Listen();
            ThreadPool.RegisterWaitForSingleObject(
                openWithEvent,
                (_, _) => _dispatcher.TryEnqueue(HandleSecondLaunchSignal),
                null, Timeout.Infinite, executeOnlyOnce: false);

            // Toast notifications for background events (model loaded, download
            // failed) while the flyout is hidden. Clicking a toast re-opens the
            // flyout; so does a redirected second-launch activation.
            Notifications.Initialize();
            // Surface a listen-address fallback once notifications exist.
            if (resolvedListen.FellBack)
                Notifications.Show("Listen address unavailable",
                    $"The configured listen address ({Settings.Current.ListenAddress}) isn't assigned to this machine. " +
                    $"The llama server will listen on {resolvedListen.Address} instead.");
            Notifications.Invoked += args => _dispatcher.TryEnqueue(() => HandleToastActivation(args));
            instance.Activated += (sender, args) => _dispatcher.TryEnqueue(() =>
            {
                // Belt-and-braces: when the redirect DOES deliver (MSIX on
                // other configurations), handle the activation directly.
                var request = FirstGgufFromFileActivation(args.Data) is { } directModel
                    ? Common.HandoffRequest.ForModel(directModel)
                    : Common.InstanceHandoff.TryConsume();
                DispatchHandoff(request);
            });

            // Ensure a llama.cpp server is reachable on the configured port:
            // adopt an already-running one, launch one from a found binary, or
            // download the binary via install.ps1 then launch. Fire-and-forget;
            // once reachable the MainWindow fetches the Available model list via
            // GET /models.
            Llama.LlamaManager.Shared.CacheDirectory = Settings.Current.CacheDirectory;
            Llama.LlamaManager.Shared.HuggingFaceToken = Settings.Current.HuggingFaceToken;
            Llama.LlamaManager.Shared.IdleUnloadSeconds = Settings.Current.IdleUnloadSeconds;
            Llama.LlamaManager.Shared.MaxLoadedModels = Settings.Current.MaxLoadedModels;
            Llama.LlamaManager.Shared.CacheTypeK = Settings.Current.CacheTypeK;
            Llama.LlamaManager.Shared.CacheTypeV = Settings.Current.CacheTypeV;
            Llama.LlamaManager.Shared.CustomServeArguments = Settings.Current.CustomServeArguments;
            // Shared by reference: the details view mutates this dictionary when
            // the user picks a context length, and the manager renders it into
            // the server's --models-preset INI (see ReloadModelPresetsAsync).
            Llama.LlamaManager.Shared.ModelContextLengths = Settings.Current.ModelContextLengths;
            // Draft-head detection shares the same INI channel: spec-type
            // entries (draft-dflash/…) detected from Hub repo listings ride
            // beside the context lengths (see DraftHeadDetection).
            Llama.LlamaManager.Shared.ModelSpeculativeTypes = Settings.Current.ModelSpeculativeTypes;
            // Enrolled model folders share the same INI channel: the recursive
            // scan (Settings → General → Model folders) renders a
            // model = path section per discovery.
            Llama.LlamaManager.Shared.ModelFolders = Settings.Current.ModelFolders;
            Common.Log.Info($"cache directory: {Settings.Current.CacheDirectory}");
            // Presence only — never log the token itself.
            Common.Log.Info($"HF token: {(string.IsNullOrWhiteSpace(Settings.Current.HuggingFaceToken) ? "not set" : "configured")}");
            // Weekly runtime self-update: when the last check (persisted under
            // %LOCALAPPDATA%\Llama) is a week old or older, check llama.cpp's
            // latest release and install it via install.ps1 BEFORE the server
            // launches — the one point where llama.exe is not in use. The
            // scheduler then runs the ensure and keeps an hourly watch; it
            // defers installs while the server is up. Fire-and-forget like
            // the ensure it wraps.
            _runtimeUpdates = new Llama.RuntimeUpdateScheduler(Llama.LlamaManager.Shared);
            // The weekly pass is silent — but a successful install deserves a
            // toast (it explains why the server takes a moment longer the
            // next time it starts, and why the version footer changed).
            _runtimeUpdates.RuntimeUpdated += build => _dispatcher.TryEnqueue(() =>
                Notifications.Show("llama.cpp updated",
                    $"The llama.cpp runtime was updated to b{build}. The new version is used the next time the server starts."));
            _ = _runtimeUpdates.StartAsync(
                () => Llama.LlamaManager.Shared.EnsureLlamaOrDownloadAsync());

            // Spotlight-style prompt overlay, summoned by a global Alt+Space
            // hotkey. Created lazily on first press and reused thereafter; the
            // hotkey is registered for the app's lifetime only, so it stops
            // working the moment Llama exits.
            _overlay = new OverlayWindow();
            _hotkey = new GlobalHotkey();
            // Initialize the overlay's WebView2 NOW — at a calm point in
            // startup, awaited and fully observed — rather than at the first
            // summon. The app runs x64-emulated on ARM64, and the first WebView2
            // initialization occasionally trips the emulation layer when a
            // share delivery triggers the first summon while the OS share
            // machinery is churning around the process (unobserved WinRT
            // failure → stowed exception → 0xC000027B fail-fast, catchable by
            // nothing). Prewarmed, the first summon just navigates — and is
            // instant, too.
            _overlay.PrewarmWebView();
            _hotkey.Register(() => _dispatcher.TryEnqueue(() => _overlay.Summon()));

            // The hotkey is hard-coded and otherwise undiscoverable — say so
            // when it can't work instead of the shortcut silently doing
            // nothing (RegisterHotKey fails when another app owns Alt+Space).
            if (!_hotkey.IsRegistered)
            {
                Notifications.Show("Alt+Space unavailable",
                    "Another app is using the Alt+Space shortcut, so the chat overlay hotkey is off.");
            }

            // One-time first-run hint: the app is tray-only (no window appears
            // on launch) and the overlay hotkey is undiscoverable — tell the
            // user both, exactly once.
            if (!Settings.Current.TrayHintShown)
            {
                Settings.Current.TrayHintShown = true;
                Settings.Current.Save();
                Notifications.Show("Llama is running",
                    "Find it in the system tray — and press Alt+Space anytime to chat with a loaded model.");
            }

            Common.Log.Info("startup complete");

            // The .gguf this instance was opened for, or the text it received
            // as a share (see startupHandoff above) — now the tray, server
            // pipeline and overlay all exist. A handoff left by a second
            // instance that signalled before the listener was up is consumed
            // here the same way.
            var startupRequest = openModelFile is not null
                ? Common.HandoffRequest.ForModel(openModelFile)
                : (sharedPrompt is not null
                    ? Common.HandoffRequest.ForText(sharedPrompt)
                    : (openModelId is not null
                        ? Common.HandoffRequest.ForModelId(openModelId)
                        : (installRepo is not null
                            ? Common.HandoffRequest.ForInstall(installRepo)
                            : Common.InstanceHandoff.TryConsume())));
            DispatchHandoff(startupRequest);

            // Speculative-decoding draft heads (issue #97): watch the server's
            // model list — any model that appears unchecked (downloaded from
            // the app, from the WebUI in a browser, cached before the feature,
            // or adopted with the server) is detected within a poll cycle.
            // Fire-and-forget: the watcher owns its own threading.
            DraftHeadDetection.StartWatching();
            // Enrolled model folders stay live the same way: a .gguf added or
            // removed on disk refreshes the server's view without a restart
            // (fire-and-forget; the watcher owns its own threading).
            EnrolledFolderWatch.StartWatching();
        }

        /// <summary>
        /// A second instance signalled (named event): consume whatever request
        /// it handed off — a model file ("Open With"/share) loads, shared
        /// text goes to the chat overlay; a plain second launch re-opens the
        /// flyout, as it always should have.
        /// </summary>
        private void HandleSecondLaunchSignal()
        {
            DispatchHandoff(Common.InstanceHandoff.TryConsume());
        }

        /// <summary>
        /// Acts on one handoff request: a model file loads through the
        /// "Open With" pipeline, shared text is delivered to the chat
        /// overlay's composer, and no request re-opens the flyout.
        /// </summary>
        private void DispatchHandoff(Common.HandoffRequest request)
        {
            switch (request.Kind)
            {
                case Common.HandoffKind.ModelPath:
                    _ = OpenModelFileAsync(request.ModelPath!);
                    break;
                case Common.HandoffKind.Text:
                    _ = HandleSharedPromptAsync(request.Text!);
                    break;
                case Common.HandoffKind.Image:
                    _ = HandleSharedImageAsync(request.ModelPath!);
                    break;
                case Common.HandoffKind.ModelId:
                    _ = LoadModelByIdAsync(request.ModelId!);
                    break;
                case Common.HandoffKind.Install:
                    _ = InstallRepoByIdAsync(request.Repo!);
                    break;
                default:
                    _trayIcon?.ShowFlyout();
                    break;
            }
        }

        /// <summary>
        /// Lets the running instance take the foreground (for the flyout it
        /// shows on this handoff). The shell gave the foreground right to
        /// THIS short-lived process; hand it to the instance that owns the
        /// window before exiting. A failure is harmless — the flyout then
        /// stays unfocused, as before.
        /// </summary>
        private static void AllowSetForegroundToRunningInstance()
        {
            try
            {
                var self = Environment.ProcessId;
                var instances = Microsoft.Windows.AppLifecycle.AppInstance.GetInstances();
                var owner = instances.FirstOrDefault(i => i.ProcessId != self && i.Key == "Llama")
                    ?? instances.FirstOrDefault(i => i.ProcessId != self);
                if (owner is not null)
                    AllowSetForegroundWindow(unchecked((int)owner.ProcessId));
            }
            catch (Exception ex)
            {
                Common.Log.Warn(ex, "foreground-right donation to the running instance failed");
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        /// <summary>
        /// The app-wide runtime update scheduler (created in OnLaunched) —
        /// Settings uses it for the Check-for-updates card.
        /// </summary>
        internal Llama.RuntimeUpdateScheduler RuntimeUpdates => _runtimeUpdates!;

        /// <summary>
        /// The first .gguf among a file activation's files (an "Open With"
        /// activation may carry several), or null. Works for the cold-start
        /// activation (<see cref="LlamaApp.OnLaunched"/>) and for the ones
        /// redirected from a second instance (AppInstance.Activated).
        /// </summary>
        private static string? FirstGgufFromFileActivation(object? activationData)
        {
            if (activationData is Windows.ApplicationModel.Activation.FileActivatedEventArgs fileArgs)
            {
                foreach (var file in fileArgs.Files)
                    if (Common.GgufFile.IsGguf(file.Path))
                        return file.Path;
            }
            return null;
        }

        /// <summary>
        /// Distills this process's activation into a handoff request: a share
        /// activation's payload (shared text, a .gguf, or an image), an
        /// "Open With" .gguf, or nothing.
        ///
        /// <para>A share's <c>DataPackageView</c> cannot cross a process
        /// boundary, so the extraction happens in the process that received
        /// the activation — the cold-start instance (which keeps the distilled
        /// request and handles it after startup) or a second instance (which
        /// writes it to the handoff file and exits). Per the ShareOperation
        /// contract, the process that received the activation ALWAYS reports
        /// it — ReportStarted around the extraction, ReportCompleted after,
        /// ReportError on failure. Exiting with an unreported operation leaves
        /// the in-process share host in a bad state and failed fast in
        /// practice (0xC0000409 from the native share machinery).</para>
        /// </summary>
        private static async Task<Common.HandoffRequest> ExtractHandoffFromActivationAsync(
            Microsoft.Windows.AppLifecycle.AppActivationArguments activatedArgs)
        {
            try
            {
                var data = activatedArgs.Data;
                Common.Log.Debug(
                    $"activation kind={activatedArgs.Kind} data-type={data?.GetType().FullName ?? "null"}");

                // "Open With": a file activation carries the path directly.
                if (FirstGgufFromFileActivation(data) is { } gguf)
                    return Common.HandoffRequest.ForModel(gguf);

                // A llama:// deep link (protocol activation): a chat prompt, or
                // a model id to load. Parsing is strict (unknown hosts and
                // oversized payloads yield no request — the link just re-opens
                // the flyout), and the id is matched against the server's model
                // list before anything loads.
                if (activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol &&
                    data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol)
                {
                    Common.Log.Debug($"deep link received: {protocol.Uri}");
                    var link = Common.DeepLink.Parse(protocol.Uri?.ToString());
                    switch (link.Kind)
                    {
                        case Common.DeepLinkKind.Chat when link.Prompt is { } prompt:
                            return Common.HandoffRequest.ForText(prompt);
                        case Common.DeepLinkKind.LoadModel when link.ModelId is { } modelId:
                            return Common.HandoffRequest.ForModelId(modelId);
                        case Common.DeepLinkKind.Install when link.Repo is { } repo:
                            return Common.HandoffRequest.ForInstall(repo);
                        // llama://chat with no prompt behaves like a plain second launch.
                    }
                    return Common.HandoffRequest.None;
                }

                // A Windows share: text, or files (.gguf imports).
                if (data is Windows.ApplicationModel.Activation.ShareTargetActivatedEventArgs share)
                {
                    var operation = share.ShareOperation;
                    operation.ReportStarted();
                    try
                    {
                        return await ExtractSharePayloadAsync(operation);
                    }
                    catch (Exception ex)
                    {
                        Common.Log.Warn(ex, "share payload extraction failed; reporting the share as failed");
                        try { operation.ReportError("Llama could not read the shared content."); } catch { /* best-effort */ }
                        return Common.HandoffRequest.None;
                    }
                }
            }
            catch (Exception ex)
            {
                Common.Log.Warn(ex, "share activation extraction failed");
            }
            return Common.HandoffRequest.None;
        }

        /// <summary>The payload of a reported share: text, a model file, an image
        /// file, or a re-encoded raw bitmap. Assumes ReportStarted was already
        /// called and the caller reports Completed/Error.</summary>
        private static async Task<Common.HandoffRequest> ExtractSharePayloadAsync(
            Windows.ApplicationModel.DataTransfer.ShareTarget.ShareOperation operation)
        {
            var package = operation.Data;
            if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                var text = await package.GetTextAsync();
                operation.ReportCompleted();
                return text.Length > 0
                    ? Common.HandoffRequest.ForText(text)
                    : Common.HandoffRequest.None;
            }

            if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await package.GetStorageItemsAsync();
                var paths = items.Select(item => item.Path).Where(p => p is not null).Cast<string>().ToList();

                // A shared .gguf is a model to load — the same pipeline
                // as Open With.
                var model = paths.FirstOrDefault(Common.GgufFile.IsGguf);
                if (model is not null)
                {
                    operation.ReportCompleted();
                    return Common.HandoffRequest.ForModel(model);
                }

                // Otherwise an image (.jpg/.jpeg/.png) attaches to the
                // chat.
                var image = paths.FirstOrDefault(Common.SharedImage.IsImagePath);
                operation.ReportCompleted();
                return image is not null
                    ? Common.HandoffRequest.ForImage(image)
                    : Common.HandoffRequest.None;
            }

            // A Bitmap share (e.g. a screenshot tool that hands over
            // pixels, not a file) is re-encoded as PNG into the app's
            // scratch dir — the WebUI paste takes a file's bytes.
            if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            {
                var reference = await package.GetBitmapAsync();
                string? imagePath = null;
                try
                {
                    using var stream = await reference.OpenReadAsync();
                    imagePath = await SaveSharedBitmapAsync(stream);
                }
                catch (Exception ex)
                {
                    Common.Log.Warn(ex, "decoding the shared bitmap failed");
                }
                operation.ReportCompleted();
                return imagePath is not null
                    ? Common.HandoffRequest.ForImage(imagePath)
                    : Common.HandoffRequest.None;
            }

            operation.ReportCompleted();
            return Common.HandoffRequest.None;
        }

        /// <summary>
        /// A <c>llama://model/&lt;id&gt;</c> deep link: summon the chat overlay and
        /// load the model. The id is remote-controlled data (any app or site
        /// can open one of these), so it must match a model the server actually
        /// knows — never a download, never a path — and the load rides the same
        /// POST /models/load pipeline as a click in the main window. On a cold
        /// start the model list may still be loading, so the match retries for
        /// a few seconds before giving up with a toast.
        /// </summary>
        private async Task LoadModelByIdAsync(string modelId)
        {
            try
            {
                _overlay?.Summon(ShareSummonGraceMs);

                // The startup model list may not be in yet — poll briefly.
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    var snapshot = Llama.LlamaManager.Shared.LastModelsSnapshot;
                    var known = snapshot.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.Ordinal));
                    if (known is not null)
                    {
                        Common.Log.Info($"deep link: loading model '{modelId}'");
                        var item = new Views.ModelItem { Name = modelId, RepoName = modelId };
                        if (!await Llama.LlamaManager.Shared.LoadModelAsync(item))
                            Common.Log.Warn($"deep link: the server declined to load '{modelId}'");
                        return; // loaded or not, the overlay is up with the WebUI
                    }
                    await Task.Delay(500);
                }

                Common.Log.Warn($"deep link: model '{modelId}' is not known to the server");
                Notifications.Show("Model not found",
                    $"'{modelId}' isn't available on this server — pick or download a model in the chat.");
            }
            catch (Exception ex)
            {
                Common.Log.Warn(ex, "deep link model load failed");
            }
        }

        /// <summary>
        /// A <c>llama://install?repo=&lt;id&gt;</c> deep link: install a
        /// Hugging Face repo's model — resolve the repo's quants and their
        /// sizes, pick one that fits this machine (see
        /// <see cref="QuantSelection.SelectQuant"/>), and download
        /// it through the llama server (which fetches shards, mmproj and draft
        /// sidecars itself) — mirroring the Mac app's <c>llama://install</c>
        /// flow.
        ///
        /// <para>The repo id is remote-controlled (any app or site can open
        /// one of these links) or user-writable (the handoff file), so it is
        /// re-validated before it reaches a Hub URL. The download is
        /// acknowledged immediately with a throttled progress toast carrying a
        /// Cancel action (the link's only UI while the flyout is hidden); when
        /// the model lands, the model list poller adds the row — nothing is
        /// built here — and failures surface as a classified failure toast
        /// (see <see cref="DownloadFailurePresentation"/>).</para>
        /// </summary>
        private async Task InstallRepoByIdAsync(string? repo)
        {
            // (a) Validate + normalize: trim, drop outer slashes, then the
            // same safe-segment rules the link parser applies (defense in
            // depth — the handoff file is writable outside the app).
            repo = repo?.Trim().Trim('/') ?? "";
            if (!Common.DeepLink.IsSafeRepo(repo))
            {
                Common.Log.Warn("deep link install: rejected an unsafe repo id");
                Notifications.Show("Couldn't install model",
                    "The link's repo id isn't a valid Hugging Face repo.");
                return;
            }

            try
            {
                // (b) Resolve the repo's downloadable quants and their sizes
                // (GET /api/models/{repo}?blobs=true). Rejections surface as
                // typed statuses so a gated repo is never reported as missing.
                var hub = new HuggingFace.HubClient(Settings.Current.HuggingFaceToken);
                List<HuggingFace.HubClient.RepoFile> files;
                try
                {
                    files = await hub.ListRepoFileSizesAsync(repo);
                }
                catch (HuggingFace.HubSearchException ex) when (ex.Status is 401 or 403)
                {
                    Common.Log.Warn($"deep link install: '{repo}' is gated ({ex.Status})");
                    Notifications.Show($"Couldn't install {repo}",
                        "The repo is gated — accept its license on Hugging Face (or set an access " +
                        "token in Settings), then open the link again.");
                    return;
                }
                catch (HuggingFace.HubSearchException ex) when (ex.Status == 404)
                {
                    Common.Log.Warn($"deep link install: '{repo}' not found (404)");
                    Notifications.Show($"Couldn't install {repo}",
                        "No Hugging Face repo with that name was found.");
                    return;
                }

                var builds = QuantSelection.BuildsFor(repo, files);
                if (builds.Count == 0)
                {
                    Common.Log.Warn($"deep link install: '{repo}' has no downloadable GGUF quants");
                    Notifications.Show($"Couldn't install {repo}",
                        "The repo doesn't contain GGUF files with a recognizable quant tag.");
                    return;
                }

                // (c) Pick the quant: the largest one whose fit is Fit/Might
                // against this machine's memory budget (free VRAM + the usable
                // share of RAM), with the selector's conservative fallback
                // when nothing fits or the sizes are unknown.
                ulong budget = 0;
                try
                {
                    budget = await Llama.LlamaManager.Shared.ContextMemoryBudgetAsync();
                }
                catch (Exception ex)
                {
                    Common.Log.Warn(ex, "deep link install: memory budget probe failed; picking blind");
                }
                var pick = QuantSelection.SelectQuant(builds, budget);
                if (pick is null)
                {
                    Common.Log.Warn($"deep link install: '{repo}' resolved {builds.Count} quant(s), none fit a {budget}-byte budget");
                    Notifications.Show($"Couldn't install {repo}",
                        "None of this repo's quants fit this machine's memory.");
                    return;
                }
                Common.Log.Info($"deep link install: '{repo}' resolved {builds.Count} quant(s); " +
                    $"chosen {pick.Quant} against a {budget}-byte budget");

                // Disk-space preflight: a deep link starts a multi-GB download
                // with no row to block it, so refuse up front when the cache
                // drive can't hold the selected build (a failed probe never
                // blocks).
                if (pick.SizeBytes > 0 &&
                    !Common.DiskSpace.HasEnoughSpace(
                        Settings.Current.CacheDirectory, pick.SizeBytes, out var freeBytes))
                {
                    Common.Log.Info($"deep link install: '{repo}' blocked — {pick.SizeBytes} bytes needed, {freeBytes} free");
                    Notifications.Show($"Couldn't install {repo}",
                        "Not enough disk space for the selected model.");
                    return;
                }

                // Cold start: the install may fire while the server is still
                // coming up. DownloadModelAsync hard-fails on a non-Running
                // server, so wait (bounded) for it before starting the download.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (Llama.LlamaManager.Shared.ServerStatus != Llama.LlamaManager.ServerState.Running)
                {
                    if (Llama.LlamaManager.Shared.ServerStatus == Llama.LlamaManager.ServerState.Failed ||
                        Llama.LlamaManager.Shared.State == Llama.LlamaManager.InstallState.Failed ||
                        DateTime.UtcNow >= deadline)
                        break;
                    await Task.Delay(500);
                }
                if (Llama.LlamaManager.Shared.ServerStatus != Llama.LlamaManager.ServerState.Running)
                {
                    Common.Log.Warn($"deep link install: '{repo}' — server not running, cannot download");
                    Notifications.Show($"Couldn't install {repo}",
                        "The llama server isn't running yet — try again in a moment.");
                    return;
                }

                // (d) Download through the server with a throttled progress
                // toast (the row-driven counterpart lives in
                // MainWindow.DownloadAndLaunchAsync).
                await DownloadInstallAsync(pick);
            }
            catch (Exception ex)
            {
                Common.Log.Error(ex, $"deep link install of '{repo}' failed");
                Notifications.Show($"Couldn't install {repo}",
                    "Llama couldn't read the repo's file list. Check the connection and try again.");
            }
        }

        /// <summary>
        /// Drives one <c>llama://install</c> download: POSTs the
        /// <c>repo:quant</c> to the llama server (which resolves shards and
        /// sidecars itself) and mirrors the MainWindow download flow's
        /// throttled progress toast — ~1 update/sec with a percent + speed
        /// status line and a Cancel action — closing it on every terminal
        /// path. On success nothing else happens here: the file lands in the
        /// server's cache and the model list poller adds the row on its next
        /// cycle. On failure the classified
        /// <see cref="DownloadFailurePresentation"/> toast explains what to do.
        /// </summary>
        private async Task DownloadInstallAsync(HuggingFace.Repository target)
        {
            // Global single-flight: at most one deep-link install download at a
            // time — N rapid links must not start N parallel multi-GB downloads.
            if (!InstallGate.Wait(0))
            {
                Common.Log.Info("deep link install: another install is already in flight");
                Notifications.Show("Install in progress",
                    "Another model is already downloading — try again when it finishes.");
                return;
            }
            try
            {
                await DownloadInstallCoreAsync(target);
            }
            finally
            {
                InstallGate.Release();
            }
        }

        private async Task DownloadInstallCoreAsync(HuggingFace.Repository target)
        {
            var serverId = target.ServerModelId;

            // One download per repo:quant — a re-fired link while the download
            // is in flight joins the existing toast instead of a second POST.
            using var cts = new CancellationTokenSource();
            if (!InstallDownloads.TryAdd(serverId, cts))
            {
                Common.Log.Info($"deep link install: {serverId} is already downloading");
                return;
            }

            var toastTag = ToastTagForInstall(serverId);
            var toastShown = false;
            try
            {
                // Surface the download: a deep link fires with the flyout
                // hidden, and the progress toast only appears with the first
                // byte — without this the click feels like a no-op. The
                // model-list poller turns the download into a row on its
                // next cycle.
                _trayIcon?.ShowFlyout();

                var cancelToastAction = new ToastAction("Cancel",
                    ("action", "cancelInstall"), ("id", serverId));

                // Throttle toast updates to ~1/sec — each one is a
                // notification-platform call — and EMA-smooth the speed
                // between samples (same shape as DownloadAndLaunchAsync).
                long lastToastMs = 0, lastSampleMs = 0, lastSampleBytes = 0;
                double bytesPerSecond = 0;
                Common.ModelDownloadProgress? failureReport = null;
                var progress = new Progress<Common.ModelDownloadProgress>(p =>
                {
                    if (p.Failed) { failureReport = p; return; } // terminal — classified after the await
                    if (p.Done || p.TotalBytes <= 0) return;

                    var now = Environment.TickCount64;
                    if (now - lastToastMs < 1000) return;
                    lastToastMs = now;

                    if (lastSampleMs != 0 && p.DownloadedBytes > lastSampleBytes)
                    {
                        var instantaneous = (p.DownloadedBytes - lastSampleBytes)
                            * 1000.0 / Math.Max(1, now - lastSampleMs);
                        bytesPerSecond = DownloadProgressPresentation.SmoothSpeed(bytesPerSecond, instantaneous);
                    }
                    lastSampleBytes = p.DownloadedBytes;
                    lastSampleMs = now;

                    toastShown = true;
                    Notifications.ShowProgress(toastTag,
                        "Downloading " + serverId,
                        InstallToastStatus(p.Fraction, bytesPerSecond),
                        p.Fraction, cancelToastAction);
                });

                Common.Log.Info($"deep link install: downloading {serverId}");
                var ok = await Llama.LlamaManager.Shared.DownloadModelAsync(target, progress, cts.Token);
                if (ok)
                {
                    Common.Log.Info($"deep link install: {serverId} downloaded — " +
                        "the model list poller adds the row on its next cycle");
                    return;
                }

                var failure = DownloadFailurePresentation.Classify(
                    failureReport?.HttpStatus, failureReport?.Message, failureReport?.ExceptionType);
                Common.Log.Warn($"deep link install: {serverId} failed — {failure.RawDetail ?? failure.Headline}");
                Notifications.Show("Download failed",
                    DownloadFailurePresentation.ToastBody(serverId, failure));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Cancel button on the progress toast — the server has been
                // asked to abort and drops the partial bytes.
                Common.Log.Info($"deep link install: {serverId} canceled");
            }
            catch (OperationCanceledException ex)
            {
                // A timeout (the 30 s POST budget's TaskCanceledException, not
                // the user's Cancel) — surface it instead of vanishing.
                Common.Log.Error(ex, $"deep link install: download of {serverId} timed out");
                var failure = DownloadFailurePresentation.Classify(null, "The download timed out.", "Timeout");
                Notifications.Show("Download failed",
                    DownloadFailurePresentation.ToastBody(serverId, failure));
            }
            catch (Exception ex)
            {
                Common.Log.Error(ex, $"deep link install: download of {serverId} failed unexpectedly");
                var failure = DownloadFailurePresentation.Classify(null, ex.Message, ex.GetType().Name);
                Notifications.Show("Download failed",
                    DownloadFailurePresentation.ToastBody(serverId, failure));
            }
            finally
            {
                // Unregister + close before the `using` disposes so a late
                // Cancel click can never reach a disposed source.
                if (toastShown) Notifications.Close(toastTag);
                InstallDownloads.TryRemove(serverId, out _);
            }
        }

        /// <summary>
        /// A bounded toast tag for an install download: the platform toast tag
        /// has a short length limit, and the repo id is remote-controlled (up
        /// to 200 chars), so truncate plus a stable short hash keeps tags unique
        /// without overflowing the notification API.
        /// </summary>
        private static string ToastTagForInstall(string serverId)
        {
            const int head = 40;
            var prefix = serverId.Length > head ? serverId[..head] : serverId;
            return "install:" + prefix + "#" +
                   serverId.GetHashCode(StringComparison.Ordinal).ToString("x");
        }

        /// <summary>
        /// Cancels an in-flight <c>llama://install</c> download from its
        /// progress toast's Cancel button. A stale toast (the download already
        /// finished and cleaned up) is a no-op.
        /// </summary>
        private static void CancelInstallFromToast(string? serverModelId)
        {
            try
            {
                if (serverModelId is not null && InstallDownloads.TryGetValue(serverModelId, out var cts))
                    cts.Cancel();
            }
            catch (Exception ex)
            {
                // The download finished (or is finishing) between the lookup
                // and the cancel — either way there's nothing left to cancel.
                Common.Log.Warn(ex, "deep link install: cancel failed (download may already be gone)");
            }
        }

        /// <summary>
        /// The install progress toast's status line — percent + smoothed
        /// speed, e.g. "42% · 12.3 MB/s" (the same format as the row-driven
        /// download toast; a stalled stream shows just the percent).
        /// </summary>
        private static string InstallToastStatus(double fraction, double bytesPerSecond)
        {
            var pct = (int)Math.Round(fraction * 100);
            return bytesPerSecond > 0
                ? $"{pct}% · {DownloadProgressPresentation.FormatBytes(bytesPerSecond)}/s"
                : $"{pct}%";
        }

        /// <summary>
        /// Shared text lands in the chat overlay: the overlay opens (Summon)
        /// and the text is handed to it as a pending prompt, which the overlay
        /// injects into the WebUI's composer as soon as one exists (a share
        /// that arrives before any model is loaded waits until the chat
        /// screen is up — see OverlayWindow.SetPendingPrompt).
        /// </summary>
        private async Task HandleSharedPromptAsync(string prompt)
        {
            try
            {
                Common.Log.Info($"shared text received ({prompt.Length} chars); opening the chat overlay");
                _overlay?.Summon(ShareSummonGraceMs);
                _overlay?.SetPendingPrompt(prompt);
            }
            catch (Exception ex)
            {
                Common.Log.Warn(ex, "handling the shared prompt failed");
            }
            await Task.CompletedTask;
        }

        /// <summary>
        /// A programmatic overlay summon ignores focus loss for this long: the
        /// dismissing share sheet races the summon for focus and can
        /// activate-then-deactivate the overlay repeatedly, which used to flash
        /// it shut before the user ever saw it. Long enough to outlast the sheet's
        /// teardown; short enough that click-away still dismisses normally after.
        /// </summary>
        private const int ShareSummonGraceMs = 3000;

        /// <summary>
        /// A shared image attaches to the chat overlay: the overlay opens and
        /// the image is queued as a pending attachment (a synthetic paste
        /// into the WebUI's composer, gated on an image-capable model — see
        /// <see cref="OverlayWindow.SetPendingImage"/>). When no image-capable
        /// model is loaded, one is loaded for the share — the user's intent is
        /// "ask about this image", which needs vision — preferring models the
        /// user has used before (a saved context length), smallest first; the
        /// overlay's retry loop pastes once the model is resident.
        /// </summary>
        private async Task HandleSharedImageAsync(string imagePath)
        {
            try
            {
                Common.Log.Info($"shared image received '{System.IO.Path.GetFileName(imagePath)}'; opening the chat overlay");
                _overlay?.Summon(ShareSummonGraceMs);
                _overlay?.SetPendingImage(imagePath);
                _ = EnsureVisionModelForShareAsync();
            }
            catch (Exception ex)
            {
                Common.Log.Warn(ex, "handling the shared image failed");
            }
            await Task.CompletedTask;
        }

        /// <summary>
        /// Loads an image-capable model for an incoming shared image when no
        /// suitable model is loaded: prefer the loaded model when it already
        /// accepts images, else prefer models the user has configured a context
        /// length for (a trace of prior use), smallest on-disk model first so
        /// the pick fits the GPU and loads fast. Fire-and-forget — the overlay's
        /// paste retry loop delivers once the model is resident.
        /// </summary>
        private async Task EnsureVisionModelForShareAsync()
        {
            try
            {
                var snapshot = Llama.LlamaManager.Shared.LastModelsSnapshot;
                if (snapshot.FirstOrDefault(m => m.IsLoaded && m.SupportsImage) is not null)
                    return; // already usable — the retry loop will paste

                var candidates = snapshot
                    .Where(m => m.SupportsImage && !m.IsLoading && !m.IsDownloading)
                    .ToList();
                if (candidates.Count == 0)
                {
                    Common.Log.Warn("no image-capable model is available for the shared image; " +
                        "the overlay will wait for the user to load one");
                    return; // the wait-and-notify path remains
                }

                var preferred = candidates
                    .Where(m => Settings.Current.ModelContextLengths.ContainsKey(m.Id))
                    .ToList();
                var pool = preferred.Count > 0 ? preferred : candidates;
                var choice = pool
                    .OrderBy(m => System.IO.File.Exists(m.Path) ? new System.IO.FileInfo(m.Path).Length : long.MaxValue)
                    .First();

                Common.Log.Info($"loading image-capable model '{choice.Id}' for the shared image");
                var item = new Views.ModelItem { Name = choice.Id, RepoName = choice.Id };
                if (!await Llama.LlamaManager.Shared.LoadModelAsync(item))
                    Common.Log.Warn($"the server declined to load '{choice.Id}' for the shared image");
                // Loaded or not: the overlay's retry loop decides delivery, and
                // the poller refreshes LoadedModelSupportsImages within ~1s.
            }
            catch (Exception ex)
            {
                Common.Log.Warn(ex, "auto-loading a vision model for the share failed");
            }
        }

        /// <summary>
        /// Re-encodes a shared <see cref="Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap"/>
        /// stream as PNG into the app's scratch dir
        /// (<c>%LOCALAPPDATA%\Llama\shared</c>, swept on delivery). Returns the
        /// written path, or null when the pixels couldn't be decoded (some
        /// sources share raw DIB data the decoder can't read).
        /// </summary>
        private static async Task<string?> SaveSharedBitmapAsync(
            Windows.Storage.Streams.IRandomAccessStream stream)
        {
            var dir = Views.OverlayWindow.ScratchImageDir;
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"shared-{Guid.NewGuid():N}.png");

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            using var fileStream = File.Open(path, FileMode.CreateNew);
            using var winrtStream = fileStream.AsRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, winrtStream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();

            return path;
        }

        /// <summary>
        /// An unpackaged <c>LlamaApp.exe C:\path\model.gguf</c> launch (dev
        /// runs, scripts) carries the path on the command line — WinUI 3's
        /// LaunchActivatedEventArgs.Arguments is empty there, so argv is read
        /// directly. Quotes are tolerated; a non-existing file is ignored
        /// (the arguments are free-form).
        /// </summary>
        private static string? FirstGgufOnCommandLine()
        {
            var argv = Environment.GetCommandLineArgs();
            for (var i = 1; i < argv.Length; i++)
            {
                var candidate = argv[i].Trim().Trim('"');
                if (Common.GgufFile.IsGguf(candidate) && File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        /// <summary>
        /// Handles "Open With → Llama" on a .gguf: shows the flyout, ensures
        /// the llama server is up, links the file into the server's
        /// <c>--models-dir</c> (where the router discovers it as a model named
        /// after the file), refreshes the model list so the router registers
        /// the import, and loads it. On success the chat overlay opens on the
        /// model; failures surface as toasts with actionable text.
        /// </summary>
        private async Task OpenModelFileAsync(string modelPath)
        {
            var fileName = System.IO.Path.GetFileName(modelPath);
            try
            {
                // Something visibly happens immediately (loads take a while).
                _trayIcon?.ShowFlyout();

                // The server may need installing/starting (first ever use can
                // be triggered by opening a model file).
                var reachable = await Llama.LlamaManager.Shared.EnsureLlamaOrDownloadAsync();
                if (!reachable)
                {
                    Notifications.Show($"Couldn't open {fileName}",
                        "The llama server isn't running. Open the Llama flyout and check the server status, then try again.");
                    return;
                }

                var import = Llama.LlamaManager.ImportLocalModel(modelPath);
                if (import.Outcome == Llama.LocalModelImportOutcome.Copied)
                    Notifications.Show($"Copying {fileName}",
                        "The file lives on another drive, so it was copied into Llama's models folder — the model loads next.");

                // The router rescans its model sources when asked (older
                // servers only on GET /models?reload=1) — the refresh registers
                // the import, making its id loadable (a load without it would
                // answer 404 model-is-not-found).
                await Llama.LlamaManager.Shared.RefreshModelsAsync();

                // Embedded MTP head (issue #97): builds that bake the head
                // into the main weights tag their filename with an mtp token
                // (…-Q4_K_M-mtp.gguf). The server's models-dir scan wires
                // sidecar files by itself, but not embedded heads — the
                // spec-type entry for those comes from the app. Best-effort.
                if (Common.DraftHeads.HasEmbeddedMtp(fileName))
                {
                    // Under the dictionary's lock — the same shared settings
                    // dictionary the draft-head poller and the INI renderer
                    // touch from background threads (see DraftHeadDetection.Apply).
                    var specTypes = Settings.Current.ModelSpeculativeTypes;
                    lock (specTypes)
                        specTypes[import.ModelId] =
                            Common.DraftHeads.SpecType(Common.DraftHeadKind.Mtp);
                    Settings.Current.Save();
                    await Llama.LlamaManager.Shared.ReloadModelPresetsAsync();
                    Common.Log.Info($"draft heads: '{fileName}' carries an embedded MTP head — speculative decoding enabled");
                }

                // ServerModelId of a file-imported model is its id (no
                // repo:quant shape — see LocalModelImport.DeriveModelId).
                var model = new Views.ModelItem { Name = import.ModelId, RepoName = import.ModelId };
                var loaded = await Llama.LlamaManager.Shared.LoadModelAsync(model);
                if (loaded)
                {
                    Common.Log.Info($"opened model file '{modelPath}' as '{import.ModelId}'");
                    SummonChatOverlay();
                }
                else
                {
                    Common.Log.Warn($"open-with: the server rejected model '{import.ModelId}' ({modelPath})");
                    Notifications.Show($"Couldn't load {fileName}",
                        "The llama server rejected the model. It may be an unsupported or damaged GGUF file.");
                }
            }
            catch (Exception ex)
            {
                // The importer throws actionable IOExceptions (file missing,
                // no space for a cross-volume copy) — the message is the toast.
                Common.Log.Error(ex, $"open-with of '{modelPath}' failed");
                Notifications.Show($"Couldn't open {fileName}", ex.Message);
            }
        }

        /// <summary>
        /// Opens the chat overlay — the Alt+Space panel — from surfaces that
        /// don't own the hotkey (toast Chat button, tray context menu).
        /// </summary>
        internal void SummonChatOverlay() => _overlay?.Summon(ShareSummonGraceMs);

        /// <summary>
        /// Responds to a toast activation on the UI thread: a body click keeps
        /// the long-standing behavior (open the flyout); an action button's
        /// arguments name the response — retry/cancel a download by model
        /// id (the MainWindow resolves the row), or open the chat overlay.
        /// Unknown arguments fall back to the flyout rather than doing
        /// nothing — a stale toast from a previous version should still go
        /// somewhere useful.
        /// </summary>
        private void HandleToastActivation(IReadOnlyDictionary<string, string> args)
        {
            if (args.TryGetValue("action", out var action))
            {
                args.TryGetValue("id", out var id);
                switch (action)
                {
                    case "chat":
                        _overlay?.Summon();
                        return;
                    case "retryDownload":
                        _window?.RetryDownloadFromToast(id);
                        return;
                    case "cancelDownload":
                        _window?.CancelDownloadFromToast(id);
                        return;
                    case "cancelInstall":
                        CancelInstallFromToast(id);
                        return;
                    case "reloadDraftHead":
                        // A draft head was detected on a loaded model (issue
                        // #97); the reload respawns the child with its
                        // spec-type so the head engages now.
                        if (!string.IsNullOrWhiteSpace(id))
                            _ = DraftHeadDetection.ReloadForDraftHeadsAsync(id);
                        return;
                }
            }
            _trayIcon?.ShowFlyout();
        }
    }
}