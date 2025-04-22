using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using HarmonyLib;

namespace Pyrogenesis
{
    public class PyrogenesisModSystem : ModSystem
    {
        private ICoreServerAPI serverApi;
        private Harmony harmony;
        private TreeMechanics treeMechanics;
        private SoilMechanics soilMechanics;
        private PyrogenesisConfig config;
        private static readonly List<BlockPos> deferredFirePositions = new();
        private static readonly object deferredLock = new();
        private readonly HashSet<string> processedTrees = new(); // Track processed tree IDs
        private readonly Dictionary<BlockPos, string> logToTreeIdMap = new(); // Map log positions to tree IDs
        private const string MOD_ID = "pyrogenesis";
        private const float MIN_PROCESS_DELAY_SECONDS = 0.5f;

        public override void StartServerSide(ICoreServerAPI api)
        {
            serverApi = api;
            harmony = new Harmony(MOD_ID);
            LoadConfig();
            treeMechanics = new TreeMechanics(api, config);
            soilMechanics = new SoilMechanics(api, config);
            ApplyHarmonyPatches();
            api.Event.GameWorldSave += SavePendingBlocks;
            api.Event.SaveGameLoaded += LoadPendingBlocks;
            api.Event.RegisterGameTickListener(OnGlobalTick, 1000);
        }

        private void LoadConfig()
        {
            config = serverApi.LoadModConfig<PyrogenesisConfig>("pyrogenesis.json") ?? new PyrogenesisConfig();
            serverApi.StoreModConfig(config, "pyrogenesis.json");
            if (config.DebugMode)
            {
                serverApi.Logger.Debug($"[{MOD_ID}] Config loaded, DebugMode: {config.DebugMode}");
            }
        }

        private void ApplyHarmonyPatches()
        {
            var initializeMethod = AccessTools.Method("Vintagestory.API.Common.BlockEntity:Initialize");
            var initializePrefix = new HarmonyMethod(typeof(PyrogenesisModSystem).GetMethod(nameof(InitializePrefix)));
            harmony.Patch(initializeMethod, prefix: initializePrefix);

            var removeMethod = AccessTools.Method("Vintagestory.API.Common.BlockEntity:OnBlockRemoved");
            var removePrefix = new HarmonyMethod(typeof(PyrogenesisModSystem).GetMethod(nameof(OnBlockRemovedPrefix)));
            harmony.Patch(removeMethod, prefix: removePrefix);
        }

        public static void InitializePrefix(BlockEntity __instance, ICoreAPI api)
        {
            if (api == null || __instance == null || __instance.Pos == null) return;

            var pos = __instance.Pos;
            var block = api.World?.BlockAccessor?.GetBlock(pos);
            if (block == null || block.Code == null || block.Code.ToString() != "game:fire") return;

            var mod = api.ModLoader?.GetModSystem<PyrogenesisModSystem>();
            if (mod == null || mod.serverApi == null)
            {
                lock (deferredLock)
                {
                    deferredFirePositions.Add(new BlockPos(pos.X, pos.Y, pos.Z));
                }
                return;
            }

            var logInfo = mod.treeMechanics.FindNearbyLog(api.World, pos);
            if (logInfo.HasValue)
            {
                var firePosCopy = pos.Copy();
                var logPosCopy = logInfo.Value.Pos.Copy();
                var logBlock = api.World.BlockAccessor.GetBlock(logPosCopy);
                string treeId = logBlock.Attributes?["treeFellingGroupCode"].AsString();
                if (string.IsNullOrEmpty(treeId))
                {
                    if (mod.config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [tree] InitializePrefix skipped: Invalid treeId for log at ({logPosCopy.X}, {logPosCopy.Y}, {logPosCopy.Z})");
                    }
                    return;
                }

                string treeKey = null;
                if (mod.logToTreeIdMap.TryGetValue(logPosCopy, out var existingTreeKey))
                {
                    treeKey = existingTreeKey;
                    if (mod.config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [tree] InitializePrefix: Log at ({logPosCopy.X}, {logPosCopy.Y}, {logPosCopy.Z}) mapped to existing tree {treeKey}");
                    }
                }
                else
                {
                    var basePos = mod.treeMechanics.FindTreeBase(api.World, logPosCopy, logBlock.Code.ToString());
                    treeKey = $"{treeId}:{basePos.X},{basePos.Y},{basePos.Z}";
                    mod.logToTreeIdMap[logPosCopy] = treeKey;
                    if (mod.config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [tree] InitializePrefix: Log at ({logPosCopy.X}, {logPosCopy.Y}, {logPosCopy.Z}) mapped to new tree {treeKey}");
                    }
                }

                if (mod.processedTrees.Contains(treeKey))
                {
                    if (mod.config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [tree] InitializePrefix skipped: Tree {treeKey} already processed for fire at ({firePosCopy.X}, {firePosCopy.Y}, {firePosCopy.Z})");
                    }
                    return;
                }

                mod.processedTrees.Add(treeKey);
                mod.treeMechanics.AddFireToLogMapping(firePosCopy, logPosCopy);
                mod.treeMechanics.TryFellTree(firePosCopy);
            }
            mod.soilMechanics.QueueFireForProcessing(pos);
        }

