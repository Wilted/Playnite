﻿using Playnite.Controllers;
using Playnite.Input;
using Playnite.SDK;
using Playnite.Plugins;
using Playnite.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics;
using Playnite.Database;
using Playnite.API;
using TheArtOfDev.HtmlRenderer;
using Playnite.Services;
using System.Windows.Input;
using System.Windows.Interop;
using System.Reflection;
using System.IO;
using Playnite.Common;
using System.ComponentModel;
using Playnite.Windows;
using Polly;
using System.Windows.Media;
using Playnite.SDK.Events;
using System.Windows.Threading;
using System.Net;
using Playnite.Common.Web;
using System.ServiceProcess;

namespace Playnite
{
    public abstract class PlayniteApplication : ObservableObject, IPlayniteApplication
    {
        private ILogger logger = LogManager.GetLogger();
        private const string instanceMuxet = "PlayniteInstaceMutex";
        private Mutex appMutex;
        private bool resourcesReleased = false;
        private PipeService pipeService;
        private PipeServer pipeServer;
        private XInputDevice xdevice;
        private System.Threading.Timer updateCheckTimer;
        private bool installingAddon = false;
        private AddonLoadError themeLoadError = AddonLoadError.None;
        private ThemeManifest customTheme;

        private bool isActive;
        public bool IsActive
        {
            get => isActive;
            set
            {
                isActive = value;
                OnPropertyChanged();
            }
        }

        public System.Version CurrentVersion
        {
            get => Updater.CurrentVersion;
        }

        public event EventHandler ExtensionsLoaded;
        public ApplicationMode Mode { get; }
        public IDialogsFactory Dialogs { get; set; }
        public PlayniteSettings AppSettings { get; set; }
        public GamesEditor GamesEditor { get; set; }
        public ExtensionFactory Extensions { get; set; }
        public GameDatabase Database { get; set; }
        public PlayniteAPI Api { get; set; }
        public GameControllerFactory Controllers { get; set; }
        public CmdLineOptions CmdLine { get; set; }
        public DpiScale DpiScale { get; set; } = new DpiScale(1, 1);
        public ComputerScreen CurrentScreen { get; set; } = Computer.GetPrimaryScreen();
        public DiscordManager Discord { get; set; }
        public SynchronizationContext SyncContext { get; private set; }
        public Action<PlayniteUriEventArgs> AppUriHandler { get; set; }
        public static Application CurrentNative { get; private set; }
        public static PlayniteApplication Current { get; private set; }
        public ServicesClient ServicesClient { get; private set; }
        public static bool SoundsEnabled { get; set; } = true;
        public MainViewModelBase MainModelBase { get; set; }
        public List<ExtensionInstallResult> ExtensionsInstallResult { get; set; }

        private ExtensionsStatusBinder extensionsStatusBinder = new ExtensionsStatusBinder();
        public ExtensionsStatusBinder ExtensionsStatusBinder { get => extensionsStatusBinder; set => SetValue(ref extensionsStatusBinder, value); }

        public PlayniteApplication()
        {
        }

