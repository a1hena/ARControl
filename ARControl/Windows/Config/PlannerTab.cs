using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ARControl.External;
using ARControl.GameData;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using ECommons;

namespace ARControl.Windows.Config;

/// <summary>
/// One place to set up what retainers collect: venture lists, character groups and characters
/// are listed on the left, and the selected entry is edited on the right.
/// </summary>
internal sealed class PlannerTab : ITab
{
    private const string NewListPopup = "##NewVentureList";
    private const string NewGroupPopup = "##NewCharacterGroup";

    private readonly ConfigWindow _configWindow;
    private readonly Configuration _configuration;
    private readonly ITextureProvider _textureProvider;
    private readonly VentureListEditor _listEditor;
    private readonly Dictionary<Guid, string> _groupNameBuffers = [];

    private Selection _selection = Selection.None;
    private string _newName = string.Empty;

    public PlannerTab(ConfigWindow configWindow, Configuration configuration, GameCache gameCache,
        ITextureProvider textureProvider, AutoRetainerConfigReader autoRetainerConfigReader, IPluginLog pluginLog)
    {
        _configWindow = configWindow;
        _configuration = configuration;
        _textureProvider = textureProvider;
        _listEditor = new VentureListEditor(configWindow, configuration, gameCache, textureProvider,
            autoRetainerConfigReader, pluginLog);
    }

    public void Draw()
    {
        using var tab = ImRaii.TabItem("Planner###TabPlanner");
        if (!tab)
            return;

        float navigationWidth = Math.Max(220 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.3f);
        using (var navigation = ImRaii.Child("PlannerNavigation", new Vector2(navigationWidth, 0), true))
        {
            if (navigation)
                DrawNavigation();
        }

        ImGui.SameLine();

        using (var detail = ImRaii.Child("PlannerDetail", Vector2.Zero, true))
        {
            if (detail)
                DrawDetail();
        }
    }

    #region Navigation

    private void DrawNavigation()
    {
        DrawSectionHeader("Venture Lists", NewListPopup, "Add a venture list");
        DrawNewNamePopup(NewListPopup, "List Name...", name => _listEditor.IsValidListName(name, null), name =>
        {
            var list = new Configuration.ItemList
            {
                Id = Guid.NewGuid(),
                Name = name,
                Type = Configuration.ListType.CollectOneTime,
                Priority = Configuration.ListPriority.InOrder,
            };
            _configuration.ItemLists.Add(list);
            Select(Selection.ForList(list.Id));
        });

        foreach (var list in _configuration.ItemLists)
        {
            if (ImGui.Selectable($"{list.Name} {list.GetIcon()}###NavList{list.Id}",
                    _selection == Selection.ForList(list.Id)))
                Select(Selection.ForList(list.Id));
        }

        if (_configuration.ItemLists.Count == 0)
            ImGui.TextDisabled("No venture lists yet.");

        ImGui.Spacing();
        DrawSectionHeader("Groups", NewGroupPopup, "Add a character group");
        DrawNewNamePopup(NewGroupPopup, "Group Name...", name => IsValidGroupName(name, null), name =>
        {
            var group = new Configuration.CharacterGroup
            {
                Id = Guid.NewGuid(),
                Name = name,
            };
            _configuration.CharacterGroups.Add(group);
            Select(Selection.ForGroup(group.Id));
        });

        foreach (var group in _configuration.CharacterGroups)
        {
            int members = GetGroupMembers(group).Count;
            if (ImGui.Selectable($"{group.Name} ({members})###NavGroup{group.Id}",
                    _selection == Selection.ForGroup(group.Id)))
                Select(Selection.ForGroup(group.Id));
        }

        if (_configuration.CharacterGroups.Count == 0)
            ImGui.TextDisabled("No groups yet.");

        ImGui.Spacing();
        DrawSectionHeader("Characters", null, null);
        bool anyCharacter = false;
        foreach (var world in GetCharacters().GroupBy(x => x.WorldName))
        {
            anyCharacter = true;
            ImGui.TextDisabled(world.Key);
            foreach (var character in world)
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.TextColored(GetCharacterColor(character), FontAwesomeIcon.Circle.ToIconString());
                ImGui.PopFont();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(GetCharacterStatus(character));

                ImGui.SameLine();
                if (ImGui.Selectable($"{GetCharacterLabel(character)}###NavCharacter{character.LocalContentId}",
                        _selection == Selection.ForCharacter(character.LocalContentId)))
                    Select(Selection.ForCharacter(character.LocalContentId));
            }
        }

