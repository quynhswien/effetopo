namespace effetopo.Models
{
    /// <summary>
    /// User interface preferences (Example model usage)
    /// </summary>
    public class UIPreferences
    {
        /// <summary>
        /// Whether to use dark mode
        /// </summary>
        public bool UseDarkMode { get; set; } = false;

        /// <summary>
        /// Font size for UI elements
        /// </summary>
        public int FontSize { get; set; } = 12;

        /// <summary>
        /// Whether to show advanced options
        /// </summary>
        public bool ShowAdvancedOptions { get; set; } = false;
    }
}