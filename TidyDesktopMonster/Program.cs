﻿using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TidyDesktopMonster.AppHelper;
using TidyDesktopMonster.Interface;
using TidyDesktopMonster.KeyValueStore;
using TidyDesktopMonster.Logging;
using TidyDesktopMonster.Scheduling;
using TidyDesktopMonster.Subject;
using TidyDesktopMonster.WinApi;
using TidyDesktopMonster.WinApi.Shell32;

namespace TidyDesktopMonster
{
    static class Program
    {
        static Assembly _appAssembly = typeof(Program).Assembly;

        static string AppName { get; } = _appAssembly
            .GetCustomAttribute<AssemblyTitleAttribute>()
            .Title;

        static string AppPath { get; } = _appAssembly.Location;

        static string ProgramId { get; } = _appAssembly
            .GetCustomAttribute<GuidAttribute>()
            .Value;

        static string[] ApplicationExtensions { get; } = Environment.GetEnvironmentVariable("PATHEXT")
            ?.Split(';')
            ?? new string[0];

        [STAThread]
        static void Main(string[] args)
        {
            using (var guard = new SingleInstanceGuard(ProgramId, SingleInstanceGuard.Scope.CurrentUser))
            {
                if (guard.IsPrimaryInstance)
                    RunApp(args);
                else
                    User32Messages.BroadcastMessage(Constants.OpenWindowMessage);
            }
        }

        static void RunApp(string[] args)
        {
            var shouldStartService = args.Any(x => "-StartService".Equals(x, StringComparison.InvariantCultureIgnoreCase));

            var settingsStore = new InMemoryKeyValueCache(
                new EnvironmentOverride(AppName, new RegistryKeyValueStore(AppName)));

            var logBuffer = new RotatingBufferSink();
            InitializeLogging(logBuffer, settingsStore);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var retryLogic = new ExponentialBackoffLogic(min: TimeSpan.FromMilliseconds(10), max: TimeSpan.FromHours(1));
            var startupRegistration = new StartupFolderRegistration(
                AppName.ToLowerInvariant(),
                new ShortcutOptions { Arguments = "-StartService", Target = AppPath },
                WindowsScriptHostWrapper.CreateShortcut,
                ShellifyWrapper.ReadShortcut);

            using (var scheduler = new WorkScheduler(retryLogic.CalculateRetryAfter))
            {
                var service = new WatchForFilesToDelete<string>(
                    subjectFactory: () => CreateSubject(settingsStore),
                    delete: Shell32Delete.DeleteFile,
                    scheduler: scheduler);

                RunForm(new MainForm(
                    showSettingsForm: !shouldStartService,
                    appPath: AppPath,
                    logEntries: logBuffer,
                    openWindowMessage: (int)User32Messages.GetMessage(Constants.OpenWindowMessage),
                    settingsStore: settingsStore,
                    startService: service.Run,
                    startupRegistration: startupRegistration));
            }
        }

        static void InitializeLogging(ILogSink sink, IKeyValueStore settingsStore)
        {
            Log.Sink = sink;
            Log.Info("Logging initialized");

            var minimumSeverity = settingsStore.Read<LogLevel?>("MinimumSeverity") ?? LogLevel.Info;
            Log.Info($"Setting minimum severity to {minimumSeverity}");

            Log.Sink = new MinimumSeveritySink(sink, minimumSeverity);
        }

        static IUpdatingSubject<string> CreateSubject(IKeyValueStore settingsStore)
        {
            var filter = settingsStore.Read<ShortcutFilterType?>(Constants.ShortcutFilterSetting);
            switch (filter)
            {
                case ShortcutFilterType.Apps:
                    return new FilteringSubject<string>(
                        CreateDirectoryWatcher(settingsStore),
                        path => PathHasExtension(ShellifyWrapper.ReadShortcut(path).Target, ApplicationExtensions));
                default:
                    return CreateDirectoryWatcher(settingsStore);
            }
        }

        static IUpdatingSubject<string> CreateDirectoryWatcher(IKeyValueStore settingsStore)
        {
            var allUsersDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            var currentUserDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var searchPattern = "*.lnk";

            return settingsStore.Read<bool?>(Constants.TidyAllUsersSetting) == true
                ? (IUpdatingSubject<string>)new CompositeSubject<string>(new[]
                {
                    new FilesInDirectorySubject(allUsersDesktop, searchPattern),
                    new FilesInDirectorySubject(currentUserDesktop, searchPattern),

                })
                : new FilesInDirectorySubject(currentUserDesktop, searchPattern);
        }

        static bool PathHasExtension(string path, string[] extensions)
        {
            return extensions.Contains(Path.GetExtension(path), StringComparer.InvariantCultureIgnoreCase);
        }

        static void RunForm(Form form)
        {
            using (var ctx = new ApplicationContext(form))
            {
                Application.Run(ctx);
            }
        }
    }
}
