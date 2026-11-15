// Lists of C#: growth, insertion and removal, the indexer and its bounds, searches by Equals, copies
// to and from arrays, and changes during a foreach, which .NET's enumerator refuses. Written for this
// repository.
#nullable disable
using System;
using System.Collections.Generic;

namespace Polypiler.Fixtures.Prototype.Collections;

public sealed class Box {

  public int Value;

  public Box(int value) {
    this.Value = value;
  }

}

public struct Pair {

  public int A;

  public int B;

}

public static class CollectionsDemo {

  private static int Digits(List<int> list) {
    var result = 0;
    foreach (var digit in list)
      result = result * 10 + digit;

    return result;
  }

  public static int ListOps() {
    var list = new List<int> { 3, 2 };
    list.Insert(0, 7);
    list.Add(9);
    list[3] = 3;
    list.RemoveAt(1);
    list.Add(3);
    list.Add(5);
    list.RemoveAt(list.Count - 1);
    if (!list.Contains(2) || list.IndexOf(3) != 2 || list.IndexOf(8) != -1)
      return -1;

    return Digits(list);
  }

  public static int Grow(int count) {
    var list = new List<int>();
    for (var i = 0; i < count; ++i)
      list.Add(i * i);

    var sum = 0;
    for (var i = 0; i < list.Count; ++i)
      sum += list[i] % 7;

    list.Clear();
    return sum * 10 + list.Count;
  }

  public static int Element(int index) {
    var list = new List<int> { 10, 20, 30 };
    try {
      return list[index];
    } catch (ArgumentOutOfRangeException) {
      return -1;
    }
  }

  public static int Write(int index) {
    List<int> list = [10, 20, 30];
    list[index] += 5;
    return Digits(list);
  }

  public static int InsertAt(int index) {
    var list = new List<int> { 1, 2 };
    try {
      list.Insert(index, 9);
    } catch (ArgumentOutOfRangeException) {
      return -1;
    }

    return Digits(list);
  }

  public static int RemoveLastOfEmpty() {
    var list = new List<int>();
    list.RemoveAt(list.Count - 1);
    return list.Count;
  }

  public static int Search(double value) {
    var list = new List<double> { double.NaN, -0.0 };
    return list.IndexOf(value) * 10 + (list.Contains(value) ? 1 : 0);
  }

  public static int Copies() {
    var array = new int[] { 4, 5 };
    var list = new List<int>(array);
    array[0] = 6;
    list.Add(7);
    var copy = list.ToArray();
    list[0] = 1;
    return copy[0] * 100 + copy.Length * 10 + list[0];
  }

  public static string Words() {
    var words = new List<string> { "b", "c" };
    words.Insert(0, "a");
    var text = "";
    foreach (var word in words)
      text += word;

    return text + (words.IndexOf("c") == 2 ? "!" : "?");
  }

  public static int Boxes() {
    var first = new Box(1);
    var boxes = new List<Box> { first, new Box(2) };
    boxes[1].Value = 5;
    var sum = 0;
    foreach (var box in boxes)
      sum = sum * 10 + box.Value;

    return sum * 10 + boxes.IndexOf(first) + (boxes.Contains(new Box(1)) ? 100 : 0);
  }

  public static int Pairs() {
    var pairs = new List<Pair>();
    var pair = new Pair { A = 1, B = 2 };
    pairs.Add(pair);
    pair.A = 7;
    var read = pairs[0];
    read.B = 9;
    return pairs[0].A * 10 + pairs[0].B;
  }

  public static int ModifyWhileEnumerating() {
    var list = new List<int> { 1, 2, 3 };
    var passes = 0;
    foreach (var item in list) {
      ++passes;
      list.Add(item);
    }

    return passes;
  }

  public static int Passes(int change) {
    var list = new List<int> { 1, 2, 3 };
    var passes = 0;
    try {
      foreach (var item in list) {
        ++passes;
        if (change == 0 && passes == 1)
          list[0] = 5;
        if (change == 1 && passes == 3)
          list.Add(4);
        if (change == 2 && passes == 2)
          list.Clear();
        if (change == 3 && item == 2)
          list.RemoveAt(0);
      }
    } catch (InvalidOperationException) {
      return -passes;
    }

    return passes;
  }

  public static long Widened() {
    var list = new List<int> { 1, 2 };
    long sum = 0;
    foreach (long item in list)
      sum = sum * 100 + item;

    return sum;
  }

}
