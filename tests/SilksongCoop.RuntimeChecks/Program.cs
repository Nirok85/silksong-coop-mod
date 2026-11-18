using SilksongCoop.Mod.Revive;
using UnityEngine;

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
void Setup(Vector3 center, Vector3 size)
{
    var hero = new HeroController();
    hero.Collider = new BoxCollider2D { transform = hero.transform, bounds = new Bounds(center, size), size = size };
    HeroController.instance = hero;
    Physics2D.Hits.Clear(); Physics2D.Obstacles.Clear(); Physics2D.RayCapacities.Clear();
    Physics2D.Hits.Add(new RaycastHit2D { collider = new Collider2D(), normal = new Vector2(0, 1), point = new Vector2(5, 0), distance = 1 });
}
Setup(new Vector3(10.6f, 21.2f, 0), new Vector3(2, 4, 0));
HeroController.instance!.transform.position = new Vector3(10, 20, 0);
Check(new UnityGroundProber().TryFindSafeGround(5, 0, out float x, out float y), "offset body must fit");
Check(Math.Abs(x - 4.4f) < .001f && Math.Abs(y - .81f) < .001f, "root coordinate must subtract world center offset");
Check(Math.Abs(Physics2D.LastSize.x - 2) < .001f, "clearance must use full body width");
Check((Physics2D.LastMask & (1 << 13)) == 0, "Hero Detector excluded from ground mask");

Setup(new Vector3(0, 1, 0), new Vector3(2, 2, 0));
Physics2D.Obstacles.Add(new Collider2D { bounds = new Bounds(new Vector3(5.8f, 1, 0), new Vector3(.1f, 1, 0)) });
Check(!new UnityGroundProber().TryFindSafeGround(5, 0, out _, out _), "wide body must reject wall outside old .8 clearance");

Setup(new Vector3(0, 1, 0), new Vector3(1, 2, 0));
Physics2D.Hits[0].collider!.isTrigger = true;
Check(!new UnityGroundProber().TryFindSafeGround(5, 0, out _, out _), "trigger cannot support player");

Setup(new Vector3(0, 1, 0), new Vector3(1, 2, 0));
var selfHit = Physics2D.Hits[0]; selfHit.collider = HeroController.instance!.Collider; Physics2D.Hits[0] = selfHit;
Check(!new UnityGroundProber().TryFindSafeGround(5, 0, out _, out _), "hero collider cannot support own spawn");

Setup(new Vector3(0, 1, 0), new Vector3(1, 2, 0));
var solid = Physics2D.Hits[0]; Physics2D.Hits.Clear();
for (int i = 0; i < 16; i++) Physics2D.Hits.Add(new RaycastHit2D { collider = new Collider2D { isTrigger = true }, normal = new Vector2(0, 1) });
Physics2D.Hits.Add(solid);
Check(new UnityGroundProber().TryFindSafeGround(5, 0, out _, out _), "must grow ray buffer to find later solid hit");
Check(Physics2D.RayCapacities.Contains(32), "ray buffer grows to32");

Setup(new Vector3(0, 1, 0), new Vector3(1, 2, 0));
while (Physics2D.Hits.Count < 128) Physics2D.Hits.Add(Physics2D.Hits[0]);
Check(!new UnityGroundProber().TryFindSafeGround(5, 0, out _, out _), "saturated ray buffer must fail closed");
Check(Physics2D.RayCapacities.Max() == 128, "ray buffer bounded128");

Setup(Vector3.zero, new Vector3(2, 4, 0));
var body = (BoxCollider2D)HeroController.instance!.Collider!;
body.enabled = false; body.bounds = default; body.offset = new Vector2(.3f, .5f);
body.transform.position = new Vector3(10, 20, 0); body.transform.scale = new Vector3(2, 3, 1);
Check(new UnityGroundProber().TryFindSafeGround(5, 0, out x, out y), "disabled body geometry must remain available");
Check(Math.Abs(x - 4.4f) < .001f && Math.Abs(y - 4.51f) < .001f, "disabled scaled collider uses world offset and extents");
Check(!body.enabled, "prober must not enable body collision");
Check(Math.Abs(Physics2D.LastSize.x - 4f) < .001f, "scaled width used");

Setup(new Vector3(0, 1, 0), new Vector3(1, 2, 0));
Physics2D.Hits.Clear();
HeroController.instance!.hazardRespawnLocation = new Vector3(88.5f, 14.25f, 0);
Check(new UnityGroundProber().TryFindSafeGround(5, 0, out x, out y), "fallback to hazardRespawnLocation when raycasts find no floor");
Check(Math.Abs(x - 88.5f) < .001f && Math.Abs(y - 14.25f) < .001f, "hazard fallback coordinates must match hazardRespawnLocation");

