using Autodesk.Revit.DB;

namespace effetopo.Services
{
    /// <summary>
    /// Revit 2024 uses SlabShapeEditor.DrawPoint; Revit 2025+ uses AddPoint.
    /// </summary>
    internal static class SlabShapeEditorHelper
    {
        public static bool TryAddPoint(SlabShapeEditor editor, XYZ point)
        {
            if (editor == null || point == null) return false;
            try
            {
#if REVIT2025_OR_GREATER
                editor.AddPoint(point);
#else
                editor.DrawPoint(point);
#endif
                return true;
            }
            catch (System.Exception ex)
            {
                Log.Debug("SlabShapeEditor add point failed at ({X},{Y},{Z}): {Error}",
                    point.X, point.Y, point.Z, ex.Message);
                return false;
            }
        }
    }
}
