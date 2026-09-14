using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Canonical bounded top-K selection helper using a min- or max-heap.
    /// Eliminates sorting entire collections of N items when only top-K items are needed.
    /// </summary>
    public static class TopKHelper
    {
        public static List<T> SelectTopK<T>(IEnumerable<T> source, int k, IComparer<T> comparer, out int totalCount)
        {
            if (source == null || k <= 0)
            {
                totalCount = 0;
                return new List<T>(0);
            }

            int count = 0;
            var heap = new T[k];
            int heapSize = 0;

            foreach (var item in source)
            {
                count++;
                if (heapSize < k)
                {
                    heap[heapSize] = item;
                    int child = heapSize;
                    while (child > 0)
                    {
                        int parent = (child - 1) >> 1;
                        if (comparer.Compare(heap[child], heap[parent]) > 0)
                        {
                            var tmp = heap[child];
                            heap[child] = heap[parent];
                            heap[parent] = tmp;
                            child = parent;
                        }
                        else break;
                    }
                    heapSize++;
                }
                else if (comparer.Compare(item, heap[0]) < 0)
                {
                    heap[0] = item;
                    int parent = 0;
                    while (true)
                    {
                        int left = (parent << 1) + 1;
                        if (left >= k) break;
                        int right = left + 1;
                        int bestChild = (right < k && comparer.Compare(heap[right], heap[left]) > 0) ? right : left;
                        if (comparer.Compare(heap[bestChild], heap[parent]) > 0)
                        {
                            var tmp = heap[parent];
                            heap[parent] = heap[bestChild];
                            heap[bestChild] = tmp;
                            parent = bestChild;
                        }
                        else break;
                    }
                }
            }

            totalCount = count;
            if (heapSize < k)
            {
                Array.Resize(ref heap, heapSize);
            }
            Array.Sort(heap, comparer);
            return new List<T>(heap);
        }
    }
}
