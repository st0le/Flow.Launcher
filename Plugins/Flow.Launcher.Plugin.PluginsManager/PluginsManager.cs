using Flow.Launcher.Core.Plugin;
using Flow.Launcher.Infrastructure;
using Flow.Launcher.Infrastructure.Http;
using Flow.Launcher.Infrastructure.Logger;
using Flow.Launcher.Infrastructure.UserSettings;
using Flow.Launcher.Plugin.PluginsManager.Models;
using Flow.Launcher.Plugin.SharedCommands;
using Flow.Launcher.Plugin.SharedModels;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Flow.Launcher.Plugin.PluginsManager
{
    internal class PluginsManager
    {
        private PluginsManifest pluginsManifest;

        private PluginInitContext Context { get; set; }

        private Settings Settings { get; set; }

        private bool shouldHideWindow = true;

        private bool ShouldHideWindow
        {
            set { shouldHideWindow = value; }
            get
            {
                var setValue = shouldHideWindow;
                // Default value for hide main window is true. Revert after get call.
                // This ensures when set by another method to false, it is only used once.
                shouldHideWindow = true;

                return setValue;
            }
        }

        internal readonly string icoPath = "Images\\pluginsmanager.png";

        internal PluginsManager(PluginInitContext context, Settings settings)
        {
            pluginsManifest = new PluginsManifest();
            Context = context;
            Settings = settings;
        }

        private Task _downloadManifestTask = Task.CompletedTask;


        internal Task UpdateManifest()
        {
            if (_downloadManifestTask.Status == TaskStatus.Running)
            {
                return _downloadManifestTask;
            }
            else
            {
                _downloadManifestTask = pluginsManifest.DownloadManifest();
                _downloadManifestTask.ContinueWith(_ =>
                        Context.API.ShowMsg(Context.API.GetTranslation("plugin_pluginsmanager_update_failed_title"),
                            Context.API.GetTranslation("plugin_pluginsmanager_update_failed_subtitle"), icoPath, false),
                    TaskContinuationOptions.OnlyOnFaulted);
                return _downloadManifestTask;
            }
        }

        internal List<Result> GetDefaultHotKeys()
        {
            return new List<Result>()
            {
                new Result()
                {
                    Title = Settings.HotKeyInstall,
                    IcoPath = icoPath,
                    Action = _ =>
                    {
                        Context.API.ChangeQuery($"{Context.CurrentPluginMetadata.ActionKeyword} install ");
                        return false;
                    }
                },
                new Result()
                {
                    Title = Settings.HotkeyUninstall,
                    IcoPath = icoPath,
                    Action = _ =>
                    {
                        Context.API.ChangeQuery("pm uninstall ");
                        return false;
                    }
                },
                new Result()
                {
                    Title = Settings.HotkeyUpdate,
                    IcoPath = icoPath,
                    Action = _ =>
                    {
                        Context.API.ChangeQuery("pm update ");
                        return false;
                    }
                }
            };
        }

        internal async Task InstallOrUpdate(UserPlugin plugin)
        {
            if (PluginExists(plugin.ID))
            {
                if (Context.API.GetAllPlugins()
                    .Any(x => x.Metadata.ID == plugin.ID && x.Metadata.Version.CompareTo(plugin.Version) < 0))
                {
                    if (MessageBox.Show(Context.API.GetTranslation("plugin_pluginsmanager_update_exists"),
                        Context.API.GetTranslation("plugin_pluginsmanager_update_title"),
                        MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                        Context
                            .API
                            .ChangeQuery($"{Context.CurrentPluginMetadata.ActionKeywords.FirstOrDefault()} {Settings.HotkeyUpdate} {plugin.Name}");

                    var mainWindow = Application.Current.MainWindow;
                    mainWindow.Visibility = Visibility.Visible;
                    mainWindow.Focus();

                    shouldHideWindow = false;

                    return;
                }

                Context.API.ShowMsg(Context.API.GetTranslation("plugin_pluginsmanager_update_alreadyexists"));
                return;
            }

            var message = string.Format(Context.API.GetTranslation("plugin_pluginsmanager_install_prompt"),
                plugin.Name, plugin.Author,
                Environment.NewLine, Environment.NewLine);

            if (MessageBox.Show(message, Context.API.GetTranslation("plugin_pluginsmanager_install_title"),
                MessageBoxButton.YesNo) == MessageBoxResult.No)
                return;

            var filePath = Path.Combine(DataLocation.PluginsDirectory, $"{plugin.Name}-{plugin.Version}.zip");

            try
            {
                Context.API.ShowMsg(Context.API.GetTranslation("plugin_pluginsmanager_downloading_plugin"),
                    Context.API.GetTranslation("plugin_pluginsmanager_please_wait"));

                await Http.DownloadAsync(plugin.UrlDownload, filePath).ConfigureAwait(false);

                Context.API.ShowMsg(Context.API.GetTranslation("plugin_pluginsmanager_downloading_plugin"),
                    Context.API.GetTranslation("plugin_pluginsmanager_download_success"));

                Install(plugin, filePath);
            }
            catch (Exception e)
            {
                Context.API.ShowMsg(Context.API.GetTranslation("plugin_pluginsmanager_install_error_title"),
                    string.Format(Context.API.GetTranslation("plugin_pluginsmanager_install_error_subtitle"),
                        plugin.Name));

                Log.Exception("PluginsManager", "An error occured while downloading plugin", e, "InstallOrUpdate");

                return;
            }

            Context.API.RestartApp();
        }

        internal async ValueTask<List<Result>> RequestUpdate(string search, CancellationToken token)
        {
            if (!pluginsManifest.UserPlugins.Any())
            {
                await UpdateManifest();
            }

            token.ThrowIfCancellationRequested();

            var uninstallSearch = search.Replace(Settings.HotkeyUpdate, string.Empty).TrimStart();

            var results =
                (from existingPlugin in Context.API.GetAllPlugins()
                 join pluginFromManifest in pluginsManifest.UserPlugins
                     on existingPlugin.Metadata.ID equals pluginFromManifest.ID
                 where existingPlugin.Metadata.Version.CompareTo(pluginFromManifest.Version) <
                       0 // if current version precedes manifest version
                 select ConstructUpdatablePluginResult(pluginFromManifest, existingPlugin.Metadata))
                .ToList();

            if (!results.Any())
                return new List<Result>
                {
                    new()
                    {
                        Title = Context.API.GetTranslation("plugin_pluginsmanager_update_noresult_title"),
                        SubTitle = Context.API.GetTranslation("plugin_pluginsmanager_update_noresult_subtitle"),
                        IcoPath = icoPath
                    }
                };

            return Search(results, uninstallSearch);
        }

        internal bool PluginExists(string id)
        {
            return Context.API.GetAllPlugins().Any(x => x.Metadata.ID == id);
        }

        private static List<Result> Search(IEnumerable<Result> results, string searchName)
        {
            if (string.IsNullOrEmpty(searchName))
                return results.ToList();

            return results
                .Where(x =>
                {
                    var matchResult = StringMatcher.FuzzySearch(searchName, x.Title);
                    if (matchResult.IsSearchPrecisionScoreMet())
                        x.Score += matchResult.Score;

                    return matchResult.IsSearchPrecisionScoreMet();
                })
                .ToList();
        }
        internal async ValueTask<List<Result>> RequestInstallOrUpdate(string searchName, CancellationToken token)
        {
            if (!pluginsManifest.UserPlugins.Any())
            {
                await UpdateManifest();
            }

            token.ThrowIfCancellationRequested();

            var installedPluginMeta = Context.API.GetAllPlugins().Select(x => (x.Metadata.Version, x.Metadata)).ToList();

            var results =
                pluginsManifest.UserPlugins
                    .Where(plugin => installedPluginMeta.All(pluginMeta => plugin.ID != pluginMeta.Metadata.ID))
                    .Select(ConstructNewPluginResult)
                    .Concat(
                        from meta in installedPluginMeta
                        join plugin in pluginsManifest.UserPlugins on meta.Metadata.ID equals plugin.ID
                        where string.Compare(plugin.Version, meta.Version, StringComparison.Ordinal) > 0
                        select ConstructUpdatablePluginResult(plugin, meta.Metadata)
                    );

            return Search(results, searchName);
        }

        private static readonly List<int> updateHighlightInfo = Enumerable.Range(0, 17).ToList();

        private Result ConstructUpdatablePluginResult(UserPlugin newMeta, PluginMetadata currentMeta) => new()
        {
            Title = $"Update Available {currentMeta.Name} by {currentMeta.Author}",
            TitleHighlightData = updateHighlightInfo,
            SubTitle = $"Update from version {currentMeta.Version} to {newMeta.Version}",
            IcoPath = currentMeta.IcoPath,
            Action = e =>
            {
                string message = string.Format(
                    Context.API.GetTranslation("plugin_pluginsmanager_update_prompt"),
                    currentMeta.Name, currentMeta.Author,
                    Environment.NewLine, Environment.NewLine);

                if (MessageBox.Show(message,
                    Context.API.GetTranslation("plugin_pluginsmanager_update_title"),
                    MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    Uninstall(currentMeta, false);

                    var downloadToFilePath = Path.Combine(DataLocation.PluginsDirectory,
                        $"{newMeta.Name}-{newMeta.Version}.zip");

                    Task.Run(async delegate
                    {
                        Context.API.ShowMsg(
                            Context.API.GetTranslation("plugin_pluginsmanager_downloading_plugin"),
                            Context.API.GetTranslation("plugin_pluginsmanager_please_wait"));

                        await Http.DownloadAsync(newMeta.UrlDownload, downloadToFilePath)
                            .ConfigureAwait(false);

                        Context.API.ShowMsg(
                            Context.API.GetTranslation("plugin_pluginsmanager_downloading_plugin"),
                            Context.API.GetTranslation("plugin_pluginsmanager_download_success"));

                        Install(newMeta, downloadToFilePath);

                        Context.API.RestartApp();
                    }).ContinueWith(t =>
                    {
                        Log.Exception("PluginsManager", $"Update failed for {currentMeta.Name}",
                            t.Exception.InnerException, "RequestUpdate");
                        Context.API.ShowMsg(
                            Context.API.GetTranslation("plugin_pluginsmanager_install_error_title"),
                            string.Format(
                                Context.API.GetTranslation("plugin_pluginsmanager_install_error_subtitle"),
                                currentMeta.Name));
                    }, TaskContinuationOptions.OnlyOnFaulted);

                    return true;
                }

                return false;
            },
            Score = 100,
            ContextData = newMeta
        };

        private Result ConstructNewPluginResult(UserPlugin plugin) => new()
        {
            Title = $"{plugin.Name} by {plugin.Author}",
            SubTitle = plugin.Description,
            IcoPath = icoPath,
            Score = 50,
            Action = e =>
            {
                if (e.SpecialKeyState.CtrlPressed)
                {
                    SearchWeb.NewTabInBrowser(plugin.Website);
                    return ShouldHideWindow;
                }

                Application.Current.MainWindow.Hide();
                _ = InstallOrUpdate(plugin); // No need to wait
                return ShouldHideWindow;
            },
            ContextData = plugin
        };

        private void Install(UserPlugin plugin, string downloadedFilePath)
        {
            if (!File.Exists(downloadedFilePath))
                return;

            var tempFolderPath = Path.Combine(Path.GetTempPath(), "flowlauncher");
            var tempFolderPluginPath = Path.Combine(tempFolderPath, "plugin");

            if (Directory.Exists(tempFolderPath))
                Directory.Delete(tempFolderPath, true);

            Directory.CreateDirectory(tempFolderPath);

            var zipFilePath = Path.Combine(tempFolderPath, Path.GetFileName(downloadedFilePath));

            File.Copy(downloadedFilePath, zipFilePath);

            File.Delete(downloadedFilePath);

            Utilities.UnZip(zipFilePath, tempFolderPluginPath, true);

            var pluginFolderPath = Utilities.GetContainingFolderPathAfterUnzip(tempFolderPluginPath);

            var metadataJsonFilePath = string.Empty;
            if (File.Exists(Path.Combine(pluginFolderPath, Constant.PluginMetadataFileName)))
                metadataJsonFilePath = Path.Combine(pluginFolderPath, Constant.PluginMetadataFileName);

            if (string.IsNullOrEmpty(metadataJsonFilePath) || string.IsNullOrEmpty(pluginFolderPath))
            {
                MessageBox.Show(Context.API.GetTranslation("plugin_pluginsmanager_install_errormetadatafile"));
                return;
            }

            string newPluginPath = Path.Combine(DataLocation.PluginsDirectory, $"{plugin.Name}-{plugin.Version}");

            FilesFolders.CopyAll(pluginFolderPath, newPluginPath);

            Directory.Delete(pluginFolderPath, true);
        }

        internal List<Result> RequestUninstall(string search)
        {
            var matchedPlugins =
                from pluginInfo in
                    from pair in Context.API.GetAllPlugins()
                    join userPlugin in pluginsManifest.UserPlugins on pair.Metadata.ID equals userPlugin.ID
                    select (Match: Context.API.FuzzySearch(search, pair.Metadata.Name),
                        PluginPair: pair,
                        Context: userPlugin)
                where string.IsNullOrEmpty(search) || pluginInfo.Match.IsSearchPrecisionScoreMet()
                select new Result
                {
                    Title = $"{pluginInfo.PluginPair.Metadata.Name} {pluginInfo.PluginPair.Metadata.Version} by {pluginInfo.PluginPair.Metadata.Author}",
                    SubTitle = pluginInfo.PluginPair.Metadata.Description,
                    IcoPath = pluginInfo.PluginPair.Metadata.IcoPath,
                    Score = pluginInfo.Match.Score,
                    Action = e =>
                    {
                        var message = string.Format(Context.API.GetTranslation("plugin_pluginsmanager_uninstall_prompt"), pluginInfo.PluginPair.Metadata.Name, pluginInfo.PluginPair.Metadata.Author,
                            Environment.NewLine,
                            Environment.NewLine);

                        if (MessageBox.Show(message, Context.API.GetTranslation("plugin_pluginsmanager_uninstall_title"), MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                            return false;

                        Application.Current.MainWindow?.Hide();
                        Uninstall(pluginInfo.PluginPair.Metadata);
                        Context.API.RestartApp();

                        return true;
                    },
                    ContextData = pluginInfo.Context
                };

            return matchedPlugins.ToList();
        }

        private void Uninstall(PluginMetadata plugin, bool removedSetting = true)
        {
            if (removedSetting)
            {
                PluginManager.Settings.Plugins.Remove(plugin.ID);
                PluginManager.AllPlugins.RemoveAll(p => p.Metadata.ID == plugin.ID);
            }

            // Marked for deletion. Will be deleted on next start up
            using var _ = File.CreateText(Path.Combine(plugin.PluginDirectory, "NeedDelete.txt"));
        }
    }
}