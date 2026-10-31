using System.Collections.Generic;
using Ignis;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Keeps Ignis drawing the existing fire, and releases the plugin's OnlySmoke
/// blocks from each burning object. Blocks drift through open indoor air:
/// above a floor, below the ceiling, and clear of walls. An open doorway counts
/// as open air, so smoke can leave the fire's room and keep spreading.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-50)]
public class IgnisSmokeDiffusion : MonoBehaviour
{
    const float FinalVisibleAlpha = 0.001f;
    const float CastSkin = 0.02f;
    const float CellSize = 0.6f;
    const float MaxRoomRadius = 22f;
    const int MaxOpenCells = 2400;

    static readonly Vector2Int[] Neighbors =
    {
        new Vector2Int(1, 0),
        new Vector2Int(-1, 0),
        new Vector2Int(0, 1),
        new Vector2Int(0, -1)
    };

    [Header("Fire Sources")]
    [Tooltip("Fires that release drifting smoke while they are burning. An empty list uses a FlammableObject on this GameObject, or every burning fire in the scene when this object has none.")]
    public FlammableObject[] fireSources;

    [Tooltip("Optional scene cube already set up as Ignis OnlySmoke. Its color, alpha, particle size and multiplier are copied. Leave empty to use the values below.")]
    public FlammableObject smokeTemplate;

    [Tooltip("While this component owns a burning fire, Ignis keeps the flames and the drifting blocks carry the smoke.")]
    public bool muteEmbeddedSmoke = true;

    [Header("Flame Spread And Smoke")]
    [Tooltip("Extra drift speed, in meters per second, added for each meter per second of the fire's crawl speed.")]
    [Min(0f)]
    public float flameSpeedToSmoke = 1f;

    [Tooltip("Horizontal drift speed while a fire is burning, including a point fire whose crawl speed is zero.")]
    [Min(0f)]
    public float baseSmokeSpeed = 1.05f;

    [Tooltip("Upward speed mixed into every smoke volume. The plugin particles also rise on their own.")]
    [Min(0f)]
    public float riseSpeed = 0.55f;

    [Tooltip("How quickly a volume changes its horizontal direction, in degrees per second.")]
    [Min(0f)]
    public float wanderDegreesPerSecond = 16f;

    [Tooltip("Seconds of burning after which a fire's smoke volumes reach their full plugin alpha.")]
    [Min(0.01f)]
    public float fullStrengthSeconds = 8f;

    [Tooltip("Flame spread distance, in meters, that also counts as a fully established smoke output.")]
    [Min(0.01f)]
    public float fullSpreadMeters = 3f;

    [Range(0f, 1f)]
    [Tooltip("Fraction of the plugin alpha used the moment a volume appears.")]
    public float minimumSmokeStrength = 0.4f;

    [Header("Drifting Smoke Volumes")]
    [Tooltip("Seconds between new smoke volumes while the fire is burning.")]
    [Min(0.05f)]
    public float spawnInterval = 1f;

    [Tooltip("Highest number of live Ignis smoke volumes one fire may release.")]
    [Min(1)]
    public int maxVolumesPerFire = 36;

    [Tooltip("Highest number of live Ignis smoke volumes in the scene.")]
    [Min(1)]
    public int maxLiveVolumes = 72;

    [Tooltip("Height above the floor where a volume is released.")]
    public float heightOffset = 1.25f;

    [Tooltip("Sphere radius used to stop a volume on a collider. Triggers are ignored.")]
    [Min(0.05f)]
    public float bodyRadius = 0.3f;

    [Tooltip("Extra space kept between the top of a smoke volume and the ceiling, so Ignis particles stay inside the room.")]
    [Min(0f)]
    public float particleCeilingMargin = 0.45f;

    [Tooltip("Colliders that stop free drift. Smoke keeps the part of its speed that slides along the surface.")]
    public LayerMask obstacleMask = ~0;

    [Tooltip("Seconds a new volume takes to fade up to the current fire output.")]
    [Min(0f)]
    public float growthSeconds = 2.5f;

    [Tooltip("Seconds a volume takes to disappear after its fire goes out.")]
    [Min(0f)]
    public float fadeSeconds = 8f;

    [Tooltip("Extra fade delay, in seconds, given to the volume that has traveled farthest.")]
    [Min(0f)]
    public float fadeDistanceDelay = 1.5f;

    [Header("Plugin Smoke Appearance")]
    [Tooltip("Ignis multiplies this by Smoke Color Intensity. The product is the color sent to OnlySmoke.")]
    public Color smokeColor = new Color(0.02f, 0.02f, 0.02f, 1f);

