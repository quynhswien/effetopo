using System;
using System.Collections.Generic;

namespace effetopo.Models
{
    /// <summary>
    /// Elevation reference matching Revit spot-elevation / level elevation bases.
    /// </summary>
    public enum ElevationBaseType
    {
        TopPlane,
        CurrentLevel,
        ProjectBasePoint,
        SurveyPoint,
        InternalOrigin
    }

    /// <summary>
    /// Persisted UI settings for the Set Elevation dialog.
    /// </summary>
    public class SetElevationSettings
    {
        public bool AddLabel { get; set; } = true;

        public long TextTypeId { get; set; }

        public double StartElevationDisplay { get; set; } = 0;

        public double IncrementDisplay { get; set; } = 1;

        public ElevationBaseType ElevationBase { get; set; } = ElevationBaseType.SurveyPoint;

        public byte OverrideColorR { get; set; } = 255;

        public byte OverrideColorG { get; set; } = 128;

        public byte OverrideColorB { get; set; } = 0;

        public SetElevationOptions ToOptions(bool useMillimeters)
        {
            double ToFeet(double display) => useMillimeters ? display / 304.8 : display;
            return new SetElevationOptions
            {
                AddLabel = AddLabel,
                TextTypeId = TextTypeId,
                StartElevationFeet = ToFeet(StartElevationDisplay),
                IncrementFeet = ToFeet(IncrementDisplay),
                ElevationBase = ElevationBase,
                OverrideColor = new Autodesk.Revit.DB.Color(OverrideColorR, OverrideColorG, OverrideColorB)
            };
        }

        public static SetElevationSettings FromOptions(SetElevationOptions options, bool useMillimeters)
        {
            double ToDisplay(double feet) => useMillimeters ? feet * 304.8 : feet;
            return new SetElevationSettings
            {
                AddLabel = options.AddLabel,
                TextTypeId = options.TextTypeId,
                StartElevationDisplay = ToDisplay(options.StartElevationFeet),
                IncrementDisplay = ToDisplay(options.IncrementFeet),
                ElevationBase = options.ElevationBase,
                OverrideColorR = options.OverrideColor.Red,
                OverrideColorG = options.OverrideColor.Green,
                OverrideColorB = options.OverrideColor.Blue
            };
        }
    }

    /// <summary>
    /// Runtime options for Set Elevation (all lengths in Revit internal feet).
    /// </summary>
    public class SetElevationOptions
    {
        public bool AddLabel { get; set; } = true;

        public long TextTypeId { get; set; }

        public double StartElevationFeet { get; set; }

        public double IncrementFeet { get; set; }

        public ElevationBaseType ElevationBase { get; set; }

        public Autodesk.Revit.DB.Color OverrideColor { get; set; } =
            new Autodesk.Revit.DB.Color(255, 128, 0);
    }

    /// <summary>
    /// Linked elevation assignment stored in project metadata and local data file.
    /// </summary>
    public class SetElevationLineRecord
    {
        public int SequenceOrder { get; set; }

        public long CurveElementId { get; set; }

        public long TextNoteElementId { get; set; }

        public double ElevationFeet { get; set; }

        public string FormattedElevation { get; set; } = string.Empty;

        public DateTime AssignedAt { get; set; }
    }

    /// <summary>
    /// Full elevation session linked across curves, persisted per project.
    /// </summary>
    public class SetElevationProjectData
    {
        public string ProjectUniqueId { get; set; } = string.Empty;

        public string ProjectName { get; set; } = string.Empty;

        public DateTime LastUpdated { get; set; }

        public ElevationBaseType ElevationBase { get; set; }

        public double StartElevationFeet { get; set; }

        public double IncrementFeet { get; set; }

        public long TextTypeId { get; set; }

        public int NextSequenceIndex { get; set; }

        public List<SetElevationLineRecord> Lines { get; set; } = new List<SetElevationLineRecord>();
    }

    /// <summary>
    /// Result of applying elevation to a single model curve.
    /// </summary>
    public class SetElevationLineResult
    {
        public long ElementId { get; set; }

        public int SequenceOrder { get; set; }

        public double DisplayElevation { get; set; }

        public string FormattedElevation { get; set; } = string.Empty;

        public long TextNoteElementId { get; set; }

        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;
    }
}