        if (!anyCharacter)
            ImGui.TextDisabled("Log in and open the retainer bell\nto add your characters.");
    }

    private static void DrawSectionHeader(string title, string? popupId, string? addTooltip)
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, title);
        if (popupId != null)
        {
            ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.GetFrameHeight());
            if (ImGuiComponents.IconButton($"{popupId}Button", FontAwesomeIcon.Plus))
                ImGui.OpenPopup(popupId);
            if (addTooltip != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(addTooltip);
        }

        ImGui.Separator();
    }

    private void DrawNewNamePopup(string popupId, string hint, Func<string, bool> isValid, Action<string> create)
    {
        if (!ImGui.BeginPopup(popupId))
            return;

        if (ImGui.IsWindowAppearing())
        {
            _newName = string.Empty;
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        bool submit = ImGui.InputTextWithHint("", hint, ref _newName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        bool valid = isValid(_newName);

        ImGui.BeginDisabled(!valid);
        submit |= ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Create");
        ImGui.EndDisabled();

        if (submit && valid)
        {
            create(_newName);
            _newName = string.Empty;
            _configWindow.ShouldSave();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    #endregion

    #region Detail

    private void DrawDetail()
    {
        switch (_selection.Kind)
        {
            case SelectionKind.List
                when _configuration.ItemLists.FirstOrDefault(x => x.Id == _selection.Id) is { } list:
                DrawListDetail(list);
                break;

            case SelectionKind.Group
                when _configuration.CharacterGroups.FirstOrDefault(x => x.Id == _selection.Id) is { } group:
                DrawGroupDetail(group);
                break;

            case SelectionKind.Character
                when _configuration.Characters.FirstOrDefault(x => x.LocalContentId == _selection.CharacterId) is
                    { } character:
                DrawCharacterDetail(character);
                break;

            default:
                DrawOverview();
                break;
        }
    }

    private static void DrawOverview()
    {
        ImGui.TextWrapped("Select a venture list, group or character on the left.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudGrey, "How it fits together");
        ImGui.TextWrapped("1. Venture Lists say what to collect, and how much.");
        ImGui.TextWrapped("2. Groups share an ordered set of venture lists between several characters.");
        ImGui.TextWrapped(
            "3. Characters either join a group or pick their own venture lists, and choose which retainers ARC sends out.");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "When a retainer comes back, ARC goes through the character's lists from top to bottom and sends it out for the first item that still needs collecting. If nothing is left, it does a Quick Venture.");
    }

    private void DrawListDetail(Configuration.ItemList list)
    {
        DrawTitle($"{list.Name} {list.GetIcon()}", "Venture List");
        _listEditor.DrawSettings(list);

        DrawSubheading("Items");
        ImGui.Indent(_configWindow.MainIndentSize);
        _listEditor.DrawItems(list);
        ImGui.Unindent(_configWindow.MainIndentSize);

        DrawSubheading("Used by");
        var groups = _configuration.CharacterGroups.Where(x => x.ItemListIds.Contains(list.Id)).ToList();
        var characters = _configuration.Characters
            .Where(x => x.Type == Configuration.CharacterType.Standalone && x.ItemListIds.Contains(list.Id))
            .OrderBy(x => x.WorldName)
            .ThenBy(x => x.LocalContentId)
            .ToList();
        foreach (var group in groups)
        {
            if (ImGui.Selectable($"Group: {group.Name}###UsedByGroup{group.Id}"))
                Select(Selection.ForGroup(group.Id));
        }

        foreach (var character in characters)
        {
            if (ImGui.Selectable($"Character: {character}###UsedByCharacter{character.LocalContentId}"))
                Select(Selection.ForCharacter(character.LocalContentId));
        }

        if (groups.Count == 0 && characters.Count == 0)
            ImGui.TextDisabled("Not used by any group or character yet.");

        ImGui.Spacing();
        ImGui.Separator();
        if (DrawDeleteButton("Delete List", groups.Count > 0 || characters.Count > 0,
                "Remove this list from every group and character using it first."))
        {
            _configuration.ItemLists.Remove(list);
            Select(Selection.None);
            _configWindow.ShouldSave();
        }
    }

    private void DrawGroupDetail(Configuration.CharacterGroup group)
    {
        DrawTitle(group.Name, "Group");

        string name = _groupNameBuffers.GetValueOrDefault(group.Id, group.Name);
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Name##GroupName", ref name, 64))
        {
            if (IsValidGroupName(name, group))
            {
                group.Name = name;
                _groupNameBuffers.Remove(group.Id);
                _configWindow.ShouldSave();
            }
            else
                _groupNameBuffers[group.Id] = name;
        }

        if (_groupNameBuffers.ContainsKey(group.Id))
            ImGui.TextColored(ImGuiColors.DalamudRed,
                "Names need at least 2 characters, no '%' and must be unique. The change is not saved yet.");

        DrawSubheading("Venture Lists (checked from top to bottom)");
        ImGui.Indent(_configWindow.MainIndentSize);
        _configWindow.DrawVentureListSelection(group.Id.ToString(), group.ItemListIds);
        ImGui.Unindent(_configWindow.MainIndentSize);

        DrawSubheading("Characters");
        var members = GetGroupMembers(group);
        Configuration.CharacterConfiguration? memberToRemove = null;
        foreach (var member in members)
        {
            ImGui.PushID($"Member{member.LocalContentId}");
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Times))
                memberToRemove = member;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove from this group");

            ImGui.SameLine();
            if (ImGui.Selectable($"{GetCharacterLabel(member)} @ {member.WorldName}"))
                Select(Selection.ForCharacter(member.LocalContentId));
            ImGui.PopID();
        }

        if (members.Count == 0)
            ImGui.TextDisabled("No characters in this group yet.");

        if (memberToRemove != null)
        {
            memberToRemove.Type = Configuration.CharacterType.Standalone;
            memberToRemove.CharacterGroupId = Guid.Empty;
            _configWindow.ShouldSave();
        }

        var candidates = GetCharacters()
            .Where(x => x.Type != Configuration.CharacterType.PartOfCharacterGroup || x.CharacterGroupId != group.Id)
            .ToList();
        ImGui.BeginDisabled(candidates.Count == 0);
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##AddMember", "Add character...", ImGuiComboFlags.HeightLarge))
        {
            foreach (var candidate in candidates)
            {
                string currentGroup = candidate.Type == Configuration.CharacterType.PartOfCharacterGroup
                    ? _configuration.CharacterGroups.FirstOrDefault(x => x.Id == candidate.CharacterGroupId)?.Name ??
                      string.Empty
                    : string.Empty;
                string suffix = currentGroup.Length > 0 ? $" (moves from {currentGroup})" : string.Empty;
                if (ImGui.Selectable($"{candidate}{suffix}###Candidate{candidate.LocalContentId}"))
                {
                    candidate.Type = Configuration.CharacterType.PartOfCharacterGroup;
                    candidate.CharacterGroupId = group.Id;
                    _configWindow.ShouldSave();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        if (DrawDeleteButton("Delete Group", members.Count > 0, "Remove every character from this group first."))
        {
            _configuration.CharacterGroups.Remove(group);
            Select(Selection.None);
            _configWindow.ShouldSave();
        }
    }

    private void DrawCharacterDetail(Configuration.CharacterConfiguration character)
    {
        DrawTitle(character.ToString(), "Character");

        bool managed = character.Type != Configuration.CharacterType.NotManaged;
        if (ImGui.Checkbox("Let ARC choose ventures for this character", ref managed))
        {
            character.Type = managed ? Configuration.CharacterType.Standalone : Configuration.CharacterType.NotManaged;
            character.CharacterGroupId = Guid.Empty;
            _configWindow.ShouldSave();
        }

        if (managed)
        {
            DrawSubheading("Venture Lists");

            List<(Guid Id, string Name)> sources =
                new List<(Guid Id, string Name)> { (Guid.Empty, "Own lists (pick below)") }
                    .Concat(_configuration.CharacterGroups.Select(x => (x.Id, $"Group: {x.Name}")))
                    .ToList();
            int sourceIndex = character.Type == Configuration.CharacterType.PartOfCharacterGroup
                ? Math.Max(0, sources.FindIndex(x => x.Id == character.CharacterGroupId))
                : 0;
            ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo("Lists from", ref sourceIndex, sources.Select(x => x.Name).ToArray(), sources.Count))
            {
                if (sourceIndex == 0)
                {
                    character.Type = Configuration.CharacterType.Standalone;
                    character.CharacterGroupId = Guid.Empty;
                }
                else
                {
                    character.Type = Configuration.CharacterType.PartOfCharacterGroup;
                    character.CharacterGroupId = sources[sourceIndex].Id;
                }

                _configWindow.ShouldSave();
            }

            ImGui.Spacing();
            if (sourceIndex == 0)
            {
                // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
                character.ItemListIds ??= [];
                ImGui.Indent(_configWindow.MainIndentSize);
                _configWindow.DrawVentureListSelection(
                    character.LocalContentId.ToString(CultureInfo.InvariantCulture), character.ItemListIds);
                ImGui.Unindent(_configWindow.MainIndentSize);
            }
            else
                DrawGroupListsReadOnly(_configuration.CharacterGroups.First(x => x.Id == sources[sourceIndex].Id));
        }
        else
        {
            ImGui.TextWrapped("ARC doesn't pick ventures for this character. AutoRetainer handles it on its own.");
        }

        DrawSubheading("Retainers");
        DrawRetainers(character);
    }

    private void DrawGroupListsReadOnly(Configuration.CharacterGroup group)
    {
        ImGui.TextWrapped($"Collects with the lists of the group '{group.Name}', from top to bottom:");
        ImGui.Indent(_configWindow.MainIndentSize);
        var lists = group.ItemListIds
            .Select(listId => _configuration.ItemLists.FirstOrDefault(x => x.Id == listId))
            .OfType<Configuration.ItemList>()
            .Distinct()
            .ToList();
        for (int i = 0; i < lists.Count; ++i)
        {
            if (ImGui.Selectable($"{i + 1}. {lists[i].Name} {lists[i].GetIcon()}###GroupList{lists[i].Id}"))
                Select(Selection.ForList(lists[i].Id));
        }

        if (lists.Count == 0)
            ImGui.TextColored(ImGuiColors.DalamudRed, "This group has no venture lists yet.");
        ImGui.Unindent(_configWindow.MainIndentSize);

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ArrowRight, "Edit group"))
            Select(Selection.ForGroup(group.Id));
    }

    private void DrawRetainers(Configuration.CharacterConfiguration character)
    {
        var retainers = character.Retainers
            .Where(x => x.Job > 0)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.RetainerContentId)
            .ToList();
        if (retainers.Count == 0)
        {
            ImGui.TextDisabled("No retainers found. Open the retainer bell on this character.");
            return;
        }

        ImGui.TextWrapped("Checked retainers are sent out by ARC.");
        if (ImGui.SmallButton("All"))
        {
            foreach (var retainer in retainers.Where(x => x.Level >= ConfigWindow.MinLevel))
                retainer.Managed = true;
            _configWindow.ShouldSave();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("None"))
        {
            foreach (var retainer in retainers)
                retainer.Managed = false;
            _configWindow.ShouldSave();
        }

        foreach (var retainer in retainers)
        {
            ImGui.BeginDisabled(retainer is { Managed: false, Level: < ConfigWindow.MinLevel });

            var icon = _textureProvider.GetFromGameIcon(new GameIconLookup(62000 + retainer.Job));
            ImGui.Image(icon.GetWrapOrEmpty().Handle, new Vector2(ImGui.GetFrameHeight()));
            ImGui.SameLine(0, ImGui.GetStyle().FramePadding.X);

            bool retainerManaged = retainer.Managed;
            if (ImGui.Checkbox($"{retainer.Name}###Retainer{retainer.Name}{retainer.RetainerContentId}",
                    ref retainerManaged))
            {
                retainer.Managed = retainerManaged;
                _configWindow.ShouldSave();
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"Lv{retainer.Level}");

            ImGui.EndDisabled();
            if (retainer is { Managed: false, Level: < ConfigWindow.MinLevel } &&
                ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip($"Retainers need level {ConfigWindow.MinLevel} for ventures.");
        }
    }

    private static void DrawTitle(string title, string kind)
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, kind);
        ImGui.TextUnformatted(title);
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void DrawSubheading(string text)
    {
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, text);
        ImGui.Separator();
    }

    private static bool DrawDeleteButton(string label, bool blocked, string blockedReason)
    {
        bool ctrl = ImGui.GetIO().KeyCtrl;
        ImGui.BeginDisabled(blocked || !ctrl);
        bool clicked = ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Trash, label);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ? blockedReason : "Hold Ctrl and click to delete.");
        return clicked;
    }

    #endregion

    #region Helpers

    private void Select(Selection selection)
    {
        _selection = selection;
        _listEditor.ForgetUnsavedChanges();
        _groupNameBuffers.Clear();
    }

    private IEnumerable<Configuration.CharacterConfiguration> GetCharacters()
    {
        return _configuration.Characters
            .Where(x => x.Retainers.Any(y => y.Job != 0))
            .OrderBy(x => x.WorldName)
            .ThenBy(x => x.LocalContentId);
    }

    private List<Configuration.CharacterConfiguration> GetGroupMembers(Configuration.CharacterGroup group)
    {
        return _configuration.Characters
            .Where(x => x.Type == Configuration.CharacterType.PartOfCharacterGroup && x.CharacterGroupId == group.Id)
            .OrderBy(x => x.WorldName)
            .ThenBy(x => x.LocalContentId)
            .ToList();
    }

    private bool IsValidGroupName(string name, Configuration.CharacterGroup? existingGroup)
    {
        return name.Length >= 2 &&
               !name.Contains('%', StringComparison.Ordinal) &&
               !_configuration.CharacterGroups.Any(x => x != existingGroup && name.EqualsIgnoreCase(x.Name));
    }

    private static string GetCharacterLabel(Configuration.CharacterConfiguration character)
    {
        if (character.Type == Configuration.CharacterType.NotManaged)
            return character.CharacterName;

        int total = character.Retainers.Count(x => x.Job > 0);
        int managed = character.Retainers.Count(x => x.Job > 0 && x.Managed);
        return $"{character.CharacterName} ({managed}/{total})";
    }

    private static Vector4 GetCharacterColor(Configuration.CharacterConfiguration character)
    {
        if (character.Type == Configuration.CharacterType.NotManaged)
            return ImGuiColors.DalamudGrey3;

        var retainers = character.Retainers.Where(x => x.Job > 0).ToList();
        if (retainers.Count > 0 && retainers.All(x => x.Managed))
            return ImGuiColors.HealerGreen;
        if (retainers.All(x => !x.Managed))
            return ImGuiColors.DalamudRed;
        return ImGuiColors.DalamudOrange;
    }

    private string GetCharacterStatus(Configuration.CharacterConfiguration character)
    {
        return character.Type switch
        {
            Configuration.CharacterType.NotManaged => "Not managed by ARC",
            Configuration.CharacterType.PartOfCharacterGroup =>
                $"Group: {_configuration.CharacterGroups.FirstOrDefault(x => x.Id == character.CharacterGroupId)?.Name ?? "?"}",
            _ => "Own venture lists",
        };
    }

    #endregion

    private enum SelectionKind
    {
        None,
        List,
        Group,
        Character,
    }

    private readonly record struct Selection(SelectionKind Kind, Guid Id, ulong CharacterId)
    {
        public static Selection None => new(SelectionKind.None, Guid.Empty, 0);
        public static Selection ForList(Guid id) => new(SelectionKind.List, id, 0);
        public static Selection ForGroup(Guid id) => new(SelectionKind.Group, id, 0);
        public static Selection ForCharacter(ulong id) => new(SelectionKind.Character, Guid.Empty, id);
    }
}