        public PlayniteApplication(
            Func<Application> appInitializer,
            ApplicationMode mode,
            string defaultThemeName,
            CmdLineOptions cmdLine)
        {
            if (Current != null)
            {
                throw new Exception("Only one application instance is allowed.");
            }

            // TODO: remove after switch to .NET 5
            // Fixes various network issues on 2004+ Win10 if TLS 1.3 is forced via registry.
            if (Computer.IsTLS13SystemWideEnabled())
            {
                logger.Warn("System wide TLS 1.3 is enabled, forcing 1.2.");
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            }

            CmdLine = cmdLine;
            Mode = mode;
            Current = this;

            if (!Debugger.IsAttached)
            {
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            }

            if (!CmdLine.MasterInstance)
            {
                if (CheckOtherInstances() || CmdLine.Shutdown)
                {
                    resourcesReleased = true;
                    Environment.Exit(0);
                    return;
                }
            }

#if !DEBUG
            if (FileSystem.FileExists(PlaynitePaths.SafeStartupFlagFile))
            {
                if (MessageBox.Show(
                    "Playnite closed unexpectedly while starting. This is usually caused by 3rd party theme or extension. Do you want to start in safe mode with all 3rd party add-ons disabled?",
                    "Startup Error",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    cmdLine.SafeStartup = true;
                }
            }
            else
            {
                FileSystem.CreateFile(PlaynitePaths.SafeStartupFlagFile);
            }
#endif

            // All code above has to be called before we create instance of WPF app,
            // because MessageBox forces WPF to initialize and fire startup app events.
            CurrentNative = appInitializer();
            CurrentNative.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SyncContext = new DispatcherSynchronizationContext(CurrentNative.Dispatcher);
            SynchronizationContext.SetSynchronizationContext(SyncContext);
            appMutex = new Mutex(true, instanceMuxet);

            try
            {
                // This can fail in rare cases when switching application modes
                // if an old instance fails to clean after itself or if it gets stuck on exit.
                Policy.Handle<Exception>()
                        .WaitAndRetry(3, a => TimeSpan.FromSeconds(3))
                        .Execute(() => pipeService = new PipeService());
                pipeService.CommandExecuted += PipeService_CommandExecuted;
                pipeServer = new PipeServer(PlayniteSettings.GetAppConfigValue("PipeEndpoint"));
                pipeServer.StartServer(pipeService);
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(exc, "Failed to start pipe service.");
            }

            PlayniteSettings.MigrateSettingsConfig();
            AppSettings = PlayniteSettings.LoadSettings();
            Commands.GlobalCommands.AppSettings = AppSettings;
            NLogLogger.IsTraceEnabled = AppSettings.TraceLogEnabled;
            if (CmdLine.ResetSettings)
            {
                var settings = PlayniteSettings.GetDefaultSettings();
                settings.FirstTimeWizardComplete = true;
                settings.DatabasePath = AppSettings.DatabasePath;
                settings.SaveSettings();
                AppSettings = settings;
            }

            var relaunchPath = string.Empty;
            if (AppSettings.StartInFullscreen && mode == ApplicationMode.Desktop && !CmdLine.StartInDesktop)
            {
                relaunchPath = PlaynitePaths.FullscreenExecutablePath;
            }

            if (CmdLine.StartInDesktop && mode != ApplicationMode.Desktop)
            {
                relaunchPath = PlaynitePaths.DesktopExecutablePath;
            }
            else if (CmdLine.StartInFullscreen && mode != ApplicationMode.Fullscreen)
            {
                relaunchPath = PlaynitePaths.FullscreenExecutablePath;
            }

            if (!relaunchPath.IsNullOrEmpty())
            {
                FileSystem.DeleteFile(PlaynitePaths.SafeStartupFlagFile);
                ProcessStarter.StartProcess(relaunchPath, CmdLine.ToString());
                CurrentNative.Shutdown(0);
                return;
            }

            ServicesClient = new ServicesClient();
            CurrentNative.SessionEnding += Application_SessionEnding;
            CurrentNative.Exit += Application_Exit;
            CurrentNative.Startup += Application_Startup;
            CurrentNative.Activated += Application_Activated;
            CurrentNative.Deactivated += Application_Deactivated;

            OnPropertyChanged(nameof(AppSettings));
            var defaultTheme = new ThemeManifest()
            {
                DirectoryName = defaultThemeName,
                DirectoryPath = Path.Combine(PlaynitePaths.ThemesProgramPath, ThemeManager.GetThemeRootDir(Mode), defaultThemeName),
                Name = defaultThemeName,
                Id = mode == ApplicationMode.Desktop ? ThemeManager.DefaultDesktopThemeId : ThemeManager.DefaultFullscreenThemeId
            };

            try
            {
                if (Process.GetProcesses().Where(a => a.ProcessName.StartsWith("Playnite.")).Count() > 1)
                {
                    logger.Warn("Multiple Playnite processes detected before installing addons.");
                    for (int i = 0; i < 10; i++)
                    {
                        Thread.Sleep(500);
                        if (Process.GetProcesses().Where(a => a.ProcessName.StartsWith("Playnite.")).Count() == 1)
                        {
                            break;
                        }
                        else if (i == 9)
                        {
                            logger.Warn("Another Playnite instance didn't shutdown in time before addon installation.");
                        }
                    }
                }

                ExtensionsInstallResult = ExtensionInstaller.InstallExtensionQueue();
                var installedTheme = ExtensionsInstallResult.FirstOrDefault(a => a.InstalledManifest is ThemeManifest && !a.Updated);
                if (installedTheme?.InstalledManifest != null)
                {
                    var theme = installedTheme.InstalledManifest as ThemeManifest;
                    if (theme.Mode == Mode)
                    {
                        if (theme.Mode == ApplicationMode.Desktop)
                        {
                            AppSettings.Theme = theme.Id;
                        }
                        else
                        {
                            AppSettings.Fullscreen.Theme = theme.Id;
                        }
                    }
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to finish installing extenions.");
            }

            ThemeManager.SetDefaultTheme(defaultTheme);

            // Theme must be set BEFORE default app resources are initialized for ThemeFile markup to apply custom theme's paths.
            customTheme = null;
            if (CmdLine.ForceDefaultTheme || CmdLine.SafeStartup)
            {
                logger.Warn("Default theme forced by cmdline.");
            }
            else
            {
                var theme = mode == ApplicationMode.Desktop ? AppSettings.Theme : AppSettings.Fullscreen.Theme;
                if (theme != ThemeManager.DefaultTheme.Id)
                {
                    customTheme = ThemeManager.GetAvailableThemes(mode).Where(a => a.Id == theme).OrderByDescending(a => a.Version).FirstOrDefault();
                    if (customTheme == null)
                    {
                        logger.Error($"Failed to apply theme {theme}, theme not found.");
                        if (mode == ApplicationMode.Desktop)
                        {
                            AppSettings.Theme = ThemeManager.DefaultDesktopThemeId;
                        }
                        else
                        {
                            AppSettings.Fullscreen.Theme = ThemeManager.DefaultFullscreenThemeId;
                        }

                        ThemeManager.SetCurrentTheme(defaultTheme);
                    }
                    else
                    {
                        ThemeManager.SetCurrentTheme(customTheme);
                    }
                }
            }

            InitializeNative();

            // Must be applied AFTER default app resources are initialized, otherwise custom resource dictionaries won't be properly added to application scope.
            if (customTheme != null)
            {
                themeLoadError = ThemeManager.ApplyTheme(CurrentNative, customTheme, Mode);
                if (themeLoadError != AddonLoadError.None)
                {
                    ThemeManager.SetCurrentTheme(null);
                    logger.Error($"Failed to load theme {customTheme.Name}, {themeLoadError}.");
                }
            }

            try
            {
                Localization.SetLanguage(AppSettings.Language);
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(exc, $"Failed to set {AppSettings.Language} langauge.");
            }

            if (mode == ApplicationMode.Desktop)
            {
                try
                {
                    if (System.Drawing.FontFamily.Families.Any(a => a.Name == AppSettings.FontFamilyName))
                    {
                        CurrentNative.Resources.Add(
                            "FontFamily", new FontFamily(AppSettings.FontFamilyName));
                    }
                    else
                    {
                        logger.Error($"Cannot set font {AppSettings.FontFamilyName}, font not found.");
                    }

                    if (System.Drawing.FontFamily.Families.Any(a => a.Name == AppSettings.MonospaceFontFamilyName))
                    {
                        CurrentNative.Resources.Add(
                            "MonospaceFontFamily", new FontFamily(AppSettings.MonospaceFontFamilyName));
                    }
                    else
                    {
                        logger.Error($"Cannot set monospace font {AppSettings.MonospaceFontFamilyName}, font not found.");
                    }

                    if (AppSettings.FontSize > 0)
                    {
                        CurrentNative.Resources.Add(
                            "FontSize", AppSettings.FontSize);
                    }

                    if (AppSettings.FontSizeSmall > 0)
                    {
                        CurrentNative.Resources.Add(
                            "FontSizeSmall", AppSettings.FontSizeSmall);
                    }

                    if (AppSettings.FontSizeLarge > 0)
                    {
                        CurrentNative.Resources.Add(
                            "FontSizeLarge", AppSettings.FontSizeLarge);
                    }

                    if (AppSettings.FontSizeLarger > 0)
                    {
                        CurrentNative.Resources.Add(
                            "FontSizeLarger", AppSettings.FontSizeLarger);
                    }

                    if (AppSettings.FontSizeLargest > 0)
                    {
                        CurrentNative.Resources.Add(
                            "FontSizeLargest", AppSettings.FontSizeLargest);
                    }
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(e, $"Failed to set font {AppSettings.FontFamilyName}");
                }
            }
            else
            {
                if (AppSettings.Fullscreen.FontSize > 0)
                {
                    CurrentNative.Resources.Add(
                        "FontSize", AppSettings.Fullscreen.FontSize);
                }

                if (AppSettings.Fullscreen.FontSizeSmall > 0)
                {
                    CurrentNative.Resources.Add(
                        "FontSizeSmall", AppSettings.Fullscreen.FontSizeSmall);
                }
            }

            // Only use this for Desktop mode. Non-default options look terrible in Fullscreen because of viewport scaling.
            if (mode == ApplicationMode.Desktop)
            {
                Controls.WindowBase.SetTextRenderingOptions(AppSettings.TextFormattingMode, AppSettings.TextRenderingMode);
            }
        }

        public abstract void InstantiateApp();

        public abstract void InitializeNative();

        public abstract void Restore();

        public abstract void Minimize();

        public abstract void ShowWindowsNotification(string title, string body, Action action);

        public abstract void SwitchAppMode(ApplicationMode mode);

        private void Application_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            logger.Info("Shutting down application because of session ending.");
            // Don't dispose CefSharp here because of bug in CefSharp during system shutdown
            // https://github.com/JosefNemec/Playnite/issues/866
            ReleaseResources(false);
            CurrentNative.Shutdown(0);
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            ReleaseResources();
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = (Exception)e.ExceptionObject;
            var crashInfo = Exceptions.GetExceptionInfo(exception, Extensions);
            logger.Error(exception, "Unhandled exception occured.");
            CrashHandlerViewModel crashModel = null;

            // Delete safe startup flag if we are able to handle the crash,
            // safe startup option should show for crashes we are not handling.
            FileSystem.DeleteFile(PlaynitePaths.SafeStartupFlagFile);
            if (crashInfo.IsExtensionCrash)
            {
                crashModel = new CrashHandlerViewModel(
                    new ExtensionCrashHandlerWindowFactory(),
                    Dialogs,
                    new ResourceProvider(),
                    Mode,
                    crashInfo,
                    AppSettings);
            }
            else
            {
                crashModel = new CrashHandlerViewModel(
                    new CrashHandlerWindowFactory(),
                    Dialogs,
                    new ResourceProvider(),
                    Mode);
            }

            crashModel.OpenView();
            Process.GetCurrentProcess().Kill();
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            logger.Info($"Application started from '{PlaynitePaths.ProgramPath}'");
            SDK.Data.Markup.Init(new MarkupConverter());
            SDK.Data.Serialization.Init(new DataSerializer());
            SDK.Data.SQLite.Init((a,b) => new Sqlite(a, b));
            EventManager.RegisterClassHandler(typeof(Controls.WindowBase), Controls.WindowBase.ClosedRoutedEvent, new RoutedEventHandler(WindowBaseCloseHandler));
            EventManager.RegisterClassHandler(typeof(Controls.WindowBase), Controls.WindowBase.LoadedRoutedEvent, new RoutedEventHandler(WindowBaseLoadedHandler));
            if (!Startup())
            {
                return;
            }

            logger.Info($"Application {CurrentVersion} started");

            ExtensionsInstallResult?.Where(a => a.InstallError != null).ForEach(ext =>
                Api.Notifications.Add(new NotificationMessage(
                    "inst_err" + ext.PackagePath,
                    ResourceProvider.GetString(LOC.AddonInstallFaild).Format(Path.GetFileNameWithoutExtension(ext.PackagePath)) +
                        "\n" + ext.InstallError.Message,
                    NotificationType.Error)));

            foreach (var fail in Extensions.FailedExtensions)
            {
                Api.Notifications.Add(new NotificationMessage(
                    fail.manifest.DirectoryPath,
                    fail.error == AddonLoadError.SDKVersion ?
                        ResourceProvider.GetString(LOC.SpecificExtensionLoadSDKError).Format(fail.manifest.Name) :
                        ResourceProvider.GetString(LOC.SpecificExtensionLoadError).Format(fail.manifest.Name),
                    NotificationType.Error));
            }

            if (themeLoadError != AddonLoadError.None && customTheme != null)
            {
                Api.Notifications.Add(new NotificationMessage(
                    customTheme.DirectoryPath,
                    themeLoadError == AddonLoadError.SDKVersion ?
                        ResourceProvider.GetString(LOC.SpecificThemeLoadSDKError).Format(customTheme.Name) :
                        ResourceProvider.GetString(LOC.SpecificThemeLoadError).Format(customTheme.Name),
                    NotificationType.Error));
            }

            try
            {
                if (AppSettings.ShowNahimicServiceWarning)
                {
                    if (ServiceController.GetServices().FirstOrDefault(a =>
                        (a.ServiceName?.Contains("nahimic", StringComparison.OrdinalIgnoreCase) == true ||
                         a.DisplayName?.Contains("nahimic", StringComparison.OrdinalIgnoreCase) == true) &&
                        a.Status != ServiceControllerStatus.Stopped) != null)
                    {
                        var okResponse = new MessageBoxOption(LOC.OKLabel, true, true);
                        var dontShowResponse = new MessageBoxOption(LOC.DontShowAgainTitle);
                        var res = Dialogs.ShowMessage(
                            LOC.NahimicServiceWarning, "",
                            MessageBoxImage.Warning,
                            new List<MessageBoxOption> { okResponse, dontShowResponse });
                        if (res == dontShowResponse)
                        {
                            AppSettings.ShowNahimicServiceWarning = false;
                        }
                    }
                }
            }
            catch (Exception nahExc)
            {
                // ServiceController.GetServices() can apparently blow up on Win32Exception sometimes
                logger.Error(nahExc, "Failed to check for Nahimic service.");
            }

            if (PlayniteEnvironment.IsElevated && AppSettings.ShowElevatedRightsWarning)
            {
                var okResponse = new MessageBoxOption(LOC.OKLabel, true, true);
                var dontShowResponse = new MessageBoxOption(LOC.DontShowAgainTitle);
                var res = Dialogs.ShowMessage(
                    LOC.ElevatedProcessWarning, "",
                    MessageBoxImage.Warning,
                    new List<MessageBoxOption> { okResponse, dontShowResponse });
                if (res == dontShowResponse)
                {
                    AppSettings.ShowElevatedRightsWarning = false;
                }
            }
        }

        private void WindowBaseCloseHandler(object sender, RoutedEventArgs e)
        {
            WindowManager.NotifyChildOwnershipChanges();
        }

        private void WindowBaseLoadedHandler(object sender, RoutedEventArgs e)
        {
            WindowManager.NotifyChildOwnershipChanges();
        }

        private void PipeService_CommandExecuted(object sender, CommandExecutedEventArgs args)
        {
            logger.Info($"Executing command \"{args.Command}\" from pipe with arguments \"{args.Args}\"");

            switch (args.Command)
            {
                case CmdlineCommand.Focus:
                    Restore();
                    break;

                case CmdlineCommand.Start:
                    if (Guid.TryParse(args.Args, out var gameId))
                    {
                        var game = Database.Games[gameId];
                        if (game == null)
                        {
                            logger.Error($"Cannot start game, game {args.Args} not found.");
                        }
                        else
                        {
                            GamesEditor.PlayGame(game);
                        }
                    }
                    else
                    {
                        logger.Error($"Can't start game, failed to parse game id: {args.Args}");
                    }

                    break;

                case CmdlineCommand.UriRequest:
                    (Api.UriHandler as PlayniteUriHandler).ProcessUri(args.Args);
                    break;

                case CmdlineCommand.ExtensionInstall:
                    if (installingAddon)
                    {
                        return;
                    }

                    var extPath = args.Args;
                    if (!File.Exists(extPath))
                    {
                        logger.Error($"Cannot install extension, file doesn't exists: {extPath}");
                        return;
                    }

                    installingAddon = true;
                    var ext = Path.GetExtension(extPath).ToLower();
                    if (ext.Equals(PlaynitePaths.PackedThemeFileExtention, StringComparison.OrdinalIgnoreCase))
                    {
                        InstallThemeFile(extPath);
                    }
                    else if (ext.Equals(PlaynitePaths.PackedExtensionFileExtention, StringComparison.OrdinalIgnoreCase))
                    {
                        InstallExtensionFile(extPath);
                    }

                    installingAddon = false;
                    break;

                case CmdlineCommand.SwitchMode:
                    if (args.Args == "desktop")
                    {
                        SyncContext.Post(_ => SwitchAppMode(ApplicationMode.Desktop), null);
                    }
                    else if (args.Args == "fullscreen")
                    {
                        SyncContext.Post(_ => SwitchAppMode(ApplicationMode.Fullscreen), null);
                    }
                    else
                    {
                        logger.Error($"Can't switch to uknwon application mode: {args.Args}");
                    }
                    break;

                case CmdlineCommand.Shutdown:
                    Quit();
                    break;

                default:
                    logger.Warn("Unknown command received");
                    break;
            }
        }

        private void Application_Activated(object sender, EventArgs e)
        {
            IsActive = true;
        }

        private void Application_Deactivated(object sender, EventArgs e)
        {
            IsActive = false;
        }

        public void Run()
        {
            CurrentNative.Run();
        }

        public abstract bool Startup();

        public bool CheckOtherInstances()
        {
            if (Mutex.TryOpenExisting(instanceMuxet, out var mutex))
            {
                try
                {
                    Policy.Handle<Exception>()
                        .WaitAndRetry(3, a => TimeSpan.FromSeconds(3))
                        .Execute(() =>
                        {
                            var client = new PipeClient(PlayniteSettings.GetAppConfigValue("PipeEndpoint"));
                            if (!CmdLine.Start.IsNullOrEmpty())
                            {
                                client.InvokeCommand(CmdlineCommand.Start, CmdLine.Start);
                            }
                            else if (!CmdLine.UriData.IsNullOrEmpty())
                            {
                                client.InvokeCommand(CmdlineCommand.UriRequest, CmdLine.UriData);
                            }
                            else if (!CmdLine.InstallExtension.IsNullOrEmpty())
                            {
                                client.InvokeCommand(CmdlineCommand.ExtensionInstall, CmdLine.InstallExtension);
                            }
                            else if (CmdLine.StartInDesktop)
                            {
                                client.InvokeCommand(CmdlineCommand.SwitchMode, "desktop");
                            }
                            else if (CmdLine.StartInFullscreen)
                            {
                                client.InvokeCommand(CmdlineCommand.SwitchMode, "fullscreen");
                            }
                            else if (CmdLine.Shutdown)
                            {
                                client.InvokeCommand(CmdlineCommand.Shutdown, null);
                            }
                            else
                            {
                                client.InvokeCommand(CmdlineCommand.Focus, string.Empty);
                            }
                        });
                }
                catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    MessageBox.Show(
                        "Playnite failed to start. Please close all other instances and try again.",
                        "Startup Error");
                    logger.Error(exc, "Can't process communication with other instances.");
                }

                logger.Info("Application already running, shutting down.");
                return true;
            }
            else
            {
                var curProcess = Process.GetCurrentProcess();
                var processes = Process.GetProcesses().Where(a => a.ProcessName.StartsWith("Playnite.")).ToList();
                // In case multiple processes end up in this branch,
                // the process with highest process id gets to live.
                if (processes.Count > 1 && processes.Max(a => a.Id) != curProcess.Id)
                {
                    logger.Info("Another process instance(s) is already running, shutting down.");
                    return true;
                }
            }

            return false;
        }

