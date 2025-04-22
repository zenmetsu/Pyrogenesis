using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
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
        private readonly Dictionary<string, BlockPos> treeBaseByTreeId = new(); // Track base block for deduplication
        private const string MOD_ID = "pyrogenesis";
        private const float TREE_FELLING_COOLDOWN_SECONDS = 15f;
        private const float HIGHLIGHT_LINE_WIDTH = 1f;

        // Caches for optimization
        private readonly Dictionary<string, string> groupCodeCache = new Dictionary<string, string>();
        private readonly Dictionary<string, bool> spreadIndexCache = new Dictionary<string, bool>();

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

        public void ClearHighlights(string treeId, BlockPos firePos)
        {
            if (!config.DebugMode) return;
            int groupId = (treeId + firePos.ToString()).GetHashCode();
            foreach (var player in api.World.AllOnlinePlayers)
            {
                api.World.HighlightBlocks(player, groupId + 1, new List<BlockPos>(), EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube);
                api.World.HighlightBlocks(player, groupId + 2, new List<BlockPos>(), EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube);
                api.World.HighlightBlocks(player, groupId + 3, new List<BlockPos>(), EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube);
            }
        }

        public void AddFireToLogMapping(BlockPos firePos, BlockPos logPos) => fireToLogMap[firePos] = logPos;

        public void RemoveFireToLogMapping(BlockPos firePos) => fireToLogMap.Remove(firePos);

        private bool IsTreeLog(string blockCode)
        {
            if (string.IsNullOrEmpty(blockCode)) return false;
            return config.TreeLogPrefixes.Any(prefix => blockCode.StartsWith($"game:{prefix}-"));
        }

        private bool IsTreeLeaf(string blockCode)
        {
            if (string.IsNullOrEmpty(blockCode)) return false;
            if (!blockCode.StartsWith("game:leaves-") && !blockCode.StartsWith("game:leavesbranchy-")) return false;
            if (IsTreeLog(blockCode)) return false; // Ensure logs are not mistaken for leaves
            return true;
        }

        public (BlockPos Pos, int Id, string Name)? FindNearbyLog(IWorldAccessor world, BlockPos firePos)
        {
            for (int yOffset = 0; yOffset >= -3; yOffset--)
            {
                var checkPos = new BlockPos(firePos.X, firePos.Y + yOffset, firePos.Z);
                var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                var checkBlockCode = checkBlock?.Code?.ToString();
                if (checkBlock != null && !string.IsNullOrEmpty(checkBlockCode) && IsTreeLog(checkBlockCode))
                {
                    var basePos = FindTreeBase(world, checkPos, checkBlockCode);
                    return (basePos.Copy(), checkBlock.Id, checkBlockCode);
                }
            }

            for (int xOffset = -2; xOffset <= 2; xOffset++)
            {
                for (int yOffset = -2; yOffset <= 2; yOffset++)
                {
                    for (int zOffset = -2; zOffset <= 2; zOffset++)
                    {
                        var checkPos = new BlockPos(firePos.X + xOffset, firePos.Y + yOffset, firePos.Z + zOffset);
                        var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                        var checkBlockCode = checkBlock?.Code?.ToString();
                        if (checkBlock != null && !string.IsNullOrEmpty(checkBlockCode) && IsTreeLog(checkBlockCode))
                        {
                            var basePos = FindTreeBase(world, checkPos, checkBlockCode);
                            return (basePos.Copy(), checkBlock.Id, checkBlockCode);
                        }
                    }
                }
            }

            for (int xOffset = -2; xOffset <= 2; xOffset++)
            {
                for (int yOffset = -2; yOffset <= 2; yOffset++)
                {
                    for (int zOffset = -2; zOffset <= 2; zOffset++)
                    {
                        var checkPos = new BlockPos(firePos.X + xOffset, firePos.Y + yOffset, firePos.Z + zOffset);
                        var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                        var checkBlockCode = checkBlock?.Code?.ToString();
                        if (checkBlock != null && !string.IsNullOrEmpty(checkBlockCode) && IsTreeLeaf(checkBlockCode))
                        {
                            var logPos = FindTreeBaseFromLeaf(world, checkPos, checkBlockCode);
                            if (logPos != null)
                            {
                                var logBlock = world.BlockAccessor.GetBlock(logPos);
                                var logBlockCode = logBlock.Code?.ToString();
                                if (logBlockCode != null && IsTreeLog(logBlockCode))
                                {
                                    return (logPos.Copy(), logBlock.Id, logBlockCode);
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        public BlockPos FindTreeBase(IWorldAccessor world, BlockPos logPos, string logBlockCode)
        {
            var parts = logBlockCode.Split('-');
            string treeFellingGroupCode = parts.Length >= 3 ? parts[2].Split(':').Last() : null;
            if (treeFellingGroupCode == null)
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] FindTreeBase: No group code found for block at ({logPos.X}, {logPos.Y}, {logPos.Z}), returning original position");
                }
                return logPos;
            }

            var currentPos = logPos.Copy();
            var lowestPos = logPos.Copy();
            var candidates = new List<BlockPos> { lowestPos };

            for (int y = logPos.Y - 1; y >= 0; y--)
            {
                var checkPos = new BlockPos(logPos.X, y, logPos.Z);
                var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                var checkBlockCode = checkBlock?.Code?.ToString();
                if (checkBlockCode == null || !IsTreeLog(checkBlockCode)) break;

                string checkGroupCode = checkBlock.Attributes?["treeFellingGroupCode"].AsString();
                if (checkGroupCode == null || !checkGroupCode.EndsWith(treeFellingGroupCode))
                {
                    break;
                }

                lowestPos = checkPos.Copy();
                candidates.Add(lowestPos);
            }

            // For 2x2 trunks, check adjacent blocks at the lowest Y
            var baseY = candidates.Min(p => p.Y);
            var baseCandidates = candidates.Where(p => p.Y == baseY).ToList();
            foreach (var pos in candidates.Where(p => p.Y == baseY))
            {
                for (int xOffset = -1; xOffset <= 1; xOffset++)
                {
                    for (int zOffset = -1; zOffset <= 1; zOffset++)
                    {
                        if (xOffset == 0 && zOffset == 0) continue;
                        var checkPos = new BlockPos(pos.X + xOffset, pos.Y, pos.Z + zOffset);
                        var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                        var checkBlockCode = checkBlock?.Code?.ToString();
                        if (checkBlockCode == null || !IsTreeLog(checkBlockCode)) continue;

                        string checkGroupCode = checkBlock.Attributes?["treeFellingGroupCode"].AsString();
                        if (checkGroupCode == treeFellingGroupCode)
                        {
                            baseCandidates.Add(checkPos.Copy());
                        }
                    }
                }
            }

            // Select northwest-most base block
            var finalBase = baseCandidates.OrderBy(p => p.Z).ThenBy(p => p.X).First();
            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [tree] FindTreeBase for ({logPos.X}, {logPos.Y}, {logPos.Z}): Base position ({finalBase.X}, {finalBase.Y}, {finalBase.Z}), Candidates: {baseCandidates.Count}");
            }
            return finalBase;
        }

        private BlockPos FindTreeBaseFromLeaf(IWorldAccessor world, BlockPos leafPos, string leafBlockCode)
        {
            string leafGroupCode = leafBlockCode.Split('-').Length >= 3 ? leafBlockCode.Split('-')[2].Split(':').Last() : null;
            if (leafGroupCode == null || !char.IsDigit(leafGroupCode[0]))
            {
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
                        if (checkBlockCode == null || !IsTreeLog(checkBlockCode)) continue;

                        string checkGroupCode = checkBlock.Attributes?["treeFellingGroupCode"].AsString();
                        if (checkGroupCode == treeFellingGroupCode)
                        {
                            var basePos = FindTreeBase(world, checkPos, checkBlockCode);
                            return basePos;
                        }
                    }
                }
            }

            return null;
        }

        private bool CanFellTree(BlockPos logPos, string treeId)
        {
            if (logPos == null || string.IsNullOrEmpty(treeId))
            {
                return false;
            }
            if (activeTrees.Contains(treeId))
            {
                return false;
            }
            if (treeFellingCooldowns.TryGetValue(treeId, out var lastFelledTime))
            {
                return api.World.ElapsedMilliseconds / 1000f - lastFelledTime >= TREE_FELLING_COOLDOWN_SECONDS;
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
            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [tree] TryFellTree called for fire at ({firePos.X}, {firePos.Y}, {firePos.Z})");
            }
            if (firePos == null || !fireToLogMap.TryGetValue(firePos, out var logPos))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] TryFellTree failed: No log mapping for fire at ({firePos.X}, {firePos.Y}, {firePos.Z})");
                }
                return;
            }

            var logBlock = api.World.BlockAccessor.GetBlock(logPos);
            var logBlockCode = logBlock.Code?.ToString();
            if (logBlockCode == null || !IsTreeLog(logBlockCode))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] TryFellTree failed: Invalid log at ({logPos.X}, {logPos.Y}, {logPos.Z}), BlockCode: {logBlockCode}");
                }
                return;
            }

            string treeId = logBlock.Attributes?["treeFellingGroupCode"].AsString();
            if (string.IsNullOrEmpty(treeId) || !CanFellTree(logPos, treeId) || api.World == null)
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] TryFellTree failed: Invalid treeId ({treeId}) or cannot fell tree at ({logPos.X}, {logPos.Y}, {logPos.Z})");
                }
                return;
            }

            // Check for duplicate tree by base block
            var basePos = FindTreeBase(api.World, logPos, logBlockCode);
            if (treeBaseByTreeId.TryGetValue(treeId, out var existingBase) && existingBase.Equals(basePos))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] TryFellTree skipped: Tree {treeId} at base ({basePos.X}, {basePos.Y}, {basePos.Z}) already processed");
                }
                fireToLogMap.Remove(firePos);
                return;
            }

            var (treeBlocks, logPositions, leafPositions) = FindTree(api.World, logPos);
            if (treeBlocks.Count == 0)
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] TryFellTree failed: No tree blocks found for tree at ({logPos.X}, {logPos.Y}, {logPos.Z})");
                }
                return;
            }

            if (!treeBlocksByTreeId.ContainsKey(treeId))
            {
                treeBlocksByTreeId[treeId] = new HashSet<BlockPos>();
            }
            var treeBlockSet = treeBlocksByTreeId[treeId];
            foreach (var blockPos in treeBlocks)
            {
                treeBlockSet.Add(blockPos.Copy());
            }
            treeBaseByTreeId[treeId] = basePos.Copy();

            int logCount = 0;
            int leafCount = 0;
            var currentTime = api.World.ElapsedMilliseconds / 1000f;

            // Lists for highlighting
            var logHighlightPositions = new List<BlockPos>();
            var leafHighlightPositions = new List<BlockPos>();

            foreach (var blockPos in treeBlocks)
            {
                if (!burningBlocks.ContainsKey(blockPos))
                {
                    var block = api.World.BlockAccessor.GetBlock(blockPos);
                    var blockCode = block.Code?.ToString();
                    if (blockCode == null || block.Id == 0) continue;

                    int distanceFromBase = 0;
                    bool isLog = IsTreeLog(blockCode);
                    bool isLeaf = IsTreeLeaf(blockCode);

                    if (isLog)
                    {
                        logCount++;
                        logHighlightPositions.Add(blockPos.Copy());
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Log queued for burning at ({blockPos.X}, {blockPos.Y}, {blockPos.Z}), BlockCode: {blockCode}, TreeFellingGroupCode: {block.Attributes?["treeFellingGroupCode"].AsString() ?? "null"}");
                        }
                    }
                    else if (isLeaf)
                    {
                        leafCount++;
                        var leafTuple = leafPositions.Find(l => l.Pos.Equals(blockPos));
                        if (leafTuple.Pos != null)
                        {
                            distanceFromBase = leafTuple.DistanceFromBase;
                        }
                        leafHighlightPositions.Add(blockPos.Copy());
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Leaf queued for burning at ({blockPos.X}, {blockPos.Y}, {blockPos.Z}), BlockCode: {blockCode}, DistanceFromBase: {distanceFromBase}, TreeFellingGroupCode: {block.Attributes?["treeFellingGroupCode"].AsString() ?? "null"}");
                        }
                    }
                    else
                    {
                        if (config.DebugMode)
                        {
                            api.Logger.Warning($"[{MOD_ID}] [tree] Unclassified block at ({blockPos.X}, {blockPos.Y}, {blockPos.Z}), BlockCode: {blockCode}");
                        }
                        continue;
                    }

                    burningBlocks[blockPos.Copy()] = new BurningBlock(blockPos.Copy(), blockCode, currentTime, config, distanceFromBase);
                }
            }

            if (logCount > 0 || leafCount > 0)
            {
                int maxLeafDistance = leafPositions.Any() ? leafPositions.Max(l => l.DistanceFromBase) : 0;
                api.Logger.Notification($"[{MOD_ID}] [tree] Queued {logCount} new logs and {leafCount} new leaves for burning for tree {treeId} at ({logPos.X}, {logPos.Y}, {logPos.Z}), max leaf distance: {maxLeafDistance}");

                int groupId = (treeId + firePos.ToString()).GetHashCode();

                if (config.DebugMode)
                {
                    // Highlight fire in red
                    if (firePos != null)
                    {
                        var firePositions = new List<BlockPos> { firePos.Copy() };
                        var fireColors = new List<int> { unchecked((int)0xFFFF0000) }; // Red (ARGB)
                        foreach (var player in api.World.AllOnlinePlayers)
                        {
                            api.World.HighlightBlocks(player, groupId + 1, firePositions, fireColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
                        }
                    }

                    // Highlight logs in blue
                    if (logHighlightPositions.Count > 0)
                    {
                        var logColors = Enumerable.Repeat(unchecked((int)0xFF0000FF), logHighlightPositions.Count).ToList(); // Blue (ARGB)
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Highlighting {logHighlightPositions.Count} log blocks in blue");
                            foreach (var pos in logHighlightPositions)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Log highlight position: ({pos.X}, {pos.Y}, {pos.Z})");
                            }
                        }
                        foreach (var player in api.World.AllOnlinePlayers)
                        {
                            api.World.HighlightBlocks(player, groupId + 2, logHighlightPositions.Select(p => p.Copy()).ToList(), logColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
                        }
                    }

                    // Highlight leaves in green
                    if (leafHighlightPositions.Count > 0)
                    {
                        var leafColors = Enumerable.Repeat(unchecked((int)0xFF00FF00), leafHighlightPositions.Count).ToList(); // Green (ARGB)
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Highlighting {leafHighlightPositions.Count} leaf blocks in green");
                        }
                        foreach (var player in api.World.AllOnlinePlayers)
                        {
                            api.World.HighlightBlocks(player, groupId + 3, leafHighlightPositions.Select(l => l.Copy()).ToList(), leafColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
                        }
                    }
                }

                MarkTreeFelled(logPos, treeId);
            }

            fireToLogMap.Remove(firePos);
        }

        private (List<BlockPos> TreeBlocks, List<BlockPos> LogPositions, List<(BlockPos Pos, int DistanceFromBase)> LeafPositions) FindTree(IWorldAccessor world, BlockPos startPos)
        {
            if (world == null || startPos == null)
            {
                return (new List<BlockPos>(), new List<BlockPos>(), new List<(BlockPos, int)>());
            }

            var startBlock = world.BlockAccessor.GetBlock(startPos);
            var startBlockCode = startBlock.Code?.ToString();
            if (startBlockCode == null || !IsTreeLog(startBlockCode))
            {
                return (new List<BlockPos>(), new List<BlockPos>(), new List<(BlockPos, int)>());
            }

            string treeFellingGroupCode = startBlock.Attributes?["treeFellingGroupCode"].AsString();
            if (string.IsNullOrEmpty(treeFellingGroupCode))
            {
                return (new List<BlockPos>(), new List<BlockPos>(), new List<(BlockPos, int)>());
            }

            // Pre-fetch blocks for efficiency
            var blockCache = PreFetchBlocks(world, startPos, config.LeafSearchRadius);

            var basePos = FindTreeBase(world, startPos, startBlockCode);
            var treeId = $"{treeFellingGroupCode}:{basePos.X},{basePos.Y},{basePos.Z}";

            var queue = new Queue<(BlockPos Pos, int Distance)>();
            var visited = new HashSet<BlockPos>();
            var treeBlocks = new List<BlockPos>();
            var logPositions = new List<BlockPos>();
            var leafPositions = new List<(BlockPos Pos, int DistanceFromBase)>();

            queue.Enqueue((startPos.Copy(), 0));
            visited.Add(startPos);

            while (queue.Count > 0)
            {
                var (currentPos, currentDistance) = queue.Dequeue();
                if (!blockCache.TryGetValue(currentPos, out var blockData)) continue;
                var block = blockData.Block;
                var blockCode = blockData.BlockCode;
                if (blockCode == null || block.Id == 0) continue;

                string ngCode = block.Attributes?["treeFellingGroupCode"].AsString();

                bool isLog = IsTreeLog(blockCode);
                bool isLeaf = IsTreeLeaf(blockCode);
                bool isValidBlock = false;

                if (isLog && ngCode != null && (ngCode == treeFellingGroupCode || ngCode.EndsWith(treeFellingGroupCode)))
                {
                    isValidBlock = true;
                    logPositions.Add(currentPos.Copy());
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [tree] Log detected at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}), BlockCode: {blockCode}, GroupCode: {ngCode}");
                    }
                }
                else if (isLeaf && ngCode != null && NormalizeGroupCode(ngCode) == treeFellingGroupCode)
                {
                    isValidBlock = true;
                    leafPositions.Add((currentPos.Copy(), currentDistance));
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [tree] Leaf detected at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}), BlockCode: {blockCode}, GroupCode: {ngCode}, Normalized: {NormalizeGroupCode(ngCode)}");
                    }
                }

                if (isValidBlock)
                {
                    treeBlocks.Add(currentPos.Copy());

                    // Explore neighbors
                    for (int i = 0; i < Vec3i.DirectAndIndirectNeighbours.Length; i++)
                    {
                        var facing = Vec3i.DirectAndIndirectNeighbours[i];
                        var neighborPos = new BlockPos(currentPos.X + facing.X, currentPos.Y + facing.Y, currentPos.Z + facing.Z);
                        float horDist = GameMath.Sqrt(neighborPos.HorDistanceSqTo(startPos.X, startPos.Z));
                        if (horDist > config.LeafSearchRadius || visited.Contains(neighborPos)) continue;

                        if (!blockCache.TryGetValue(neighborPos, out var neighborData)) continue;
                        var neighborBlock = neighborData.Block;
                        var neighborCode = neighborData.BlockCode;
                        if (neighborCode == null || neighborBlock.Id == 0) continue; // Skip air blocks

                        string neighborGroupCode = neighborBlock.Attributes?["treeFellingGroupCode"].AsString();
                        if (IsTreeLog(neighborCode) && neighborGroupCode != null && (neighborGroupCode == treeFellingGroupCode || neighborGroupCode.EndsWith(treeFellingGroupCode)))
                        {
                            queue.Enqueue((neighborPos.Copy(), currentDistance + 1));
                            visited.Add(neighborPos);
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Enqueued log neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}), BlockCode: {neighborCode}, GroupCode: {neighborGroupCode}");
                            }
                        }
                        else if (IsTreeLeaf(neighborCode) && neighborGroupCode != null && NormalizeGroupCode(neighborGroupCode) == treeFellingGroupCode)
                        {
                            queue.Enqueue((neighborPos.Copy(), currentDistance + 1));
                            visited.Add(neighborPos);
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Enqueued leaf neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}), BlockCode: {neighborCode}, GroupCode: {neighborGroupCode}");
                            }
                        }
                    }
                }
            }

            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [tree] Found {logPositions.Count} logs and {leafPositions.Count} leaves for tree {treeId} at base ({basePos.X}, {basePos.Y}, {basePos.Z})");
            }

            return (treeBlocks, logPositions, leafPositions);
        }

        private Dictionary<BlockPos, (Block Block, string BlockCode)> PreFetchBlocks(IWorldAccessor world, BlockPos startPos, float radius)
        {
            var cache = new Dictionary<BlockPos, (Block, string)>();
            int minX = startPos.X - (int)radius;
            int maxX = startPos.X + (int)radius;
            int minY = Math.Max(0, startPos.Y - 50); // Limit height to reasonable tree height
            int maxY = startPos.Y + 50;
            int minZ = startPos.Z - (int)radius;
            int maxZ = startPos.Z + (int)radius;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        var pos = new BlockPos(x, y, z);
                        var block = world.BlockAccessor.GetBlock(pos);
                        cache[pos] = (block, block.Code?.ToString());
                    }
                }
            }
            return cache;
        }

        private string NormalizeGroupCode(string groupCode)
        {
            if (string.IsNullOrEmpty(groupCode)) return groupCode;
            if (groupCodeCache.TryGetValue(groupCode, out var normalized))
                return normalized;
            normalized = groupCode;
            if (groupCode.Length > 1 && char.IsDigit(groupCode[0]))
                normalized = groupCode.Substring(1);
            groupCodeCache[groupCode] = normalized;
            return normalized;
        }

        private bool NeedsSpreadIndex(string blockCode)
        {
            if (spreadIndexCache.TryGetValue(blockCode, out var result))
                return result;
            result = blockCode?.StartsWith("game:logsection-") == true;
            spreadIndexCache[blockCode] = result;
            return result;
        }
    }
}