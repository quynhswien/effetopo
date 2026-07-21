using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    internal static class CreateContourLineRevitHelper
    {
        private const double ElevationTolerance = 1e-4;

        internal static IReadOnlyList<string> GetLineStyleNames(Document doc)
        {
            if (doc == null) return Array.Empty<string>();

            var names = new List<string> { string.Empty };
            try
            {
                Category linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                foreach (Category subCategory in linesCategory.SubCategories)
                {
                    if (!string.IsNullOrWhiteSpace(subCategory?.Name))
                        names.Add(subCategory.Name);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read line styles from document");
            }

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => string.IsNullOrEmpty(n) ? 0 : 1)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static GraphicsStyle ResolveLineStyle(Document doc, string lineStyleName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(lineStyleName))
                return null;

            try
            {
                Category linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                foreach (Category subCategory in linesCategory.SubCategories)
                {
                    if (!subCategory.Name.Equals(lineStyleName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return subCategory.GetGraphicsStyle(GraphicsStyleType.Projection);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not resolve line style '{LineStyle}'", lineStyleName);
            }

            return null;
        }

        internal static IReadOnlyList<Level> GetSortedLevels(Document doc)
        {
            if (doc == null) return Array.Empty<Level>();

            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static bool TryAssignReferenceLevel(Element curveElement, ElementId levelId)
        {
            if (curveElement == null || levelId == null || levelId == ElementId.InvalidElementId)
                return false;

            Parameter levelParam = curveElement.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
            if (levelParam != null && !levelParam.IsReadOnly)
            {
                levelParam.Set(levelId);
                return true;
            }

            return false;
        }

        internal static bool IsMajorContourElevation(double elevationFeet, double majorIntervalFeet)
        {
            if (majorIntervalFeet <= 0)
                return false;

            double remainder = Math.Abs(elevationFeet % majorIntervalFeet);
            return remainder < ElevationTolerance || Math.Abs(remainder - majorIntervalFeet) < ElevationTolerance;
        }

        internal static string FormatLineStyleDisplayName(string lineStyleName) =>
            string.IsNullOrEmpty(lineStyleName) ? "(Default)" : lineStyleName;
    }
#endif
}