        public void ConfigureApplication()
        {
            HtmlRendererSettings.ImageCachePath = PlaynitePaths.ImagesCachePath;
            if (AppSettings.DisableHwAcceleration || CmdLine.ForceSoftwareRender)
            {
                logger.Info("Enabling software rendering.");
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            }

            if (CmdLine.ClearWebCache)
            {
                try
                {
                    FileSystem.DeleteDirectory(PlaynitePaths.BrowserCachePath);
                }
                catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(exc, "Failed to clear CEF cache.");
                }
            }

            try
            {
                CefTools.ConfigureCef(AppSettings.TraceLogEnabled);
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(exc, "Failed to initialize CefSharp.");
                Dialogs.ShowErrorMessage(
                    ResourceProvider.GetString("LOCCefSharpInitError"),
                    ResourceProvider.GetString("LOCStartupError"));
                Quit();
                return;
            }

            try
            {
                ExtensionFactory.CreatePluginFolders();
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(exc, "Failed to script and plugin directories.");
            }

            try
            {
                SystemIntegration.SetBootupStateRegistration(AppSettings.StartOnBoot);
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(exc, "Failed to register Playnite to start on boot.");
            }

            try
            {
                SystemIntegration.RegisterPlayniteUriProtocol();
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(exc, "Failed to register playnite URI scheme.");
            }

