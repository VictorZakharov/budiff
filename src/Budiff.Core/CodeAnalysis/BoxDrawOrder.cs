using System;
using System.Collections.Generic;
using System.Numerics;

namespace Budiff.CodeAnalysis;

// Back-to-front order for axis-aligned boxes that do not intersect (the
// painter's algorithm). Sorting by distance alone is wrong for boxes of very
// different sizes: a long flat plinth can be nearer the eye than a tall
// building standing in front of it, and get painted over it.
//
// Exact rule: two disjoint boxes always have a separating plane along some
// axis. The box on the far side of that plane from the eye can never cover
// the one on the eye's side, so it is drawn first. Only pairs that overlap on
// screen need the rule; the rest keep the distance order.
public static class BoxDrawOrder
{
    public readonly record struct Box(Vector3 Min, Vector3 Max);

    // boxes: the scene; pairs: index pairs whose screen extents overlap;
    // farKey: a fallback distance (bigger = farther, drawn earlier).
    // Returns box indices in draw order.
    public static int[] Sort(IReadOnlyList<Box> boxes, Vector3 eye, IEnumerable<(int A, int B)> pairs, Func<int, float> farKey)
    {
        var count = boxes.Count;
        var after = new List<int>?[count];
        var waiting = new int[count];
        foreach (var (a, b) in pairs)
        {
            switch (Behind(boxes[a], boxes[b], eye))
            {
                case < 0:
                    Edge(a, b);
                    break;
                case > 0:
                    Edge(b, a);
                    break;
            }
        }

        void Edge(int first, int then)
        {
            (after[first] ??= new List<int>()).Add(then);
            waiting[then]++;
        }

        // Kahn's topological sort, farthest ready box first.
        var ready = new PriorityQueue<int, float>();
        for (var i = 0; i < count; i++)
        {
            if (waiting[i] == 0)
            {
                ready.Enqueue(i, -farKey(i));
            }
        }

        var order = new int[count];
        var done = new bool[count];
        var placed = 0;
        while (placed < count)
        {
            if (ready.Count == 0)
            {
                // A cycle (three or more boxes covering each other in turn):
                // break it at the farthest box still waiting.
                var far = -1;
                for (var i = 0; i < count; i++)
                {
                    if (!done[i] && (far < 0 || farKey(i) > farKey(far)))
                    {
                        far = i;
                    }
                }

                waiting[far] = 0;
                ready.Enqueue(far, -farKey(far));
            }

            var next = ready.Dequeue();
            if (done[next])
            {
                continue;
            }

            done[next] = true;
            order[placed++] = next;
            if (after[next] is { } list)
            {
                foreach (var then in list)
                {
                    if (!done[then] && --waiting[then] == 0)
                    {
                        ready.Enqueue(then, -farKey(then));
                    }
                }
            }
        }

        return order;
    }

    // -1: a is behind b (draw a first); 1: b is behind a; 0: no constraint
    // (the boxes intersect, or the eye stands between them).
    public static int Behind(Box a, Box b, Vector3 eye)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            var aMin = Get(a.Min, axis);
            var aMax = Get(a.Max, axis);
            var bMin = Get(b.Min, axis);
            var bMax = Get(b.Max, axis);
            var e = Get(eye, axis);
            if (aMax <= bMin + 1e-4f)
            {
                return e >= bMin ? -1 : e <= aMax ? 1 : 0;
            }

            if (bMax <= aMin + 1e-4f)
            {
                return e >= aMin ? 1 : e <= bMax ? -1 : 0;
            }
        }

        return 0;
    }

    private static float Get(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