    [Min(0f)]
    [Tooltip("Ignis multiplies this into the smoke color. Higher is lighter; kept moderate so the cloud stays dark but not solid black.")]
    public float smokeColorIntensity = 20f;

    [Min(0f)]
    public float smokeAlpha = 0.7f;

    [Min(0f)]
    public float smokeVFXMultiplier = 4f;

    [Min(0f)]
    public float smokeParticleSize = 4f;

    [Tooltip("World size of a generated OnlySmoke volume. Ignis emits through this whole box.")]
    public Vector3 generatedBlockScale = new Vector3(1.2f, 0.7f, 1.2f);

    readonly List<FireFront> fronts = new List<FireFront>();
    Transform blockHolder;
    bool warnedAboutMissingEngine;
    float nextDiscoveryTime;

    class FireFront
    {
        public FlammableObject fire;
        public NetworkedFireState lifecycle;
        public RoomAir room;
        public Vector3 origin;
        public bool originLocked;
        public bool burning;
        public bool embeddedMuted;
        public float savedSmokeAlpha;
        public float savedSmokeMultiplier;
        public float burnSeconds;
        public float spawnCredit;
        public float flameOutput;
        public float farthestDistance;
        public float nextMapTime;
        public readonly List<SmokeVolume> volumes = new List<SmokeVolume>();
    }

    class SmokeVolume
    {
        public FlammableObject smoke;
        public float headingRadians;
        public float distance;
        public float age;
        public float alpha;
        public float fadeStartAlpha;
        public float authoredAlpha;
        public float authoredColorIntensity;
        public float fadeDelay;
        public bool ignited;
        public int spawnFrame;
        public VolumePhase phase;
    }

    /// <summary>Open cells of one room, in meters, keyed from the floor point under the fire.</summary>
    class RoomAir
    {
        public Collider floor;
        public Collider ceiling;
        public float floorY;
        public float ceilingY;
        public Vector3 origin;
        public readonly HashSet<Vector2Int> open = new HashSet<Vector2Int>();
    }

    enum VolumePhase
    {
        Growing,
        Holding,
        Fading
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void InstallOnFlameEngine()
    {
        FlameEngine[] engines = Object.FindObjectsByType<FlameEngine>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < engines.Length; i++)
        {
            FlameEngine engine = engines[i];
            if (engine == null || engine.GetComponent<IgnisSmokeDiffusion>() != null)
                continue;

            string path = engine.gameObject.scene.path.Replace('\\', '/');
            if (path.Contains("OAVA-Flame"))
                continue;

            engine.gameObject.AddComponent<IgnisSmokeDiffusion>();
        }
    }

    void Awake()
    {
        if (fireSources == null || fireSources.Length == 0)
        {
            FlammableObject self = GetComponent<FlammableObject>();
            if (self != null)
                fireSources = new[] { self };
        }

        blockHolder = transform.Find("Ignis Smoke Blocks");
        if (blockHolder == null)
        {
            var holderObject = new GameObject("Ignis Smoke Blocks");
            blockHolder = holderObject.transform;
            blockHolder.SetParent(transform, false);
        }
    }

    void OnDisable()
    {
        ClearFronts();
    }

    void Update()
    {
        if (FlameEngine.instance == null)
        {
            if (!warnedAboutMissingEngine)
            {
                Debug.LogWarning("IgnisSmokeDiffusion needs a FlameEngine in the scene before it can spawn OnlySmoke volumes.", this);
                warnedAboutMissingEngine = true;
            }
            return;
        }

        EnsureFronts();
        for (int i = fronts.Count - 1; i >= 0; i--)
        {
            if (fronts[i].fire == null)
            {
                ReleaseFront(fronts[i]);
                fronts.RemoveAt(i);
                continue;
            }

            AdvanceFront(fronts[i]);
            if (!fronts[i].burning && fronts[i].volumes.Count == 0)
            {
                RestoreEmbeddedSmoke(fronts[i]);
                fronts.RemoveAt(i);
            }
        }
    }

    void LateUpdate()
    {
        if (FlameEngine.instance == null)
            return;

        for (int i = 0; i < fronts.Count; i++)
            DriftVolumes(fronts[i]);
    }

