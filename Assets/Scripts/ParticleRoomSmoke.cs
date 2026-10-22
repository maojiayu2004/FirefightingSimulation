using UnityEngine;
using UnityEngine.VFX;
using Ignis;

/// <summary>One emitter per real fire; lifecycle is read-only and particle simulation stays on the GPU.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(100)]
public sealed class ParticleRoomSmoke : MonoBehaviour
{
    public const int GraphCapacity = 4096;
    public FlammableObject source;
    [Tooltip("Optional shared fire lifecycle. The Source still defines the smoke emission geometry.")]
    public FireProfileController lifecycleFireSource;
    [Tooltip("Optional explicit emission origin. Its up axis defines the emission cone.")]
    public Transform emissionOrigin;
    public BoxCollider emissionSurface;
    public VisualEffect effect;
    public Bounds roomBounds;
    [Tooltip("Optional real-geometry GPU collision. Room Bounds is unused by this mode.")]
    public SmokeCollisionField collisionField;
    [Min(0.02f)] public float collisionRadius=0.08f;
    [Min(0)] public float particlesPerSecond = 15;
    [Range(0, 65)] public float spreadAngle = 32;
    [Min(0)] public float lateralSpread = 0.3f;
    [Min(0)] public float density = 1;
    [Range(64, 8192)] public int maxParticles = GraphCapacity;
    [Min(0.1f)] public float riseSpeed = 1.1f;
    [Min(0)] public float ceilingSpeed = 0.7f;
    [Min(0)] public float turbulence = 0.1f;
    [Header("Interior mixing and upper smoke layer")]
    [Range(0,2)] public float mixingStrength = 1.0f;
    [Min(0)] public float wallReturnSpeed = 0.4f;
    [Min(0.15f)] public float wallAvoidanceDistance = 0.45f;
    [Min(0.18f)] public float maxLayerDepth = 1.65f;
    [Min(1)] public float layerBuildSeconds = 24;
    [Min(1)] public float lifetime = 180;
    [Range(0,1)] public float opacity = 0.4f;
    [Min(0.05f)] public float startSize = 1f;
    [Min(0.05f)] public float endSize = 3.5f;
    [Min(0.1f)] public float growthSeconds = 4.5f;
    public Color smokeColor = new Color(0.32f, 0.33f, 0.34f);
    [Min(0.05f)] public float emissionTransitionSeconds = 0.6f;
    [Tooltip("Replace only this fire's old smoke; restore it when this component is disabled.")]
    public bool replaceLegacySmoke;
    public RoomSmokeController legacyRoomSmoke;
    public bool emitFromTopSurface = true;
    public bool useMeasuredSurface;
    public Vector3 measuredSurfaceLocalCenter;
    public Vector3 measuredSurfaceWorldSize;
    public bool emissionEnabled = true;
    float rate;
    float layerFill;
    public float LayerFill => layerFill;
    NetworkedFireState lifecycle;
    FlammableObject cachedSource;
    bool initialized, legacyCaptured, legacyWasEnabled;
    float oldAlpha, oldMultiplier;
    readonly System.Collections.Generic.List<Vector2> oldZones = new System.Collections.Generic.List<Vector2>();
    public float CurrentRate => rate;
    public bool IsBurning { get; private set; }
    public float EffectiveRate => Mathf.Min(Mathf.Max(0,particlesPerSecond*density), Mathf.Clamp(maxParticles,64,collisionField!=null?8192:GraphCapacity)/(Mathf.Max(1,lifetime)*1.15f));
    public Vector3 SpawnCenter { get; private set; }
    public Vector3 SpawnSize { get; private set; }

