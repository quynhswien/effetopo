namespace effetopo.Models
{
    /// <summary>How a wall follows a Toposolid along its path.</summary>
    public enum WallFollowTopoMode
    {
        /// <summary>Constant height; wall base follows topo at each segment (stepped segments).</summary>
        StepFixedHeight,

        /// <summary>Wall top and bottom follow topo; top = bottom + wall height (profile wall).</summary>
        SlopeTopOnTopo
    }
}
