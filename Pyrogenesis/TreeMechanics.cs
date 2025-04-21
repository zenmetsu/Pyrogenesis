using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Pyrogenesis;
using Vintagestory.API.Client;

namespace Pyrogenesis
{
    public class TreeMechanics
    {
        private readonly ICoreServerAPI api;
        private readonly PyrogenesisConfig config;
        private readonly Dictionary<BlockPos, BurningBlock> burningBlocks = new();
        private readonly Dictionary<BlockPos, BlockPos> fireToLogMap = new();
        private readonly Dictionary<string, float> treeFellingCooldowns = new();
        private readonly HashSet<string> activeTrees = new();
        private readonly Dictionary<string, HashSet<BlockPos>> treeBlocksByTreeId = new();
        private const string MOD_ID = "pyrogenesis";
        private const float TREE_FELLING_COOLDOWN_SECONDS = 15f;
        private const float HIGHLIGHT_LINE_WIDTH = 1f;

        public TreeMechanics(ICoreServerAPI api, PyrogenesisConfig config)
        {
            this.api = api;
            this.config = config ?? new PyrogenesisConfig();
        }

        public Dictionary<BlockPos, BurningBlock> GetBurningBlocks() => burningBlocks;

        public void RemoveBurningBlock(BlockPos pos)
        {
            burningBlocks.Remove(pos);
            foreach (var treeEntry in treeBlocksByTreeId)
            {
                treeEntry.Value.Remove(pos);
            }
        }

        public void AddFireToLogMapping(BlockPos firePos, BlockPos logPos) => fireToLogMap[firePos] = logPos;

        public void RemoveFireToLogMapping(BlockPos firePos) => fireToLogMap.Remove(firePos);

        private bool IsTreeLog(string blockCode, PyrogenesisConfig config)
        {
            if (string.IsNullOrEmpty(blockCode)) return false;
            config = config ?? new PyrogenesisConfig();
            bool isLog = config.TreeLogPrefixes.Any(prefix => blockCode.StartsWith($"game:{prefix}-"));
            if (isLog && config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] Identified block as log: {blockCode}");
            }
            return isLog;
        }

        private static bool IsTreeLeaf(string blockCode, PyrogenesisConfig config)
        {
            if (string.IsNullOrEmpty(blockCode)) return false;
            if (!blockCode.StartsWith("game:leaves-")) return false;
            // Use instance method IsTreeLog via temporary instance
            if (new TreeMechanics(null, config).IsTreeLog(blockCode, config))
            {
                return false; // Prevent logs from being misidentified as leaves
            }
            return true;
        }