    void Awake(){CaptureLegacy();LateUpdate();}
    void Start()
    {
        initialized=true;
        CaptureLegacy();
        Configure();
        if(effect!=null) { effect.SetFloat("EmissionRate",0); effect.Reinit(); effect.Play(); }
    }
    void OnEnable() { if(initialized) { CaptureLegacy(); if(effect!=null) effect.Play(); } }
    void OnDisable()
    {
        rate=0; IsBurning=false;
        if(effect!=null) effect.SetFloat("EmissionRate",0);
        RestoreLegacy();
    }
    void CaptureLegacy()
    {
        if(!replaceLegacySmoke || source==null || legacyCaptured)return;
        oldAlpha=source.smokeAlpha; oldMultiplier=source.smokeVFXMultiplier;
        legacyCaptured=true;
        if(legacyRoomSmoke!=null && legacyRoomSmoke.fireSource!=null && legacyRoomSmoke.fireSource.gameObject==source.gameObject)
        {
            legacyWasEnabled=legacyRoomSmoke.enabled;legacyRoomSmoke.enabled=false;
            foreach(var zone in legacyRoomSmoke.smokeZones)
                oldZones.Add(zone.smokeSource!=null?new Vector2(zone.smokeSource.smokeAlpha,zone.smokeSource.smokeVFXMultiplier):Vector2.zero);
        }
    }
    void LateUpdate()
    {
        if(!legacyCaptured)return;
        if(source!=null){source.smokeAlpha=0;source.smokeVFXMultiplier=0;}
        if(legacyRoomSmoke!=null && oldZones.Count==legacyRoomSmoke.smokeZones.Count)
            foreach(var zone in legacyRoomSmoke.smokeZones)if(zone.smokeSource!=null){zone.smokeSource.smokeAlpha=0;zone.smokeSource.smokeVFXMultiplier=0;}
    }
    void RestoreLegacy()
    {
        if(!legacyCaptured)return;
        if(source!=null){source.smokeAlpha=oldAlpha;source.smokeVFXMultiplier=oldMultiplier;}
        if(legacyRoomSmoke!=null && oldZones.Count==legacyRoomSmoke.smokeZones.Count)
        {
            for(int i=0;i<oldZones.Count;i++)if(legacyRoomSmoke.smokeZones[i].smokeSource!=null)
            {legacyRoomSmoke.smokeZones[i].smokeSource.smokeAlpha=oldZones[i].x;legacyRoomSmoke.smokeZones[i].smokeSource.smokeVFXMultiplier=oldZones[i].y;}
            legacyRoomSmoke.enabled=legacyWasEnabled;
        }
        oldZones.Clear();legacyCaptured=false;
    }
    void Update()
    {
        if(effect==null)return;
        if(source==null){rate=0;IsBurning=false;effect.SetFloat("EmissionRate",0);return;}
        FlammableObject fire=lifecycleFireSource!=null?lifecycleFireSource.GetComponent<FlammableObject>():source;
        if(fire==null){rate=0;IsBurning=false;effect.SetFloat("EmissionRate",0);return;}
        if(cachedSource!=fire){cachedSource=fire;lifecycle=fire.GetComponent<NetworkedFireState>();}
        bool burning=fire.onFire && !fire.IsExtinguished() && !fire.hasBurnedOut();
        if(lifecycle!=null && lifecycle.isActiveAndEnabled)
            burning=lifecycle.TryGetLifecycleSnapshot(out var state) && state.IsBurning && !state.IsExtinguished && !state.IsBurnedOut;
        IsBurning=burning;
        // Authored smoke yield per active source, not a temperature-to-mass conversion.
        float target=burning && emissionEnabled ? EffectiveRate : 0;
        rate=Mathf.MoveTowards(rate,target,Mathf.Max(rate,EffectiveRate)*Time.deltaTime/Mathf.Max(0.05f,emissionTransitionSeconds));
        if(target==0 && rate<0.01f)rate=0;
        // Visual retained-smoke proxy, not physical concentration. Keep residual
        // layer depth after extinguishment while existing particles dissipate.
        float smokeYield=rate/Mathf.Max(1,EffectiveRate);
        layerFill=Mathf.Clamp01(layerFill+(smokeYield/Mathf.Max(1,layerBuildSeconds)-layerFill/Mathf.Max(1,lifetime*2))*Time.deltaTime);
        Configure();
        effect.SetFloat("EmissionRate",rate);
        effect.SetFloat("StepSeconds",Mathf.Min(Time.deltaTime,0.05f));
        SetFloatIfPresent("FlowTime",Time.time);
        SetFloatIfPresent("LayerFill",layerFill);
    }
    public void Configure()
    {
        if(effect==null || source==null) return;
        SpawnCenter=source.GetFireOrigin();
        SpawnSize=Vector3.one*0.12f;
        Vector3 axisX=Vector3.right,axisZ=Vector3.forward;
        if(emissionOrigin!=null)
        {
            SpawnCenter=emissionOrigin.position;
            axisX=emissionOrigin.right;axisZ=emissionOrigin.forward;
        }
        else if(emissionSurface!=null)
        {
            Bounds box=emissionSurface.bounds;
            SpawnSize=box.size;
            SpawnCenter=box.center;
            if(useMeasuredSurface)
            {
                SpawnCenter=source.transform.TransformPoint(measuredSurfaceLocalCenter);
                SpawnSize=measuredSurfaceWorldSize;
            }
            if(emitFromTopSurface)
            {
                // Ignis's visual emission bounds can be a volume. Use its actual top face.
                SpawnCenter+=Vector3.up*(SpawnSize.y*0.5f+0.025f);
                SpawnSize=new Vector3(SpawnSize.x,0.04f,SpawnSize.z);
            }
            SpawnSize=new Vector3(Mathf.Max(0.08f,SpawnSize.x-0.08f),SpawnSize.y,Mathf.Max(0.08f,SpawnSize.z-0.08f));
            float spread=Mathf.Max(0.12f,source.fireSpread);
            // Appliance pivots can lie behind their visible burner surface. Never collapse
            // a measured surface back toward that pivot (the old stove strip was only 1 cm wide).
            Vector3 origin=useMeasuredSurface?SpawnCenter:source.GetFireOrigin();
            if(useMeasuredSurface)spread=Mathf.Max(spread,Mathf.Max(SpawnSize.x,SpawnSize.z)*0.5f);
            Vector3 low=SpawnCenter-SpawnSize*0.5f, high=SpawnCenter+SpawnSize*0.5f;
            low.x=Mathf.Max(low.x,origin.x-spread);low.z=Mathf.Max(low.z,origin.z-spread);
            high.x=Mathf.Min(high.x,origin.x+spread);high.z=Mathf.Min(high.z,origin.z+spread);
            if(collisionField==null)
            {
                low.x=Mathf.Max(low.x,roomBounds.min.x+0.22f);low.z=Mathf.Max(low.z,roomBounds.min.z+0.22f);
                high.x=Mathf.Min(high.x,roomBounds.max.x-0.22f);high.z=Mathf.Min(high.z,roomBounds.max.z-0.22f);
            }
            high.x=Mathf.Max(low.x+0.01f,high.x);high.z=Mathf.Max(low.z+0.01f,high.z);
            SpawnCenter=(low+high)*0.5f;SpawnSize=high-low;
        }
        effect.SetVector3("SourceCenter",SpawnCenter);
        effect.SetVector3("SourceSize",SpawnSize);
        effect.SetVector3("SourceAxisX",axisX);
        effect.SetVector3("SourceAxisZ",axisZ);
        if(effect.HasVector3("RoomMin"))effect.SetVector3("RoomMin",roomBounds.min);
        if(effect.HasVector3("RoomMax"))effect.SetVector3("RoomMax",roomBounds.max);
        if(collisionField!=null){collisionField.Bind(effect);SetFloatIfPresent("CollisionRadius",collisionRadius);}
        if(effect.HasVector3("BoundsCenter"))effect.SetVector3("BoundsCenter",roomBounds.center);
        if(effect.HasVector3("BoundsSize"))effect.SetVector3("BoundsSize",roomBounds.size+Vector3.one);
        effect.SetFloat("RiseSpeed",riseSpeed);
        effect.SetFloat("CeilingSpeed",ceilingSpeed);
        effect.SetFloat("TurbulenceStrength",turbulence);
        effect.SetFloat("ParticleLifetime",lifetime);
        effect.SetFloat("SmokeOpacity",opacity);
        // Original prototype graph remains available for before/after comparison.
        SetFloatIfPresent("SpreadAngle",spreadAngle);SetFloatIfPresent("LateralSpread",lateralSpread);
        SetFloatIfPresent("StartSize",startSize);SetFloatIfPresent("EndSize",endSize);SetFloatIfPresent("GrowthSeconds",growthSeconds);
        SetFloatIfPresent("MixingStrength",mixingStrength);SetFloatIfPresent("WallReturnSpeed",wallReturnSpeed);
        SetFloatIfPresent("WallAvoidanceDistance",wallAvoidanceDistance);SetFloatIfPresent("MaxLayerDepth",maxLayerDepth);
        if(effect.HasVector3("SmokeColor"))effect.SetVector3("SmokeColor",new Vector3(smokeColor.r,smokeColor.g,smokeColor.b));
        // Renderer bounds include the entire room, not just the initial plume.
        var renderer=effect.GetComponent<Renderer>();
        if(renderer!=null && collisionField!=null)renderer.ResetLocalBounds();
        if(renderer!=null && collisionField==null)
        {
            var local= new Bounds(transform.InverseTransformPoint(roomBounds.center),Vector3.zero);
            for(int i=0;i<8;i++) local.Encapsulate(transform.InverseTransformPoint(roomBounds.center+Vector3.Scale(roomBounds.extents+Vector3.one,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1))));
            renderer.localBounds=local;
        }
    }
    void SetFloatIfPresent(string name,float value){if(effect.HasFloat(name))effect.SetFloat(name,value);}
    void OnDrawGizmosSelected()
    {
        Configure();
        if(collisionField==null){Gizmos.color=Color.cyan; Gizmos.DrawWireCube(roomBounds.center,roomBounds.size);}
        Gizmos.color=Color.yellow; Gizmos.DrawWireCube(SpawnCenter,SpawnSize);
        Gizmos.DrawWireSphere(SpawnCenter,0.12f);
        Vector3 up=emissionOrigin!=null?emissionOrigin.up:Vector3.up;
        Vector3 right=emissionOrigin!=null?emissionOrigin.right:Vector3.right;
        Vector3 forward=emissionOrigin!=null?emissionOrigin.forward:Vector3.forward;
        float height=Mathf.Min(2,Mathf.Max(0.2f,roomBounds.max.y-SpawnCenter.y));
        Gizmos.DrawLine(SpawnCenter,SpawnCenter+up*height);
        float radius=Mathf.Tan(spreadAngle*Mathf.Deg2Rad)*height;
        for(int i=0;i<24;i++)
        {
            float a=i*Mathf.PI/12,b=(i+1)*Mathf.PI/12;
            Vector3 end=SpawnCenter+up*height+(right*Mathf.Cos(a)+forward*Mathf.Sin(a))*radius;
            Gizmos.DrawLine(end,SpawnCenter+up*height+(right*Mathf.Cos(b)+forward*Mathf.Sin(b))*radius);
            if(i%6==0)Gizmos.DrawLine(SpawnCenter,end);
        }
    }
}
