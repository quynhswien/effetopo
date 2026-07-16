using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Samples the exact Toposolid solid Revit would produce by temporarily applying
    /// calculated vertices inside a rolled-back transaction.
    /// </summary>
    internal static class ToposolidPreviewGeometrySampler
    {
        public static IList<GeometryObject> SampleExactSolid(
            Document doc,
            Toposolid toposolid,
            IList<ModifyTopoService.SculptVertexSnapshot> baseVertices,
            IList<ModifyTopoService.SculptVertexSnapshot> targetVertices)
        {
            if (doc == null || toposolid == null || targetVertices == null || targetVertices.Count == 0)
                return Array.Empty<GeometryObject>();

            using (Transaction tx = new Transaction(doc, "Preview sample Toposolid"))
            {
                tx.Start();
                try
                {
                    ModifyTopoService.Instance.ApplyCalculatedVertices(
                        doc, toposolid, baseVertices, targetVertices, logResult: false);
                    doc.Regenerate();

                    IList<GeometryObject> cloned = CloneToposolidGeometry(toposolid);
                    tx.RollBack();

                    Log.Information(
                        "Revit-exact preview sampled: {SolidCount} geometry object(s), {TargetVerts} target verts",
                        cloned.Count, targetVertices.Count);
                    return cloned;
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && tx.GetStatus() == TransactionStatus.Started)
                        tx.RollBack();
                    Log.Warning("Revit-exact preview sample failed: {Error}", ex.Message);
                    return Array.Empty<GeometryObject>();
                }
            }
        }

        private static IList<GeometryObject> CloneToposolidGeometry(Toposolid toposolid)
        {
            var result = new List<GeometryObject>();
            if (toposolid == null)
                return result;

            var opt = new Options
            {
                DetailLevel = ViewDetailLevel.Fine,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };

            GeometryElement ge = toposolid.get_Geometry(opt);
            if (ge == null)
                return result;

            foreach (GeometryObject obj in ge)
            {
                if (obj is Solid solid && solid.Volume > 1e-9)
                {
                    IList<GeometryObject> cloned = CloneSolid(solid);
                    if (cloned != null && cloned.Count > 0)
                        result.AddRange(cloned);
                }
            }

            return result;
        }

        private static IList<GeometryObject> CloneSolid(Solid solid)
        {
            var builder = new TessellatedShapeBuilder
            {
                Target = TessellatedShapeBuilderTarget.AnyGeometry,
                Fallback = TessellatedShapeBuilderFallback.Mesh
            };

            builder.OpenConnectedFaceSet(false);
            int faceCount = 0;

            foreach (Face face in solid.Faces)
            {
                Mesh mesh = face.Triangulate();
                if (mesh == null || mesh.NumTriangles == 0)
                    continue;

                for (int i = 0; i < mesh.NumTriangles; i++)
                {
                    MeshTriangle tri = mesh.get_Triangle(i);
                    XYZ v0 = tri.get_Vertex(0);
                    XYZ v1 = tri.get_Vertex(1);
                    XYZ v2 = tri.get_Vertex(2);
                    XYZ normal = (v1 - v0).CrossProduct(v2 - v0);
                    if (normal.GetLength() < 1e-9)
                        continue;

                    normal = normal.Normalize();
                    builder.AddFace(new TessellatedFace(
                        new List<XYZ> { v0, v1, v2 },
                        ElementId.InvalidElementId));
                    faceCount++;
                }
            }

            builder.CloseConnectedFaceSet();
            if (faceCount == 0)
                return Array.Empty<GeometryObject>();

            builder.Build();
            TessellatedShapeBuilderResult built = builder.GetBuildResult();
            return built?.GetGeometricalObjects() ?? Array.Empty<GeometryObject>();
        }
    }
#endif
}
