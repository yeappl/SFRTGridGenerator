// =============================================================================
// GridTubeGenerator.cs
//
// Eclipse Scripting API (ESAPI) script.
// Generates a grid of either superior-inferior (SI) oriented tubes, or
// spheres, inside a user-selected target ROI, for Spatially Fractionated
// Radiation Therapy (SFRT) planning.
//
// Output: a single new structure, named "GRID_TUBES" (tube mode) or
// "GRID_SPHERE" (sphere mode), containing all generated shapes.
//
// GAP DEFINITION
//   "Tube/Sphere gap" is the boundary-to-boundary (surface-to-surface)
//   spacing between neighbouring tubes/spheres, NOT the centre-to-centre
//   spacing. The lattice pitch (centre-to-centre spacing) used internally is
//   therefore pitch = diameter + gap.
//
// SHAPE MODES
//   Tube:   Same hexagonal in-plane lattice + per-slice safety testing as
//           the original version of this script. Tube length in the SI
//           direction is whatever contiguous run of "safe" slices each
//           lattice column achieves - tubes are not a fixed length.
//   Sphere: A simple-cubic 3D lattice (pitch in x, y, and z). Each candidate
//           sphere center is tested as a single rigid 3D safety check
//           (sampling a full ring of points, in 3D, around the sphere's own
//           surface + boundary margin) rather than being tested and split
//           slice-by-slice. Accepted spheres are drawn as circular contours
//           of position-dependent radius on every slice they intersect.
//
// Both modes use Structure.IsPointInsideSegment(VVector) for containment
// testing (see IsCandidateSafeAtSlice / IsSphereCandidateSafe below) rather
// than manual point-in-polygon / distance math, since ESAPI does not expose
// RayStation-style structure algebra (SetAlgebraExpression) or native
// cylinder/sphere primitives to intersect against a contracted ROI.
//
// CLINICAL / VALIDATION NOTE
//   This is a template, not a commissioned or clinically validated tool.
//   Review the generated geometry slice-by-slice (diameter, spacing,
//   boundary clearance) before any clinical use. Confirm units, coordinate
//   conventions, and structure algebra behave as expected on your own
//   Eclipse/ESAPI version before relying on this output.
//
// PROJECT REFERENCES
//   Standard ESAPI references (VMS.TPS.Common.Model.API,
//   VMS.TPS.Common.Model.Types) plus the standard WPF assemblies
//   (PresentationCore, PresentationFramework, WindowsBase, System.Xaml) for
//   the input dialog, which is defined in InputDialog.xaml /
//   InputDialog.xaml.cs (build action: Page, for InputDialog.xaml).
//
// ADJUSTABLE CONSTANTS
//   See the "Tunable constants" region below (circle/sphere sampling
//   resolution, phase-search resolution, minimum segment length, contour
//   polygon resolution). These are not exposed in the input dialog because
//   the task only calls for a fixed set of user inputs, but they materially
//   affect runtime and packing quality and are worth tuning per site/target
//   size.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Media3D;

