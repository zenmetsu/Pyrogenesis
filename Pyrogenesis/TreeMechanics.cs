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
            return config.TreeLogPrefixes.Any(prefix => blockCode.StartsWith($"game:{prefix}-"));
        }

        private static bool IsTreeLeaf(string blockCode, PyrogenesisConfig config)
        {
            if (string.IsNullOrEmpty(blockCode)) return false;
            if (!blockCode.StartsWith("game:leaves-")) return false;
            if (new TreeMechanics(null, config).IsTreeLog(blockCode, config))
            {
                return false;
            }
            return true;
        }

        public (BlockPos Pos, int Id, string Name)? FindNearbyLog(IWorldAccessor world, BlockPos firePos)
        {
            for (int yOffset = 0; yOffset >= -3; yOffset--)
            {
                var checkPos = new BlockPos(firePos.X, firePos.Y + yOffset, firePos.Z);
                var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                var checkBlockCode = checkBlock?.Code?.ToString();
                if (checkBlock != null && !string.IsNullOrEmpty(checkBlockCode) && IsTreeLog(checkBlockCode, config))
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
                        if (checkBlock != null && !string.IsNullOrEmpty(checkBlockCode) && IsTreeLog(checkBlockCode, config))
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
                        if (checkBlock != null && !string.IsNullOrEmpty(checkBlockCode) && IsTreeLeaf(checkBlockCode, config))
                        {
                            var logPos = FindTreeBaseFromLeaf(world, checkPos, checkBlockCode);
                            if (logPos != null)
                            {
                                var logBlock = world.BlockAccessor.GetBlock(logPos);
                                var logBlockCode = logBlock.Code?.ToString();
                                if (logBlockCode != null && IsTreeLog(logBlockCode, config))
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

        private BlockPos FindTreeBase(IWorldAccessor world, BlockPos logPos, string logBlockCode)
        {
            var parts = logBlockCode.Split('-');
            string treeFellingGroupCode = parts.Length >= 3 ? parts[2].Split(':').Last() : null;
            if (treeFellingGroupCode == null)
            {
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
                    break;
                }

                lowestPos = checkPos.Copy();
            }

            return lowestPos;
        }

        private BlockPos FindTreeBaseFromLeaf(IWorldAccessor world, BlockPos leafPos, string leafBlockCode)
        {
            string leafGroupCode = leafBlockCode.Split('-').Length >= 3 ? leafBlockCode.Split('-')[2].Split(':').Last() : null;
            if (leafGroupCode == null || !int.TryParse(leafGroupCode[0].ToString(), out int leafGroup))
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
                        if (checkBlockCode == null || !IsTreeLog(checkBlockCode, config)) continue;

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
            if (firePos == null || !fireToLogMap.TryGetValue(firePos, out var logPos))
            {
                return;
            }

            var logBlock = api.World.BlockAccessor.GetBlock(logPos);
            var logBlockCode = logBlock.Code?.ToString();
            if (logBlockCode == null || !IsTreeLog(logBlockCode, config))
            {
                return;
            }

            string treeId = logBlock.Attributes?["treeFellingGroupCode"].AsString();
            if (string.IsNullOrEmpty(treeId) || !CanFellTree(logPos, treeId) || api.World == null)
            {
                return;
            }

            var (treeBlocks, logPositions, leafPositions) = FindTree(api.World, logPos);
            if (treeBlocks.Count == 0)
            {
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
                    }
                    else if (IsTreeLeaf(blockCode, config))
                    {
                        leafCount++;
                    }

                    burningBlocks[blockPos.Copy()] = new BurningBlock(blockPos.Copy(), blockCode, currentTime, config);
                }
            }

            if (logCount > 0 || leafCount > 0)
            {
                api.Logger.Notification($"[{MOD_ID}] [tree] Queued {logCount} new logs and {leafCount} new leaves for burning for tree {treeId} at ({logPos.X}, {logPos.Y}, {logPos.Z}), {newBlocksAdded} new blocks added to tree");

                int groupId = (treeId + firePos.ToString()).GetHashCode();

                if (firePos != null)
                {
                    var firePositions = new List<BlockPos> { firePos.Copy() };
                    var fireColors = new List<int> { unchecked((int)0xFFFF0000) }; // Red (ARGB)
                    foreach (var player in api.World.AllOnlinePlayers)
                    {
                        api.World.HighlightBlocks(player, groupId, firePositions, fireColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
                    }
                }

                if (logPositions.Count > 0)
                {
                    var logColors = Enumerable.Repeat(unchecked((int)0xFF0000FF), logPositions.Count).ToList(); // Blue (ARGB)
                    foreach (var player in api.World.AllOnlinePlayers)
                    {
                        api.World.HighlightBlocks(player, groupId, logPositions.Select(p => p.Copy()).ToList(), logColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
                    }
                }

                if (leafPositions.Count > 0)
                {
                    var leafColors = Enumerable.Repeat(unchecked((int)0xFF00FF00), leafPositions.Count).ToList(); // Lime Green (ARGB)
                    foreach (var player in api.World.AllOnlinePlayers)
                    {
                        api.World.HighlightBlocks(player, groupId, leafPositions.Select(p => p.Copy()).ToList(), leafColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
                    }
                }

                MarkTreeFelled(logPos, treeId);
            }

            fireToLogMap.Remove(firePos);
        }

        private (List<BlockPos> TreeBlocks, List<BlockPos> LogPositions, List<BlockPos> LeafPositions) FindTree(IWorldAccessor world, BlockPos startPos)
        {
            if (world == null || startPos == null)
            {
                return (new List<BlockPos>(), new List<BlockPos>(), new List<BlockPos>());
            }

            var startBlock = world.BlockAccessor.GetBlock(startPos);
            var startBlockCode = startBlock.Code?.ToString();
            if (startBlockCode == null || !IsTreeLog(startBlockCode, config))
            {
                return (new List<BlockPos>(), new List<BlockPos>(), new List<BlockPos>());
            }

            string treeFellingGroupCode = startBlock.Attributes?["treeFellingGroupCode"].AsString();
            int spreadIndex = NeedsSpreadIndex(startBlockCode) ? startBlock.Attributes?["treeFellingGroupSpreadIndex"].AsInt(0) ?? 0 : -1;
            if (string.IsNullOrEmpty(treeFellingGroupCode))
            {
                return (new List<BlockPos>(), new List<BlockPos>(), new List<BlockPos>());
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
                    }

                    for (int i = 0; i < Vec3i.DirectAndIndirectNeighbours.Length; i++)
                    {
                        var facing = Vec3i.DirectAndIndirectNeighbours[i];
                        var neighborPos = new BlockPos(currentPos.X + facing.X, currentPos.Y + facing.Y, currentPos.Z + facing.Z);

                        float horDist = GameMath.Sqrt(neighborPos.HorDistanceSqTo(startPos.X, startPos.Z));
                        if (horDist > config.LeafSearchRadius) continue;
                        if (visited.Contains(neighborPos)) continue;

                        var neighborBlock = world.BlockAccessor.GetBlock(neighborPos);
                        var neighborCode = neighborBlock.Code?.ToString();
                        if (neighborCode == null || neighborBlock.Id == 0)
                        {
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
                    }

                    for (int i = 0; i < Vec3i.DirectAndIndirectNeighbours.Length; i++)
                    {
                        var facing = Vec3i.DirectAndIndirectNeighbours[i];
                        var neighborPos = new BlockPos(currentPos.X + facing.X, currentPos.Y + facing.Y, currentPos.Z + facing.Z);

                        float horDist = GameMath.Sqrt(neighborPos.HorDistanceSqTo(startPos.X, startPos.Z));
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
                    }
                }
            }

            return (treeBlocks, logPositions, leafPositions);
        }

        private bool NeedsSpreadIndex(string blockCode)
        {
            return blockCode.StartsWith("game:logsection-");
        }
    }
}