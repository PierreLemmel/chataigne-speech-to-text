using System;
using System.Collections.Generic;
using System.Linq;


namespace TranscriptionServer;

public static class Utils
{
    public static bool IsEmpty<T>(this IEnumerable<T> enumerable) => !enumerable.Any();
}

public static class Arrays
{
    public static T[] Merge<T>(IEnumerable<T[]> arrays)
    {
        int capacity = arrays.Sum(arr => arr.Length);
        T[] result = new T[capacity];

        int destIdx = 0;
        foreach (T[] array in arrays)
        {
            int length = array.Length;
            Array.Copy(array, 0, result, destIdx, length);
            destIdx += length;
        }

        return result;
    }
}