using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace VMS.TPS
{
    // Shared between Script and InputDialog - which shape mode was chosen.
    public enum GridShape
    {
        Tube,
        Sphere
    }

    public class Script
    {
        public Script()
        {
        }

        public void Execute(ScriptContext context)
        {
            Patient patient = context.Patient;
            StructureSet structureSet = context.StructureSet;

            if (patient == null || structureSet == null)
            {
                MessageBox.Show(
                    "Open a patient with a structure set before running this script.",
                    "Grid Tube Generator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            List<string> roiNames = structureSet.Structures
                .Select(s => s.Id)
                .OrderBy(id => id)
                .ToList();

            if (roiNames.Count == 0)
            {
                MessageBox.Show(
                    "The current structure set has no structures.",
                    "Grid Tube Generator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            InputDialog dialog = new InputDialog(roiNames);

            bool? dialogResult = dialog.ShowDialog();

            if (dialogResult != true)
            {
                return;
            }

            string targetRoiName = dialog.SelectedRoi;
            double diameterMm = dialog.DiameterMm;
            double gapMm = dialog.GapMm;
            double boundaryMarginMm = dialog.BoundaryMarginMm;
            GridShape shape = dialog.SelectedShape;

            try
            {
                RunGeneration(
                    patient,
                    structureSet,
                    targetRoiName,
                    diameterMm,
                    gapMm,
                    boundaryMarginMm,
                    shape);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Grid generation failed:\n\n" + ex.Message,
                    "Grid Tube Generator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // =====================================================================
        // Tunable constants
        // =====================================================================
        #region Tunable constants

        // Number of points sampled around the safety-check ring at each slice
        // (tube mode).
        private const int CircleSafetySamples = 16;

        // Number of points sampled over the full 3D safety-check sphere
        // (sphere mode).
        private const int SphereSafetySamples = 32;

        // Number of points used to draw each circular contour.
        private const int ContourPolygonPoints = 32;

        // Number of lattice phase offsets tested per axis in tube mode
        // (NxN total phases). Higher = better packing optimization, slower.
        private const int PhaseSearchSteps = 3;

        // Number of lattice phase offsets tested per axis in sphere mode
        // (NxNxN total phases). Kept lower than PhaseSearchSteps since the
        // search space grows with the cube of this value.
        private const int SpherePhaseSearchSteps = 2;

        // Minimum number of contiguous "safe" slices required to keep a tube
        // segment. Guards against single-slice slivers. (Tube mode only.)
        private const int MinSegmentSlices = 2;

        // Maximum allowed gap (in slice indices) when merging safe slices
        // into a contiguous segment. 1 = slices must be strictly contiguous.
        // (Tube mode only.)
        private const int MaxContiguousIndexGap = 1;

        // Smallest local sphere-slice radius (mm) worth drawing as a contour.
        // Slices closer than this to a sphere's pole produce a degenerate
        // sliver and are skipped. (Sphere mode only.)
        private const double MinSphereSliceRadiusMm = 0.1;

        #endregion

        // =====================================================================
        // Shared validation, bounds, and shape dispatch
        // =====================================================================

        private void RunGeneration(
            Patient patient,
            StructureSet structureSet,
            string targetRoiName,
            double diameterMm,
            double gapMm,
            double boundaryMarginMm,
            GridShape shape)
        {
            if (diameterMm <= 0)
            {
                throw new ArgumentException("Diameter must be positive.");
            }

            if (gapMm <= 0)
            {
                throw new ArgumentException("Tube/Sphere gap (boundary-to-boundary spacing) must be positive.");
            }

            if (boundaryMarginMm < 0)
            {
                throw new ArgumentException("Boundary margin cannot be negative.");
            }

            Structure targetStructure = structureSet.Structures
                .FirstOrDefault(s => s.Id == targetRoiName);

            if (targetStructure == null)
            {
                throw new ArgumentException("Target ROI '" + targetRoiName + "' was not found.");
            }

            if (targetStructure.IsEmpty)
            {
                throw new ArgumentException("Target ROI '" + targetRoiName + "' has no geometry.");
            }

            Image image = structureSet.Image;

            double radiusMm = diameterMm / 2.0;
            double safetyRadiusMm = radiusMm + boundaryMarginMm;

            // Gap is specified boundary-to-boundary, so the lattice pitch
            // (centre-to-centre spacing) is diameter + gap.
            double pitchMm = diameterMm + gapMm;

            // ---- Bounding box of the target, in the image/patient coordinate
            // ---- frame used by VVector / IsPointInsideSegment.
            Rect3D bounds = targetStructure.MeshGeometry.Bounds;

            double xMin = bounds.X;
            double xMax = bounds.X + bounds.SizeX;
            double yMin = bounds.Y;
            double yMax = bounds.Y + bounds.SizeY;
            double zMinTarget = bounds.Z;
            double zMaxTarget = bounds.Z + bounds.SizeZ;

            if (shape == GridShape.Tube)
            {
                RunTubeGeneration(
                    patient, structureSet, targetStructure, image,
                    radiusMm, safetyRadiusMm, pitchMm,
                    xMin, xMax, yMin, yMax, zMinTarget, zMaxTarget);
            }
            else
            {
                RunSphereGeneration(
                    patient, structureSet, targetStructure, image,
                    radiusMm, safetyRadiusMm, pitchMm,
                    xMin, xMax, yMin, yMax, zMinTarget, zMaxTarget);
            }
        }

        // =====================================================================
        // Tube generation pipeline
        // =====================================================================

        private void RunTubeGeneration(
            Patient patient,
            StructureSet structureSet,
            Structure targetStructure,
            Image image,
            double radiusMm,
            double safetyRadiusMm,
            double pitchMm,
            double xMin, double xMax,
            double yMin, double yMax,
            double zMinTarget, double zMaxTarget)
        {
            // Shrink the SI (z) range by the boundary margin directly, so tube
            // ends are kept at least boundaryMarginMm from the target's
            // superior/inferior extent, not just its in-plane boundary.
            double zMinAllowed = zMinTarget + (safetyRadiusMm - radiusMm);
            double zMaxAllowed = zMaxTarget - (safetyRadiusMm - radiusMm);

            if (zMinAllowed >= zMaxAllowed)
            {
                throw new InvalidOperationException(
                    "Boundary margin is too large for the target's SI extent - no room left for any tube.");
            }

            // Shrink the in-plane candidate-center search box similarly, so we
            // don't waste candidates whose centers could never satisfy the
            // margin.
            double xMinC = xMin + safetyRadiusMm;
            double xMaxC = xMax - safetyRadiusMm;
            double yMinC = yMin + safetyRadiusMm;
            double yMaxC = yMax - safetyRadiusMm;

            if (xMinC >= xMaxC || yMinC >= yMaxC)
            {
                throw new InvalidOperationException(
                    "Diameter + boundary margin leaves no room for any tube center in-plane.");
            }

            // ---- Map the allowed Z range onto image slice indices.
            List<int> candidateSliceIndices = GetSliceIndicesInRange(image, zMinAllowed, zMaxAllowed);

            if (candidateSliceIndices.Count < MinSegmentSlices)
            {
                throw new InvalidOperationException(
                    "Not enough image slices remain within the allowed SI range after applying the boundary margin.");
            }

            // ---- Multi-phase hexagonal lattice search: try several phase
            // ---- offsets and keep whichever gives the greatest total safe
            // ---- SI tube length.
            List<TubeSegment> bestTubes = new List<TubeSegment>();
            double bestScore = -1.0;

            int steps = Math.Max(1, PhaseSearchSteps);

            for (int ix = 0; ix < steps; ix++)
            {
                for (int iy = 0; iy < steps; iy++)
                {
                    double phaseX = pitchMm * ix / steps;
                    double phaseY = (pitchMm * Math.Sqrt(3.0) / 2.0) * iy / steps;

                    List<Tuple<double, double>> candidateCenters = GenerateHexCandidates(
                        xMinC, xMaxC, yMinC, yMaxC, pitchMm, phaseX, phaseY);

                    List<TubeSegment> candidateTubes = new List<TubeSegment>();

                    foreach (Tuple<double, double> center in candidateCenters)
                    {
                        double cx = center.Item1;
                        double cy = center.Item2;

                        List<int> safeIndices = GetSafeSliceIndices(
                            targetStructure,
                            image,
                            cx,
                            cy,
                            safetyRadiusMm,
                            candidateSliceIndices);

                        List<List<int>> segments = SplitIntoContiguousSegments(safeIndices);

                        foreach (List<int> segment in segments)
                        {
                            if (segment.Count < MinSegmentSlices)
                            {
                                continue;
                            }

                            double zA = SliceIndexToZ(image, segment.First());
                            double zB = SliceIndexToZ(image, segment.Last());

                            candidateTubes.Add(new TubeSegment
                            {
                                CenterX = cx,
                                CenterY = cy,
                                ZMin = Math.Min(zA, zB),
                                ZMax = Math.Max(zA, zB),
                                SliceIndexMin = segment.First() <= segment.Last() ? segment.First() : segment.Last(),
                                SliceIndexMax = segment.First() <= segment.Last() ? segment.Last() : segment.First(),
                            });
                        }
                    }

                    double score = candidateTubes.Sum(t => Math.Abs(t.ZMax - t.ZMin));

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTubes = candidateTubes;
                    }
                }
            }

            if (bestTubes.Count == 0)
            {
                throw new InvalidOperationException(
                    "No valid tube positions were found for the given diameter, gap, and boundary margin.");
            }

            // ---- Build the output structure.
            const string outputRoiName = "GRID_TUBES";

            patient.BeginModifications();

            Structure existing = structureSet.Structures.FirstOrDefault(s => s.Id == outputRoiName);
            if (existing != null)
            {
                structureSet.RemoveStructure(existing);
            }

            Structure gridTubes = structureSet.AddStructure("CONTROL", outputRoiName);

            foreach (TubeSegment tube in bestTubes)
            {
                for (int sliceIndex = tube.SliceIndexMin; sliceIndex <= tube.SliceIndexMax; sliceIndex++)
                {
                    double z = SliceIndexToZ(image, sliceIndex);

                    VVector[] polygon = BuildCirclePolygon(
                        tube.CenterX, tube.CenterY, z, radiusMm, ContourPolygonPoints);

                    gridTubes.AddContourOnImagePlane(polygon, sliceIndex);
                }
            }

            MessageBox.Show(
                string.Format(
                    "Created structure '{0}'.\n\n" +
                    "Tube segments generated: {1}\n" +
                    "Total SI length across all tubes: {2:F1} mm\n\n" +
                    "This is an unvalidated template - review geometry slice-by-slice " +
                    "before clinical use.",
                    outputRoiName,
                    bestTubes.Count,
                    bestScore),
                "Grid Tube Generator",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // =====================================================================
        // Sphere generation pipeline
        // =====================================================================

        private void RunSphereGeneration(
            Patient patient,
            StructureSet structureSet,
            Structure targetStructure,
            Image image,
            double radiusMm,
            double safetyRadiusMm,
            double pitchMm,
            double xMin, double xMax,
            double yMin, double yMax,
            double zMin, double zMax)
        {
            // Shrink the whole 3D candidate-center box by the safety radius,
            // so candidates that could never satisfy the margin in any
            // direction aren't even tested.
            double xMinC = xMin + safetyRadiusMm;
            double xMaxC = xMax - safetyRadiusMm;
            double yMinC = yMin + safetyRadiusMm;
            double yMaxC = yMax - safetyRadiusMm;
            double zMinC = zMin + safetyRadiusMm;
            double zMaxC = zMax - safetyRadiusMm;

            if (xMinC >= xMaxC || yMinC >= yMaxC || zMinC >= zMaxC)
            {
                throw new InvalidOperationException(
                    "Diameter + boundary margin leaves no room for any sphere center in 3D.");
            }

            List<Tuple<double, double, double>> unitSpherePoints =
                GenerateFibonacciSpherePoints(SphereSafetySamples);

            List<SphereCenter> bestSpheres = new List<SphereCenter>();
            int bestCount = -1;

            int steps = Math.Max(1, SpherePhaseSearchSteps);

            for (int ix = 0; ix < steps; ix++)
            {
                for (int iy = 0; iy < steps; iy++)
                {
                    for (int iz = 0; iz < steps; iz++)
                    {
                        double phaseX = pitchMm * ix / steps;
                        double phaseY = pitchMm * iy / steps;
                        double phaseZ = pitchMm * iz / steps;

                        List<Tuple<double, double, double>> candidateCenters = GenerateCubicCandidates(
                            xMinC, xMaxC, yMinC, yMaxC, zMinC, zMaxC,
                            pitchMm, phaseX, phaseY, phaseZ);

                        List<SphereCenter> candidateSpheres = new List<SphereCenter>();

                        foreach (Tuple<double, double, double> center in candidateCenters)
                        {
                            double cx = center.Item1;
                            double cy = center.Item2;
                            double cz = center.Item3;

                            if (IsSphereCandidateSafe(targetStructure, cx, cy, cz, safetyRadiusMm, unitSpherePoints))
                            {
                                candidateSpheres.Add(new SphereCenter
                                {
                                    CenterX = cx,
                                    CenterY = cy,
                                    CenterZ = cz,
                                });
                            }
                        }

                        if (candidateSpheres.Count > bestCount)
                        {
                            bestCount = candidateSpheres.Count;
                            bestSpheres = candidateSpheres;
                        }
                    }
                }
            }

            if (bestSpheres.Count == 0)
            {
                throw new InvalidOperationException(
                    "No valid sphere positions were found for the given diameter, gap, and boundary margin.");
            }

            // ---- Build the output structure.
            const string outputRoiName = "GRID_SPHERE";

            patient.BeginModifications();

            Structure existing = structureSet.Structures.FirstOrDefault(s => s.Id == outputRoiName);
            if (existing != null)
            {
                structureSet.RemoveStructure(existing);
            }

            Structure gridSpheres = structureSet.AddStructure("CONTROL", outputRoiName);

            foreach (SphereCenter sphere in bestSpheres)
            {
                List<int> sliceIndices = GetSliceIndicesInRange(
                    image, sphere.CenterZ - radiusMm, sphere.CenterZ + radiusMm);

                foreach (int sliceIndex in sliceIndices)
                {
                    double z = SliceIndexToZ(image, sliceIndex);
                    double dz = z - sphere.CenterZ;
                    double localRadiusSquared = (radiusMm * radiusMm) - (dz * dz);

                    if (localRadiusSquared <= 0)
                    {
                        continue;
                    }

                    double localRadiusMm = Math.Sqrt(localRadiusSquared);

                    if (localRadiusMm < MinSphereSliceRadiusMm)
                    {
                        continue;
                    }

                    VVector[] polygon = BuildCirclePolygon(
                        sphere.CenterX, sphere.CenterY, z, localRadiusMm, ContourPolygonPoints);

                    gridSpheres.AddContourOnImagePlane(polygon, sliceIndex);
                }
            }

            MessageBox.Show(
                string.Format(
                    "Created structure '{0}'.\n\n" +
                    "Spheres generated: {1}\n\n" +
                    "This is an unvalidated template - review geometry slice-by-slice " +
                    "before clinical use.",
                    outputRoiName,
                    bestSpheres.Count),
                "Grid Tube Generator",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // =====================================================================
        // Slice / coordinate helpers (shared)
        // =====================================================================

        private static double SliceIndexToZ(Image image, int sliceIndex)
        {
            return image.Origin.z + sliceIndex * image.ZRes;
        }

        private static List<int> GetSliceIndicesInRange(Image image, double zMin, double zMax)
        {
            List<int> indices = new List<int>();

            for (int i = 0; i < image.ZSize; i++)
            {
                double z = SliceIndexToZ(image, i);

                if (z >= Math.Min(zMin, zMax) && z <= Math.Max(zMin, zMax))
                {
                    indices.Add(i);
                }
            }

            // Ensure ascending order regardless of image.ZRes sign.
            indices.Sort();
            return indices;
        }

        // =====================================================================
        // Hexagonal lattice candidate generation (tube mode)
        // =====================================================================

        private static List<Tuple<double, double>> GenerateHexCandidates(
            double xMin, double xMax, double yMin, double yMax,
            double pitchMm, double phaseX, double phaseY)
        {
            List<Tuple<double, double>> candidates = new List<Tuple<double, double>>();

            double rowSpacing = pitchMm * Math.Sqrt(3.0) / 2.0;

            double y = yMin + phaseY;
            int rowIndex = 0;

            while (y <= yMax)
            {
                double xOffset = (rowIndex % 2 != 0) ? 0.5 * pitchMm : 0.0;
                double x = xMin + phaseX + xOffset;

                while (x <= xMax)
                {
                    candidates.Add(Tuple.Create(x, y));
                    x += pitchMm;
                }

                y += rowSpacing;
                rowIndex++;
            }

            return candidates;
        }

        // =====================================================================
        // Cubic lattice candidate generation (sphere mode)
        // =====================================================================

        private static List<Tuple<double, double, double>> GenerateCubicCandidates(
            double xMin, double xMax, double yMin, double yMax, double zMin, double zMax,
            double pitchMm, double phaseX, double phaseY, double phaseZ)
        {
            List<Tuple<double, double, double>> candidates = new List<Tuple<double, double, double>>();

            for (double x = xMin + phaseX; x <= xMax; x += pitchMm)
            {
                for (double y = yMin + phaseY; y <= yMax; y += pitchMm)
                {
                    for (double z = zMin + phaseZ; z <= zMax; z += pitchMm)
                    {
                        candidates.Add(Tuple.Create(x, y, z));
                    }
                }
            }

            return candidates;
        }

        // Evenly distributed points on a unit sphere via the Fibonacci sphere
        // method - used to sample a 3D safety ring around each sphere
        // candidate, analogous to the 2D ring sampled per-slice for tubes.
        private static List<Tuple<double, double, double>> GenerateFibonacciSpherePoints(int n)
        {
            List<Tuple<double, double, double>> points = new List<Tuple<double, double, double>>();

            double goldenAngle = Math.PI * (3.0 - Math.Sqrt(5.0));

            for (int i = 0; i < n; i++)
            {
                double yUnit = 1.0 - (i / (double)Math.Max(1, n - 1)) * 2.0;
                double radiusAtY = Math.Sqrt(Math.Max(0.0, 1.0 - yUnit * yUnit));
                double theta = goldenAngle * i;

                double xUnit = Math.Cos(theta) * radiusAtY;
                double zUnit = Math.Sin(theta) * radiusAtY;

                points.Add(Tuple.Create(xUnit, yUnit, zUnit));
            }

            return points;
        }

        // =====================================================================
        // Per-candidate safety testing
        // =====================================================================

        // Tests, for one candidate (cx, cy) and one z-slice, whether a ring of
        // points at radius = safetyRadiusMm (tube radius + boundary margin),
        // plus the center itself, all lie inside the target structure. This
        // uses ESAPI's native 3D point-in-structure test rather than manual
        // polygon math. (Tube mode.)
        private static bool IsCandidateSafeAtSlice(
            Structure targetStructure,
            double cx, double cy, double z,
            double safetyRadiusMm)
        {
            VVector centerPoint = new VVector(cx, cy, z);

            if (!targetStructure.IsPointInsideSegment(centerPoint))
            {
                return false;
            }

            for (int k = 0; k < CircleSafetySamples; k++)
            {
                double angle = 2.0 * Math.PI * k / CircleSafetySamples;
                double x = cx + safetyRadiusMm * Math.Cos(angle);
                double y = cy + safetyRadiusMm * Math.Sin(angle);

                if (!targetStructure.IsPointInsideSegment(new VVector(x, y, z)))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<int> GetSafeSliceIndices(
            Structure targetStructure,
            Image image,
            double cx, double cy,
            double safetyRadiusMm,
            List<int> candidateSliceIndices)
        {
            List<int> safe = new List<int>();

            foreach (int sliceIndex in candidateSliceIndices)
            {
                double z = SliceIndexToZ(image, sliceIndex);

                if (IsCandidateSafeAtSlice(targetStructure, cx, cy, z, safetyRadiusMm))
                {
                    safe.Add(sliceIndex);
                }
            }

            return safe;
        }

        private static List<List<int>> SplitIntoContiguousSegments(List<int> sortedIndices)
        {
            List<List<int>> segments = new List<List<int>>();

            if (sortedIndices.Count == 0)
            {
                return segments;
            }

            List<int> current = new List<int> { sortedIndices[0] };

            for (int i = 1; i < sortedIndices.Count; i++)
            {
                if (sortedIndices[i] - current.Last() <= MaxContiguousIndexGap)
                {
                    current.Add(sortedIndices[i]);
                }
                else
                {
                    segments.Add(current);
                    current = new List<int> { sortedIndices[i] };
                }
            }

            segments.Add(current);
            return segments;
        }

        // Tests whether an entire candidate sphere (center + a ring of points
        // sampled over its full 3D surface at radius = safetyRadiusMm) lies
        // inside the target structure. This enforces the boundary margin
        // uniformly in all directions (in-plane and SI) in a single test,
        // unlike the tube case which needs separate in-plane + SI-trim
        // handling. (Sphere mode.)
        private static bool IsSphereCandidateSafe(
            Structure targetStructure,
            double cx, double cy, double cz,
            double safetyRadiusMm,
            List<Tuple<double, double, double>> unitSpherePoints)
        {
            if (!targetStructure.IsPointInsideSegment(new VVector(cx, cy, cz)))
            {
                return false;
            }

            foreach (Tuple<double, double, double> u in unitSpherePoints)
            {
                double x = cx + safetyRadiusMm * u.Item1;
                double y = cy + safetyRadiusMm * u.Item2;
                double z = cz + safetyRadiusMm * u.Item3;

                if (!targetStructure.IsPointInsideSegment(new VVector(x, y, z)))
                {
                    return false;
                }
            }

            return true;
        }

        // =====================================================================
        // Contour construction (shared)
        // =====================================================================

        private static VVector[] BuildCirclePolygon(
            double cx, double cy, double z, double radiusMm, int numPoints)
        {
            VVector[] points = new VVector[numPoints];

            for (int i = 0; i < numPoints; i++)
            {
                double angle = 2.0 * Math.PI * i / numPoints;
                double x = cx + radiusMm * Math.Cos(angle);
                double y = cy + radiusMm * Math.Sin(angle);

                points[i] = new VVector(x, y, z);
            }

            return points;
        }

        // =====================================================================
        // Data types
        // =====================================================================

        private class TubeSegment
        {
            public double CenterX;
            public double CenterY;
            public double ZMin;
            public double ZMax;
            public int SliceIndexMin;
            public int SliceIndexMax;
        }

        private class SphereCenter
        {
            public double CenterX;
            public double CenterY;
            public double CenterZ;
        }
    }
}
