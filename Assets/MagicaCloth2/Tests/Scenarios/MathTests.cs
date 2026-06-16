// MagicaCloth2 characterization test suite — Phase 10: math / utility units.
// Pure functions with exact expected values — the cheapest, most portable golden for the engine port.
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MagicaCloth2.Tests
{
    public static class MathTests
    {
        public static void Register(List<TestEntry> list)
        {
            list.Add(new TestEntry("math.geometry_and_packing", GeometryAndPacking));
        }

        static IEnumerator GeometryAndPacking(Check check)
        {
            // ClosestPtPointSegment: point above the middle of a horizontal segment -> projects to origin
            var cps = MathUtility.ClosestPtPointSegment(
                new float3(0, 1, 0), new float3(-1, 0, 0), new float3(1, 0, 0));
            check.Approx("closest_pt_segment", cps, new float3(0, 0, 0), 1e-5f);

            // ClosestPtPointTriangle: point above the triangle -> drops to (0.25,0,0.25), uvw=(0.5,0.25,0.25)
            var cpt = MathUtility.ClosestPtPointTriangle(
                new float3(0.25f, 1f, 0.25f),
                new float3(0, 0, 0), new float3(1, 0, 0), new float3(0, 0, 1), out float3 uvw);
            check.Approx("closest_pt_triangle", cpt, new float3(0.25f, 0, 0.25f), 1e-5f);
            check.Approx("triangle_barycentric", uvw, new float3(0.5f, 0.25f, 0.25f), 1e-5f);

            // ClosestPtSegmentSegment: two crossing skew segments 1 unit apart in Y
            float sqDist = MathUtility.ClosestPtSegmentSegment(
                new float3(-1, 0, 0), new float3(1, 0, 0),
                new float3(0, 1, -1), new float3(0, 1, 1),
                out float s, out float t, out float3 c1, out float3 c2);
            check.Approx("segseg_sqdist", sqDist, 1f, 1e-5f);
            check.Approx("segseg_s", s, 0.5f, 1e-5f);
            check.Approx("segseg_t", t, 0.5f, 1e-5f);
            check.Approx("segseg_c1", c1, new float3(0, 0, 0), 1e-5f);
            check.Approx("segseg_c2", c2, new float3(0, 1, 0), 1e-5f);

            // DataUtility.Pack12_20 / Unpack12_20: a (count,start) pair round-trips
            uint packed = DataUtility.Pack12_20(7, 100000);
            DataUtility.Unpack12_20(packed, out int hi, out int low);
            check.Approx("pack_hi", hi, 7, 0);
            check.Approx("pack_low", low, 100000, 0);
            check.Approx("pack_hi_accessor", DataUtility.Unpack12_20Hi(packed), 7, 0);
            check.Approx("pack_low_accessor", DataUtility.Unpack12_20Low(packed), 100000, 0);

            check.Metric("segseg_sqDist", sqDist);
            check.Metric("packed", (int)packed);

            yield break;
        }
    }
}