        public static void OnBlockRemovedPrefix(BlockEntity __instance)
        {
            if (__instance == null || __instance.Api == null || __instance.Pos == null) return;

            var pos = __instance.Pos;
            var block = __instance.Api.World?.BlockAccessor?.GetBlock(pos);
            if (block == null || block.Code == null || block.Code.ToString() != "game:fire") return;

            var mod = __instance.Api.ModLoader?.GetModSystem<PyrogenesisModSystem>();
            if (mod == null || mod.serverApi == null)
            {
                lock (deferredLock)
                {
                    deferredFirePositions.Add(new BlockPos(pos.X, pos.Y, pos.Z));
                }
                return;
            }

            mod.soilMechanics.ProcessPendingBlock(pos, true);
            var logInfo = mod.treeMechanics.FindNearbyLog(__instance.Api.World, pos);
            if (logInfo.HasValue)
            {
                var logPosCopy = logInfo.Value.Pos.Copy();
                var logBlock = __instance.Api.World.BlockAccessor.GetBlock(logPosCopy);
                string treeId = logBlock.Attributes?["treeFellingGroupCode"].AsString();
                if (string.IsNullOrEmpty(treeId))
                {
                    if (mod.config.DebugMode)
                    {
                        __instance.Api.Logger.Debug($"[{MOD_ID}] [tree] OnBlockRemovedPrefix skipped: Invalid treeId for log at ({logPosCopy.X}, {logPosCopy.Y}, {logPosCopy.Z})");
                    }
                    return;
                }

                string treeKey = null;
                if (mod.logToTreeIdMap.TryGetValue(logPosCopy, out var existingTreeKey))
                {
                    treeKey = existingTreeKey;
                    if (mod.config.DebugMode)
                    {
                        __instance.Api.Logger.Debug($"[{MOD_ID}] [tree] OnBlockRemovedPrefix: Log at ({logPosCopy.X}, {logPosCopy.Y}, {logPosCopy.Z}) mapped to existing tree {treeKey}");
                    }
                }
                else
                {
                    var basePos = mod.treeMechanics.FindTreeBase(__instance.Api.World, logPosCopy, logBlock.Code.ToString());
                    treeKey = $"{treeId}:{basePos.X},{basePos.Y},{basePos.Z}";
                    mod.logToTreeIdMap[logPosCopy] = treeKey;
                    if (mod.config.DebugMode)
                    {
                        __instance.Api.Logger.Debug($"[{MOD_ID}] [tree] OnBlockRemovedPrefix: Log at ({logPosCopy.X}, {logPosCopy.Y}, {logPosCopy.Z}) mapped to new tree {treeKey}");
                    }
                }

                if (mod.processedTrees.Contains(treeKey))
                {
                    if (mod.config.DebugMode)
                    {
                        __instance.Api.Logger.Debug($"[{MOD_ID}] [tree] OnBlockRemovedPrefix skipped: Tree {treeKey} already processed for fire at ({pos.X}, {pos.Y}, {pos.Z})");
                    }
                }
                else
                {
                    mod.processedTrees.Add(treeKey);
                    mod.treeMechanics.TryFellTree(pos);
                }
            }
            mod.treeMechanics.RemoveFireToLogMapping(pos);
        }

