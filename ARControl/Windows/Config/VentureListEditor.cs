using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ARControl.External;
using ARControl.GameData;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.ImGuiMethods;

namespace ARControl.Windows.Config;

/// <summary>
/// Edits the settings and the items of a single venture list. Embedded in the planner's detail pane.
/// </summary>
internal sealed class VentureListEditor(
    ConfigWindow configWindow,
    Configuration configuration,
    GameCache gameCache,
    ITextureProvider textureProvider,
    AutoRetainerConfigReader autoRetainerConfigReader,
    IPluginLog pluginLog)
{
    private static readonly string[] StockingTypeLabels = ["Collect Once", "Keep in Stock"];

    private static readonly string[] PriorityLabels =
        ["Collect in order of the list", "Collect item with lowest inventory first"];

    private static readonly Regex CountAndName = new(@"^(\d{1,5})x?\s+(.*)$", RegexOptions.Compiled);
    private const FontAwesomeIcon WarningIcon = FontAwesomeIcon.ExclamationCircle;
    private const FontAwesomeIcon ExcessCrystalsIcon = FontAwesomeIcon.Diamond;

    private readonly Dictionary<Guid, string> _nameBuffers = [];
    private string _searchString = string.Empty;
    private (Guid, Guid)? _draggedItem;

    public void DrawSettings(Configuration.ItemList list)
    {
        float width = 300 * ImGuiHelpers.GlobalScale;

        string name = _nameBuffers.GetValueOrDefault(list.Id, list.Name);
        ImGui.SetNextItemWidth(width);
        if (ImGui.InputText("Name##ListName", ref name, 64))
        {
            if (IsValidListName(name, list))
            {
                list.Name = name;
                _nameBuffers.Remove(list.Id);
                configWindow.ShouldSave();
            }
            else
                _nameBuffers[list.Id] = name;
        }

        if (_nameBuffers.ContainsKey(list.Id))
            ImGui.TextColored(ImGuiColors.DalamudRed,
                "Names need at least 2 characters, no '%' and must be unique. The change is not saved yet.");

        int type = (int)list.Type;
        ImGui.SetNextItemWidth(width);
        if (ImGui.Combo("Type##ListType", ref type, StockingTypeLabels, StockingTypeLabels.Length))
        {
            list.Type = (Configuration.ListType)type;
            if (list.Type == Configuration.ListType.CollectOneTime)
            {
                list.Priority = Configuration.ListPriority.InOrder;
                list.CheckRetainerInventory = false;
            }

            configWindow.ShouldSave();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Collect Once: the quantities are shared by everyone using this list, and count down as retainers bring items back.\n\n" +
            "Keep in Stock: each character on its own collects until its inventory holds the quantity.");

        if (list.Type == Configuration.ListType.KeepStocked)
        {
            int priority = (int)list.Priority;
            ImGui.SetNextItemWidth(width);
            if (ImGui.Combo("Priority##ListPriority", ref priority, PriorityLabels, PriorityLabels.Length))
            {
                list.Priority = (Configuration.ListPriority)priority;
                configWindow.ShouldSave();
            }

            bool checkRetainerInventory = list.CheckRetainerInventory;
            if (ImGui.Checkbox("Also count items in retainer inventories (requires AllaganTools)",
                    ref checkRetainerInventory))
            {
                list.CheckRetainerInventory = checkRetainerInventory;
                configWindow.ShouldSave();
            }
        }
    }

    public void ForgetUnsavedChanges() => _nameBuffers.Clear();

    public bool IsValidListName(string name, Configuration.ItemList? existingList)
    {
        return name.Length >= 2 &&
               !name.Contains('%', StringComparison.Ordinal) &&
               !configuration.ItemLists.Any(x => x != existingList && name.EqualsIgnoreCase(x.Name));
    }

    public void DrawItems(Configuration.ItemList list)
    {
        DrawVentureListItemFilter(list);

        Configuration.QueuedItem? itemToRemove = null;
        Configuration.QueuedItem? itemToAdd = null;
        int indexToAdd = 0;

        float width = ImGui.GetContentRegionAvail().X;
        List<(Vector2 TopLeft, Vector2 BottomRight)> itemPositions = [];

        for (int i = 0; i < list.Items.Count; ++i)
        {
            Vector2 topLeft = ImGui.GetCursorScreenPos() +
                              new Vector2(-configWindow.MainIndentSize, -ImGui.GetStyle().ItemSpacing.Y / 2);

            var item = list.Items[i];
            ImGui.PushID($"QueueItem{item.InternalId}");
            var ventures = gameCache.Ventures.Where(x => x.ItemId == item.ItemId).ToList();
            var venture = ventures.First();

            if (item.ItemId is >= 2 and <= 13 && item.RemainingQuantity >= 10000)
            {
                if (list.Type == Configuration.ListType.CollectOneTime || list.CheckRetainerInventory)
                    DrawWarning(ExcessCrystalsIcon,
                        "You are responsible for manually moving shards or crystals to your retainers - ARC won't do that for you.\nIf you don't, this may lead to wasted ventures.",
                        ImGuiColors.ParsedBlue);
                else
                    DrawWarning(WarningIcon, "You can never have this many of a shard or crystal in your inventory.");
            }

            var itemListKind = autoRetainerConfigReader.GetItemListKind(item.ItemId);
            if (itemListKind != ItemListKind.None)
                DrawAutoRetainerIndicator(itemListKind);

            var icon = textureProvider.GetFromGameIcon(new GameIconLookup(venture.IconId));
            ImGui.Image(icon.GetWrapOrEmpty().Handle, new Vector2(ImGui.GetFrameHeight()));
            ImGui.SameLine(0, ImGui.GetStyle().FramePadding.X);

            ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
            int quantity = item.RemainingQuantity;
            if (ImGui.InputInt($"{venture.Name} ({string.Join(" ", ventures.Select(x => x.CategoryType.ToString()))})",
                    ref quantity, 100))
            {
                item.RemainingQuantity = quantity;
                configWindow.ShouldSave();
            }

            DrawDragDropIcons(list, item);

            if (ImGuiComponents.IconButton($"##Remove{i}", FontAwesomeIcon.Times))
                itemToRemove = item;

            ImGui.PopID();

            Vector2 bottomRight = new Vector2(topLeft.X + width + configWindow.MainIndentSize,
                ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y + 2);
            //ImGui.GetWindowDrawList().AddRect(topLeft, bottomRight, ImGui.GetColorU32(i % 2 == 0 ? ImGuiColors.DalamudRed : ImGuiColors.HealerGreen));
            itemPositions.Add((topLeft, bottomRight));
        }

        if (!ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            _draggedItem = null;
        else if (_draggedItem != null && _draggedItem.Value.Item1 == list.Id)
        {
            var draggedItem = list.Items.Single(x => x.InternalId == _draggedItem.Value.Item2);
            int oldIndex = list.Items.IndexOf(draggedItem);

            var (topLeft, bottomRight) = itemPositions[oldIndex];
            topLeft += new Vector2(configWindow.MainIndentSize, 0);
            ImGui.GetWindowDrawList().AddRect(topLeft, bottomRight, ImGui.GetColorU32(ImGuiColors.DalamudGrey), 3f,
                ImDrawFlags.RoundCornersAll);

            int newIndex = itemPositions.FindIndex(x => ImGui.IsMouseHoveringRect(x.TopLeft, x.BottomRight, true));
            if (newIndex >= 0 && oldIndex != newIndex)
            {
                itemToAdd = list.Items.Single(x => x.InternalId == _draggedItem.Value.Item2);
                indexToAdd = newIndex;
            }
        }

        if (itemToRemove != null)
        {
            list.Items.Remove(itemToRemove);
            configWindow.ShouldSave();
        }

        if (itemToAdd != null)
        {
            pluginLog.Information($"Updating {itemToAdd.ItemId} → {indexToAdd}");
            list.Items.Remove(itemToAdd);
            list.Items.Insert(indexToAdd, itemToAdd);
            configWindow.ShouldSave();
        }

        ImGui.Spacing();
        List<Configuration.QueuedItem> clipboardItems = ParseClipboardItems();
        ImportFromClipboardButton(list, clipboardItems);
        RemoveFinishedItemsButton(list);

        ImGui.Spacing();
    }

    private void DrawDragDropIcons(Configuration.ItemList list, Configuration.QueuedItem item)
    {
        if (list.Items.Count > 1)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.SameLine(ImGui.GetContentRegionAvail().X +
                           configWindow.MainIndentSize +
                           ImGui.GetStyle().WindowPadding.X -
                           ImGui.CalcTextSize(FontAwesomeIcon.ArrowsUpDown.ToIconString()).X -
                           ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X -
                           ImGui.GetStyle().FramePadding.X * 4 -
                           ImGui.GetStyle().ItemSpacing.X);
            ImGui.PopFont();

            if (_draggedItem != null && _draggedItem.Value.Item1 == list.Id &&
                _draggedItem.Value.Item2 == item.InternalId)
            {
                ImGuiComponents.IconButton("##Move", FontAwesomeIcon.ArrowsUpDown,
                    ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.ButtonActive)));
            }
            else
                ImGuiComponents.IconButton("##Move", FontAwesomeIcon.ArrowsUpDown);

            if (_draggedItem == null && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                _draggedItem = (list.Id, item.InternalId);

            ImGui.SameLine();
        }
        else
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.SameLine(ImGui.GetContentRegionAvail().X +
                           configWindow.MainIndentSize +
                           ImGui.GetStyle().WindowPadding.X -
                           ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X -
                           ImGui.GetStyle().FramePadding.X * 2);
            ImGui.PopFont();
        }
    }

    private static void DrawAutoRetainerIndicator(ItemListKind listKind)
    {
        (FontAwesomeIcon icon, Vector4 color, string tooltip) = listKind switch
        {
            ItemListKind.Discard => (FontAwesomeIcon.Trash, ImGuiColors.DalamudRed,
                "AutoRetainer: This item is marked for automatic discard"),
            ItemListKind.HardSell => (FontAwesomeIcon.Coins, ImGuiColors.DalamudOrange,
                "AutoRetainer: This item is marked for automatic NPC sale (Hard Sell)"),
            ItemListKind.SoftSell => (FontAwesomeIcon.Coins, ImGuiColors.DalamudYellow,
                "AutoRetainer: This item is marked for automatic NPC sale (Soft Sell)"),
            ItemListKind.Desynth => (FontAwesomeIcon.Recycle, ImGuiColors.ParsedPurple,
                "AutoRetainer: This item is marked for automatic desynthesis"),
            ItemListKind.Protect => (FontAwesomeIcon.Shield, ImGuiColors.HealerGreen,
                "AutoRetainer: This item is protected from automatic actions"),
            _ => (FontAwesomeIcon.None, Vector4.Zero, string.Empty)
        };

        if (icon == FontAwesomeIcon.None)
            return;

        DrawWarning(icon, tooltip, color);
    }

    private static void DrawWarning(FontAwesomeIcon icon, string tooltip, Vector4? color = null)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        var pos = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(pos.X - ImGui.CalcTextSize(icon.ToIconString()).X - 5, pos.Y + 2));
        ImGui.TextColored(color ?? ImGuiColors.DalamudYellow, icon.ToIconString());
        ImGui.SetCursorPos(pos);
        ImGui.PopFont();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private void DrawVentureListItemFilter(Configuration.ItemList list)
    {
        ImGuiEx.SetNextItemFullWidth();
        if (ImGui.BeginCombo($"##VentureSelection{list.Id}", "Add Venture...", ImGuiComboFlags.HeightLarge))
        {
            ImGuiEx.SetNextItemFullWidth();

            bool addFirst = ImGui.InputTextWithHint("", "Filter...", ref _searchString, 256,
                ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue);

            int quantity;
            string itemName;
            var regexMatch = CountAndName.Match(_searchString);
            if (regexMatch.Success)
            {
                quantity = int.Parse(regexMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                itemName = regexMatch.Groups[2].Value;
            }
            else
            {
                quantity = 0;
                itemName = _searchString;
            }

            foreach (var filtered in gameCache.Ventures
                         .Where(x => x.Name.Contains(itemName, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => x.Level)
                         .ThenBy(x => x.Name)
                         .ThenBy(x => x.ItemId)
                         .GroupBy(x => x.ItemId)
                         .Select(x => new
                         {
                             Venture = x.First(),
                             CategoryNames = x.Select(y => y.CategoryType.ToString())
                         }))
            {
                var icon = textureProvider.GetFromGameIcon(new GameIconLookup(filtered.Venture.IconId));
                Vector2 pos = ImGui.GetCursorPos();
                Vector2 iconSize = new Vector2(ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.Y);

                ImGui.SetCursorPos(pos + new Vector2(iconSize.X + ImGui.GetStyle().FramePadding.X,
                    ImGui.GetStyle().ItemSpacing.Y / 2));

                bool addThis = ImGui.Selectable(
                    $"{filtered.Venture.Name} ({string.Join(" ", filtered.CategoryNames)})##SelectVenture{filtered.Venture.RowId}");


                ImGui.SameLine(0, 0);
                ImGui.SetCursorPos(pos);
                ImGui.Image(icon.GetWrapOrEmpty().Handle, iconSize);

                if (addThis || addFirst)
                {
                    list.Items.Add(new Configuration.QueuedItem
                    {
                        ItemId = filtered.Venture.ItemId,
                        RemainingQuantity = quantity,
                    });

                    if (addFirst)
                    {
                        addFirst = false;
                        ImGui.CloseCurrentPopup();
                    }

                    configWindow.ShouldSave();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.Spacing();
    }

    private void ImportFromClipboardButton(Configuration.ItemList list, List<Configuration.QueuedItem> clipboardItems)
    {
        ImGui.BeginDisabled(clipboardItems.Count == 0);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Download, "Import from Clipboard"))
        {
            pluginLog.Information($"Importing {clipboardItems.Count} clipboard items");
            foreach (var item in clipboardItems)
            {
                var existingItem = list.Items.FirstOrDefault(x => x.ItemId == item.ItemId);
                if (existingItem != null)
                    existingItem.RemainingQuantity += item.RemainingQuantity;
                else
                    list.Items.Add(item);
            }

            configWindow.ShouldSave();
        }

        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Supports importing a list in a Teamcraft-compatible format.");
            ImGui.Spacing();
            if (clipboardItems.Count > 0)
            {
                ImGui.Text("Clicking this button now would add the following items:");
                ImGui.Indent();
                foreach (var item in clipboardItems)
                    ImGui.TextUnformatted(
                        $"{item.RemainingQuantity}x {gameCache.Ventures.First(x => item.ItemId == x.ItemId).Name}");
                ImGui.Unindent();
            }
            else
            {
                ImGui.Text("For example:");
                ImGui.Indent();
                ImGui.Text("2000x Cobalt Ore");
                ImGui.Text("1000x Gold Ore");
                ImGui.Unindent();
            }

            ImGui.EndTooltip();
        }
    }

    private void RemoveFinishedItemsButton(Configuration.ItemList list)
    {
        if (list.Items.Count > 0 && list.Type == Configuration.ListType.CollectOneTime)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(list.Items.All(x => x.RemainingQuantity > 0));
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Check, "Remove all finished items"))
            {
                list.Items.RemoveAll(q => q.RemainingQuantity <= 0);
                configWindow.ShouldSave();
            }

            ImGui.EndDisabled();
        }
    }

    private List<Configuration.QueuedItem> ParseClipboardItems()
    {
        List<Configuration.QueuedItem> clipboardItems = [];
        try
        {
            string clipboardText = ImGui.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clipboardText))
            {
                foreach (var clipboardLine in clipboardText.ReplaceLineEndings().Split(Environment.NewLine))
                {
                    var match = CountAndName.Match(clipboardLine);
                    if (!match.Success)
                        continue;

                    var venture = gameCache.Ventures.FirstOrDefault(x =>
                        x.Name.Equals(match.Groups[2].Value, StringComparison.OrdinalIgnoreCase));
                    if (venture != null && int.TryParse(match.Groups[1].Value, out int quantity))
                    {
                        clipboardItems.Add(new Configuration.QueuedItem
                        {
                            ItemId = venture.ItemId,
                            RemainingQuantity = quantity,
                        });
                    }
                }
            }
        }
        catch (Exception e)
        {
            pluginLog.Warning(e, "Unable to extract clipboard text");
        }

        return clipboardItems;
    }


}