            try
            {
                SystemIntegration.RegisterFileExtensions();
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(exc, "Failed to register playnite extensions.");
            }
        }

        public void ProcessArguments()
        {
            (Api.UriHandler as PlayniteUriHandler).Handlers.Add("playnite", ProcessUriRequest);
            if (!CmdLine.Start.IsNullOrEmpty())
            {
                PipeService_CommandExecuted(this, new CommandExecutedEventArgs(CmdlineCommand.Start, CmdLine.Start));
            }
            else if (!CmdLine.UriData.IsNullOrEmpty())
            {
                PipeService_CommandExecuted(this, new CommandExecutedEventArgs(CmdlineCommand.UriRequest, CmdLine.UriData));
            }
            else if (!CmdLine.InstallExtension.IsNullOrEmpty())
            {
                PipeService_CommandExecuted(this, new CommandExecutedEventArgs(CmdlineCommand.ExtensionInstall, CmdLine.InstallExtension));
            }
            else if (CmdLine.StartInDesktop)
            {
                PipeService_CommandExecuted(this, new CommandExecutedEventArgs(CmdlineCommand.SwitchMode, "desktop"));
            }
            else if (CmdLine.StartInFullscreen)
            {
                PipeService_CommandExecuted(this, new CommandExecutedEventArgs(CmdlineCommand.SwitchMode, "fullscreen"));
            }
            else if (CmdLine.Shutdown)
            {
                PipeService_CommandExecuted(this, new CommandExecutedEventArgs(CmdlineCommand.Shutdown, null));
            }
        }

