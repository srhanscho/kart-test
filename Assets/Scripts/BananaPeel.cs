using UnityEngine;

/// <summary>Dropped banana: the first kart to touch it spins out (unless shielded).</summary>
public class BananaPeel : MonoBehaviour
{
    const float OwnerGrace = 1f;
    const float Lifetime = 90f;

    KartItems owner;
    float spawnTime;

    public void Init(KartItems ownerItems)
    {
        owner = ownerItems;
        spawnTime = Time.time;
    }

    void Update()
    {
        if (Time.time - spawnTime > Lifetime) Destroy(gameObject);
    }

    void OnTriggerEnter(Collider other)
    {
        var items = other.GetComponentInParent<KartItems>();
        if (items == null) return;
        if (items == owner && Time.time - spawnTime < OwnerGrace) return;
        items.Hit(ItemType.Banana);
        Destroy(gameObject);
    }
}