        private void OnGlobalTick(float dt)
        {
            if (serverApi == null) return;

            var currentTime = serverApi.World.ElapsedMilliseconds / 1000f;
            var toRemove = new List<BlockPos>();
            var leafBlocksDestroyed = new List<BlockPos>();
            var logBlocksDestroyed = new List<BlockPos>();
            var treeIdsToClear = new HashSet<string>();

            foreach (var kvp in treeMechanics.GetBurningBlocks())
            {
                var burning = kvp.Value;
                if (currentTime - burning.BurnStartTime >= burning.BurnDuration)
                {
                    var block = serverApi.World.BlockAccessor.GetBlock(burning.Pos);
                    if (block.Code?.ToString() == burning.BlockCode)
                    {
                        serverApi.World.BlockAccessor.BreakBlock(burning.Pos, null, 1f);
                        if (burning.BlockCode.StartsWith("game:leaves-") || burning.BlockCode.StartsWith("game:leavesbranchy-"))
                        {
                            leafBlocksDestroyed.Add(burning.Pos);
                            if (config.DebugMode)
                            {
                                serverApi.Logger.Debug($"[{MOD_ID}] [tree] Destroyed leaf at ({burning.Pos.X}, {burning.Pos.Y}, {burning.Pos.Z}), BlockCode: {burning.BlockCode}, Duration: {burning.BurnDuration}s");
                            }
                        }
                        else if (config.TreeLogPrefixes.Any(prefix => burning.BlockCode.StartsWith($"game:{prefix}-")))
                        {
                            logBlocksDestroyed.Add(burning.Pos);
                            if (config.DebugMode)
                            {
                                serverApi.Logger.Debug($"[{MOD_ID}] [tree] Destroyed log at ({burning.Pos.X}, {burning.Pos.Y}, {burning.Pos.Z}), BlockCode: {burning.BlockCode}, Duration: {burning.BurnDuration}s");
                            }
                        }
                    }
                    toRemove.Add(burning.Pos);
                }
            }

            if (leafBlocksDestroyed.Count > 0)
            {
                serverApi.Logger.Notification($"[{MOD_ID}] [tree] Leaf destruction event: {leafBlocksDestroyed.Count} blocks destroyed");
            }
            if (logBlocksDestroyed.Count > 0)
            {
                serverApi.Logger.Notification($"[{MOD_ID}] [tree] Tree felling event: {logBlocksDestroyed.Count} log blocks destroyed");
                // Find associated tree IDs to clear highlights
                foreach (var pos in logBlocksDestroyed)
                {
                    foreach (var entry in logToTreeIdMap)
                    {
                        if (entry.Key.Equals(pos))
                        {
                            var treeIdParts = entry.Value.Split(':');
                            if (treeIdParts.Length >= 4)
                            {
                                string treeId = treeIdParts[0];
                                treeIdsToClear.Add(treeId);
                            }
                            break;
                        }
                    }
                }
            }

            foreach (var pos in toRemove)
            {
                treeMechanics.RemoveBurningBlock(pos);
            }

            // Clear highlights for destroyed trees
            foreach (var treeId in treeIdsToClear)
            {
                foreach (var firePos in treeMechanics.GetBurningBlocks().Keys)
                {
                    treeMechanics.ClearHighlights(treeId, firePos);
                }
            }
            toRemove.Clear();

            lock (deferredLock)
            {
                foreach (var firePos in deferredFirePositions.ToList())
                {
                    var block = serverApi.World.BlockAccessor.GetBlock(firePos);
                    if (block.Code?.ToString() == "game:fire")
                    {
                        if (soilMechanics.GetPendingBlocks().Values.Any(p => p.Pos.Equals(firePos) && currentTime - p.QueueTime >= MIN_PROCESS_DELAY_SECONDS))
                        {
                            soilMechanics.QueueFireForProcessing(firePos);
                        }
                    }
                    else
                    {
                        soilMechanics.ProcessPendingBlock(firePos, true);
                    }
                    toRemove.Add(firePos);
                }

                foreach (var pos in toRemove)
                {
                    deferredFirePositions.Remove(pos);
                }
                toRemove.Clear();
            }

            foreach (var kvp in soilMechanics.GetPendingBlocks())
            {
                var pending = kvp.Value;
                var firePos = new BlockPos(pending.Pos.X, pending.Pos.Y + 1, pending.Pos.Z);
                var fireBlock = serverApi.World.BlockAccessor.GetBlock(firePos);

                if ((currentTime - pending.QueueTime >= SoilMechanics.PROCESS_TIMEOUT_SECONDS || fireBlock.Code?.ToString() != "game:fire") &&
                    currentTime - pending.QueueTime >= MIN_PROCESS_DELAY_SECONDS)
                {
                    soilMechanics.ProcessPendingBlock(firePos, true);
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var pos in toRemove)
            {
                soilMechanics.RemovePendingBlock(pos);
            }
        }

        private void SavePendingBlocks()
        {
            serverApi.WorldManager.SaveGame.StoreData("pyrogenesis_pending", soilMechanics.GetPendingBlocks());
        }

        private void LoadPendingBlocks()
        {
            soilMechanics.ClearPendingBlocks();
            var data = serverApi.WorldManager.SaveGame.GetData<Dictionary<BlockPos, PendingBlock>>("pyrogenesis_pending");
            if (data != null)
            {
                foreach (var kvp in data)
                {
                    soilMechanics.AddPendingBlock(kvp.Key, kvp.Value);
                }
            }
            processedTrees.Clear(); // Clear processed trees on load to allow reprocessing
            logToTreeIdMap.Clear(); // Clear log to tree ID mapping on load
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(MOD_ID);
            base.Dispose();
        }
    }
}