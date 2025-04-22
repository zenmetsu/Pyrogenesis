using System.Linq;
using Vintagestory.API.MathTools;

namespace Pyrogenesis
{
    public struct BurningBlock
    {
        public BlockPos Pos { get; }
        public string BlockCode { get; }
        public float BurnStartTime { get; }
        public float BurnDuration { get; }
        public int DistanceFromBase { get; } // New field for distance score

        public BurningBlock(BlockPos pos, string blockCode, float burnStartTime, PyrogenesisConfig config, int distanceFromBase = 0)
        {
            Pos = pos;
            BlockCode = blockCode;
            BurnStartTime = burnStartTime;
            DistanceFromBase = distanceFromBase; // Initialize the new field

            // Determine burn duration based on block type
            if (blockCode != null && (blockCode.StartsWith("game:leaves-") || blockCode.StartsWith("game:leavesbranchy-")))
            {
                BurnDuration = config?.LeafBurnDuration ?? 40.0f;
            }
            else if (blockCode != null && config?.TreeLogPrefixes?.Any(prefix => blockCode.StartsWith($"game:{prefix}-")) == true)
            {
                BurnDuration = config?.LogBurnDuration ?? 80.0f;
            }
            else
            {
                BurnDuration = 40.0f; // Fallback duration
            }
        }
    }

    public struct PendingBlock
    {
        public BlockPos Pos { get; }
        public string BlockCode { get; }
        public float QueueTime { get; }
        public bool RequiresFertilityRolling { get; }

        public PendingBlock(BlockPos pos, string blockCode, float queueTime, bool requiresFertilityRolling)
        {
            Pos = pos;
            BlockCode = blockCode;
            QueueTime = queueTime;
            RequiresFertilityRolling = requiresFertilityRolling;
        }
    }
}