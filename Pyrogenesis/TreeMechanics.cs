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
            foreach (var blockPos in treeBlocks)
            {
                treeBlockSet.Add(blockPos.Copy());
            }

            int logCount = 0;
            int leafCount = 0;
            var currentTime = api.World.ElapsedMilliseconds / 1000f;

            api.Logger.Debug($"[{MOD_ID}] [tree] leafPositions count: {leafPositions.Count}");

            foreach (var blockPos in treeBlocks)
            {
                if (!burningBlocks.ContainsKey(blockPos))
                {
                    var block = api.World.BlockAccessor.GetBlock(blockPos);
                    var blockCode = block.Code?.ToString();
                    if (blockCode == null || block.Id == 0) continue;

                    int distanceFromBase = 0;
                    if (IsTreeLeaf(blockCode, config))
                    {
                        var leafTuple = leafPositions.Find(l => l.Pos.Equals(blockPos));
                        if (leafTuple.Pos != null)
                        {
                            distanceFromBase = leafTuple.DistanceFromBase;
                        }
                        api.Logger.Debug($"[{MOD_ID}] [tree] Leaf detected at ({blockPos.X}, {blockPos.Y}, {blockPos.Z}), BlockCode: {blockCode}, DistanceFromBase: {distanceFromBase}, TreeFellingGroupCode: {block.Attributes?["treeFellingGroupCode"].AsString() ?? "null"}");
                        leafCount++;
                    }
                    else if (IsTreeLog(blockCode, config))
                    {
                        api.Logger.Debug($"[{MOD_ID}] [tree] Log detected at ({blockPos.X}, {blockPos.Y}, {blockPos.Z}), BlockCode: {blockCode}, TreeFellingGroupCode: {block.Attributes?["treeFellingGroupCode"].AsString() ?? "null"}");
                        logCount++;
                    }

                    burningBlocks[blockPos.Copy()] = new BurningBlock(blockPos.Copy(), blockCode, currentTime, config, distanceFromBase);
                }
            }

            if (logCount > 0 || leafCount > 0)
            {
                int maxLeafDistance = leafPositions.Any() ? leafPositions.Max(l => l.DistanceFromBase) : 0;
                api.Logger.Notification($"[{MOD_ID}] [tree] Queued {logCount} new logs and {leafCount} new leaves for burning for tree {treeId} at ({logPos.X}, {logPos.Y}, {logPos.Z}), max leaf distance: {maxLeafDistance}");

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
                    var logColors = Enumerable.Repeat(unchecked((int)0xFF00FF00), logPositions.Count).ToList(); // Blue (ARGB)
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
                        api.World.HighlightBlocks(player, groupId, leafPositions.Select(l => l.Pos.Copy()).ToList(), leafColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
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
            if (startBlockCode == null || !IsTreeLog(startBlockCode, config))
            {
                return (new List<BlockPos>(), new List<BlockPos>(), new List<(BlockPos, int)>());
            }

            string treeFellingGroupCode = startBlock.Attributes?["treeFellingGroupCode"].AsString();
            int spreadIndex = NeedsSpreadIndex(startBlockCode) ? startBlock.Attributes?["treeFellingGroupSpreadIndex"].AsInt(0) ?? 0 : -1;
            if (string.IsNullOrEmpty(treeFellingGroupCode))
            {
                return (new List<BlockPos>(), new List<BlockPos>(), new List<(BlockPos, int)>());
            }

            api.Logger.Debug($"[{MOD_ID}] [tree] Starting tree search at ({startPos.X}, {startPos.Y}, {startPos.Z}), TreeFellingGroupCode: {treeFellingGroupCode}, SpreadIndex: {spreadIndex}");

            var queue = new Queue<(BlockPos Pos, int SpreadIndex, int Distance)>();
            var leafQueue = new Queue<(BlockPos Pos, int SpreadIndex, string GroupCode, int Distance)>();
            var visited = new HashSet<BlockPos>();
            var treeBlocks = new List<BlockPos>();
            var logPositions = new List<BlockPos>();
            var leafPositions = new List<(BlockPos Pos, int DistanceFromBase)>();
            var leafGroupCounts = new Dictionary<string, int>();

            queue.Enqueue((startPos.Copy(), spreadIndex, 0));
            visited.Add(startPos);

            while (queue.Count > 0)
            {
                var (currentPos, currentSpreadIndex, currentDistance) = queue.Dequeue();
                var block = world.BlockAccessor.GetBlock(currentPos);
                var blockCode = block.Code?.ToString();

                if (blockCode == null || block.Id == 0) continue;

                string ngCode = block.Attributes?["treeFellingGroupCode"].AsString();
                int nSpreadIndex = NeedsSpreadIndex(blockCode) ? block.Attributes?["treeFellingGroupSpreadIndex"].AsInt(0) ?? 0 : -1;

                api.Logger.Debug($"[{MOD_ID}] [tree] Processing block at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}), BlockCode: {blockCode}, TreeFellingGroupCode: {ngCode ?? "null"}, SpreadIndex: {currentSpreadIndex}, Distance: {currentDistance}");

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

                        api.Logger.Debug($"[{MOD_ID}] [tree] Checking neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}), BlockCode: {neighborCode}, TreeFellingGroupCode: {nNgCode ?? "null"}, SpreadIndex: {nNSpreadIndex}");

                        string normalizedGroupCode = nNgCode;
                        if (IsTreeLeaf(neighborCode, config) && nNgCode != null && int.TryParse(nNgCode[0].ToString(), out _))
                        {
                            normalizedGroupCode = nNgCode.Substring(1); // Strip numeric prefix (e.g., "6oak" -> "oak")
                        }

                        if (nNgCode != null && normalizedGroupCode == treeFellingGroupCode && (nNSpreadIndex <= currentSpreadIndex || !NeedsSpreadIndex(neighborCode)))
                        {
                            queue.Enqueue((neighborPos.Copy(), nNSpreadIndex, currentDistance + 1));
                            visited.Add(neighborPos);
                        }
                        else if (IsTreeLeaf(neighborCode, config) && nNgCode != null && normalizedGroupCode == treeFellingGroupCode)
                        {
                            leafQueue.Enqueue((neighborPos.Copy(), nNSpreadIndex, nNgCode, currentDistance + 1));
                            visited.Add(neighborPos);
                            leafGroupCounts[nNgCode] = leafGroupCounts.GetValueOrDefault(nNgCode, 0) + 1;
                            api.Logger.Debug($"[{MOD_ID}] [tree] Enqueued leaf at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) with DistanceFromBase: {currentDistance + 1}, TreeFellingGroupCode: {nNgCode}, Normalized: {normalizedGroupCode}");
                        }
                        else
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Skipped neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}), IsLeaf: {IsTreeLeaf(neighborCode, config)}, GroupCodeMatch: {nNgCode != null && normalizedGroupCode == treeFellingGroupCode}");
                        }
                    }
                }
            }

            while (leafQueue.Count > 0)
            {
                var (currentPos, currentSpreadIndex, currentGroupCode, currentDistance) = leafQueue.Dequeue();
                var block = world.BlockAccessor.GetBlock(currentPos);
                var blockCode = block.Code?.ToString();

                if (blockCode == null || block.Id == 0) continue;

                string ngCode = block.Attributes?["treeFellingGroupCode"].AsString();
                string normalizedGroupCode = ngCode;
                if (IsTreeLeaf(blockCode, config) && ngCode != null && int.TryParse(ngCode[0].ToString(), out _))
                {
                    normalizedGroupCode = ngCode.Substring(1); // Strip numeric prefix
                }

                api.Logger.Debug($"[{MOD_ID}] [tree] Processing leaf queue block at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}), BlockCode: {blockCode}, TreeFellingGroupCode: {ngCode ?? "null"}, Normalized: {normalizedGroupCode}, Distance: {currentDistance}");

                if (ngCode != null && normalizedGroupCode == treeFellingGroupCode)
                {
                    treeBlocks.Add(currentPos.Copy());
                    if (IsTreeLeaf(blockCode, config))
                    {
                        leafPositions.Add((currentPos.Copy(), currentDistance));
                        api.Logger.Debug($"[{MOD_ID}] [tree] Added leaf to leafPositions at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}) with DistanceFromBase: {currentDistance}, TreeFellingGroupCode: {ngCode}, Normalized: {normalizedGroupCode}");
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

                        string neighborNormalizedGroupCode = nNgCode;
                        if (IsTreeLeaf(neighborCode, config) && nNgCode != null && int.TryParse(nNgCode[0].ToString(), out _))
                        {
                            neighborNormalizedGroupCode = nNgCode.Substring(1);
                        }

                        api.Logger.Debug($"[{MOD_ID}] [tree] Checking leaf neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}), BlockCode: {neighborCode}, TreeFellingGroupCode: {nNgCode ?? "null"}, Normalized: {neighborNormalizedGroupCode}, SpreadIndex: {nNSpreadIndex}");

                        if (nNgCode != null && neighborNormalizedGroupCode == treeFellingGroupCode)
                        {
                            leafQueue.Enqueue((neighborPos.Copy(), nNSpreadIndex, nNgCode, currentDistance + 1));
                            visited.Add(neighborPos);
                            leafGroupCounts[nNgCode] = leafGroupCounts.GetValueOrDefault(nNgCode, 0) + 1;
                            api.Logger.Debug($"[{MOD_ID}] [tree] Enqueued leaf neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) with DistanceFromBase: {currentDistance + 1}, TreeFellingGroupCode: {nNgCode}, Normalized: {neighborNormalizedGroupCode}");
                        }
                    }
                }
                else
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] Skipped leaf queue block at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}), GroupCodeMatch: {ngCode != null && normalizedGroupCode == treeFellingGroupCode}");
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