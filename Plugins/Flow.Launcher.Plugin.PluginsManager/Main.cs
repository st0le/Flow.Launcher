using Flow.Launcher.Infrastructure;
using Flow.Launcher.Plugin.PluginsManager.ViewModels;
using Flow.Launcher.Plugin.PluginsManager.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Flow.Launcher.Plugin.PluginsManager
{
    public class Main : ISettingProvider, IAsyncPlugin, IContextMenu, IPluginI18n, IAsyncReloadable
    {
        internal PluginInitContext Context { get; set; }

        internal Settings Settings;

        private SettingsViewModel viewModel;

        private IContextMenu contextMenu;

        internal PluginsManager pluginManager;

        private DateTime lastUpdateTime = DateTime.MinValue;

        public Control CreateSettingPanel()
        {
            return new PluginsManagerSettings(viewModel);
        }

        public Task InitAsync(PluginInitContext context)
        {
            Context = context;
            Settings = context.API.LoadSettingJsonStorage<Settings>();
            viewModel = new SettingsViewModel(context, Settings);
            contextMenu = new ContextMenu(Context);
            pluginManager = new PluginsManager(Context, Settings);
            _manifestUpdateTask = pluginManager.UpdateManifestAsync().ContinueWith(_ =>
            {
                lastUpdateTime = DateTime.Now;
            }, TaskContinuationOptions.OnlyOnRanToCompletion);

            return Task.CompletedTask;
        }

        public List<Result> LoadContextMenus(Result selectedResult)
        {
            return contextMenu.LoadContextMenus(selectedResult);
        }

        private Task _manifestUpdateTask = Task.CompletedTask;

        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(query.Search))
                return pluginManager.GetDefaultHotKeys();

            if ((DateTime.Now - lastUpdateTime).TotalHours > 12 && _manifestUpdateTask.IsCompleted) // 12 hours
            {
                _manifestUpdateTask = pluginManager.UpdateManifestAsync().ContinueWith(t =>
                {
                    lastUpdateTime = DateTime.Now;
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }

            return query.FirstSearch.ToLower() switch
            {
                Settings.HotKeyList => await pluginManager.RequestListAsync(query.SecondToEndSearch, token),
                Settings.HotKeyInstall => await pluginManager.RequestInstallAsync(query.SecondToEndSearch, token),
                Settings.HotkeyUninstall => pluginManager.RequestUninstall(query.SecondToEndSearch),
                Settings.HotkeyUpdate => await pluginManager.RequestUpdateAsync(query.SecondToEndSearch, token),
                _ => pluginManager.GetDefaultHotKeys().Where(hotkey =>
                {
                    hotkey.Score = StringMatcher.FuzzySearch(query.Search, hotkey.Title).Score;
                    return hotkey.Score > 0;
                }).ToList()
            };
        }

        public string GetTranslatedPluginTitle()
        {
            return Context.API.GetTranslation("plugin_pluginsmanager_plugin_name");
        }

        public string GetTranslatedPluginDescription()
        {
            return Context.API.GetTranslation("plugin_pluginsmanager_plugin_description");
        }

        public async Task ReloadDataAsync()
        {
            await pluginManager.UpdateManifestAsync();
            lastUpdateTime = DateTime.Now;
        }
    }
}