        internal void ProcessUriRequest(PlayniteUriEventArgs args)
        {
            var arguments = args.Arguments;
            if (args.Arguments.Count() == 0)
            {
                return;
            }

            var command = arguments[0];
            switch (command)
            {
                case UriCommands.CreateDiag:
                    CrashHandlerViewModel.CreateDiagPackage(Dialogs);
                    break;

                case UriCommands.StartGame:
                    if (arguments.Count() != 2)
                    {
                        return;
                    }

                    if (Guid.TryParse(arguments[1], out var gameId))
                    {
                        var game = Database.Games[gameId];
                        if (game == null)
                        {
                            logger.Error($"Cannot start game, game {arguments[1]} not found.");
                        }
                        else
                        {
                            GamesEditor.PlayGame(game);
                        }
                    }
                    else
                    {
                        logger.Error($"Can't start game, failed to parse game id: {arguments[1]}");
                    }

                    break;

                case UriCommands.InstallAddon:
                    if (arguments.Count() != 2)
                    {
                        return;
                    }

                    InstallOnlineAddon(arguments[1]);
                    break;

                default:
                    AppUriHandler(args);
                    break;
            }
        }

        public void SetupInputs(bool enableXinput)
        {
            if (enableXinput)
            {
                try
                {
                    if (xdevice == null)
                    {
                        xdevice = new XInputDevice(InputManager.Current, this)
                        {
                            SimulateAllKeys = false,
                            SimulateNavigationKeys = true
                        };
                    }
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(e, "Failed intitialize XInput");
                    Dialogs.ShowErrorMessage(ResourceProvider.GetString("LOCXInputInitErrorMessage"), "");
                }
            }
            else
            {
                if (xdevice != null)
                {
                    xdevice.Dispose();
                    xdevice = null;
                }
            }
        }

