using Google.Api;
using System;
using System.Collections.Generic;
using System.Linq;


namespace TranscriptionServer;

public static class Utils
{
    public static bool IsEmpty<T>(this IEnumerable<T> enumerable) => !enumerable.Any();

    public static (IEnumerable<T> trueElts, IEnumerable<T> falseElts) Split<T>(this IEnumerable<T> elements, Func<T, bool> predicate)
    {
        IEnumerable<T> trueElts = elements.Where(predicate);
        IEnumerable<T> falseElts = elements.Where(elt => !predicate(elt));

        return (trueElts, falseElts);
    }
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

    public static TOut[] MapTo<TIn, TOut>(this TIn[] array, Func<TIn, TOut> predicate)
    {
        int length = array.Length;
        TOut[] result = new TOut[length];

        for(int i = 0; i < length; i++)
            result[i] = predicate(array[i]);

        return result;
    }
}