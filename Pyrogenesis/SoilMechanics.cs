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
            api.Logger.Debug($"[{MOD_ID}] Available soil blocks: {string.Join(", ", soilBlocks)}");
            var noneVariants = soilBlocks.Where(b => b.EndsWith("-none")).ToList();
            if (!noneVariants.Any())
            {
                api.Logger.Warning($"[{MOD_ID}] No soil or cob blocks with '-none' variant found in block registry");
            }
            else
            {
                api.Logger.Debug($"[{MOD_ID}] Found '-none' variants: {string.Join(", ", noneVariants)}");
            }
        }

        public Dictionary<BlockPos, PendingBlock> GetPendingBlocks() => pendingBlocks;

        public void QueueFireForProcessing(BlockPos pos)
        {
            // Log fire detection for debugging
            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] Processing fire at ({pos.X}, {pos.Y}, {pos.Z})");
            }

            // Check block at y-1 (e.g., grass tufts or soil)
            var belowPos = new BlockPos(pos.X, pos.Y - 1, pos.Z);
            var block = api.World.BlockAccessor.GetBlock(belowPos);
            var blockCode = block?.Code?.ToString();

            if (IsSoilBlock(blockCode))
            {
                pendingBlocks[belowPos.Copy()] = new PendingBlock(belowPos.Copy(), blockCode, api.World.ElapsedMilliseconds / 1000f, true);
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] Queued soil block for processing at ({belowPos.X}, {belowPos.Y}, {belowPos.Z}): {blockCode}");
                }
                TryConvertToBarrenSoil(belowPos); // Process immediately to avoid timing issues
                pendingBlocks.Remove(belowPos);
            }
            else if (blockCode != null && blockCode.StartsWith("game:tallgrass-"))
            {
                // Check y-2 for soil if y-1 is grass tuft
                var soilPos = new BlockPos(pos.X, pos.Y - 2, pos.Z);
                var soilBlock = api.World.BlockAccessor.GetBlock(soilPos);
                var soilBlockCode = soilBlock?.Code?.ToString();
                if (IsSoilBlock(soilBlockCode))
                {
                    pendingBlocks[soilPos.Copy()] = new PendingBlock(soilPos.Copy(), soilBlockCode, api.World.ElapsedMilliseconds / 1000f, true);
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] Queued soil block below grass tuft for processing at ({soilPos.X}, {soilPos.Y}, {soilPos.Z}): {soilBlockCode}");
                    }
                    TryConvertToBarrenSoil(soilPos); // Process immediately
                    pendingBlocks.Remove(soilPos);
                }
                else if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] Skipped queuing block below grass tuft at ({soilPos.X}, {soilPos.Y}, {soilPos.Z}): not a soil block, code={soilBlockCode ?? "null"}");
                }
            }
            else if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] Skipped queuing block at ({belowPos.X}, {belowPos.Y}, {belowPos.Z}): not a soil block, code={blockCode ?? "null"}");
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
                        api.Logger.Debug($"[{MOD_ID}] No pending soil block found at ({belowPos.X}, {belowPos.Y}, {belowPos.Z}) or ({pos.X}, {pos.Y}, {pos.Z}) for fire at ({pos.X}, {pos.Y}, {pos.Z})");
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
                    api.Logger.Debug($"[{MOD_ID}] Block at ({belowPos.X}, {belowPos.Y}, {belowPos.Z}) changed: expected {pending.BlockCode}, found {blockCode ?? "null"}");
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
                    api.Logger.Debug($"[{MOD_ID}] Block at ({belowPos.X}, {belowPos.Y}, {belowPos.Z}) is not a soil block: {blockCode ?? "null"}");
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
                    api.Logger.Debug($"[{MOD_ID}] Cannot convert block at ({pos.X}, {pos.Y}, {pos.Z}) to -none variant: not a soil block, code={blockCode ?? "null"}");
                }
                return;
            }

            // Extract base prefix and determine if cob or soil
            var parts = blockCode.Split('-');
            if (parts.Length < 2)
            {
                api.Logger.Error($"[{MOD_ID}] Invalid soil block code at ({pos.X}, {pos.Y}, {pos.Z}): {blockCode}");
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
                    api.Logger.Error($"[{MOD_ID}] Invalid soil block code at ({pos.X}, {pos.Y}, {pos.Z}): {blockCode}");
                    return;
                }
                basePrefix = "game:soil";
            }
            else
            {
                api.Logger.Error($"[{MOD_ID}] Unexpected soil block code at ({pos.X}, {pos.Y}, {pos.Z}): {blockCode}");
                return;
            }

            // Extract fertility tier for soil blocks
            string fertilityTier = isCob ? "cob" : parts[1]; // e.g., "cob" or "verylow", "low", "medium", "compost", "high"
            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] Block code parts: {string.Join(", ", parts)}, extracted fertility tier: {fertilityTier}, base prefix: {basePrefix}");
            }

            // Convert to -none variant
            var noneBlockCode = $"{basePrefix}-{fertilityTier}-none";
            var barrenBlock = api.World.GetBlock(new AssetLocation(noneBlockCode));
            if (barrenBlock == null)
            {
                // Try alternative grass coverage variants
                var fallbackCodes = new[] { $"{basePrefix}-{fertilityTier}-no-grass", $"{basePrefix}-{fertilityTier}-free" };
                foreach (var code in fallbackCodes)
                {
                    barrenBlock = api.World.GetBlock(new AssetLocation(code));
                    if (barrenBlock != null)
                    {
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] Found fallback -none soil block: {code}");
                        }
                        noneBlockCode = code;
                        break;
                    }
                }
                if (barrenBlock == null)
                {
                    api.Logger.Error($"[{MOD_ID}] Failed to find -none soil block (tried: {noneBlockCode}, {string.Join(", ", fallbackCodes)}) for conversion at ({pos.X}, {pos.Y}, {pos.Z})");
                    return;
                }
            }

            // Convert to -none
            api.World.BlockAccessor.SetBlock(barrenBlock.Id, pos);
            api.Logger.Notification($"[{MOD_ID}] Converted soil block at ({pos.X}, {pos.Y}, {pos.Z}) from {blockCode} to {noneBlockCode}");
            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] Successfully converted block at ({pos.X}, {pos.Y}, {pos.Z}) to -none variant (ID: {barrenBlock.Id}, Code: {noneBlockCode})");
            }

            // Apply fertility upgrades only for soil blocks, not cob
            if (!isCob)
            {
                var newFertilityTier = TryUpgradeFertility(fertilityTier);
                if (newFertilityTier != fertilityTier)
                {
                    var upgradedBlockCode = $"game:soil-{newFertilityTier}-none";
                    var upgradedBlock = api.World.GetBlock(new AssetLocation(upgradedBlockCode));
                    if (upgradedBlock == null)
                    {
                        // Try fallback for upgraded block
                        var upgradedFallbackCodes = new[] { $"game:soil-{newFertilityTier}-no-grass", $"game:soil-{newFertilityTier}-free" };
                        foreach (var code in upgradedFallbackCodes)
                        {
                            upgradedBlock = api.World.GetBlock(new AssetLocation(code));
                            if (upgradedBlock != null)
                            {
                                if (config.DebugMode)
                                {
                                    api.Logger.Debug($"[{MOD_ID}] Found fallback upgraded soil block: {code}");
                                }
                                upgradedBlockCode = code;
                                break;
                            }
                        }
                        if (upgradedBlock == null)
                        {
                            api.Logger.Error($"[{MOD_ID}] Failed to find upgraded soil block (tried: {upgradedBlockCode}, {string.Join(", ", upgradedFallbackCodes)}) at ({pos.X}, {pos.Y}, {pos.Z})");
                            return;
                        }
                    }
                    api.World.BlockAccessor.SetBlock(upgradedBlock.Id, pos);
                    api.Logger.Notification($"[{MOD_ID}] Upgraded soil fertility at ({pos.X}, {pos.Y}, {pos.Z}) from {noneBlockCode} to {upgradedBlockCode}");
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] Successfully upgraded fertility at ({pos.X}, {pos.Y}, {pos.Z}) to {upgradedBlockCode}");
                    }
                }
            }
        }

        private string TryUpgradeFertility(string currentTier)
        {
            double roll = rand.NextDouble();
            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] Fertility upgrade roll: {roll}, current tier: {currentTier}");
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
                    // No further upgrades (Terra Preta is the highest)
                    break;
                default:
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] Unknown fertility tier: {currentTier}");
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
                api.Logger.Debug($"[{MOD_ID}] Checking block: {blockCode}");
            }
            if (blockCode.StartsWith("game:soil-") || blockCode.StartsWith("game:cob-"))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] Identified block as soil: {blockCode}");
                }
                return true;
            }
            if (config.ConvertForestFloorToSoil && blockCode.StartsWith("game:forestfloor-"))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] Identified block as forest floor (treated as soil due to ConvertForestFloorToSoil): {blockCode}");
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