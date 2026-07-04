namespace effetopo.Models
{
    public enum BoundarySampleMode
    {
        /// <summary>Divide each boundary curve by approximate spacing (feet internally).</summary>
        ByDistance,
        /// <summary>Divide each boundary curve into a fixed number of equal segments.</summary>
        BySegmentCount
    }

    /// <summary>
    /// User settings for sampling points along floor boundary curves (Step 2b).
    /// </summary>
    public class FloorBoundarySamplingOptions
    {
        public BoundarySampleMode Mode { get; set; } = BoundarySampleMode.ByDistance;

        /// <summary>Target spacing between sample points (Revit internal feet).</summary>
        public double SpacingFeet { get; set; } = 1.0;

        /// <summary>Number of equal segments per curve (sample points = segments + 1, including endpoints).</summary>
        public int SegmentsPerCurve { get; set; } = 2;

        public static FloorBoundarySamplingOptions Default => new FloorBoundarySamplingOptions();
    }
}