Console.WriteLine($"Production UnityGroundProber source checks passed: {passed}");

public sealed class HeroController
{
    public static HeroController? instance;
    public Transform transform = new();
    public Collider2D? Collider;
    public Vector3 hazardRespawnLocation = Vector3.zero;
    public T? GetComponent<T>() where T : class => Collider as T;
}
public sealed class DamageHero { public GlobalEnums.HazardType hazardType; }
namespace GlobalEnums { public enum HazardType { NON_HAZARD, SPIKES } }
namespace SilksongCoop.Mod { public static class SilksongCoopPlugin { public static void LogWarn(string _) { } } }
namespace UnityEngine
{
    public struct Vector2(float x, float y)
    {
        public float x = x, y = y;
        public static Vector2 down => new(0, -1);
        public static Vector2 operator *(Vector2 value, float scale) => new(value.x * scale, value.y * scale);
        public static implicit operator Vector2(Vector3 value) => new(value.x, value.y);
        public static implicit operator Vector3(Vector2 value) => new(value.x, value.y, 0);
    }
    public struct Vector3(float x, float y, float z)
    {
        public float x = x, y = y, z = z;
        public static Vector3 zero => new(0, 0, 0);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    }
    public struct Bounds(Vector3 center, Vector3 size)
    {
        public Vector3 center = center, size = size;
        public void Encapsulate(Vector3 point)
        {
            float minX = Math.Min(center.x - size.x / 2, point.x), maxX = Math.Max(center.x + size.x / 2, point.x);
            float minY = Math.Min(center.y - size.y / 2, point.y), maxY = Math.Max(center.y + size.y / 2, point.y);
            center = new((minX + maxX) / 2, (minY + maxY) / 2, 0); size = new(maxX - minX, maxY - minY, 0);
        }
    }
    public class Transform
    {
        public Vector3 position, scale = new(1, 1, 1);
        public Transform? parent;
        public bool IsChildOf(Transform root) => parent == root || parent?.IsChildOf(root) == true;
        public Vector3 TransformPoint(Vector3 p) => new(position.x + p.x * scale.x, position.y + p.y * scale.y, position.z + p.z * scale.z);
    }
    public class Collider2D
    {
        public bool isTrigger, enabled = true;
        public Transform transform = new();
        public Bounds bounds;
        public Vector2 offset;
        public T? GetComponentInParent<T>() where T : class => null;
        public bool CompareTag(string _) => false;
    }
    public sealed class BoxCollider2D : Collider2D { public Vector2 size; }
    public sealed class CapsuleCollider2D : Collider2D { public Vector2 size; }
    public sealed class CircleCollider2D : Collider2D { public float radius; }
    public struct RaycastHit2D { public Collider2D? collider; public Vector2 normal, point; public float distance; }
    public struct ContactFilter2D { public bool useTriggers; }
    public sealed class UnityException : Exception { }
    public static class GameObject { public static object FindWithTag(string _) => new(); }
    public static class LayerMask { public static int NameToLayer(string name) => name switch { "Terrain" => 8, "Hero Detector" => 13, "Soft Terrain" => 25, _ => -1 }; }
    public static class Physics2D
    {
        public static readonly List<RaycastHit2D> Hits = new();
        public static readonly List<Collider2D> Obstacles = new();
        public static readonly List<int> RayCapacities = new();
        public static Vector2 LastSize;
        public static int LastMask;
        public static int RaycastNonAlloc(Vector2 origin, Vector2 direction, RaycastHit2D[] buffer, float distance, int mask)
        {
            LastMask = mask; RayCapacities.Add(buffer.Length);
            int count = Math.Min(buffer.Length, Hits.Count);
            for (int i = 0; i < count; i++) buffer[i] = Hits[i];
            return count;
        }
        public static int OverlapBox(Vector2 center, Vector2 size, float angle, ContactFilter2D filter, Collider2D[] buffer)
        {
            LastSize = size; int count = 0;
            foreach (var collider in Obstacles)
            {
                var b = collider.bounds;
                if (Math.Abs(center.x - b.center.x) * 2 >= size.x + b.size.x || Math.Abs(center.y - b.center.y) * 2 >= size.y + b.size.y) continue;
                if (count == buffer.Length) return count;
                buffer[count++] = collider;
            }
            return count;
        }
    }
}
