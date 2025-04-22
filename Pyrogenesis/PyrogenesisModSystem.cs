using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using HarmonyLib;
using Pyrogenesis;

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
                mod.treeMechanics.TryFellTree(pos);
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

            foreach (var kvp in treeMechanics.GetBurningBlocks())
            {
                var burning = kvp.Value;
                if (currentTime - burning.BurnStartTime >= burning.BurnDuration)
                {
                    var block = serverApi.World.BlockAccessor.GetBlock(burning.Pos);
                    if (block.Code?.ToString() == burning.BlockCode)
                    {
                        serverApi.World.BlockAccessor.BreakBlock(burning.Pos, null, 1f);
                        if (burning.BlockCode.StartsWith("game:leaves-"))
                        {
                            leafBlocksDestroyed.Add(burning.Pos);
                        }
                        else if (config.TreeLogPrefixes.Any(prefix => burning.BlockCode.StartsWith($"game:{prefix}-")))
                        {
                            logBlocksDestroyed.Add(burning.Pos);
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
            }

            foreach (var pos in toRemove)
            {
                treeMechanics.RemoveBurningBlock(pos);
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
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(MOD_ID);
            base.Dispose();
        }
    }
}