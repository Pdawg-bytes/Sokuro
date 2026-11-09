using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sokuro.Consumer;

internal static class RecordSorter
{
    private const int InsertionThreshold = 16;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Sort<T, TComparer>(Span<T> items, TComparer comparer) where T : struct where TComparer : struct, IComparer<T>
    {
        if (items.Length < 2 || IsSorted(items, comparer))
            return;

        int depthLimit = 2 * (BitOperations.Log2((uint)items.Length) + 1);
        Introsort(items, comparer, depthLimit);
    }

    private static bool IsSorted<T, TComparer>(Span<T> items, TComparer comparer)
        where T : struct where TComparer : struct, IComparer<T>
    {
        for (int i = 1; i < items.Length; i++)
        {
            if (comparer.Compare(items[i - 1], items[i]) > 0)
                return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Introsort<T, TComparer>(Span<T> items, TComparer comparer, int depthLimit)
        where T : struct where TComparer : struct, IComparer<T>
    {
        while (items.Length > InsertionThreshold)
        {
            if (depthLimit-- == 0)
            {
                Heapsort(items, comparer);
                return;
            }

            int pivot = Partition(items, comparer);
            Introsort(items[(pivot + 1)..], comparer, depthLimit);
            items = items[..pivot];
        }

        InsertionSort(items, comparer);
    }

    private static int Partition<T, TComparer>(Span<T> items, TComparer comparer)
        where T : struct where TComparer : struct, IComparer<T>
    {
        int last   = items.Length - 1;
        int middle = last / 2;

        if (comparer.Compare(items[middle], items[0])      < 0) Swap(items, middle, 0);
        if (comparer.Compare(items[last],   items[0])      < 0) Swap(items, last,   0);
        if (comparer.Compare(items[last],   items[middle]) < 0) Swap(items, last,   middle);

        Swap(items, middle, last - 1);

        T pivot   = items[last - 1];
        int left  = 0;
        int right = last - 1;

        while (true)
        {
            while (comparer.Compare(items[++left], pivot)  < 0) { }
            while (comparer.Compare(pivot, items[--right]) < 0) { }

            if (left >= right)
                break;

            Swap(items, left, right);
        }

        Swap(items, left, last - 1);
        return left;
    }

    private static void InsertionSort<T, TComparer>(Span<T> items, TComparer comparer)
        where T : struct where TComparer : struct, IComparer<T>
    {
        for (int i = 1; i < items.Length; i++)
        {
            T value = items[i];

            int j = i - 1;
            while (j >= 0 && comparer.Compare(value, items[j]) < 0)
            {
                items[j + 1] = items[j];
                j--;
            }

            items[j + 1] = value;
        }
    }

    private static void Heapsort<T, TComparer>(Span<T> items, TComparer comparer)
        where T : struct where TComparer : struct, IComparer<T>
    {
        for (int i = items.Length / 2 - 1; i >= 0; i--)
            SiftDown(items, comparer, i, items.Length);

        for (int end = items.Length - 1; end > 0; end--)
        {
            Swap(items, 0, end);
            SiftDown(items, comparer, 0, end);
        }
    }

    private static void SiftDown<T, TComparer>(Span<T> items, TComparer comparer, int root, int length)
        where T : struct where TComparer : struct, IComparer<T>
    {
        while (true)
        {
            int child = 2 * root + 1;
            if (child >= length)
                return;

            if (child + 1 < length && comparer.Compare(items[child], items[child + 1]) < 0)
                child++;

            if (comparer.Compare(items[root], items[child]) >= 0)
                return;

            Swap(items, root, child);
            root = child;
        }
    }

    private static void Swap<T>(Span<T> items, int a, int b) => (items[a], items[b]) = (items[b], items[a]);
}