        public void Quit()
        {
            logger.Info("Shutting down Playnite");
            ReleaseResources();
            CurrentNative.Shutdown(0);
        }

        public void QuitAndStart(string path, string arguments, bool asAdmin = false)
        {
            logger.Info("Shutting down Playnite and starting an app.");
            ReleaseResources();
            try
            {
                ProcessStarter.StartProcess(path, arguments, asAdmin);
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                // Not sure how this can happen, but there are some "operation cancelled by user" crashes here.
                // People probably running Playnite as admin and cancelling UAC for new process, or something...
                logger.Error(e, "Failed to start process on app shutdown.");
            }

            CurrentNative.Shutdown(0);
        }

        public abstract void Restart();

        public abstract void Restart(CmdLineOptions options);

        public virtual void ReleaseResources(bool releaseCefSharp = true)
        {
            if (resourcesReleased)
            {
                return;
            }

            logger.Debug("Releasing Playnite resources...");
            CurrentNative.Dispatcher.Invoke(() =>
            {
                try
                {
                    appMutex?.ReleaseMutex();
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    // Only happens when trying to release mutext created by a different process.
                    // This shouldn't normally happen since the mutex is released here before starting another instance.
                    logger.Error(e, "Failed to release app mutext.");
                }
            });

            try
            {
                pipeServer?.StopServer();
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                // I have no idea why this fails for some people.
                logger.Error(e, "Failed to stop pipe server.");
            }

            Discord?.Dispose();
            updateCheckTimer?.Dispose();
            Extensions?.NotifiyOnApplicationStopped();
            var progressModel = new ProgressViewViewModel(new ProgressWindowFactory(), (_) =>
            {
                try
                {
                    if (GlobalTaskHandler.CancelAndWait(Common.Timer.SecondsToMilliseconds(5)) == false)
                    {
                        logger.Warn("Global task cancelation failed in time.");
                    }

                    GamesEditor?.Dispose();
                    AppSettings?.SaveSettings();
                    Controllers?.Dispose();
                    Extensions?.Dispose();
                }
                catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(exc, "Failed to dispose Playnite objects.");
                }
            }, new GlobalProgressOptions("LOCClosingPlaynite"));

            progressModel.ActivateProgress();

            // This must run on main thread
            if (releaseCefSharp)
            {
                CurrentNative.Dispatcher.Invoke(() =>
                {
                    if (CefTools.IsInitialized)
                    {
                        CefTools.Shutdown();
                    }
                });
            }

