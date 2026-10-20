Shader "Thermal/White Hot Fullscreen"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "WhiteHot"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Fragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord.xy;
                half3 source = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, _BlitMipLevel).rgb;

                // Preserve real material and lighting detail, then shape it into
                // the high-contrast luminance response of a white-hot TIC.
                half luminance = dot(source, half3(0.2126, 0.7152, 0.0722));
                luminance = saturate((luminance - 0.42) * 1.55 + 0.46);
                luminance = pow(max(luminance, 0.001), 0.78);

                float2 centered = uv * 2.0 - 1.0;
                half vignette = saturate(1.0 - dot(centered, centered) * 0.10);
                half detectorNoise = frac(sin(dot(floor(input.positionCS.xy), float2(12.9898, 78.233))) * 43758.5453) - 0.5;
                luminance = saturate(luminance * vignette + detectorNoise * 0.007);
                // Lift the detector black floor without dimming white-hot highlights.
                luminance = lerp(0.025, 1.0, luminance);

                return half4(luminance.xxx, 1.0);
            }
            ENDHLSL
        }
    }
}
