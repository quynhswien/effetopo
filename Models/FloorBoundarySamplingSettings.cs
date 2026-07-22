namespace effetopo.Models
{
    /// <summary>
    /// Persisted UI settings for the Boundary Point Division dialog.
    /// </summary>
    public class FloorBoundarySamplingSettings
    {
        public BoundarySampleMode Mode { get; set; } = BoundarySampleMode.ByDistance;

        /// <summary>Spacing in Revit internal feet (used when Mode = ByDistance).</summary>
        public double SpacingFeet { get; set; } = 1.0;

        /// <summary>Segments per curve (used when Mode = BySegmentCount).</summary>
        public int SegmentsPerCurve { get; set; } = 5;

        public bool UseCustomSpacing { get; set; }

        /// <summary>Preset index 0–2, or -1 when UseCustomSpacing.</summary>
        public int DistancePresetIndex { get; set; } = 0;

        public bool UseCustomSegmentCount { get; set; }

        /// <summary>Preset index 0–2, or -1 when UseCustomSegmentCount.</summary>
        public int SegmentPresetIndex { get; set; } = 0;

        /// <summary>Custom spacing in display units (mm or feet) when UseCustomSpacing is true.</summary>
        public double CustomSpacingDisplay { get; set; } = 1.0;

        public int CustomSegmentCount { get; set; } = 10;

        public bool AddToposolidPointsWithinBoundary { get; set; } = true;

        public WallFollowTopoMode WallFollowMode { get; set; } = WallFollowTopoMode.SlopeTopOnTopo;

        public FloorBoundarySamplingOptions ToOptions()
        {
            return new FloorBoundarySamplingOptions
            {
                Mode = Mode,
                SpacingFeet = SpacingFeet,
                SegmentsPerCurve = SegmentsPerCurve,
                AddToposolidPointsWithinBoundary = AddToposolidPointsWithinBoundary,
                WallFollowMode = WallFollowMode
            };
        }
    }
}