    void EnsureFronts()
    {
        if (fireSources != null && fireSources.Length > 0)
        {
            for (int i = 0; i < fireSources.Length; i++)
                Track(fireSources[i]);
            return;
        }

        if (Time.unscaledTime < nextDiscoveryTime)
            return;

        nextDiscoveryTime = Time.unscaledTime + 0.5f;
        FlammableObject[] fires = Object.FindObjectsByType<FlammableObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < fires.Length; i++)
        {
            FlammableObject fire = fires[i];
            if (!IsSceneFire(fire) || !BurningNow(fire))
                continue;

            Track(fire);
        }
    }

    void Track(FlammableObject fire)
    {
        if (fire == null || HasFront(fire) || !IsSceneFire(fire))
            return;

        fronts.Add(new FireFront
        {
            fire = fire,
            lifecycle = fire.GetComponent<NetworkedFireState>()
        });
    }

    bool IsSceneFire(FlammableObject fire)
    {
        if (fire == null)
            return false;

        if (blockHolder != null && fire.transform.IsChildOf(blockHolder))
            return false;

        if (!fire.enableFlameVFX)
            return false;

        if (fire.overrideFireVFX && fire.overrideVFXVariant == FlameEngine.FireVFXVariant.OnlySmoke)
            return false;

        return true;
    }

    bool HasFront(FlammableObject fire)
    {
        for (int i = 0; i < fronts.Count; i++)
        {
            if (fronts[i].fire == fire)
                return true;
        }

        return false;
    }

    void AdvanceFront(FireFront front)
    {
        bool burning = IsBurning(front);
        if (burning && !front.burning)
            BeginBurn(front);
        else if (!burning && front.burning)
            BeginFade(front);

        front.burning = burning;
        if (burning)
        {
            if (muteEmbeddedSmoke)
                MuteEmbeddedSmoke(front);

            if (front.room == null && Time.unscaledTime >= front.nextMapTime)
            {
                front.room = BuildRoom(front);
                front.nextMapTime = Time.unscaledTime + 0.5f;
            }

            float delta = Time.deltaTime;
            front.burnSeconds += delta;
            front.flameOutput = FlameOutput(front);
            if (front.volumes.Count >= maxVolumesPerFire || LiveVolumeCount() >= maxLiveVolumes)
                front.spawnCredit = 0f;
            else
                front.spawnCredit += delta;

            while (front.spawnCredit >= spawnInterval && front.volumes.Count < maxVolumesPerFire && LiveVolumeCount() < maxLiveVolumes)
            {
                front.spawnCredit -= spawnInterval;
                SpawnVolume(front);
            }
        }

        UpdateVolumes(front);
    }

    int LiveVolumeCount()
    {
        int count = 0;
        for (int i = 0; i < fronts.Count; i++)
            count += fronts[i].volumes.Count;
        return count;
    }

    void BeginBurn(FireFront front)
    {
        if (!front.originLocked || front.volumes.Count == 0)
        {
            Vector3 origin = front.fire.onFire ? front.fire.GetFireOrigin() : front.fire.transform.position;
            front.origin = origin;
            front.originLocked = true;
            front.burnSeconds = 0f;
            front.spawnCredit = spawnInterval;
            front.farthestDistance = 0f;
            front.room = BuildRoom(front);
            front.nextMapTime = Time.unscaledTime + 0.5f;
        }

        for (int i = 0; i < front.volumes.Count; i++)
        {
            if (front.volumes[i].phase == VolumePhase.Fading)
                front.volumes[i].phase = VolumePhase.Holding;
        }
    }

    void BeginFade(FireFront front)
    {
        float span = Mathf.Max(0.5f, front.farthestDistance);
        for (int i = 0; i < front.volumes.Count; i++)
        {
            SmokeVolume volume = front.volumes[i];
            volume.phase = VolumePhase.Fading;
            volume.age = 0f;
            volume.fadeStartAlpha = volume.alpha;
            volume.fadeDelay = span > 0.0001f
                ? (volume.distance / span) * fadeDistanceDelay
                : 0f;
        }
    }

    float FlameOutput(FireFront front)
    {
        float byTime = Mathf.Clamp01(front.burnSeconds / fullStrengthSeconds);
        float bySpread = Mathf.Clamp01(front.fire.fireSpread / fullSpreadMeters);
        return Mathf.Max(byTime, bySpread);
    }

    float CurrentDriftSpeed(FireFront front)
    {
        float crawl = Mathf.Max(0f, front.fire.fireCrawlSpeed);
        return baseSmokeSpeed + crawl * flameSpeedToSmoke;
    }

    void SpawnVolume(FireFront front)
    {
        Vector3 position = SpawnPoint(front);
        FlammableObject smoke = smokeTemplate != null
            ? Instantiate(smokeTemplate, position, Quaternion.identity, blockHolder)
            : CreateSmokeBlock(position);

        smoke.name = "IgnisSmoke_" + front.fire.name + "_" + front.volumes.Count;
        smoke.gameObject.SetActive(true);
        PrepareSmokeObject(smoke);

        float authoredAlpha = Mathf.Max(0f, smoke.smokeAlpha);
        float authoredIntensity = Mathf.Max(0f, smoke.smokeColorIntensity);
        smoke.smokeAlpha = 0f;

        front.volumes.Add(new SmokeVolume
        {
            smoke = smoke,
            headingRadians = Random.Range(0f, Mathf.PI * 2f),
            authoredAlpha = authoredAlpha,
            authoredColorIntensity = authoredIntensity,
            spawnFrame = Time.frameCount,
            phase = VolumePhase.Growing
        });
    }

    Vector3 SpawnPoint(FireFront front)
    {
        RoomAir room = front.room;
        if (room == null || room.open.Count == 0)
        {
            Vector3 fallback = front.origin + Vector3.up * heightOffset;
            fallback += Random.insideUnitSphere * 0.15f;
            fallback.y = front.origin.y + heightOffset;
            return fallback;
        }

        Vector2Int cell = ToCell(room, front.origin);
        if (!room.open.Contains(cell))
            cell = NearestOpenCell(room, cell);

        Vector3 position = CellCenter(room, cell);
        position.y = CruiseHeight(room);
        position.x += Random.Range(-0.08f, 0.08f);
        position.z += Random.Range(-0.08f, 0.08f);
        return position;
    }

    FlammableObject CreateSmokeBlock(Vector3 position)
    {
        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.transform.SetParent(blockHolder, false);
        block.transform.position = position;
        block.transform.localScale = generatedBlockScale;

        FlammableObject smoke = block.AddComponent<FlammableObject>();
        smoke.smokeColor = smokeColor;
        smoke.smokeColorIntensity = smokeColorIntensity;
        smoke.smokeAlpha = smokeAlpha;
        smoke.smokeVFXMultiplier = smokeVFXMultiplier;
        smoke.smokeParticleSize = smokeParticleSize;
        return smoke;
    }

    /// <summary>
    /// Makes the object a smoke emitter only. It must not crawl, play fire audio,
    /// or ignite whatever it overlaps. Its collider stays a trigger so the drift
    /// cast, not a rigid body, decides when the volume hits the room.
    /// </summary>
    void PrepareSmokeObject(FlammableObject smoke)
    {
        smoke.overrideFireVFX = true;
        smoke.overrideVFXVariant = FlameEngine.FireVFXVariant.OnlySmoke;
        smoke.enableFlameVFX = false;
        smoke.embersVFXMultiplier = 0f;
        smoke.embersBurstVFXMultiplier = 0f;
        smoke.embersParticleSize = 0f;
        smoke.useFireSFX = false;
        smoke.enableMaterialAnimation = false;
        smoke.setThisOnFireOnStart = false;
        smoke.isEffectOnForever = true;
        smoke.burnOutStart_s = float.MaxValue;
        smoke.burnOutLength_s = float.MaxValue;
        smoke.fireCrawlSpeed = 0f;
        smoke.flameEnvironmentalSpeed = 0.25f;
        smoke.affectedByWind = false;
        smoke.maxSpread = 0.1f;
        smoke.spreadLayerMask = 0;
        smoke.ignitionTime = 0f;
        smoke.onFire = false;
        smoke.ResetObj();

        Renderer[] renderers = smoke.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
            renderers[i].enabled = false;

        BoxCollider[] boxes = smoke.GetComponentsInChildren<BoxCollider>(true);
        for (int i = 0; i < boxes.Length; i++)
            boxes[i].isTrigger = true;

        if (boxes.Length > 0)
            smoke.flammableColliders = new List<BoxCollider> { boxes[0] };
    }

    void DriftVolumes(FireFront front)
    {
        float delta = Time.deltaTime;
        float driftSpeed = front.burning ? CurrentDriftSpeed(front) : CurrentDriftSpeed(front) * 0.35f;
        float turn = wanderDegreesPerSecond * Mathf.Deg2Rad * delta;

        for (int i = 0; i < front.volumes.Count; i++)
        {
            SmokeVolume volume = front.volumes[i];
            if (volume.smoke == null)
                continue;

            volume.headingRadians += Mathf.Lerp(-turn, turn, Random.value);
            Vector3 heading = new Vector3(Mathf.Cos(volume.headingRadians), 0f, Mathf.Sin(volume.headingRadians));
            Vector3 desired = heading * driftSpeed + Vector3.up * riseSpeed;
            Vector3 from = volume.smoke.transform.position;
            Vector3 position = MoveUntilBlocked(from, desired * delta);
            position = StayInOpenAir(front.room, from, position);
            position = KeepUnderCeiling(position);
            if ((new Vector3(position.x - from.x, 0f, position.z - from.z)).sqrMagnitude < 0.0000004f)
                volume.headingRadians += Mathf.PI * 0.5f;

            volume.smoke.transform.position = position;
            volume.distance = Vector3.Distance(position, front.origin);
            if (volume.distance > front.farthestDistance)
                front.farthestDistance = volume.distance;
        }
    }

    /// <summary>
    /// Moves freely through open space. A collider removes only the speed aimed
    /// into it, so the volume slides along a wall or ceiling instead of passing through.
    /// </summary>
    Vector3 MoveUntilBlocked(Vector3 position, Vector3 displacement)
    {
        position = Step(position, displacement, out Vector3 hitNormal, out bool hit);
        if (!hit)
            return position;

        Vector3 slide = Vector3.ProjectOnPlane(displacement, hitNormal);
        if (slide.sqrMagnitude <= 0.0000001f)
            return position;

        return Step(position, slide, out _, out _);
    }

    Vector3 Step(Vector3 position, Vector3 displacement, out Vector3 hitNormal, out bool hit)
    {
        hitNormal = Vector3.up;
        hit = false;
        float distance = displacement.magnitude;
        if (distance <= 0.0001f)
            return position;

        Vector3 direction = displacement / distance;
        if (Cast(position, direction, distance + CastSkin, out RaycastHit castHit))
        {
            hit = true;
            hitNormal = castHit.normal;
            float travel = Mathf.Max(0f, castHit.distance - CastSkin);
            return position + direction * Mathf.Min(travel, distance);
        }

        return position + displacement;
    }

    bool Cast(Vector3 position, Vector3 direction, float distance, out RaycastHit closest)
    {
        closest = default;
        RaycastHit[] hits = Physics.SphereCastAll(position, bodyRadius, direction, distance, obstacleMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        bool found = false;
        for (int i = 0; i < hits.Length; i++)
        {
            if (IgnoreSolid(hits[i].collider) || hits[i].distance >= best)
                continue;

            best = hits[i].distance;
            closest = hits[i];
            found = true;
        }

        return found;
    }

    void UpdateVolumes(FireFront front)
    {
        for (int i = front.volumes.Count - 1; i >= 0; i--)
        {
            SmokeVolume volume = front.volumes[i];
            if (volume.smoke == null)
            {
                front.volumes.RemoveAt(i);
                continue;
            }

            if (!volume.ignited)
            {
                if (volume.phase == VolumePhase.Fading)
                {
                    ReleaseVolume(volume);
                    front.volumes.RemoveAt(i);
                    continue;
                }

                if (volume.spawnFrame != Time.frameCount)
                    volume.ignited = TryIgnite(volume);
                continue;
            }

            volume.age += Time.deltaTime;
            float target = TargetAlpha(front, volume);
            if (volume.phase == VolumePhase.Growing)
            {
                float progress = growthSeconds <= 0f ? 1f : Mathf.Clamp01(volume.age / growthSeconds);
                volume.alpha = Mathf.Lerp(volume.authoredAlpha * minimumSmokeStrength, target, Mathf.SmoothStep(0f, 1f, progress));
                if (progress >= 1f)
                {
                    volume.phase = VolumePhase.Holding;
                    volume.age = 0f;
                }
            }
            else if (volume.phase == VolumePhase.Holding)
            {
                float rise = volume.authoredAlpha / Mathf.Max(0.05f, growthSeconds);
                volume.alpha = Mathf.MoveTowards(volume.alpha, target, rise * Time.deltaTime);
            }
            else
            {
                float elapsed = volume.age - volume.fadeDelay;
                if (elapsed <= 0f)
                {
                    volume.alpha = volume.fadeStartAlpha;
                }
                else
                {
                    float progress = fadeSeconds <= 0f ? 1f : Mathf.Clamp01(elapsed / fadeSeconds);
                    volume.alpha = DispersingAlpha(volume.fadeStartAlpha, progress);
                    if (progress >= 1f)
                    {
                        ReleaseVolume(volume);
                        front.volumes.RemoveAt(i);
                        continue;
                    }
                }
            }

            ApplyAlpha(volume);
        }

        if (front.volumes.Count == 0 && !front.burning)
            ResetFront(front);
    }

    float TargetAlpha(FireFront front, SmokeVolume volume)
    {
        float strength = Mathf.Lerp(minimumSmokeStrength, 1f, front.flameOutput);
        return volume.authoredAlpha * strength;
    }

    bool TryIgnite(SmokeVolume volume)
    {
        FlameEngine engine = FlameEngine.instance;
        if (engine != null && engine.FlameCount() >= engine.maxFlamesInWorld)
            return false;

        volume.smoke.smokeAlpha = volume.alpha;
        volume.smoke.SetOnFireFromCenter();
        if (!volume.smoke.onFire)
            return false;

        ApplyAlpha(volume);
        return true;
    }

    void ApplyAlpha(SmokeVolume volume)
    {
        volume.smoke.smokeAlpha = Mathf.Max(0f, volume.alpha);
        volume.smoke.smokeColorIntensity = volume.authoredColorIntensity;
        volume.smoke.RefreshSmokeAppearance();
    }

    static float DispersingAlpha(float startingAlpha, float progress)
    {
        progress = Mathf.Clamp01(progress);
        if (progress >= 1f || startingAlpha <= 0f)
            return 0f;

        if (startingAlpha <= FinalVisibleAlpha)
            return Mathf.Lerp(startingAlpha, 0f, progress);

        return startingAlpha * Mathf.Pow(FinalVisibleAlpha / startingAlpha, progress);
    }

    bool IsBurning(FireFront front)
    {
        return BurningNow(front.fire, front.lifecycle);
    }

    static bool BurningNow(FlammableObject fire)
    {
        if (fire == null)
            return false;

        return BurningNow(fire, fire.GetComponent<NetworkedFireState>());
    }

    static bool BurningNow(FlammableObject fire, NetworkedFireState lifecycle)
    {
        if (fire == null)
            return false;

        if (lifecycle != null && lifecycle.isActiveAndEnabled)
        {
            if (!lifecycle.TryGetLifecycleSnapshot(out NetworkedFireState.FireLifecycleSnapshot state))
                return false;

            return state.IsBurning && !state.IsExtinguished && !state.IsBurnedOut;
        }

        return fire.onFire && !fire.IsExtinguished() && !fire.hasBurnedOut();
    }

    void MuteEmbeddedSmoke(FireFront front)
    {
        FlammableObject fire = front.fire;
        if (fire == null)
            return;

        if (!front.embeddedMuted)
        {
            front.savedSmokeAlpha = fire.smokeAlpha;
            front.savedSmokeMultiplier = fire.smokeVFXMultiplier;
            front.embeddedMuted = true;
        }

        fire.smokeAlpha = 0f;
        fire.smokeVFXMultiplier = 0f;
    }

    void RestoreEmbeddedSmoke(FireFront front)
    {
        if (!front.embeddedMuted || front.fire == null)
            return;

        front.fire.smokeAlpha = front.savedSmokeAlpha;
        front.fire.smokeVFXMultiplier = front.savedSmokeMultiplier;
        front.embeddedMuted = false;
    }

    RoomAir BuildRoom(FireFront front)
    {
        Vector3 rough = front.originLocked ? front.origin : front.fire.transform.position;
        if (!TryFindShell(rough, out Collider floor, out Collider ceiling, out float floorY, out float ceilingY))
            return null;

        var room = new RoomAir
        {
            floor = floor,
            ceiling = ceiling,
            floorY = floorY,
            ceilingY = ceilingY,
            origin = new Vector3(rough.x, floorY, rough.z)
        };

        Vector2Int start = Vector2Int.zero;
        if (!IsOpenCell(room, start))
        {
            bool seeded = false;
            for (int radius = 1; radius <= 4 && !seeded; radius++)
            {
                for (int z = -radius; z <= radius && !seeded; z++)
                {
                    for (int x = -radius; x <= radius; x++)
                    {
                        if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) != radius)
                            continue;

                        Vector2Int candidate = new Vector2Int(x, z);
                        if (!IsOpenCell(room, candidate))
                            continue;

                        start = candidate;
                        seeded = true;
                    }
                }
            }

            if (!seeded)
                return null;
        }

        var queue = new Queue<Vector2Int>();
        queue.Enqueue(start);
        room.open.Add(start);
        int examined = 0;
        while (queue.Count > 0 && room.open.Count < MaxOpenCells)
        {
            Vector2Int cell = queue.Dequeue();
            if (++examined > MaxOpenCells * 4)
                break;

            for (int i = 0; i < Neighbors.Length; i++)
            {
                Vector2Int next = cell + Neighbors[i];
                if (room.open.Contains(next))
                    continue;

                Vector3 world = CellCenter(room, next);
                float dx = world.x - room.origin.x;
                float dz = world.z - room.origin.z;
                if (dx * dx + dz * dz > MaxRoomRadius * MaxRoomRadius)
                    continue;

                if (!IsOpenCell(room, next))
                    continue;

                room.open.Add(next);
                queue.Enqueue(next);
            }
        }

        return room.open.Count > 0 ? room : null;
    }

    bool TryFindShell(Vector3 rough, out Collider floor, out Collider ceiling, out float floorY, out float ceilingY)
    {
        floor = null;
        ceiling = null;
        floorY = rough.y;
        ceilingY = rough.y + 2.4f;

        RaycastHit[] down = Physics.RaycastAll(rough + Vector3.up * 0.75f, Vector3.down, 8f, obstacleMask, QueryTriggerInteraction.Ignore);
        RaycastHit floorHit = default;
        float floorScore = float.MinValue;
        bool foundFloor = false;
        for (int i = 0; i < down.Length; i++)
        {
            if (SkipShell(down[i].collider))
                continue;

            float score = ShellScore(down[i], true);
            if (score <= floorScore)
                continue;

            floorScore = score;
            floorHit = down[i];
            foundFloor = true;
        }

        if (!foundFloor)
            return false;

        floor = floorHit.collider;
        floorY = floorHit.point.y;

        RaycastHit[] up = Physics.RaycastAll(floorHit.point + Vector3.up * 0.15f, Vector3.up, 12f, obstacleMask, QueryTriggerInteraction.Ignore);
        RaycastHit ceilingHit = default;
        float ceilingScore = float.MinValue;
        bool foundCeiling = false;
        for (int i = 0; i < up.Length; i++)
        {
            if (SkipShell(up[i].collider) || up[i].collider == floor)
                continue;

            float score = ShellScore(up[i], false);
            if (score <= ceilingScore)
                continue;

            ceilingScore = score;
            ceilingHit = up[i];
            foundCeiling = true;
        }

        if (foundCeiling)
        {
            ceiling = ceilingHit.collider;
            ceilingY = ceilingHit.point.y;
        }

        if (ceilingY - floorY < 0.8f)
            ceilingY = floorY + 2.4f;

        return true;
    }

    static float ShellScore(RaycastHit hit, bool floor)
    {
        string name = hit.collider.name;
        bool named = floor
            ? name.IndexOf("floor", System.StringComparison.OrdinalIgnoreCase) >= 0
            : name.IndexOf("ceiling", System.StringComparison.OrdinalIgnoreCase) >= 0;
        if (named)
            return 100000f - hit.distance;

        float facing = floor ? hit.normal.y : -hit.normal.y;
        if (facing < 0.65f)
            return float.MinValue;

        Vector3 size = hit.collider.bounds.size;
        return size.x * size.z - hit.distance;
    }

    static bool SkipShell(Collider collider)
    {
        if (collider == null || collider.isTrigger)
            return true;

        return collider.name == "Hitbox";
    }

    bool IsOpenCell(RoomAir room, Vector2Int cell)
    {
        Vector3 center = CellCenter(room, cell);
        float high = room.ceilingY - 0.05f;
        float drop = high - room.floorY;
        if (drop < bodyRadius)
            return false;

        RaycastHit[] hits = Physics.RaycastAll(new Vector3(center.x, high, center.z), Vector3.down, drop + 3f, obstacleMask, QueryTriggerInteraction.Ignore);
        bool onInteriorFloor = false;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider hit = hits[i].collider;
            if (hit == null || SkipShell(hit))
                continue;
            if (hit.name.IndexOf("floor", System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            onInteriorFloor = true;
            break;
        }

        if (!onInteriorFloor)
            return false;

        float sampleY = CruiseHeight(room);
        Collider[] overlaps = Physics.OverlapSphere(new Vector3(center.x, sampleY, center.z), bodyRadius * 0.75f, obstacleMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider collider = overlaps[i];
            if (collider == null)
                continue;
            if (collider == room.floor || collider == room.ceiling)
                continue;
            if (collider.name.IndexOf("floor", System.StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            if (collider.name.IndexOf("ceiling", System.StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            if (IgnoreSolid(collider))
                continue;

            return false;
        }

        return true;
    }

    float CruiseHeight(RoomAir room)
    {
        float low = room.floorY + bodyRadius;
        float high = room.ceilingY - bodyRadius;
        if (high < low)
            return (room.floorY + room.ceilingY) * 0.5f;

        return Mathf.Clamp(room.floorY + heightOffset, low, high);
    }

    Vector3 StayInOpenAir(RoomAir room, Vector3 from, Vector3 to)
    {
        if (room == null || room.open.Count == 0)
            return to;

        to = ClampHeight(room, to);
        if (IsInsideOpen(room, to))
            return to;

        Vector3 alongX = ClampHeight(room, new Vector3(to.x, to.y, from.z));
        if (IsInsideOpen(room, alongX))
            return alongX;

        Vector3 alongZ = ClampHeight(room, new Vector3(from.x, to.y, to.z));
        if (IsInsideOpen(room, alongZ))
            return alongZ;

        Vector3 vertical = ClampHeight(room, new Vector3(from.x, to.y, from.z));
        if (IsInsideOpen(room, vertical))
            return vertical;

        return from;
    }

    Vector3 ClampHeight(RoomAir room, Vector3 position)
    {
        float low = room.floorY + bodyRadius * 0.5f;
        float high = room.ceilingY - VolumeTopClearance();
        if (high < low)
            position.y = (room.floorY + room.ceilingY) * 0.5f;
        else
            position.y = Mathf.Clamp(position.y, low, high);
        return position;
    }

    bool IsInsideOpen(RoomAir room, Vector3 position)
    {
        if (position.y < room.floorY || position.y > room.ceilingY)
            return false;

        return room.open.Contains(ToCell(room, position));
    }

    Vector2Int NearestOpenCell(RoomAir room, Vector2Int around)
    {
        Vector2Int best = around;
        float bestDistance = float.MaxValue;
        foreach (Vector2Int cell in room.open)
        {
            float distance = (cell - around).sqrMagnitude;
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = cell;
        }

        return best;
    }

    static Vector2Int ToCell(RoomAir room, Vector3 world)
    {
        return new Vector2Int(
            Mathf.RoundToInt((world.x - room.origin.x) / CellSize),
            Mathf.RoundToInt((world.z - room.origin.z) / CellSize));
    }

    static Vector3 CellCenter(RoomAir room, Vector2Int cell)
    {
        return new Vector3(room.origin.x + cell.x * CellSize, room.floorY, room.origin.z + cell.y * CellSize);
    }

    float VolumeTopClearance()
    {
        float scaleY = blockHolder != null ? Mathf.Abs(blockHolder.lossyScale.y) : 1f;
        return generatedBlockScale.y * 0.5f * scaleY + particleCeilingMargin;
    }

    /// <summary>
    /// Pulls a volume down so its top, including the Ignis particles rising out of the box, stays under the nearest ceiling.
    /// </summary>
    Vector3 KeepUnderCeiling(Vector3 position)
    {
        float clearance = VolumeTopClearance();
        RaycastHit[] hits = Physics.RaycastAll(position, Vector3.up, 8f, obstacleMask, QueryTriggerInteraction.Ignore);
        float underside = float.PositiveInfinity;
        for (int i = 0; i < hits.Length; i++)
        {
            if (IgnoreSolid(hits[i].collider) || hits[i].normal.y > -0.35f)
                continue;

            if (hits[i].point.y < underside)
                underside = hits[i].point.y;
        }

        if (float.IsPositiveInfinity(underside))
            return position;

        float limit = underside - clearance;
        if (position.y > limit)
            position.y = limit;
        return position;
    }

    bool IgnoreSolid(Collider collider)
    {
        if (collider == null || collider.isTrigger)
            return true;

        if (blockHolder != null && collider.transform.IsChildOf(blockHolder))
            return true;

        if (collider.name != "Hitbox")
            return false;

        Vector3 size = collider.bounds.size;
        return size.x > 2f && size.y > 2f && size.z > 2f;
    }

    void ReleaseVolume(SmokeVolume volume)
    {
        if (volume.smoke == null)
            return;

        if (volume.smoke.onFire)
            volume.smoke.ResetObj();

        Destroy(volume.smoke.gameObject);
        volume.smoke = null;
    }

    void ResetFront(FireFront front)
    {
        front.originLocked = false;
        front.burnSeconds = 0f;
        front.spawnCredit = 0f;
        front.flameOutput = 0f;
        front.farthestDistance = 0f;
        front.room = null;
    }

    void ReleaseFront(FireFront front)
    {
        for (int i = 0; i < front.volumes.Count; i++)
            ReleaseVolume(front.volumes[i]);

        front.volumes.Clear();
        RestoreEmbeddedSmoke(front);
        ResetFront(front);
    }

    void ClearFronts()
    {
        for (int i = 0; i < fronts.Count; i++)
            ReleaseFront(fronts[i]);

        fronts.Clear();
    }

    void OnDrawGizmosSelected()
    {
        for (int i = 0; i < fronts.Count; i++)
        {
            RoomAir room = fronts[i].room;
            if (room == null)
                continue;

            Gizmos.color = new Color(0.4f, 0.85f, 1f, 0.35f);
            foreach (Vector2Int cell in room.open)
            {
                Vector3 center = CellCenter(room, cell);
                center.y = CruiseHeight(room);
                Gizmos.DrawWireCube(center, new Vector3(CellSize * 0.85f, 0.05f, CellSize * 0.85f));
            }
        }
    }
}
