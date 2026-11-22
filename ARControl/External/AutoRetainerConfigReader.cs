using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace ARControl.External;

internal enum ItemListKind
{
    None,
    Protect,
    SoftSell,
    HardSell,
    Discard,
    Desynth
}

internal sealed class AutoRetainerConfigReader : IDisposable
{
    private readonly IPluginLog _pluginLog;
    private readonly string _configDirectory;
    private readonly object _lock = new();

    private FileSystemWatcher? _watcher;

    private HashSet<uint> _discardItems = [];
    private HashSet<uint> _blacklistedItems = [];
    private HashSet<uint> _hardSellItems = [];
    private HashSet<uint> _softSellItems = [];
    private HashSet<uint> _desynthItems = [];
    private HashSet<uint> _protectItems = [];

    public AutoRetainerConfigReader(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog)
    {
        _pluginLog = pluginLog;

        var configRoot = pluginInterface.ConfigDirectory.Parent?.FullName
                         ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             "XIVLauncher", "pluginConfigs");
        _configDirectory = Path.Combine(configRoot, "AutoRetainer");

        _pluginLog.Debug($"AutoRetainer config directory: {_configDirectory}");

        LoadConfigs();
        SetupWatcher();
    }

    public bool IsConfigAvailable => Directory.Exists(_configDirectory);

    public ItemListKind GetItemListKind(uint itemId)
    {
        lock (_lock)
        {
            if (_protectItems.Contains(itemId) || _blacklistedItems.Contains(itemId))
                return ItemListKind.Protect;
            if (_hardSellItems.Contains(itemId))
                return ItemListKind.HardSell;
            if (_softSellItems.Contains(itemId))
                return ItemListKind.SoftSell;
            if (_discardItems.Contains(itemId))
                return ItemListKind.Discard;
            if (_desynthItems.Contains(itemId))
                return ItemListKind.Desynth;

            return ItemListKind.None;
        }
    }

    public bool IsItemForDiscard(uint itemId)
    {
        lock (_lock)
        {
            return _discardItems.Contains(itemId) && !_blacklistedItems.Contains(itemId) && !_protectItems.Contains(itemId);
        }
    }

    public bool IsItemForSell(uint itemId)
    {
        lock (_lock)
        {
            return (_hardSellItems.Contains(itemId) || _softSellItems.Contains(itemId))
                   && !_blacklistedItems.Contains(itemId) && !_protectItems.Contains(itemId);
        }
    }

    public bool IsItemForDesynth(uint itemId)
    {
        lock (_lock)
        {
            return _desynthItems.Contains(itemId) && !_blacklistedItems.Contains(itemId) && !_protectItems.Contains(itemId);
        }
    }

    public void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void SetupWatcher()
    {
        if (!IsConfigAvailable)
            return;

        try
        {
            _watcher = new FileSystemWatcher(_configDirectory, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnConfigFileChanged;
            _watcher.Created += OnConfigFileChanged;
            _pluginLog.Debug("FileSystemWatcher setup for AutoRetainer config");
        }
        catch (Exception e)
        {
            _pluginLog.Warning(e, "Failed to setup FileSystemWatcher for AutoRetainer config");
        }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        var fileName = Path.GetFileName(e.FullPath);
        if (fileName is "Config.json" or "ARDiscardMiniConfig.json")
        {
            _pluginLog.Debug($"AutoRetainer config changed: {fileName}");
            LoadConfigs();
        }
    }

    private void LoadConfigs()
    {
        lock (_lock)
        {
            _discardItems = [];
            _blacklistedItems = [];
            _hardSellItems = [];
            _softSellItems = [];
            _desynthItems = [];
            _protectItems = [];

            if (!IsConfigAvailable)
            {
                _pluginLog.Debug("AutoRetainer config directory not found");
                return;
            }

            LoadDiscardMiniConfig();
            LoadMainConfig();
        }
    }

    private void LoadDiscardMiniConfig()
    {
        var configPath = Path.Combine(_configDirectory, "ARDiscardMiniConfig.json");
        if (!File.Exists(configPath))
        {
            _pluginLog.Debug("ARDiscardMiniConfig.json not found");
            return;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JObject.Parse(json);

            if (config["DiscardingItems"] is JArray discardArray)
            {
                foreach (var item in discardArray)
                {
                    if (item.Type == JTokenType.Integer)
                        _discardItems.Add(item.Value<uint>());
                }
            }

            if (config["BlacklistedItems"] is JArray blacklistArray)
            {
                foreach (var item in blacklistArray)
                {
                    if (item.Type == JTokenType.Integer)
                        _blacklistedItems.Add(item.Value<uint>());
                }
            }

            _pluginLog.Debug($"Loaded ARDiscardMiniConfig: {_discardItems.Count} discard items, {_blacklistedItems.Count} blacklisted items");
        }
        catch (Exception e)
        {
            _pluginLog.Warning(e, "Failed to load ARDiscardMiniConfig.json");
        }
    }

    private void LoadMainConfig()
    {
        var configPath = Path.Combine(_configDirectory, "Config.json");
        if (!File.Exists(configPath))
        {
            _pluginLog.Debug("Config.json not found");
            return;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JObject.Parse(json);

            LoadInventoryManagementSettings(config["DefaultIMSettings"]);

            if (config["AdditionalIMSettings"] is JArray additionalSettings)
            {
                foreach (var settings in additionalSettings)
                {
                    LoadInventoryManagementSettings(settings);
                }
            }

            _pluginLog.Debug($"Loaded Config: {_hardSellItems.Count} hard sell, {_softSellItems.Count} soft sell, " +
                           $"{_discardItems.Count} discard, {_desynthItems.Count} desynth, {_protectItems.Count} protect items");
        }
        catch (Exception e)
        {
            _pluginLog.Warning(e, "Failed to load Config.json");
        }
    }

    private void LoadInventoryManagementSettings(JToken? settings)
    {
        if (settings == null)
            return;

        LoadItemList(settings["HardVendorList"], _hardSellItems);
        LoadItemList(settings["SoftVendorList"], _softSellItems);
        LoadItemList(settings["DiscardList"], _discardItems);
        LoadItemList(settings["DesynthQueue"], _desynthItems);
        LoadItemList(settings["ProtectList"], _protectItems);
    }

    private static void LoadItemList(JToken? token, HashSet<uint> targetSet)
    {
        if (token is not JArray array)
            return;

        foreach (var item in array)
        {
            if (item.Type == JTokenType.Integer)
                targetSet.Add(item.Value<uint>());
        }
    }
}
