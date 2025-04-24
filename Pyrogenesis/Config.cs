namespace Pyrogenesis
{
    public class PyrogenesisConfig
    {
        // Toggle for debug mode (enables debug logging and block highlighting)
        public bool DebugMode { get; set; } = false;

        // Probability of very low fertility soil upgrading to low
        public double VeryLowToLow { get; set; } = 0.9;

        // Probability of low fertility soil upgrading to medium
        public double LowToMedium { get; set; } = 0.5;

        // Probability of medium fertility soil upgrading to compost (high)
        public double MediumToHigh { get; set; } = 0.25;

        // Probability of compost (high) fertility soil upgrading to terra preta
        public double HighToTerraPreta { get; set; } = 0.05;

        // Modifier for multi-tier fertility upgrades (e.g., very low to medium)
        public double MultiTierModifier { get; set; } = 0.1;

        // Toggle to convert forest floor blocks to medium fertility soil (false = leave unchanged)
        public bool ConvertForestFloorToSoil { get; set; } = false;

        // Duration (in seconds) before initial canopy pruning event takes place
        public float CanopyPruningDelaySeconds { get; set; } = 5f; // Default delay of 5 seconds

        // Duration (in seconds) for logs to burn before breaking
        public float LogBurnDuration { get; set; } = 80.0f;

        // Duration (in seconds) for leaves to burn before breaking
        public float LeafBurnDuration { get; set; } = 40.0f;

        // Maximum distance (in blocks) to search for leaves from a log
        public float LeafSearchRadius { get; set; } = 20.0f;

        // List of prefixes for tree log blocks (e.g., "log", "logsection")
        public string[] TreeLogPrefixes { get; set; } = new[] { "log", "logsection" };
    }
}