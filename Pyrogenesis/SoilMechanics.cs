using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Pyrogenesis
{
    public class SoilMechanics
    {
        private readonly ICoreServerAPI api;
        private readonly PyrogenesisConfig config;
        private readonly Dictionary<BlockPos, PendingBlock> pendingBlocks = new();
        private readonly Random rand = new Random();
        private const string MOD_ID = "pyrogenesis";
        public const float PROCESS_TIMEOUT_SECONDS = 60f;

        public SoilMechanics(ICoreServerAPI api, PyrogenesisConfig config)
        {
            this.api = api;
            this.config = config ?? new PyrogenesisConfig();
            LogAvailableSoilBlocks();
        }

        private void LogAvailableSoilBlocks()
        {
            if (!config.DebugMode) return;
            var soilBlocks = api.World.Blocks
                .Where(b => b.Code != null && (b.Code.ToString().StartsWith("game:soil-") || b.Code.ToString().StartsWith("game:cob-")))
                .Select(b => b.Code.ToString())
                .ToList();
            api.Logger.Debug($"[{MOD_ID}] [soil] Available soil blocks: {string.Join(", ", soilBlocks)}");
            var noneVariants = soilBlocks.Where(b => b.EndsWith("-none")).ToList();
            if (!noneVariants.Any())
            {
                api.Logger.Warning($"[{MOD_ID}] [soil] No soil or cob blocks with '-none' variant found in block registry");
            }
            else
            {
                api.Logger.Debug($"[{MOD_ID}] [soil] Found '-none' variants: {string.Join(", ", noneVariants)}");
            }
        }

        public Dictionary<BlockPos, PendingBlock> GetPendingBlocks() => pendingBlocks;

        public void QueueFireForProcessing(BlockPos pos)
        {
            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [soil] Processing fire at ({pos.X}, {pos.Y}, {pos.Z})");
            }

            var belowPos = new BlockPos(pos.X, pos.Y - 1, pos.Z);
            var block = api.World.BlockAccessor.GetBlock(belowPos);
            var blockCode = block?.Code?.ToString();

            if (IsSoilBlock(blockCode))
            {
                pendingBlocks[belowPos.Copy()] = new PendingBlock(belowPos.Copy(), blockCode, api.World.ElapsedMilliseconds / 1000f, true);
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [soil] Queued soil block for processing at ({belowPos.X}, {belowPos.Y}, {belowPos.Z}): {blockCode}");
                }
                TryConvertToBarrenSoil(belowPos);
                pendingBlocks.Remove(belowPos);
            }
            else if (blockCode != null && blockCode.StartsWith("game:tallgrass-"))
            {
                var soilPos = new BlockPos(pos.X, pos.Y - 2, pos.Z);
                var soilBlock = api.World.BlockAccessor.GetBlock(soilPos);
                var soilBlockCode = soilBlock?.Code?.ToString();
                if (IsSoilBlock(soilBlockCode))
                {
                    pendingBlocks[soilPos.Copy()] = new PendingBlock(soilPos.Copy(), soilBlockCode, api.World.ElapsedMilliseconds / 1000f, true);
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [soil] Queued soil block below grass tuft for processing at ({soilPos.X}, {soilPos.Y}, {soilPos.Z}): {soilBlockCode}");
                    }
                    TryConvertToBarrenSoil(soilPos);
                    pendingBlocks.Remove(soilPos);
                }
                else if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [soil] Skipped queuing block below grass tuft at ({soilPos.X}, {soilPos.Y}, {soilPos.Z}): not a soil block, code={soilBlockCode ?? "null"}");
                }
            }
            else if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [soil] Skipped queuing block at ({belowPos.X}, {belowPos.Y}, {belowPos.Z}): not a soil block, code={blockCode ?? "null"}");
            }
        }

        public void ProcessPendingBlock(BlockPos pos, bool removeIfProcessed)
        {
            var belowPos = new BlockPos(pos.X, pos.Y - 1, pos.Z);
            if (!pendingBlocks.TryGetValue(belowPos, out var pending))
            {
                if (!pendingBlocks.TryGetValue(pos, out pending))
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [soil] No pending soil block found at ({belowPos.X}, {belowPos.Y}, {belowPos.Z}) or ({pos.X}, {pos.Y}, {pos.Z}) for fire at ({pos.X}, {pos.Y}, {pos.Z})");
                    }
                    TryConvertToBarrenSoil(belowPos);
                    return;
                }
                belowPos = pos;
            }

            var block = api.World.BlockAccessor.GetBlock(belowPos);
            var blockCode = block?.Code?.ToString();
            if (blockCode != pending.BlockCode)
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [soil] Block at ({belowPos.X}, {belowPos.Y}, {belowPos.Z}) changed: expected {pending.BlockCode}, found {blockCode ?? "null"}");
                }
                if (IsSoilBlock(blockCode))
                {
                    TryConvertToBarrenSoil(belowPos);
                }
                if (removeIfProcessed)
                {
                    pendingBlocks.Remove(belowPos);
                }
                return;
            }

            if (!IsSoilBlock(blockCode))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [soil] Block at ({belowPos.X}, {belowPos.Y}, {belowPos.Z}) is not a soil block: {blockCode ?? "null"}");
                }
                if (removeIfProcessed)
                {
                    pendingBlocks.Remove(belowPos);
                }
                return;
            }

            TryConvertToBarrenSoil(belowPos);
            if (removeIfProcessed)
            {
                pendingBlocks.Remove(belowPos);
            }
        }

        private void TryConvertToBarrenSoil(BlockPos pos)
        {
            var block = api.World.BlockAccessor.GetBlock(pos);
            var blockCode = block?.Code?.ToString();
            if (!IsSoilBlock(blockCode))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [soil] Cannot convert block at ({pos.X}, {pos.Y}, {pos.Z}) to -none variant: not a soil block, code={blockCode ?? "null"}");
                }
                return;
            }

            var parts = blockCode.Split('-');
            if (parts.Length < 2)
            {
                return;
            }
            string basePrefix;
            bool isCob = blockCode.StartsWith("game:cob-");
            if (isCob)
            {
                basePrefix = "game:cob";
            }
            else if (blockCode.StartsWith("game:soil-"))
            {
                if (parts.Length < 3)
                {
                    return;
                }
                basePrefix = "game:soil";
            }
            else
            {
                return;
            }

            string fertilityTier = isCob ? "cob" : parts[1];
            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [soil] Block code parts: {string.Join(", ", parts)}, extracted fertility tier: {fertilityTier}, base prefix: {basePrefix}");
            }

            var noneBlockCode = $"{basePrefix}-{fertilityTier}-none";
            var barrenBlock = api.World.GetBlock(new AssetLocation(noneBlockCode));
            if (barrenBlock == null)
            {
                var fallbackCodes = new[] { $"{basePrefix}-{fertilityTier}-no-grass", $"{basePrefix}-{fertilityTier}-free" };
                foreach (var code in fallbackCodes)
                {
                    barrenBlock = api.World.GetBlock(new AssetLocation(code));
                    if (barrenBlock != null)
                    {
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [soil] Found fallback -none soil block: {code}");
                        }
                        noneBlockCode = code;
                        break;
                    }
                }
                if (barrenBlock == null)
                {
                    return;
                }
            }

            api.World.BlockAccessor.SetBlock(barrenBlock.Id, pos);
            api.Logger.Notification($"[{MOD_ID}] [soil] Converted soil block at ({pos.X}, {pos.Y}, {pos.Z}) from {blockCode} to {noneBlockCode}");
            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [soil] Successfully converted block at ({pos.X}, {pos.Y}, {pos.Z}) to -none variant (ID: {barrenBlock.Id}, Code: {noneBlockCode})");
            }

            if (!isCob)
            {
                var newFertilityTier = TryUpgradeFertility(fertilityTier);
                if (newFertilityTier != fertilityTier)
                {
                    var upgradedBlockCode = $"game:soil-{newFertilityTier}-none";
                    var upgradedBlock = api.World.GetBlock(new AssetLocation(upgradedBlockCode));
                    if (upgradedBlock == null)
                    {
                        var upgradedFallbackCodes = new[] { $"game:soil-{newFertilityTier}-no-grass", $"game:soil-{newFertilityTier}-free" };
                        foreach (var code in upgradedFallbackCodes)
                        {
                            upgradedBlock = api.World.GetBlock(new AssetLocation(code));
                            if (upgradedBlock != null)
                            {
                                if (config.DebugMode)
                                {
                                    api.Logger.Debug($"[{MOD_ID}] [soil] Found fallback upgraded soil block: {code}");
                                }
                                upgradedBlockCode = code;
                                break;
                            }
                        }
                        if (upgradedBlock == null)
                        {
                            return;
                        }
                    }
                    api.World.BlockAccessor.SetBlock(upgradedBlock.Id, pos);
                    api.Logger.Notification($"[{MOD_ID}] [soil] Upgraded soil fertility at ({pos.X}, {pos.Y}, {pos.Z}) from {noneBlockCode} to {upgradedBlockCode}");
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [soil] Successfully upgraded fertility at ({pos.X}, {pos.Y}, {pos.Z}) to {upgradedBlockCode}");
                    }
                }
            }
        }

        private string TryUpgradeFertility(string currentTier)
        {
            double roll = rand.NextDouble();
            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [soil] Fertility upgrade roll: {roll}, current tier: {currentTier}");
            }
            double multiTierMod = config.MultiTierModifier;

            switch (currentTier.ToLower())
            {
                case "verylow":
                    if (roll < config.VeryLowToLow)
                        return "low";
                    if (roll < config.VeryLowToLow * multiTierMod * config.LowToMedium)
                        return "medium";
                    if (roll < config.VeryLowToLow * multiTierMod * config.LowToMedium * multiTierMod * config.MediumToHigh)
                        return "compost";
                    if (roll < config.VeryLowToLow * multiTierMod * config.LowToMedium * multiTierMod * config.MediumToHigh * multiTierMod * config.HighToTerraPreta)
                        return "high";
                    break;
                case "low":
                    if (roll < config.LowToMedium)
                        return "medium";
                    if (roll < config.LowToMedium * multiTierMod * config.MediumToHigh)
                        return "compost";
                    if (roll < config.LowToMedium * multiTierMod * config.MediumToHigh * multiTierMod * config.HighToTerraPreta)
                        return "high";
                    break;
                case "medium":
                    if (roll < config.MediumToHigh)
                        return "compost";
                    if (roll < config.MediumToHigh * multiTierMod * config.HighToTerraPreta)
                        return "high";
                    break;
                case "compost":
                    if (roll < config.HighToTerraPreta)
                        return "high";
                    break;
                case "high":
                    break;
                default:
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [soil] Unknown fertility tier: {currentTier}");
                    }
                    break;
            }
            return currentTier;
        }

        private bool IsSoilBlock(string blockCode)
        {
            if (string.IsNullOrEmpty(blockCode)) return false;
            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [soil] Checking block: {blockCode}");
            }
            if (blockCode.StartsWith("game:soil-") || blockCode.StartsWith("game:cob-"))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [soil] Identified block as soil: {blockCode}");
                }
                return true;
            }
            if (config.ConvertForestFloorToSoil && blockCode.StartsWith("game:forestfloor-"))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [soil] Identified block as forest floor (treated as soil due to ConvertForestFloorToSoil): {blockCode}");
                }
                return true;
            }
            return false;
        }

        public void AddPendingBlock(BlockPos pos, PendingBlock block) => pendingBlocks[pos.Copy()] = block;

        public void RemovePendingBlock(BlockPos pos) => pendingBlocks.Remove(pos);

        public void ClearPendingBlocks() => pendingBlocks.Clear();
    }
}