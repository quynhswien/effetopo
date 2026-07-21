using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Services
{
    /// <summary>
    /// Samples points along a curve by distance or equal segment count.
    /// </summary>
    internal static class CurvePointSampler
    {
        public static List<XYZ> Sample(Curve curve, FloorBoundarySamplingOptions options)
        {
            var points = new List<XYZ>();
            if (curve == null || options == null)
                return points;

            int steps = GetSampleSteps(curve, options);
            for (int k = 0; k <= steps; k++)
            {
                double t = steps > 0 ? k / (double)steps : 0;
                try
                {
                    points.Add(curve.Evaluate(t, true));
                }
                catch (Exception ex)
                {
                    Log.Debug("Curve evaluate failed at t={T}: {Error}", t, ex.Message);
                }
            }

            return points;
        }

        public static int GetSampleSteps(Curve curve, FloorBoundarySamplingOptions options)
        {
            if (curve == null || options == null)
                return 1;

            if (options.Mode == BoundarySampleMode.BySegmentCount)
                return Math.Max(1, options.SegmentsPerCurve);

            double length = curve.ApproximateLength;
            return Math.Max(1, (int)Math.Ceiling(length / Math.Max(options.SpacingFeet, 1e-6)));
        }
    }
}
