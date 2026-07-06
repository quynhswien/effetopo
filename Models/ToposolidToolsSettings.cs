namespace effetopo.Models
{
    /// <summary>
    /// Persisted ribbon preferences for Toposolid Tools split button.
    /// </summary>
    public class ToposolidToolsSettings
    {
        /// <summary>Command id of the most recently used Toposolid tool (matches COMMAND_NAME).</summary>
        public string LastUsedCommandId { get; set; } = string.Empty;
    }
}
