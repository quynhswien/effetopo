namespace effetopo.Models
{
    public enum ModifyTopoTool
    {
        InflateSurface,
        MeshControl,
        ShapeByPoint,
        SmoothGeometry
    }

    public enum SculptFalloffType
    {
        Gaussian,
        Linear,
        Smooth,
        Constant
    }

    public enum SmoothAlgorithm
    {
        Laplacian,
        Taubin,
        HcSmoothing
    }

    /// <summary>Runtime options passed from dialog to ModifyTopoService.</summary>
    public class ModifyTopoOptions
    {
        public ModifyTopoTool Tool { get; set; } = ModifyTopoTool.InflateSurface;

        public double CellSizeFeet { get; set; } = 3.28084; // 1 m default
        public double RotationDegrees { get; set; }
        public double MeshDensityFeet { get; set; } = 24.6063; // 7.5 m default
        public bool ModifyBoundary { get; set; }
        public bool ShowPreview { get; set; }

        // Inflate Surface
        public double InflateRadiusFeet { get; set; } = 32.8084; // 10 m
        public double InflateHeightFeet { get; set; } = 3.28084; // 1 m
        public SculptFalloffType InflateFalloff { get; set; } = SculptFalloffType.Gaussian;

        // Shape by Point
        public double ShapeRadiusFeet { get; set; } = 32.8084;
        public double ShapeTargetElevationFeet { get; set; }
        public bool ShapeUseDelta { get; set; }
        public double ShapeDeltaFeet { get; set; } = 3.28084;
        public SculptFalloffType ShapeFalloff { get; set; } = SculptFalloffType.Smooth;

        // Smooth Geometry
        public SmoothAlgorithm SmoothAlgorithm { get; set; } = SmoothAlgorithm.Taubin;
        public int SmoothIterations { get; set; } = 3;
        public double SmoothStrength { get; set; } = 0.5;

        // Mesh Control
        public double CurvatureThreshold { get; set; } = 0.02; // ft vertical change per ft horizontal
        public bool RemeshEntireSurface { get; set; } = true;
    }

    /// <summary>Persisted UI settings for Modify Topo dialog.</summary>
    public class ModifyTopoSettings
    {
        public ModifyTopoTool LastTool { get; set; } = ModifyTopoTool.InflateSurface;
        public double CellSizeDisplay { get; set; } = 1.0;
        public double RotationDegrees { get; set; }
        public double MeshDensityDisplay { get; set; } = 7.5;
        public bool ModifyBoundary { get; set; }
        public bool ShowPreview { get; set; }

        public double InflateRadiusDisplay { get; set; } = 10.0;
        public double InflateHeightDisplay { get; set; } = 1.0;
        public SculptFalloffType InflateFalloff { get; set; } = SculptFalloffType.Gaussian;

        public double ShapeRadiusDisplay { get; set; } = 10.0;
        public double ShapeTargetElevationDisplay { get; set; }
        public bool ShapeUseDelta { get; set; } = true;
        public double ShapeDeltaDisplay { get; set; } = 1.0;
        public SculptFalloffType ShapeFalloff { get; set; } = SculptFalloffType.Smooth;

        public SmoothAlgorithm SmoothAlgorithm { get; set; } = SmoothAlgorithm.Taubin;
        public int SmoothIterations { get; set; } = 3;
        public double SmoothStrength { get; set; } = 0.5;

        public double CurvatureThreshold { get; set; } = 0.02;
        public bool RemeshEntireSurface { get; set; } = true;

        public ModifyTopoOptions ToOptions(bool useMillimeters)
        {
            double ToFeet(double display) => useMillimeters ? display / 304.8 : display;

            return new ModifyTopoOptions
            {
                Tool = LastTool,
                CellSizeFeet = ToFeet(CellSizeDisplay),
                RotationDegrees = RotationDegrees,
                MeshDensityFeet = ToFeet(MeshDensityDisplay),
                ModifyBoundary = ModifyBoundary,
                ShowPreview = ShowPreview,
                InflateRadiusFeet = ToFeet(InflateRadiusDisplay),
                InflateHeightFeet = ToFeet(InflateHeightDisplay),
                InflateFalloff = InflateFalloff,
                ShapeRadiusFeet = ToFeet(ShapeRadiusDisplay),
                ShapeTargetElevationFeet = ToFeet(ShapeTargetElevationDisplay),
                ShapeUseDelta = ShapeUseDelta,
                ShapeDeltaFeet = ToFeet(ShapeDeltaDisplay),
                ShapeFalloff = ShapeFalloff,
                SmoothAlgorithm = SmoothAlgorithm,
                SmoothIterations = SmoothIterations,
                SmoothStrength = SmoothStrength,
                CurvatureThreshold = CurvatureThreshold,
                RemeshEntireSurface = RemeshEntireSurface
            };
        }
    }

    public class ModifyTopoResult
    {
        public int OriginalPointCount { get; set; }
        public int PointsAfterModification { get; set; }
        public int VerticesModified { get; set; }
        public int PointsAdded { get; set; }
        public int PointsRemoved { get; set; }
        public string Summary { get; set; } = string.Empty;
    }
}
