using Ignis;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class HazardTemperature : MonoBehaviour
{
    [Header("Thermal State")]
    [Tooltip("Current normalized apparent surface heat shown by the TIC (0 = ambient, 1 = hottest).")]
    [Range(0f, 1f)] public float temperature;

    [Tooltip("Normalized heat gained per second while burning or receiving radiant heat.")]
    [Min(0f)] public float heatUpSpeed = 0.22f;

    [Tooltip("Cooling response. The existing value of 0.5 cools a full-hot surface over roughly 22 seconds.")]
    [Min(0f)] public float coolDownSpeed = 0.5f;

    public float NormalizedTemperature => temperature;
    public bool IsDirectHeatSource => directHeatSource;
    // Heated objects re-radiate a reduced fraction of their stored heat. The
    // attenuation prevents a chain of warm surfaces from amplifying itself.
    public float RadiantHeat => directHeatSource ? temperature : temperature * 0.25f;
    public float EstimatedCelsius
    {
        get
        {
            float maximum = fireProfile != null && fireProfile.maxTemperature > 0
                ? fireProfile.maxTemperature
                : 650f;
            float apparentSurfaceMaximum = Mathf.Clamp(maximum, 350f, 520f);
            return Mathf.Lerp(21f, apparentSurfaceMaximum, Mathf.Clamp01(temperature));
        }
    }

    private static readonly int TemperatureId = Shader.PropertyToID("_Temperature");
    private static readonly int TemperatureCelsiusId = Shader.PropertyToID("_TemperatureCelsius");
    private const float CoolingRateScale = 0.09f;

    private MaterialPropertyBlock propertyBlock;
    private FireProfileController fireProfile;
    private FlammableObject flammableObject;
    private Renderer[] renderers;
    private bool externallyIgnited;
    private bool directHeatSource;
    private float radiantTarget;
    private Vector3 radiantHeatOrigin;
    private bool hasRadiantHeatOrigin;
    private Bounds thermalBounds;
    private bool hasThermalBounds;
    private Matrix4x4 cachedLocalToWorld;

    private bool IsBurning => externallyIgnited || (flammableObject != null && flammableObject.onFire);

    private void Awake()
    {
        propertyBlock = new MaterialPropertyBlock();
        CacheDependencies();
        ApplyTemperature();
    }

    private void OnEnable()
    {
        CacheDependencies();
        ThermalRadiationManager.Register(this);
        ApplyTemperature();
    }

    private void OnDisable()
    {
        ThermalRadiationManager.Unregister(this);
    }

    private void Update()
    {
        bool burning = IsBurning;
        if (burning)
            directHeatSource = true;

        float directTarget = burning ? GetBurningTarget() : 0f;
        float target = Mathf.Max(directTarget, radiantTarget);
        float speed = target > temperature ? heatUpSpeed : coolDownSpeed * CoolingRateScale;
        temperature = Mathf.MoveTowards(temperature, target, Mathf.Max(0f, speed) * Time.deltaTime);

        if (!burning && temperature <= 0.001f)
        {
            directHeatSource = false;
            if (radiantTarget <= 0.001f)
                hasRadiantHeatOrigin = false;
        }

        ApplyTemperature();
    }

    private void CacheDependencies()
    {
        fireProfile = GetComponent<FireProfileController>();
        flammableObject = GetComponent<FlammableObject>();
        renderers = GetComponentsInChildren<Renderer>(true);
        CacheThermalBounds();
    }

    private void CacheThermalBounds()
    {
        hasThermalBounds = false;
        thermalBounds = new Bounds(transform.position, Vector3.zero);

        if (renderers != null)
        {
            foreach (Renderer targetRenderer in renderers)
            {
                if (targetRenderer == null || targetRenderer is ParticleSystemRenderer)
                    continue;

                if (!hasThermalBounds)
                {
                    thermalBounds = targetRenderer.bounds;
                    hasThermalBounds = true;
                }
                else
                {
                    thermalBounds.Encapsulate(targetRenderer.bounds);
                }
            }
        }

        cachedLocalToWorld = transform.localToWorldMatrix;
    }

    private float GetBurningTarget()
    {
        // Ignite() is also the replicated heat-visual contract. It must produce
        // heat even when a local FireProfileController is only a network puppet.
        if (externallyIgnited)
            return 1f;

        if (fireProfile != null && fireProfile.maxTemperature > 0)
            return Mathf.Clamp01(fireProfile.currentTemperature / fireProfile.maxTemperature);

        if (flammableObject != null && flammableObject.onFire)
        {
            float warmUpTime = Mathf.Max(0.1f, flammableObject.achieveMaxBrightness_s);
            return Mathf.Lerp(0.65f, 1f, Mathf.Clamp01(flammableObject.onFireTimer / warmUpTime));
        }

        return 1f;
    }

    private void ApplyTemperature()
    {
        if (renderers == null)
            return;

        propertyBlock ??= new MaterialPropertyBlock();
        foreach (Renderer targetRenderer in renderers)
        {
            if (targetRenderer == null || targetRenderer is ParticleSystemRenderer)
                continue;

            targetRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetFloat(TemperatureId, Mathf.Clamp01(temperature));
            propertyBlock.SetFloat(TemperatureCelsiusId, EstimatedCelsius);
            targetRenderer.SetPropertyBlock(propertyBlock);
        }
    }

    public Vector3 GetRadiationOrigin()
    {
        if (directHeatSource && flammableObject != null)
            return flammableObject.GetFireOrigin();
        if (hasRadiantHeatOrigin)
            return radiantHeatOrigin;
        return GetThermalCenter();
    }

    public Vector3 GetClosestThermalPoint(Vector3 worldPoint)
    {
        RefreshThermalBoundsIfMoved();
        return hasThermalBounds ? thermalBounds.ClosestPoint(worldPoint) : transform.position;
    }

    public void SetRadiantTarget(float normalizedHeat)
    {
        radiantTarget = Mathf.Clamp01(normalizedHeat);
    }

    public void AccumulateRadiantTarget(float normalizedHeat, Vector3 exposurePoint)
    {
        float candidate = Mathf.Clamp01(normalizedHeat);
        if (candidate <= radiantTarget)
            return;

        radiantTarget = candidate;
        radiantHeatOrigin = exposurePoint;
        hasRadiantHeatOrigin = true;
    }

    public void Ignite()
    {
        externallyIgnited = true;
        directHeatSource = true;
    }

    public void Extinguish()
    {
        externallyIgnited = false;
    }

    public void ResetTemperature()
    {
        externallyIgnited = false;
        directHeatSource = false;
        radiantTarget = 0f;
        hasRadiantHeatOrigin = false;
        temperature = 0f;

        if (renderers == null)
            CacheDependencies();

        ApplyTemperature();
    }

    private Vector3 GetThermalCenter()
    {
        RefreshThermalBoundsIfMoved();
        return hasThermalBounds ? thermalBounds.center : transform.position;
    }

    private void RefreshThermalBoundsIfMoved()
    {
        if (!hasThermalBounds || cachedLocalToWorld != transform.localToWorldMatrix)
            CacheThermalBounds();
    }
}
