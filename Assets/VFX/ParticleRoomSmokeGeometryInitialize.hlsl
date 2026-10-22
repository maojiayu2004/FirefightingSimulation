void InitializeRoomSmoke(inout VFXAttributes attributes, in float3 SourceCenter, in float3 SourceSize,
    in float3 SourceAxisX, in float3 SourceAxisZ, in float RiseSpeed, in float ParticleLifetime,
    in float SpreadAngle, in float StartSize, in float3 SmokeColor)
{
    float3 r=VFXRAND3-0.5;
    attributes.position=SourceCenter+SourceAxisX*r.x*SourceSize.x+float3(0,r.y*SourceSize.y,0)+SourceAxisZ*r.z*SourceSize.z;
    float azimuth=VFXRAND*6.283185;
    float cosTheta=lerp(1.0,cos(radians(SpreadAngle)),VFXRAND);
    float sinTheta=sqrt(max(0.0,1.0-cosTheta*cosTheta));
    float3 up=normalize(cross(SourceAxisZ,SourceAxisX));
    float3 direction=up*cosTheta+(SourceAxisX*cos(azimuth)+SourceAxisZ*sin(azimuth))*sinTheta;
    attributes.velocity=direction*RiseSpeed*(0.85+VFXRAND*0.3);
    attributes.lifetime=ParticleLifetime*(0.85+VFXRAND*0.3);
    attributes.age=0;
    float heading=frac(sin((float)attributes.particleId*12.9898+7.1)*43758.5453)*6.283185;
    // xz stores persistent transport direction; y stores the last actual overhead surface.
    attributes.mass=0; attributes.targetPosition=float3(cos(heading),SourceCenter.y,sin(heading));
    attributes.size=StartSize*(0.85+VFXRAND*0.3);
    attributes.color=SmokeColor*(0.9+VFXRAND*0.2);
    attributes.alpha=0;
    attributes.angleZ=VFXRAND*6.283185;
    attributes.texIndex=VFXRAND*15;
}