        public (BlockPos Pos, int Id, string Name)? FindNearbyLog(IWorldAccessor world, BlockPos firePos)
        {
            // First, check for logs at or below the fire's Y-level (prioritizing base of Redwood trees)
            for (int yOffset = 0; yOffset >= -3; yOffset--)
            {
                var checkPos = new BlockPos(firePos.X, firePos.Y + yOffset, firePos.Z);
                var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                var checkBlockCode = checkBlock?.Code?.ToString();
                if (checkBlock != null && !string.IsNullOrEmpty(checkBlockCode) && IsTreeLog(checkBlockCode, config))
                {
                    var basePos = FindTreeBase(world, checkPos, checkBlockCode);
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] Found log at ({checkPos.X}, {checkPos.Y}, {checkPos.Z}): {checkBlockCode}, base at ({basePos.X}, {basePos.Y}, {basePos.Z})");
                    }
                    return (basePos.Copy(), checkBlock.Id, checkBlockCode);
                }
            }

            // Then, search a 5x5x5 area around the fire for logs
            for (int xOffset = -2; xOffset <= 2; xOffset++)
            {
                for (int yOffset = -2; yOffset <= 2; yOffset++)
                {
                    for (int zOffset = -2; zOffset <= 2; zOffset++)
                    {
                        var checkPos = new BlockPos(firePos.X + xOffset, firePos.Y + yOffset, firePos.Z + zOffset);
                        var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                        var checkBlockCode = checkBlock?.Code?.ToString();
                        if (checkBlock != null && !string.IsNullOrEmpty(checkBlockCode) && IsTreeLog(checkBlockCode, config))
                        {
                            var basePos = FindTreeBase(world, checkPos, checkBlockCode);
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] Found log at ({checkPos.X}, {checkPos.Y}, {checkPos.Z}): {checkBlockCode}, base at ({basePos.X}, {basePos.Y}, {basePos.Z})");
                            }
                            return (basePos.Copy(), checkBlock.Id, checkBlockCode);
                        }
                    }
                }
            }

            // Finally, search for leaves to find associated logs
            for (int xOffset = -2; xOffset <= 2; xOffset++)
            {
                for (int yOffset = -2; yOffset <= 2; yOffset++)
                {
                    for (int zOffset = -2; zOffset <= 2; zOffset++)
                    {
                        var checkPos = new BlockPos(firePos.X + xOffset, firePos.Y + yOffset, firePos.Z + zOffset);
                        var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                        var checkBlockCode = checkBlock?.Code?.ToString();
                        if (checkBlock != null && !string.IsNullOrEmpty(checkBlockCode) && IsTreeLeaf(checkBlockCode, config))
                        {
                            var logPos = FindTreeBaseFromLeaf(world, checkPos, checkBlockCode);
                            if (logPos != null)
                            {
                                var logBlock = world.BlockAccessor.GetBlock(logPos);
                                var logBlockCode = logBlock.Code?.ToString();
                                if (logBlockCode != null && IsTreeLog(logBlockCode, config))
                                {
                                    if (config.DebugMode)
                                    {
                                        api.Logger.Debug($"[{MOD_ID}] Found leaf at ({checkPos.X}, {checkPos.Y}, {checkPos.Z}): {checkBlockCode}, associated log at ({logPos.X}, {logPos.Y}, {logPos.Z}): {logBlockCode}");
                                    }
                                    return (logPos.Copy(), logBlock.Id, logBlockCode);
                                }
                            }
                        }
                    }
                }
            }

            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] No log or leaf found near fire at ({firePos.X}, {firePos.Y}, {firePos.Z})");
            }
            return null;
        }

        private BlockPos FindTreeBase(IWorldAccessor world, BlockPos logPos, string logBlockCode)
        {
            // Extract group code, accounting for directional suffixes (e.g., nw, ne, sw, se)
            var parts = logBlockCode.Split('-');
            string treeFellingGroupCode = parts.Length >= 3 ? parts[2].Split(':').Last() : null;
            if (treeFellingGroupCode == null)
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] No treeFellingGroupCode for log at ({logPos.X}, {logPos.Y}, {logPos.Z}): {logBlockCode}");
                }
                return logPos;
            }

            var currentPos = logPos.Copy();
            var lowestPos = logPos.Copy();

            for (int y = logPos.Y - 1; y >= 0; y--)
            {
                var checkPos = new BlockPos(logPos.X, y, logPos.Z);
                var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                var checkBlockCode = checkBlock?.Code?.ToString();
                if (checkBlockCode == null || !IsTreeLog(checkBlockCode, config)) break;

                string checkGroupCode = checkBlock.Attributes?["treeFellingGroupCode"].AsString();
                if (checkGroupCode == null || !checkGroupCode.EndsWith(treeFellingGroupCode))
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] Group code mismatch in FindTreeBase at ({checkPos.X}, {checkPos.Y}, {checkPos.Z}): expected ending {treeFellingGroupCode}, got {checkGroupCode}");
                    }
                    break;
                }

                lowestPos = checkPos.Copy();
            }

            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] Found tree base at ({lowestPos.X}, {lowestPos.Y}, {lowestPos.Z}) for log at ({logPos.X}, {logPos.Y}, {logPos.Z}): {logBlockCode}");
            }
            return lowestPos;
        }

        private BlockPos FindTreeBaseFromLeaf(IWorldAccessor world, BlockPos leafPos, string leafBlockCode)
        {
            string leafGroupCode = leafBlockCode.Split('-').Length >= 3 ? leafBlockCode.Split('-')[2].Split(':').Last() : null;
            if (leafGroupCode == null || !int.TryParse(leafGroupCode[0].ToString(), out int leafGroup))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] Invalid leafGroupCode at ({leafPos.X}, {leafPos.Y}, {leafPos.Z}): {leafBlockCode}, groupCode: {leafGroupCode}");
                }
                return null;
            }

            string treeFellingGroupCode = leafGroupCode.Substring(1);

            for (int y = leafPos.Y; y >= 0; y--)
            {
                for (int xOffset = -2; xOffset <= 2; xOffset++)
                {
                    for (int zOffset = -2; zOffset <= 2; zOffset++)
                    {
                        var checkPos = new BlockPos(leafPos.X + xOffset, y, leafPos.Z + zOffset);
                        var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                        var checkBlockCode = checkBlock?.Code?.ToString();
                        if (checkBlockCode == null || !IsTreeLog(checkBlockCode, config)) continue;

                        string checkGroupCode = checkBlock.Attributes?["treeFellingGroupCode"].AsString();
                        if (checkGroupCode == treeFellingGroupCode)
                        {
                            var basePos = FindTreeBase(world, checkPos, checkBlockCode);
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] Found log for leaf at ({leafPos.X}, {leafPos.Y}, {leafPos.Z}): log at ({checkPos.X}, {checkPos.Y}, {checkPos.Z}), base at ({basePos.X}, {basePos.Y}, {basePos.Z})");
                            }
                            return basePos;
                        }
                    }
                }
            }

            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] No log found for leaf at ({leafPos.X}, {leafPos.Y}, {leafPos.Z}): {leafBlockCode}");
            }
            return null;
        }

        private bool CanFellTree(BlockPos logPos, string treeId)
        {
            if (logPos == null || string.IsNullOrEmpty(treeId))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] Cannot fell tree: logPos={logPos == null}, treeId={treeId}");
                }
                return false;
            }
            if (activeTrees.Contains(treeId))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] Tree {treeId} is already being felled");
                }
                return false;
            }
            if (treeFellingCooldowns.TryGetValue(treeId, out var lastFelledTime))
            {
                var canFell = api.World.ElapsedMilliseconds / 1000f - lastFelledTime >= TREE_FELLING_COOLDOWN_SECONDS;
                if (!canFell && config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] Tree felling blocked by cooldown for tree {treeId}");
                }
                return canFell;
            }
            return true;
        }

        private void MarkTreeFelled(BlockPos logPos, string treeId)
        {
            if (logPos != null && !string.IsNullOrEmpty(treeId))
            {
                treeFellingCooldowns[treeId] = api.World.ElapsedMilliseconds / 1000f;
                activeTrees.Add(treeId);
                api.World.RegisterCallback((dt) =>
                {
                    activeTrees.Remove(treeId);
                }, (int)(TREE_FELLING_COOLDOWN_SECONDS * 1000));
            }
        }

        public void TryFellTree(BlockPos firePos)
        {
            try
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] Entering TryFellTree for fire at ({firePos.X}, {firePos.Y}, {firePos.Z})");
                }
                if (firePos == null)
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] Fire position is null in TryFellTree");
                    }
                    return;
                }

                if (!fireToLogMap.TryGetValue(firePos, out var logPos))
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] No log mapping found for fire at ({firePos.X}, {firePos.Y}, {firePos.Z})");
                    }
                    return;
                }

                var logBlock = api.World.BlockAccessor.GetBlock(logPos);
                var logBlockCode = logBlock.Code?.ToString();
                if (logBlockCode == null || !IsTreeLog(logBlockCode, config))
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] Invalid log at ({logPos.X}, {logPos.Y}, {logPos.Z}): {logBlockCode ?? "null"}");
                    }
                    return;
                }

                string treeId = logBlock.Attributes?["treeFellingGroupCode"].AsString();
                if (string.IsNullOrEmpty(treeId))
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] No tree ID for log at ({logPos.X}, {logPos.Y}, {logPos.Z})");
                    }
                    return;
                }

                if (!CanFellTree(logPos, treeId))
                {
                    return;
                }

                if (api.World == null)
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] World is null in TryFellTree for log at ({logPos.X}, {logPos.Y}, {logPos.Z})");
                    }
                    return;
                }

                var (treeBlocks, logPositions, leafPositions) = FindTree(api.World, logPos);
                if (treeBlocks.Count == 0)
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] No tree blocks found for log at ({logPos.X}, {logPos.Y}, {logPos.Z})");
                    }
                    return;
                }

                if (!treeBlocksByTreeId.ContainsKey(treeId))
                {
                    treeBlocksByTreeId[treeId] = new HashSet<BlockPos>();
                }
                var treeBlockSet = treeBlocksByTreeId[treeId];
                int newBlocksAdded = 0;
                foreach (var blockPos in treeBlocks)
                {
                    if (treeBlockSet.Add(blockPos.Copy()))
                    {
                        newBlocksAdded++;
                    }
                }

                int logCount = 0;
                int leafCount = 0;
                var currentTime = api.World.ElapsedMilliseconds / 1000f;
                var logDetails = new List<string>();
                var leafDetails = new List<string>();

                foreach (var blockPos in treeBlocks)
                {
                    if (!burningBlocks.ContainsKey(blockPos))
                    {
                        var block = api.World.BlockAccessor.GetBlock(blockPos);
                        var blockCode = block.Code?.ToString();
                        if (blockCode == null || block.Id == 0) continue;

                        if (IsTreeLog(blockCode, config))
                        {
                            logCount++;
                            logDetails.Add($"({blockPos.X}, {blockPos.Y}, {blockPos.Z}): {blockCode}");
                        }
                        else if (IsTreeLeaf(blockCode, config))
                        {
                            leafCount++;
                            leafDetails.Add($"({blockPos.X}, {blockPos.Y}, {blockPos.Z}): {blockCode}");
                        }
                        else if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] Block at ({blockPos.X}, {blockPos.Y}, {blockPos.Z}) is neither log nor leaf: {blockCode}");
                        }

                        burningBlocks[blockPos.Copy()] = new BurningBlock(blockPos.Copy(), blockCode, currentTime, config);
                    }
                }

                if (logCount > 0 || leafCount > 0)
                {
                    api.Logger.Notification($"[{MOD_ID}] Queued {logCount} new logs and {leafCount} new leaves for burning for tree {treeId} at ({logPos.X}, {logPos.Y}, {logPos.Z}), {newBlocksAdded} new blocks added to tree");
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] Logs queued: {string.Join(", ", logDetails)}");
                        api.Logger.Debug($"[{MOD_ID}] Leaves queued: {string.Join(", ", leafDetails)}");
                    }

                    if (config.DebugMode)
                    {
                        int groupId = (treeId + firePos.ToString()).GetHashCode();

                        // Highlight fire block (using red for testing)
                        if (firePos != null)
                        {
                            var firePositions = new List<BlockPos> { firePos.Copy() };
                            var fireColors = new List<int> { unchecked((int)0xFFFF0000) }; // Red (ARGB) for testing
                            api.Logger.Debug($"[{MOD_ID}] Preparing to highlight {firePositions.Count} fire blocks at ({firePos.X}, {firePos.Y}, {firePos.Z}) in red (0xFFFF0000) with groupId {groupId}");
                            foreach (var player in api.World.AllOnlinePlayers)
                            {
                                api.World.HighlightBlocks(player, groupId, firePositions, fireColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
                                api.Logger.Debug($"[{MOD_ID}] Highlighted {firePositions.Count} fire blocks in red (0xFFFF0000) for player {player.PlayerName} with groupId {groupId}");
                            }
                        }
                        else
                        {
                            api.Logger.Debug($"[{MOD_ID}] Fire position is null, skipping fire highlight");
                        }

                        // Highlight logs (using blue for testing)
                        if (logPositions.Count > 0)
                        {
                            var logColors = Enumerable.Repeat(unchecked((int)0xFF0000FF), logPositions.Count).ToList(); // Blue (ARGB) for testing
                            api.Logger.Debug($"[{MOD_ID}] Preparing to highlight {logPositions.Count} log blocks in blue (0xFF0000FF) with groupId {groupId}: {string.Join(", ", logPositions.Select(p => $"({p.X}, {p.Y}, {p.Z})"))}");
                            foreach (var player in api.World.AllOnlinePlayers)
                            {
                                api.World.HighlightBlocks(player, groupId, logPositions.Select(p => p.Copy()).ToList(), logColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
                                api.Logger.Debug($"[{MOD_ID}] Highlighted {logPositions.Count} log blocks in blue (0xFF0000FF) for player {player.PlayerName} with groupId {groupId}");
                            }
                        }
                        else
                        {
                            api.Logger.Debug($"[{MOD_ID}] No log positions to highlight for tree {treeId}");
                        }

                        // Highlight leaves
                        if (leafPositions.Count > 0)
                        {
                            var leafColors = Enumerable.Repeat(unchecked((int)0xFF00FF00), leafPositions.Count).ToList(); // Lime Green (ARGB)
                            api.Logger.Debug($"[{MOD_ID}] Preparing to highlight {leafPositions.Count} leaf blocks in lime green (0xFF00FF00) with groupId {groupId}: {string.Join(", ", leafPositions.Select(p => $"({p.X}, {p.Y}, {p.Z})"))}");
                            foreach (var player in api.World.AllOnlinePlayers)
                            {
                                api.World.HighlightBlocks(player, groupId, leafPositions.Select(p => p.Copy()).ToList(), leafColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
                                api.Logger.Debug($"[{MOD_ID}] Highlighted {leafPositions.Count} leaf blocks in lime green (0xFF00FF00) for player {player.PlayerName} with groupId {groupId}");
                            }
                        }
                        else
                        {
                            api.Logger.Debug($"[{MOD_ID}] No leaf positions to highlight for tree {treeId}");
                        }
                    }

                    MarkTreeFelled(logPos, treeId);
                }
                else if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] No new logs or leaves queued for tree {treeId} at ({logPos.X}, {logPos.Y}, {logPos.Z}), {newBlocksAdded} new blocks added to tree");
                }

                fireToLogMap.Remove(firePos);
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[{MOD_ID}] Exception in TryFellTree for fire at ({firePos.X}, {firePos.Y}, {firePos.Z}): {ex.Message}\n{ex.StackTrace}");
            }
        }

        private (List<BlockPos> TreeBlocks, List<BlockPos> LogPositions, List<BlockPos> LeafPositions) FindTree(IWorldAccessor world, BlockPos startPos)
        {
            try
            {
                if (world == null || startPos == null)
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] Invalid input in FindTree: world={world == null}, startPos={startPos == null}");
                    }
                    return (new List<BlockPos>(), new List<BlockPos>(), new List<BlockPos>());
                }

                var startBlock = world.BlockAccessor.GetBlock(startPos);
                var startBlockCode = startBlock.Code?.ToString();
                if (startBlockCode == null || !IsTreeLog(startBlockCode, config))
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] Starting block at ({startPos.X}, {startPos.Y}, {startPos.Z}) is not a log: {startBlockCode ?? "null"}");
                    }
                    return (new List<BlockPos>(), new List<BlockPos>(), new List<BlockPos>());
                }

                string treeFellingGroupCode = startBlock.Attributes?["treeFellingGroupCode"].AsString();
                int spreadIndex = NeedsSpreadIndex(startBlockCode) ? startBlock.Attributes?["treeFellingGroupSpreadIndex"].AsInt(0) ?? 0 : -1;
                if (string.IsNullOrEmpty(treeFellingGroupCode))
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] Invalid tree attributes at ({startPos.X}, {startPos.Y}, {startPos.Z}): groupCode={treeFellingGroupCode}, spreadIndex={spreadIndex}");
                    }
                    return (new List<BlockPos>(), new List<BlockPos>(), new List<BlockPos>());
                }

                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] Starting tree search at ({startPos.X}, {startPos.Y}, {startPos.Z}): {startBlockCode}, groupCode={treeFellingGroupCode}, spreadIndex={spreadIndex}");
                }

                var queue = new Queue<(BlockPos Pos, int SpreadIndex)>();
                var leafQueue = new Queue<(BlockPos Pos, int SpreadIndex, string GroupCode)>();
                var visited = new HashSet<BlockPos>();
                var treeBlocks = new List<BlockPos>();
                var logPositions = new List<BlockPos>();
                var leafPositions = new List<BlockPos>();
                var leafGroupCounts = new Dictionary<string, int>();

                queue.Enqueue((startPos.Copy(), spreadIndex));
                visited.Add(startPos);

                while (queue.Count > 0)
                {
                    var (currentPos, currentSpreadIndex) = queue.Dequeue();
                    var block = world.BlockAccessor.GetBlock(currentPos);
                    var blockCode = block.Code?.ToString();

                    if (blockCode == null || block.Id == 0) continue;

                    string ngCode = block.Attributes?["treeFellingGroupCode"].AsString();
                    int nSpreadIndex = NeedsSpreadIndex(blockCode) ? block.Attributes?["treeFellingGroupSpreadIndex"].AsInt(0) ?? 0 : -1;

                    if (ngCode != null && ngCode.EndsWith(treeFellingGroupCode) && (nSpreadIndex <= currentSpreadIndex || !NeedsSpreadIndex(blockCode)))
                    {
                        treeBlocks.Add(currentPos.Copy());
                        if (IsTreeLog(blockCode, config))
                        {
                            logPositions.Add(currentPos.Copy());
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] Added log at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}): {blockCode}, groupCode={ngCode}, spreadIndex={nSpreadIndex}");
                            }
                        }

                        for (int i = 0; i < Vec3i.DirectAndIndirectNeighbours.Length; i++)
                        {
                            var facing = Vec3i.DirectAndIndirectNeighbours[i];
                            var neighborPos = new BlockPos(currentPos.X + facing.X, currentPos.Y + facing.Y, currentPos.Z + facing.Z);

                            float horDist = GameMath.Sqrt(neighborPos.HorDistanceSqTo(startPos.X, startPos.Z));
                            float vertDist = Math.Abs(neighborPos.Y - startPos.Y);
                            if (horDist > config.LeafSearchRadius) continue;
                            if (visited.Contains(neighborPos)) continue;

                            var neighborBlock = world.BlockAccessor.GetBlock(neighborPos);
                            var neighborCode = neighborBlock.Code?.ToString();
                            if (neighborCode == null || neighborBlock.Id == 0)
                            {
                                if (config.DebugMode)
                                {
                                    api.Logger.Debug($"[{MOD_ID}] Skipping block at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}): null or air");
                                }
                                continue;
                            }

                            string nNgCode = neighborBlock.Attributes?["treeFellingGroupCode"].AsString();
                            int nNSpreadIndex = NeedsSpreadIndex(neighborCode) ? neighborBlock.Attributes?["treeFellingGroupSpreadIndex"].AsInt(0) ?? 0 : -1;

                            if (nNgCode != null && nNgCode.EndsWith(treeFellingGroupCode) && (nNSpreadIndex <= currentSpreadIndex || !NeedsSpreadIndex(neighborCode)))
                            {
                                queue.Enqueue((neighborPos.Copy(), nNSpreadIndex));
                                visited.Add(neighborPos);
                            }
                            else if (IsTreeLeaf(neighborCode, config) && nNgCode != null && nNgCode.EndsWith(treeFellingGroupCode))
                            {
                                leafQueue.Enqueue((neighborPos.Copy(), nNSpreadIndex, nNgCode));
                                visited.Add(neighborPos);
                                leafGroupCounts[nNgCode] = leafGroupCounts.GetValueOrDefault(nNgCode, 0) + 1;
                                if (config.DebugMode)
                                {
                                    api.Logger.Debug($"[{MOD_ID}] Found leaf at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}): {neighborCode}, groupCode={nNgCode}, horDist={horDist}, vertDist={vertDist}");
                                }
                            }
                            else if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] Skipping block at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}): code={neighborCode}, groupCode={nNgCode}, expected ending={treeFellingGroupCode}, spreadIndex={nNSpreadIndex}, horDist={horDist}, vertDist={vertDist}");
                            }
                        }
                    }
                }

                while (leafQueue.Count > 0)
                {
                    var (currentPos, currentSpreadIndex, currentGroupCode) = leafQueue.Dequeue();
                    var block = world.BlockAccessor.GetBlock(currentPos);
                    var blockCode = block.Code?.ToString();

                    if (blockCode == null || block.Id == 0) continue;

                    string ngCode = block.Attributes?["treeFellingGroupCode"].AsString();
                    int nSpreadIndex = block.Attributes?["treeFellingGroupSpreadIndex"].AsInt(0) ?? 0;

                    if (ngCode != null && ngCode.EndsWith(treeFellingGroupCode))
                    {
                        treeBlocks.Add(currentPos.Copy());
                        if (IsTreeLeaf(blockCode, config))
                        {
                            leafPositions.Add(currentPos.Copy());
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] Added leaf at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}): {blockCode}, groupCode={ngCode}");
                            }
                        }
                        else if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] Expected leaf block at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}) but found: {blockCode}, groupCode={ngCode}");
                        }

                        for (int i = 0; i < Vec3i.DirectAndIndirectNeighbours.Length; i++)
                        {
                            var facing = Vec3i.DirectAndIndirectNeighbours[i];
                            var neighborPos = new BlockPos(currentPos.X + facing.X, currentPos.Y + facing.Y, currentPos.Z + facing.Z);

                            float horDist = GameMath.Sqrt(neighborPos.HorDistanceSqTo(startPos.X, startPos.Z));
                            float vertDist = Math.Abs(neighborPos.Y - startPos.Y);
                            if (horDist > config.LeafSearchRadius) continue;
                            if (visited.Contains(neighborPos)) continue;

                            var neighborBlock = world.BlockAccessor.GetBlock(neighborPos);
                            var neighborCode = neighborBlock.Code?.ToString();
                            if (neighborCode == null || neighborBlock.Id == 0) continue;

                            string nNgCode = neighborBlock.Attributes?["treeFellingGroupCode"].AsString();
                            int nNSpreadIndex = block.Attributes?["treeFellingGroupSpreadIndex"].AsInt(0) ?? 0;

                            if (nNgCode != null && nNgCode.EndsWith(treeFellingGroupCode))
                            {
                                leafQueue.Enqueue((neighborPos.Copy(), nNSpreadIndex, nNgCode));
                                visited.Add(neighborPos);
                                leafGroupCounts[nNgCode] = leafGroupCounts.GetValueOrDefault(nNgCode, 0) + 1;
                            }
                            else if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] Skipping leaf block at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}): code={neighborCode}, groupCode={nNgCode}, expected ending={treeFellingGroupCode}, spreadIndex={nNSpreadIndex}, horDist={horDist}, vertDist={vertDist}");
                            }
                        }
                    }
                    else if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] Skipping leaf block at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}): code={blockCode}, groupCode={ngCode}, expected ending={treeFellingGroupCode}");
                    }
                }

                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] Found leaf groups for tree {treeFellingGroupCode}: {string.Join(", ", leafGroupCounts.Select(kv => $"{kv.Key}: {kv.Value}"))}");
                    api.Logger.Debug($"[{MOD_ID}] Found {treeBlocks.Count} tree blocks ({logPositions.Count} logs, {leafPositions.Count} leaves) for tree {treeFellingGroupCode} at ({startPos.X}, {startPos.Y}, {startPos.Z})");
                }
                return (treeBlocks, logPositions, leafPositions);
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[{MOD_ID}] Exception in FindTree for startPos ({startPos.X}, {startPos.Y}, {startPos.Z}): {ex.Message}\n{ex.StackTrace}");
                return (new List<BlockPos>(), new List<BlockPos>(), new List<BlockPos>());
            }
        }

        private bool NeedsSpreadIndex(string blockCode)
        {
            return blockCode.StartsWith("game:logsection-");
        }
    }
}