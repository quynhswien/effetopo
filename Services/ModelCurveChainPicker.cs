using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.UI.Selection;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Picks a model curve then lets the user press Tab to extend along connected curves.
    /// </summary>
    internal static class ModelCurveChainPicker
    {
        private const double EndpointToleranceFeet = 0.05;
        private const int VkTab = 0x09;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        /// <summary>
        /// Pick seed curve, then Tab extends the chain until user confirms with a click.
        /// </summary>
        public static IList<ElementId> PickCurveChainWithTab(
            UIApplication uiApp,
            UIDocument uidoc,
            ISelectionFilter filter,
            string initialPrompt)
        {
            Reference reference = uidoc.Selection.PickObject(ObjectType.Element, filter, initialPrompt);
            Element element = uidoc.Document.GetElement(reference);
            if (element is not ModelCurve seedCurve)
                return Array.Empty<ElementId>();

            Document doc = uidoc.Document;
            View view = doc.ActiveView;
            var graph = BuildConnectivityGraph(doc, view, filter);
            var chain = new ModelCurveChainState(graph, seedCurve.Id);

            uidoc.Selection.SetElementIds(chain.ChainIds);
            uidoc.RefreshActiveView();

            bool tabWasDown = IsTabDown();
            EventHandler<IdlingEventArgs> onIdling = null;
            onIdling = (_, __) =>
            {
                bool tabDown = IsTabDown();
                if (tabDown && !tabWasDown && chain.TryExtendNext())
                {
                    uidoc.Selection.SetElementIds(chain.ChainIds);
                    uidoc.RefreshActiveView();
                }

                tabWasDown = tabDown;
            };

            uiApp.Idling += onIdling;
            try
            {
                uidoc.Selection.PickPoint(
                    chain.ChainIds.Count <= 1
                        ? "Tab để thêm line liên tiếp. Click để xác nhận. Esc giữ line đã chọn."
                        : $"Đã chọn {chain.ChainIds.Count} line(s). Tab để thêm tiếp. Click để xác nhận.");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // Keep whatever chain was built (at least the seed).
            }
            finally
            {
                uiApp.Idling -= onIdling;
            }

            return chain.ChainIds;
        }

        private static bool IsTabDown() => (GetAsyncKeyState(VkTab) & 0x8000) != 0;

        private static Dictionary<ElementId, List<ElementId>> BuildConnectivityGraph(
            Document doc,
            View view,
            ISelectionFilter filter)
        {
            var curves = new Dictionary<ElementId, Curve>();
            var collector = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(CurveElement));

            foreach (Element elem in collector)
            {
                if (elem is not ModelCurve modelCurve)
                    continue;
                if (!filter.AllowElement(modelCurve))
                    continue;

                Curve curve = modelCurve.GeometryCurve;
                if (curve == null)
                    continue;

                curves[modelCurve.Id] = curve;
            }

            var endpointMap = new Dictionary<string, List<ElementId>>();
            foreach (KeyValuePair<ElementId, Curve> pair in curves)
            {
                Curve curve = pair.Value;
                RegisterEndpoint(endpointMap, curve.GetEndPoint(0), pair.Key);
                RegisterEndpoint(endpointMap, curve.GetEndPoint(1), pair.Key);
            }

            var graph = curves.Keys.ToDictionary(id => id, _ => new List<ElementId>());
            foreach (List<ElementId> group in endpointMap.Values)
            {
                if (group.Count < 2)
                    continue;

                for (int i = 0; i < group.Count; i++)
                {
                    for (int j = i + 1; j < group.Count; j++)
                    {
                        ElementId a = group[i];
                        ElementId b = group[j];
                        if (!graph[a].Contains(b))
                            graph[a].Add(b);
                        if (!graph[b].Contains(a))
                            graph[b].Add(a);
                    }
                }
            }

            return graph;
        }

        private static void RegisterEndpoint(
            Dictionary<string, List<ElementId>> endpointMap,
            XYZ point,
            ElementId curveId)
        {
            string key = EndpointKey(point);
            if (!endpointMap.TryGetValue(key, out List<ElementId> list))
            {
                list = new List<ElementId>();
                endpointMap[key] = list;
            }

            if (!list.Contains(curveId))
                list.Add(curveId);
        }

        private static string EndpointKey(XYZ point)
        {
            double t = EndpointToleranceFeet;
            long x = (long)Math.Round(point.X / t);
            long y = (long)Math.Round(point.Y / t);
            long z = (long)Math.Round(point.Z / t);
            return $"{x}:{y}:{z}";
        }

        private sealed class ModelCurveChainState
        {
            private readonly Dictionary<ElementId, List<ElementId>> _graph;
            private readonly List<ElementId> _chain = new List<ElementId>();
            private readonly HashSet<ElementId> _visited = new HashSet<ElementId>();
            private int _candidateIndex;

            public ModelCurveChainState(Dictionary<ElementId, List<ElementId>> graph, ElementId seedId)
            {
                _graph = graph ?? new Dictionary<ElementId, List<ElementId>>();
                _chain.Add(seedId);
                _visited.Add(seedId);
            }

            public IList<ElementId> ChainIds => _chain;

            public bool TryExtendNext()
            {
                ElementId tip = _chain[_chain.Count - 1];
                if (!_graph.TryGetValue(tip, out List<ElementId> neighbors) || neighbors.Count == 0)
                    return false;

                List<ElementId> candidates = neighbors
                    .Where(id => !_visited.Contains(id))
                    .OrderBy(id => id.Value)
                    .ToList();

                if (candidates.Count == 0)
                    return false;

                if (_candidateIndex >= candidates.Count)
                    _candidateIndex = 0;

                ElementId next = candidates[_candidateIndex];
                _candidateIndex = (_candidateIndex + 1) % candidates.Count;

                _chain.Add(next);
                _visited.Add(next);
                return true;
            }
        }
    }
#endif
}
