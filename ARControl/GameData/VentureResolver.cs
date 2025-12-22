using System.Linq;
using Dalamud.Plugin.Services;

namespace ARControl.GameData;

internal sealed class VentureResolver(GameCache gameCache, IPluginLog pluginLog)
{
    public (Venture?, VentureReward?) ResolveVenture(Configuration.CharacterConfiguration character,
        Configuration.RetainerConfiguration retainer, uint itemId)
    {
        var venture = gameCache.Ventures
            .Where(x => retainer.Level >= x.Level)
            .FirstOrDefault(x => x.ItemId == itemId && x.MatchesJob(retainer.Job));
        if (venture == null)
        {
            pluginLog.Debug($"No applicable venture found for itemId {itemId} as {retainer.Job}");
            return (null, null);
        }

        if (venture.CategoryType != EVentureCategoryType.DoWM)
        {
            var itemToGather = gameCache.ItemsToGather.FirstOrDefault(x => x.ItemId == itemId);
            if (itemToGather != null && !character.GatheredItems.Contains(itemToGather.GatheredItemId))
            {
                pluginLog.Information($"Character hasn't gathered {venture.Name} yet");
                return (null, null);
            }
        }

        pluginLog.Debug(
            $"Found venture {venture.Name}, row = {venture.RowId}, checking if we have high enough stats");
        VentureReward? reward = null;
        if (venture.CategoryType is EVentureCategoryType.MIN or EVentureCategoryType.BTN)
        {
            if (retainer.Gathering >= venture.RequiredGathering)
                reward = venture.Rewards.Last(
                    x => retainer.Perception >= x.PerceptionMinerBotanist);
        }
        else if (venture.CategoryType == EVentureCategoryType.FSH)
        {
            if (retainer.Gathering >= venture.RequiredGathering)
                reward = venture.Rewards.Last(
                    x => retainer.Perception >= x.PerceptionFisher);
        }
        else
        {
            if (retainer.ItemLevel >= venture.ItemLevelCombat)
                reward = venture.Rewards.Last(
                    x => retainer.ItemLevel >= x.ItemLevelCombat);
        }

        return (venture, reward);
    }
}
