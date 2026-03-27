using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using effetopo.Services;
using JetBrains.Annotations;

namespace effetopo.Commands
{
    /// <summary>
    /// Command to join multiple Toposolids with max elevation priority
    /// </summary>
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class JoinMultipleToposolidsCommand : BaseCommand
    {
        public static readonly string COMMAND_NAME = "JoinMultipleToposolids";

        public override string CommandName => COMMAND_NAME;

        protected override Result ExecuteCommand(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                Document doc = uidoc.Document;

                if (doc == null)
                {
                    message = "No active document";
                    return Result.Failed;
                }

                // Check if Toposolid is available (Revit 2024+)
#if !REVIT2024_OR_GREATER
                message = "Toposolid elements are only available in Revit 2024 and later";
                RevitNotificationHandler.ShowGeneralMessageDialog("Version Error", message);
                return Result.Failed;
#endif

                Log.Information("Starting JoinMultipleToposolids command");

                // Prompt user to select Toposolids
                ISelectionFilter filter = new ToposolidSelectionFilter();
                IList<Reference> selectedRefs;

                try
                {
                    selectedRefs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        filter,
                        "Select 2 or more Toposolid elements to merge");
                }
                catch (Exception ex) when (ex.Message.Contains("cancelled") || ex.Message.Contains("canceled") || ex.GetType().Name.Contains("Cancel"))
                {
                    Log.Information("User cancelled selection");
                    return Result.Cancelled;
                }

                if (selectedRefs == null || selectedRefs.Count < 2)
                {
                    message = "Please select at least 2 Toposolid elements";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }

                // Get Toposolid elements from references
#if REVIT2024_OR_GREATER
                var toposolids = new List<Toposolid>();
#else
                var toposolids = new List<Element>();
#endif
                foreach (Reference reference in selectedRefs)
                {
                    Element element = doc.GetElement(reference);
#if REVIT2024_OR_GREATER
                    if (element is Toposolid toposolid)
                    {
                        toposolids.Add(toposolid);
                    }
                    else
                    {
                        Log.Warning("Selected element {Id} is not a Toposolid", GetElementIdValue(element?.Id));
                    }
#else
                    // In older versions, just add the element (will fail at runtime check)
                    toposolids.Add(element);
#endif
                }

                if (toposolids.Count < 2)
                {
                    message = "At least 2 Toposolid elements are required";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }

                Log.Information("Selected {Count} Toposolids for merging", toposolids.Count);

                // Confirm merge operation
                var result = System.Windows.MessageBox.Show(
                    $"Merge {toposolids.Count} Toposolids?\n\n" +
                    "For overlapping areas, the highest elevation will be used.",
                    "Join Multiple Toposolids",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);

                if (result == System.Windows.MessageBoxResult.No)
                {
                    return Result.Cancelled;
                }

                // Ask user if they want to delete originals
                var deleteResult = System.Windows.MessageBox.Show(
                    "Delete original Toposolids after merge?",
                    "Delete Originals",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);

                bool deleteOriginals = deleteResult == System.Windows.MessageBoxResult.Yes;

                // Perform merge
                Log.Information("Checking document transaction state before merge");
                bool docIsModifiable = doc.IsModifiable;
                Log.Information("Document.IsModifiable = {IsModifiable}", docIsModifiable);
                
                using (Transaction tx = new Transaction(doc, "Join Multiple Toposolids"))
                {
                    Log.Information("Starting transaction: {TransactionName}", tx.GetName());
                    tx.Start();
                    Log.Information("Transaction started. Document.IsModifiable = {IsModifiable}", doc.IsModifiable);

                    try
                    {
                        ToposolidMergeService mergeService = ToposolidMergeService.Instance;
                        Log.Information("Calling MergeToposolidsMaxElevation service method");
#if REVIT2024_OR_GREATER
                        Toposolid mergedToposolid = mergeService.MergeToposolidsMaxElevation(
                            doc, toposolids, deleteOriginals);
#else
                        Element mergedToposolid = mergeService.MergeToposolidsMaxElevation(
                            doc, toposolids, deleteOriginals);
#endif

                        if (mergedToposolid == null)
                        {
                            message = "Failed to merge Toposolids";
                            Log.Warning("Merge returned null Toposolid, rolling back transaction");
                            tx.RollBack();
                            return Result.Failed;
                        }

                        Log.Information("Merge successful. Committing transaction");
                        tx.Commit();
                        Log.Information("Transaction committed successfully");

                        // Select the new merged Toposolid
                        uidoc.Selection.SetElementIds(new List<ElementId> { mergedToposolid.Id });

                        RevitNotificationHandler.ShowGeneralMessageDialog(
                            "Success",
                            $"Successfully merged {toposolids.Count} Toposolids into one.\n" +
                            $"New Toposolid ID: {GetElementIdValue(mergedToposolid.Id)}");

                        Log.Information("Successfully completed JoinMultipleToposolids command");
                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error during merge operation");
                        message = $"Error merging Toposolids: {ex.Message}";
                        tx.RollBack();
                        RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                        return Result.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in JoinMultipleToposolidsCommand");
                message = ex.Message;
                RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// Selection filter for Toposolid elements only
        /// </summary>
        private class ToposolidSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
#if REVIT2024_OR_GREATER
                return elem is Toposolid;
#else
                // In older versions, check by category name
                return elem?.Category?.Name == "Topography" || elem?.Category?.Name == "Toposolid";
#endif
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the ElementId value as string (handles version differences)
        /// </summary>
        private string GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id?.Value.ToString() ?? "null";
#else
            return id?.IntegerValue.ToString() ?? "null";
#endif
        }
    }
}
