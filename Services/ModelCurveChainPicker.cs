using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Picks one or more model curves via window drag and/or multi-click (Tab cycles candidates).
    /// </summary>
    internal static class ModelCurveChainPicker
    {
        /// <summary>
        /// Drag a window to select curves, then optionally click more (Tab cycles highlight). Enter finishes.
        /// </summary>
        public static IList<ElementId> PickMultipleModelCurves(
            UIDocument uidoc,
            ISelectionFilter filter,
            string initialPrompt)
        {
            if (uidoc == null)
                throw new ArgumentNullException(nameof(uidoc));
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));

            var selected = new HashSet<ElementId>();

            try
            {
                ICollection<Element> windowPicked = uidoc.Selection.PickElementsByRectangle(
                    filter,
                    initialPrompt +
                    " Kéo cửa sổ chọn nhiều line, hoặc Esc để click từng line (Tab chuyển highlight).");

                foreach (Element element in windowPicked)
                {
                    if (element?.Id != null && element.Id != ElementId.InvalidElementId)
                        selected.Add(element.Id);
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User skipped window select — continue to click pick.
            }

            try
            {
                string clickPrompt = selected.Count > 0
                    ? $"Đã chọn {selected.Count} line. Click thêm (Tab chuyển highlight). Enter khi xong."
                    : initialPrompt + " Click từng line (Tab chuyển highlight). Enter khi xong.";

                IList<Reference> refs = uidoc.Selection.PickObjects(
                    ObjectType.Element,
                    filter,
                    clickPrompt);

                foreach (Reference reference in refs)
                {
                    if (reference != null)
                        selected.Add(reference.ElementId);
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                if (selected.Count == 0)
                    throw;
            }

            return new List<ElementId>(selected);
        }
    }
#endif
}
