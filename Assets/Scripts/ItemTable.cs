using UnityEngine;

public enum ItemType { None, Banana, Turbo, Rocket, Shield }

/// <summary>
/// Which item a box gives, by race position. Tweak the weights here:
/// one row per position bracket (leader first, last place last), one column per item.
/// </summary>
public static class ItemTable
{
    public static readonly ItemType[] Items = { ItemType.Banana, ItemType.Turbo, ItemType.Rocket, ItemType.Shield };

    //                                        Banana Turbo Rocket Shield
    public static readonly int[][] Weights =
    {
        new[] { 45,  10,   5,   40 }, // leader
        new[] { 30,  25,  20,   25 },
        new[] { 15,  35,  35,   15 },
        new[] {  5,  45,  45,    5 }, // last place
    };

    public static ItemType Roll(int position, int racerCount, System.Random rng)
    {
        float t = racerCount > 1 ? (position - 1) / (float)(racerCount - 1) : 0f;
        int row = Mathf.Clamp(Mathf.RoundToInt(t * (Weights.Length - 1)), 0, Weights.Length - 1);
        int[] w = Weights[row];
        int total = 0;
        foreach (int x in w) total += x;
        int pick = rng.Next(total);
        for (int i = 0; i < w.Length; i++)
        {
            if (pick < w[i]) return Items[i];
            pick -= w[i];
        }
        return Items[0];
    }

    public static string Label(ItemType t) => t switch
    {
        ItemType.Banana => "BANANA PEEL",
        ItemType.Turbo => "TURBO",
        ItemType.Rocket => "ROCKET",
        ItemType.Shield => "SHIELD",
        _ => ""
    };

    public static Color Tint(ItemType t) => t switch
    {
        ItemType.Banana => new Color(1f, 0.85f, 0.2f),
        ItemType.Turbo => new Color(1f, 0.45f, 0.1f),
        ItemType.Rocket => new Color(0.9f, 0.2f, 0.25f),
        ItemType.Shield => new Color(0.3f, 0.8f, 1f),
        _ => new Color(0.3f, 0.3f, 0.3f)
    };
}
