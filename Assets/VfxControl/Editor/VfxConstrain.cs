// VFX Control — constrained-proportions math (the chain-lock on multi-component values).
//
// Pure, stateless: scaling one component scales the rest by the same ratio (Unity's
// constrained-proportions behavior). Extracted from the property rows so it's unit-testable.

using UnityEngine;

namespace VfxControl.EditorTools
{
    internal static class VfxConstrain
    {
        public static Vector2 Vec2(Vector2 a, Vector2 b)
        {
            var r = Components(new[] { a.x, a.y }, new[] { b.x, b.y });
            return new Vector2(r[0], r[1]);
        }

        public static Vector3 Vec3(Vector3 a, Vector3 b)
        {
            var r = Components(new[] { a.x, a.y, a.z }, new[] { b.x, b.y, b.z });
            return new Vector3(r[0], r[1], r[2]);
        }

        public static Vector4 Vec4(Vector4 a, Vector4 b)
        {
            var r = Components(new[] { a.x, a.y, a.z, a.w }, new[] { b.x, b.y, b.z, b.w });
            return new Vector4(r[0], r[1], r[2], r[3]);
        }

        // Scale all components by the ratio of the one the user changed. If the edited component was 0
        // (ratio undefined), make every component equal to the new value. Derived components round to
        // 2 decimals so the fields don't widen with long float tails; the edited one is kept as typed.
        public static float[] Components(float[] prev, float[] next)
        {
            int changed = -1;
            for (int i = 0; i < prev.Length; i++)
                if (!Mathf.Approximately(prev[i], next[i])) { changed = i; break; }
            if (changed < 0) return next;

            var result = (float[])next.Clone();
            if (Mathf.Approximately(prev[changed], 0f))
            {
                for (int i = 0; i < result.Length; i++) result[i] = next[changed];
            }
            else
            {
                float ratio = next[changed] / prev[changed];
                for (int i = 0; i < result.Length; i++)
                    result[i] = (i == changed) ? next[changed] : Mathf.Round(prev[i] * ratio * 100f) / 100f;
            }
            return result;
        }
    }
}