            Database?.Dispose();
            resourcesReleased = true;
        }

        private void UpdateCheckerCallback(object state)
        {
            try
            {
                var updater = new Updater(this);
                if (updater.IsUpdateAvailable)
                {
                    var updateTitle = ResourceProvider.GetString("LOCUpdaterWindowTitle");
                    var updateBody = ResourceProvider.GetString("LOCUpdateIsAvailableNotificationBody");
                    if (!Current.IsActive)
                    {
                        ShowWindowsNotification(updateTitle, updateBody, () =>
                        {
                            Restore();
                            new UpdateViewModel(
                                updater,
                                new UpdateWindowFactory(),
                                new ResourceProvider(),
                                Dialogs,
                                Mode).OpenView();
                        });
                    }

                    MainModelBase.UpdatesAvailable = true;
                }
            }
            catch (Exception exc)
            {
                logger.Warn(exc, "Failed to process update.");
            }

            try
            {
                var manifests = ExtensionFactory.GetInstalledManifests();
                var blackList = ServicesClient.GetAddonBlacklist();
                var installedList = manifests.Where(a => blackList.Contains(a.Id)).ToList();
                if (installedList.HasItems())
                {
                    Dialogs.ShowMessage(ResourceProvider.GetString(LOC.WarningBlacklistedExtensions).Format(
                        string.Join(Environment.NewLine, installedList.Select(a => a.Name))),
                        "", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception exc)
            {
                logger.Warn(exc, "Failed to process addon blacklist check.");
            }

            try
            {
                var updates = Addons.CheckAddonUpdates(ServicesClient);
                if (updates.HasItems())
                {
                    Api.Notifications.Add(MainModelBase.GetAddonUpdatesFoundMessage(updates));
                }
            }
            catch (Exception exc)
            {
                logger.Warn(exc, "Failed to process addon update check.");
            }
        }

        public async Task StartUpdateCheckerAsync()
        {
            if (PlayniteEnvironment.InOfflineMode)
            {
                return;
            }

            await Task.Delay(Common.Timer.SecondsToMilliseconds(5));
            if (GlobalTaskHandler.IsActive)
            {
                await GlobalTaskHandler.ProgressTask;
            }

            updateCheckTimer = new System.Threading.Timer(
                UpdateCheckerCallback,
                null,
                0,
                Common.Timer.HoursToMilliseconds(4));
        }

        public async Task SendUsageDataAsync()
        {
            if (PlayniteEnvironment.InOfflineMode)
            {
                return;
            }

            await Task.Run(() =>
            {
                try
                {
                    ServicesClient.PostUserUsage(AppSettings.InstallInstanceId);
                }
                catch (Exception exc)
                {
                    logger.Warn(exc, "Failed to post user usage data.");
                }
            });
        }

        public bool MigrateDatabase()
        {
            if (GameDatabase.GetMigrationRequired(AppSettings.DatabasePath))
            {
                var migrationProgress = new ProgressViewViewModel(new ProgressWindowFactory(), (_) =>
                {
                    GameDatabase.MigrateNewDatabaseFormat(GameDatabase.GetFullDbPath(AppSettings.DatabasePath));
                }, new GlobalProgressOptions("LOCDBUpgradeProgress"));

                if (migrationProgress.ActivateProgress().Result != true)
                {
                    logger.Error(migrationProgress.FailException, "Failed to migrate database to new version.");
                    var message = ResourceProvider.GetString("LOCDBUpgradeFail");
                    if (migrationProgress.FailException is NoDiskSpaceException exc)
                    {
                        message = string.Format(ResourceProvider.GetString("LOCDBUpgradeEmptySpaceFail"), Units.BytesToMegaBytes(exc.RequiredSpace));
                    }

                    Dialogs.ShowErrorMessage(message, "");
                    return false;
                }
            }

            return true;
        }

        public void UpdateScreenInformation(Controls.WindowBase window)
        {
            try
            {
                DpiScale = VisualTreeHelper.GetDpi(window);
                CurrentScreen = window.GetScreen();
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                DpiScale = new DpiScale(1, 1);
                CurrentScreen = Computer.GetPrimaryScreen();
                logger.Error(e, $"Failed to get window information for main {Mode} window.");
            }
        }

        public void ShowAddonPerfNotice()
        {
            if (AppSettings.AddonsPerfNoticeShown)
            {
                return;
            }

            Dialogs.ShowMessage(LOC.AddonPerfNotice, "", MessageBoxButton.OK, MessageBoxImage.Warning);
            AppSettings.AddonsPerfNoticeShown = true;
            AppSettings.SaveSettings();
        }

        public void InstallOnlineAddon(string addonId)
        {
            try
            {
                var addon = ServicesClient.GetAddon(addonId);
                var package = addon.InstallerManifest.GetLatestCompatiblePackage();
                if (package == null)
                {
                    Dialogs.ShowErrorMessage(LOC.AddonErrorNotCompatible, "");
                    return;
                }

                var message = string.Format(
                    ResourceProvider.GetString(addon.IsTheme ? LOC.ThemeInstallPrompt : LOC.ExtensionInstallPrompt),
                    addon.Name, addon.Author, package.Version);
                BaseExtensionManifest existing = null;
                if (addon.IsTheme)
                {
                    existing = ThemeManager.GetAvailableThemes().FirstOrDefault(a => a.Id == addon.AddonId);
                }
                else
                {
                    existing = ExtensionFactory.GetInstalledManifests().FirstOrDefault(a => a.Id == addon.AddonId);
                }

                if (existing != null)
                {
                    message = string.Format(
                    ResourceProvider.GetString(addon.IsTheme ? LOC.ThemeUpdatePrompt : LOC.ExtensionUpdatePrompt),
                    addon.Name, existing.Version, package.Version);
                }

                if (Dialogs.ShowMessage(message, LOC.GeneralExtensionInstallTitle, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                {
                    return;
                }

                var licenseRes = addon.CheckAddonLicense();
                if (licenseRes == null)
                {
                    Dialogs.ShowErrorMessage(LOC.AddonErrorDownloadFailed, string.Empty);
                    return;
                }

                if (licenseRes == false)
                {
                    return;
                }

                ShowAddonPerfNotice();
                var locaPath = addon.GetTargetDownloadPath();
                FileSystem.DeleteFile(locaPath);
                var res = Dialogs.ActivateGlobalProgress((_) =>
                {
                    if (package.PackageUrl.IsHttpUrl())
                    {
                        FileSystem.PrepareSaveFile(locaPath);
                        HttpDownloader.DownloadFile(package.PackageUrl, locaPath);
                    }
                    else
                    {
                        File.Copy(package.PackageUrl, locaPath);
                    }
                },
                new GlobalProgressOptions(LOC.DownloadingLabel, false));
                if (res.Error != null)
                {
                    logger.Error(res.Error, $"Failed to download addon {package.PackageUrl}");
                    Dialogs.ShowErrorMessage(LOC.AddonErrorDownloadFailed, string.Empty);
                    return;
                }

                ExtensionInstaller.QueuePackageInstall(locaPath);
                if (Dialogs.ShowMessage(LOC.ExtInstallationRestartNotif, LOC.SettingsRestartTitle,
                    MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    Restart(new CmdLineOptions { SkipLibUpdate = true });
                };
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, $"Failed to install addon from uri {addonId}");
            }
        }

        public void InstallThemeFile(string themeFile)
        {
            try
            {
                ExtensionInstaller.VerifyThemePackage(themeFile);
                var desc = ExtensionInstaller.GetPackedThemeManifest(themeFile);
                desc.VerifyManifest();

                if (new Version(desc.ThemeApiVersion).Major != ThemeManager.GetApiVersion(desc.Mode).Major)
                {
                    throw new Exception(ResourceProvider.GetString("LOCGeneralExtensionInstallApiVersionFails"));
                }

                var message = string.Format(ResourceProvider.GetString("LOCThemeInstallPrompt"),
                    desc.Name, desc.Author, desc.Version);
                var existing = ThemeManager.GetAvailableThemes(desc.Mode).FirstOrDefault(a => a.Id == desc.Id);
                if (existing != null)
                {
                    message = string.Format(ResourceProvider.GetString("LOCThemeUpdatePrompt"),
                        desc.Name, existing.Version, desc.Version);
                }

                if (Dialogs.ShowMessage(
                        message,
                        ResourceProvider.GetString("LOCGeneralExtensionInstallTitle"),
                        MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    ShowAddonPerfNotice();
                    ExtensionInstaller.QueuePackageInstall(themeFile);
                    if (Dialogs.ShowMessage(
                        ResourceProvider.GetString("LOCExtInstallationRestartNotif"),
                        ResourceProvider.GetString("LOCSettingsRestartTitle"),
                        MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    {
                        Restart(new CmdLineOptions()
                        {
                            SkipLibUpdate = true,
                        });
                    };
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to install theme.");
                Dialogs.ShowErrorMessage(
                    string.Format(ResourceProvider.GetString("LOCThemeInstallFail"), e.Message), "");
            }
        }

        public void InstallExtensionFile(string extensionFile)
        {
            try
            {
                ExtensionInstaller.VerifyExtensionPackage(extensionFile);
                var desc = ExtensionInstaller.GetPackedExtensionManifest(extensionFile);
                desc.VerifyManifest();

                var message = string.Format(ResourceProvider.GetString("LOCExtensionInstallPrompt"),
                    desc.Name, desc.Author, desc.Version);
                var existing = ExtensionFactory.GetInstalledManifests().FirstOrDefault(a => a.Id == desc.Id);
                if (existing != null)
                {
                    message = string.Format(ResourceProvider.GetString("LOCExtensionUpdatePrompt"),
                        desc.Name, existing.Version, desc.Version);
                }

                if (Dialogs.ShowMessage(
                        message,
                        ResourceProvider.GetString("LOCGeneralExtensionInstallTitle"),
                        MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    ShowAddonPerfNotice();
                    ExtensionInstaller.QueuePackageInstall(extensionFile);
                    if (Dialogs.ShowMessage(
                        ResourceProvider.GetString("LOCExtInstallationRestartNotif"),
                        ResourceProvider.GetString("LOCSettingsRestartTitle"),
                        MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    {
                        Restart(new CmdLineOptions()
                        {
                            SkipLibUpdate = true,
                        });
                    };
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to install extension.");
                Dialogs.ShowErrorMessage(
                    string.Format(ResourceProvider.GetString("LOCExtensionInstallFail"), e.Message), "");
            }
        }

        public void OnExtensionsLoaded()
        {
            ExtensionsLoaded?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(this.ExtensionsStatusBinder));
        }
    }
}