using System;

namespace effetopo.Models
{
    public class CreateContourLineSettings
    {
        public double IntervalDisplay { get; set; } = 1;

        public bool UseMajorMinorContours { get; set; } = true;

        public double MajorIntervalDisplay { get; set; } = 5;

        public string MinorLineStyleName { get; set; } = string.Empty;

        public string MajorLineStyleName { get; set; } = string.Empty;

        public bool AssignLevel { get; set; } = true;

        public long LevelId { get; set; }

        public CreateContourLineOptions ToOptions(bool useMillimeters)
        {
            double intervalFeet = useMillimeters ? IntervalDisplay / 304.8 : IntervalDisplay;
            double majorIntervalFeet = useMillimeters ? MajorIntervalDisplay / 304.8 : MajorIntervalDisplay;

            return new CreateContourLineOptions
            {
                IntervalFeet = intervalFeet,
                UseMajorMinorContours = UseMajorMinorContours,
                MajorIntervalFeet = majorIntervalFeet,
                MinorLineStyleName = MinorLineStyleName ?? string.Empty,
                MajorLineStyleName = MajorLineStyleName ?? string.Empty,
                AssignLevel = AssignLevel,
                LevelId = LevelId
            };
        }
    }

    public class CreateContourLineOptions
    {
        public double IntervalFeet { get; set; }

        public bool UseMajorMinorContours { get; set; }

        public double MajorIntervalFeet { get; set; }

        public string MinorLineStyleName { get; set; } = string.Empty;

        public string MajorLineStyleName { get; set; } = string.Empty;

        public bool AssignLevel { get; set; }

        public long LevelId { get; set; }
    }

    public class CreateContourLineResult
    {
        public int CurveCount { get; set; }

        public int MajorCurveCount { get; set; }

        public int MinorCurveCount { get; set; }

        public int ElevationLevelCount { get; set; }

        public int MajorElevationLevelCount { get; set; }

        public double MinElevationFeet { get; set; }

        public double MaxElevationFeet { get; set; }

        public string Summary
        {
            get
            {
                if (MajorCurveCount > 0 || MinorCurveCount > 0)
                {
                    return
                        $"Created {CurveCount} model curve(s) at {ElevationLevelCount} elevation level(s) " +
                        $"({MajorCurveCount} major, {MinorCurveCount} minor).";
                }

                return $"Created {CurveCount} model curve(s) at {ElevationLevelCount} elevation level(s).";
            }
        }
    }
}
