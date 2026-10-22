// Incremental geometry variant of the existing buoyancy/eddy/layer update.
// FieldMin/FieldSize address a texture ONLY. Outside that texture is open space.
void PropagateGeometrySmoke(inout VFXAttributes attributes, in VFXSampler3D SceneField,
    in float3 FieldMin, in float3 FieldSize, in float FieldVoxel, in float CollisionRadius,
    in float3 SourceCenter, in float StepSeconds, in float RiseSpeed, in float CeilingSpeed,
    in float TurbulenceStrength, in float SmokeOpacity, in float LateralSpread,
    in float StartSize, in float EndSize, in float GrowthSeconds, in float FlowTime,
    in float MixingStrength, in float WallReturnSpeed, in float WallAvoidanceDistance,
    in float LayerFill, in float MaxLayerDepth)
{
    float dt=clamp(StepSeconds,0,0.05),age=attributes.age;
    float id=(float)attributes.particleId,phase=fmod(id,997.0)*0.043;
    float growth=smoothstep(0.0,max(0.1,GrowthSeconds),age);
    float naturalSize=lerp(StartSize,EndSize,growth)*(0.9+0.1*sin(phase));
    float3 p=attributes.position;
    float3 uv=(p-FieldMin)/FieldSize;
    float4 surface=float4(0,0,0,1);
    if(all(uv>=0) && all(uv<=1))surface=SampleTexture(SceneField,uv,0);

    // Detect actual overhanging geometry, including changing ceiling heights.
    float ceiling=0;bool overhead=false;
    for(int i=0;i<4;i++)
    {
        float3 probe=p+float3(0,i*0.45,0),coord=(probe-FieldMin)/FieldSize;
        if(any(coord<0) || any(coord>1))continue;
        float4 hitField=SampleTexture(SceneField,coord,0);
        if(hitField.y < -0.55 && hitField.w < 0.65)
        {ceiling=probe.y-hitField.y*hitField.w;overhead=true;break;}
    }
    float top=attributes.mass;
    if(overhead)
    {
        attributes.targetPosition.y=ceiling;
        top=max(top,smoothstep(ceiling-0.8,ceiling-0.3,p.y));
    }
    else top=max(0,top-dt*0.8); // released from a shelf/doorway: resume rising.
    attributes.mass=top;
    float depthSample=0.08+0.92*frac(sin(id*23.713+3.7)*19341.341);
    float thickness=lerp(0.18,MaxLayerDepth,saturate(LayerFill));
    float layerY=attributes.targetPosition.y-0.2-thickness*depthSample;
    float vertical=lerp(RiseSpeed,clamp((layerY-p.y)*0.65,-0.45,0.45),top);
    vertical+=sin(p.x*0.9+p.z*0.7+FlowTime*0.4+phase*0.2)*TurbulenceStrength*0.15;
    float2 eddy=float2(sin(p.z*0.85+FlowTime*0.37)+0.3*sin(phase+FlowTime*0.29),cos(p.x*0.83-FlowTime*0.32)+0.3*cos(phase-FlowTime*0.23));
    float2 heading=attributes.targetPosition.xz;
    float turn=0.07*dt;
    heading=float2(heading.x*cos(turn)-heading.y*sin(turn),heading.x*sin(turn)+heading.y*cos(turn));
    if(surface.w<WallAvoidanceDistance && length(surface.xz)>.55)
    {
        float2 away=normalize(surface.xz);float inward=dot(heading,away);
        // Redirect the persistent destination, not the contact velocity. Otherwise the
        // next frame reintroduces the same inward goal and pins smoke against walls.
        if(inward<0)heading=normalize(heading-2*away*inward+away*.2);
    }
    attributes.targetPosition.xz=heading;
    float2 transport=heading*CeilingSpeed*MixingStrength*lerp(0.18,1.0,top);
    float2 radial=p.xz-SourceCenter.xz;
    float2 initialSpread=radial/max(length(radial),0.08)*LateralSpread*exp(-age/3.0);
    float2 horizontal=initialSpread+eddy*TurbulenceStrength+transport;
    if(surface.w<WallAvoidanceDistance)
        horizontal+=surface.xz*WallReturnSpeed*(1-smoothstep(CollisionRadius,WallAvoidanceDistance,surface.w));
    horizontal*=min(1,(CeilingSpeed+TurbulenceStrength)/max(length(horizontal),0.001));
    float3 velocity=lerp(attributes.velocity,float3(horizontal.x,vertical,horizontal.y),1-exp(-1.8*dt));
    // Conservative clearance accounts for trilinear interpolation of a thin two-sided surface.
    float clearance=CollisionRadius+FieldVoxel*0.9;
    for(int step=0;step<4;step++)
    {
        float3 q=p+velocity*(dt*0.25),coord=(q-FieldMin)/FieldSize;
        if(all(coord>=0) && all(coord<=1))
        {
            float4 hitField=SampleTexture(SceneField,coord,0);
            if(hitField.w<clearance)
            {
                float3 normal=hitField.xyz;
                if(dot(normal,surface.xyz)<-0.1 && surface.w<0.4)normal=surface.xyz;
                normal=length(normal)>0.1?normalize(normal):normalize(-velocity+float3(0,0.001,0));
                q+=normal*(clearance-hitField.w);
                velocity-=normal*min(0,dot(velocity,normal)); // keep tangential flow; no bounce.
            }
            surface=hitField;
        }
        p=q;
    }
    attributes.position=p;attributes.velocity=velocity;
    // Preserve the tuned visual growth. Collision constrains particle centers;
    // the existing soft-particle/depth rendering clips the billboard at scene surfaces.
    attributes.size=naturalSize;
    float birth=smoothstep(0,0.45,age),death=saturate((attributes.lifetime-age)/9.0);
    attributes.alpha=SmokeOpacity*birth*death*lerp(1.8,0.75,growth);
    attributes.texIndex=fmod(age*5.0+(float)(attributes.particleId%16u),16.0);
}

